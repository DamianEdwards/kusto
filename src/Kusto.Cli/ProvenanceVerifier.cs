using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sigstore;

namespace Kusto.Cli;

internal sealed class ProvenanceVerifier(ILogger<ProvenanceVerifier> logger)
{
    private const string GitHubActionsOidcIssuer = "https://token.actions.githubusercontent.com";
    private const string TrustedReleaseWorkflowFile = "release.yml";
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(60);
    private readonly ILogger<ProvenanceVerifier> _logger = logger;
    private readonly SigstoreVerifier _sigstoreVerifier = new();

    public static string GetExpectedSha256(string checksumsPath, string assetName)
    {
        foreach (var line in File.ReadLines(checksumsPath))
        {
            var parts = line.Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && parts[^1].Equals(assetName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new UserFacingException(
            $"checksums.txt does not contain a valid SHA256 entry for '{assetName}'.");
    }

    public static void VerifyChecksum(string filePath, string checksumsPath, string assetName)
    {
        var expectedHash = GetExpectedSha256(checksumsPath, assetName);
        using var stream = File.OpenRead(filePath);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"SHA256 verification failed for '{assetName}'. The download may be corrupt or tampered with.");
        }
    }

    public static void ValidateReleaseMetadata(
        string metadataPath,
        string assetName,
        string expectedSha256,
        string expectedVersion)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version)
            || !string.Equals(
                version.GetString()?.TrimStart('v'),
                expectedVersion.TrimStart('v'),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"release-metadata.json does not identify expected version '{expectedVersion}'.");
        }

        if (!root.TryGetProperty("assets", out var assets))
        {
            throw new UserFacingException("release-metadata.json does not contain an assets array.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var metadataHash = asset.TryGetProperty("sha256", out var hash)
                ? hash.GetString()
                : null;
            if (!string.Equals(metadataHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UserFacingException(
                    $"release-metadata.json does not agree with checksums.txt for '{assetName}'.");
            }

            return;
        }

        throw new UserFacingException(
            $"release-metadata.json does not contain '{assetName}'.");
    }

    public async Task VerifyWindowsPayloadAsync(
        string payloadDirectory,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Authenticode payload verification is only available on Windows.");
        }

        foreach (var fileName in AppIdentity.GetExecutablePayloadFileNames())
        {
            var filePath = Path.Combine(payloadDirectory, fileName);
            if (!File.Exists(filePath))
            {
                throw new UserFacingException(
                    $"The Windows update payload is missing signed file '{fileName}'.");
            }

            await VerifyAuthenticodeAsync(filePath, cancellationToken);
        }
    }

    public async Task VerifyArchiveAttestationAsync(
        string archivePath,
        string repository,
        string sourceRef,
        string bundlePath,
        CancellationToken cancellationToken)
    {
        if (!TryParseRepository(repository, out var owner, out var repositoryName))
        {
            throw new UserFacingException(
                $"Update repository '{repository}' must use owner/name format.");
        }

        if (!File.Exists(bundlePath))
        {
            throw new UserFacingException(
                "The release does not contain its portable Sigstore attestation bundle.");
        }

        string[] bundleLines;
        try
        {
            bundleLines = await File.ReadAllLinesAsync(bundlePath, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new UserFacingException(
                "The release Sigstore attestation bundle could not be read.",
                ex);
        }

        var policy = CreateGitHubActionsPolicy(
            owner,
            repositoryName,
            TrustedReleaseWorkflowFile,
            sourceRef);
        var failures = new List<string>();

        foreach (var bundleJson in bundleLines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var bundle = SigstoreBundle.Deserialize(bundleJson);
                await using var artifactStream = File.OpenRead(archivePath);
                var (success, result) =
                    await _sigstoreVerifier.TryVerifyStreamAsync(artifactStream, bundle, policy);
                if (success)
                {
                    _logger.LogInformation(
                        "Verified GitHub provenance for {Archive} from {Workflow}@{Ref}",
                        archivePath,
                        TrustedReleaseWorkflowFile,
                        sourceRef);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(result?.FailureReason))
                {
                    failures.Add(result.FailureReason);
                }
            }
            catch (JsonException ex)
            {
                failures.Add(ex.Message);
            }
        }

        var detail = failures.Count == 0
            ? string.Empty
            : $" Last verifier error: {failures[^1]}";
        throw new UserFacingException(
            $"GitHub provenance for '{Path.GetFileName(archivePath)}' did not match {repository}/.github/workflows/{TrustedReleaseWorkflowFile}@{sourceRef}.{detail}");
    }

    private async Task VerifyAuthenticodeAsync(
        string binaryPath,
        CancellationToken cancellationToken)
    {
        var script = ReadEmbeddedVerificationScript();
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"kusto-verify-{Guid.NewGuid():N}.ps1");

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
            var startInfo = new ProcessStartInfo
            {
                FileName = GetWindowsPowerShellPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-BinaryPath");
            startInfo.ArgumentList.Add(binaryPath);
            startInfo.Environment["PSModulePath"] = GetWindowsPowerShellModulePath();

            using var process = Process.Start(startInfo)
                ?? throw new UserFacingException(
                    "Could not start Windows PowerShell for Authenticode verification.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(VerificationTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
                throw new UserFacingException("Authenticode verification timed out.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new UserFacingException(
                    GetVerificationError(stdout, stderr));
            }
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    private static string GetVerificationError(string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(stdout);
                if (document.RootElement.TryGetProperty("error", out var error)
                    && error.GetString() is { Length: > 0 } message)
                {
                    return message;
                }
            }
            catch (JsonException)
            {
            }
        }

        return string.IsNullOrWhiteSpace(stderr)
            ? "Authenticode verification failed."
            : stderr.Trim();
    }

    private static VerificationPolicy CreateGitHubActionsPolicy(
        string owner,
        string repository,
        string workflowFile,
        string sourceRef)
        => new()
        {
            CertificateIdentity = new CertificateIdentity
            {
                Issuer = GitHubActionsOidcIssuer,
                SubjectAlternativeNamePattern =
                    $"^https://github\\.com/{Regex.Escape(owner)}/{Regex.Escape(repository)}/\\.github/workflows/{Regex.Escape(workflowFile)}@{Regex.Escape(sourceRef)}$",
                Extensions = new CertificateExtensionPolicy
                {
                    SourceRepositoryUri = $"https://github.com/{owner}/{repository}",
                    SourceRepositoryRef = sourceRef
                }
            }
        };

    private static string ReadEmbeddedVerificationScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name =>
                name.EndsWith("verify-provenance.ps1", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new UserFacingException(
                "This Windows build does not contain the Authenticode verifier.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool TryParseRepository(
        string repository,
        out string owner,
        out string name)
    {
        var parts = repository.Split(
            '/',
            2,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        owner = parts.Length == 2 ? parts[0] : string.Empty;
        name = parts.Length == 2 ? parts[1] : string.Empty;
        return parts.Length == 2;
    }

    private static string GetWindowsPowerShellPath()
    {
        var candidate = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    private static string GetWindowsPowerShellModulePath()
    {
        var modulePaths = new List<string>();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            modulePaths.Add(Path.Combine(documents, "WindowsPowerShell", "Modules"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            modulePaths.Add(Path.Combine(programFiles, "WindowsPowerShell", "Modules"));
        }

        if (!string.IsNullOrWhiteSpace(Environment.SystemDirectory))
        {
            modulePaths.Add(Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "Modules"));
        }

        return string.Join(
            Path.PathSeparator,
            modulePaths.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
