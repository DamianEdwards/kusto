using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Kusto.Cli;

internal sealed class GitHubReleaseService
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubReleaseService> _logger;
    private readonly string _repository;
    private readonly string? _localSource;

    public GitHubReleaseService(ILogger<GitHubReleaseService> logger)
        : this(
            logger,
            SharedHttpClient,
            Environment.GetEnvironmentVariable(AppIdentity.UpdateRepositoryEnvVar)
                ?? AppIdentity.DefaultRepository,
            Environment.GetEnvironmentVariable(AppIdentity.UpdateSourceEnvVar))
    {
    }

    internal GitHubReleaseService(
        ILogger<GitHubReleaseService> logger,
        HttpClient httpClient,
        string repository,
        string? localSource)
    {
        _logger = logger;
        _httpClient = httpClient;
        _repository = repository;
        _localSource = localSource;
    }

    public string Repository => _repository;
    public bool IsLocalSource => !string.IsNullOrWhiteSpace(_localSource);

    public async Task<GitHubRelease?> GetLatestReleaseAsync(
        NuGetVersion currentVersion,
        bool allowPreRelease,
        bool stableOnly,
        CancellationToken cancellationToken)
    {
        if (IsLocalSource)
        {
            return GetLocalRelease(currentVersion, allowPreRelease, stableOnly);
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_repository}/releases?per_page=100");
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ApiTimeout);
        using var response = await SendAsync(
            request,
            ApiTimeout,
            timeoutSource.Token,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                "list releases",
                response,
                timeoutSource.Token);
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(timeoutSource.Token);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: timeoutSource.Token);
        return SelectLatestRelease(
            document.RootElement,
            currentVersion,
            allowPreRelease,
            stableOnly,
            GetPlatformAssetName());
    }

    internal static GitHubRelease? SelectLatestRelease(
        JsonElement releases,
        NuGetVersion currentVersion,
        bool allowPreRelease,
        bool stableOnly,
        string assetName)
    {
        var candidates = new List<(NuGetVersion Version, GitHubRelease Release)>();

        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tagName = release.TryGetProperty("tag_name", out var tag)
                ? tag.GetString()
                : null;
            if (!VersionHelper.TryParse(tagName, out var version)
                || tagName!.StartsWith("install-scripts-v", StringComparison.OrdinalIgnoreCase)
                || !ReleaseHasAsset(release, assetName)
                || !VersionHelper.IsUpdateCandidate(currentVersion, version, allowPreRelease, stableOnly))
            {
                continue;
            }

            var isPrerelease = release.TryGetProperty("prerelease", out var prerelease)
                && prerelease.GetBoolean();
            candidates.Add((
                version,
                new GitHubRelease(
                    tagName,
                    version.ToNormalizedString(),
                    isPrerelease,
                    VersionHelper.IsDevBuild(version))));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version, VersionComparer.VersionRelease)
            .Select(candidate => candidate.Release)
            .FirstOrDefault();
    }

    public async Task DownloadReleaseAssetAsync(
        string tag,
        string assetName,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, assetName);

        if (IsLocalSource)
        {
            var localPath = Path.Combine(_localSource!, assetName);
            if (!File.Exists(localPath))
            {
                throw new UserFacingException(
                    $"The local update source '{_localSource}' does not contain '{assetName}'.");
            }

            File.Copy(localPath, destinationPath, overwrite: true);
            return;
        }

        var downloadUrl = await GetReleaseAssetDownloadUrlAsync(tag, assetName, cancellationToken);
        using var request = CreateRequest(HttpMethod.Get, downloadUrl);
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DownloadTimeout);
        using var response = await SendAsync(
            request,
            DownloadTimeout,
            timeoutSource.Token,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                $"download '{assetName}'",
                response,
                timeoutSource.Token);
        }

        await using var source =
            await response.Content.ReadAsStreamAsync(timeoutSource.Token);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, timeoutSource.Token);
    }

    public static string ExtractReleaseArchive(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGzipSafely(archivePath, destinationDirectory);
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
        }

        var executablePath = Path.Combine(destinationDirectory, AppIdentity.GetExecutableFileName());
        if (!File.Exists(executablePath))
        {
            throw new UserFacingException(
                $"The update archive did not contain '{AppIdentity.GetExecutableFileName()}'.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return executablePath;
    }

    private static void ExtractTarGzipSafely(
        string archivePath,
        string destinationDirectory)
    {
        var root = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            var name = entry.Name
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            while (name.StartsWith(
                       $".{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal))
            {
                name = name[2..];
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (Path.IsPathRooted(name))
            {
                throw new UserFacingException(
                    $"The update archive contains absolute path '{entry.Name}'.");
            }

            var destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, name));
            if (!destinationPath.StartsWith(root, StringComparison.Ordinal))
            {
                throw new UserFacingException(
                    $"The update archive contains path outside its payload: '{entry.Name}'.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destinationPath);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    entry.ExtractToFile(destinationPath, overwrite: true);
                    break;
                case TarEntryType.ExtendedAttributes:
                case TarEntryType.GlobalExtendedAttributes:
                    break;
                default:
                    throw new UserFacingException(
                        $"The update archive contains unsupported entry '{entry.Name}' ({entry.EntryType}).");
            }
        }
    }

    public static string GetPlatformAssetName()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            var value => throw new PlatformNotSupportedException(
                $"Self-update does not support architecture '{value}'.")
        };

        if (OperatingSystem.IsWindows())
        {
            return $"kusto-win-{architecture}.zip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"kusto-osx-{architecture}.tar.gz";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"kusto-linux-{architecture}.tar.gz";
        }

        throw new PlatformNotSupportedException("Self-update supports Windows, macOS, and Linux.");
    }

    private GitHubRelease? GetLocalRelease(
        NuGetVersion currentVersion,
        bool allowPreRelease,
        bool stableOnly)
    {
        if (!Directory.Exists(_localSource))
        {
            throw new UserFacingException(
                $"The local update source '{_localSource}' does not exist.");
        }

        var metadataPath = Path.Combine(_localSource, "release-metadata.json");
        if (!File.Exists(metadataPath))
        {
            throw new UserFacingException(
                $"The local update source '{_localSource}' does not contain release-metadata.json.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var versionText = document.RootElement.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;
        if (!VersionHelper.TryParse(versionText, out var version))
        {
            throw new UserFacingException(
                $"The local update source reported invalid version '{versionText}'.");
        }

        if (!VersionHelper.IsUpdateCandidate(currentVersion, version, allowPreRelease, stableOnly))
        {
            return null;
        }

        return new GitHubRelease(
            version.ToNormalizedString(),
            version.ToNormalizedString(),
            version.IsPrerelease,
            VersionHelper.IsDevBuild(version));
    }

    private async Task<string> GetReleaseAssetDownloadUrlAsync(
        string tag,
        string assetName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ApiTimeout);
        using var response = await SendAsync(
            request,
            ApiTimeout,
            timeoutSource.Token,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(
                $"read release '{tag}'",
                response,
                timeoutSource.Token);
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(timeoutSource.Token);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: timeoutSource.Token);
        if (document.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var downloadUrl)
                    && downloadUrl.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        throw new UserFacingException(
            $"Release '{tag}' does not contain the expected asset '{assetName}'.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new UserFacingException(
                $"GitHub did not respond within {timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException(
                "The update request to GitHub failed. Check network connectivity and retry.",
                ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue(AppIdentity.CommandName, VersionHelper.GetCurrentVersion() ?? "0.0.0"));

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<UserFacingException> CreateApiExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
        var rateLimitHint = response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? " Set GITHUB_TOKEN or GH_TOKEN if the unauthenticated API rate limit was exceeded."
            : string.Empty;
        return new UserFacingException(
            $"GitHub could not {operation}.{rateLimitHint}");
    }

    private static bool ReleaseHasAsset(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets))
        {
            return false;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
