using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Kusto.Cli;

internal sealed class UpdateService(
    GitHubReleaseService releaseService,
    ProvenanceVerifier provenanceVerifier,
    UpdateStateStore stateStore,
    ILogger<UpdateService> logger)
{
    private readonly GitHubReleaseService _releaseService = releaseService;
    private readonly ProvenanceVerifier _provenanceVerifier = provenanceVerifier;
    private readonly UpdateStateStore _stateStore = stateStore;
    private readonly ILogger<UpdateService> _logger = logger;

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(
        bool allowPreRelease,
        bool stableOnly,
        CancellationToken cancellationToken)
    {
        ThrowIfDisabled();
        var currentVersionText = VersionHelper.GetCurrentVersion();
        if (!VersionHelper.TryParse(currentVersionText, out var currentVersion))
        {
            throw new UserFacingException(
                $"Could not determine the installed {AppIdentity.ProductName} version.");
        }

        var release = await _releaseService.GetLatestReleaseAsync(
            currentVersion,
            allowPreRelease,
            stableOnly,
            cancellationToken);
        return release is null
            ? null
            : new UpdateCheckResult(
                currentVersion.ToNormalizedString(),
                release.Version,
                release.TagName,
                release.IsDevBuild);
    }

    public async Task StageAsync(
        UpdateCheckResult update,
        bool skipProvenance,
        CancellationToken cancellationToken)
    {
        EnsureSelfUpdateSupported();
        if (!_stateStore.TryAcquireLock())
        {
            throw new UserFacingException(
                "Another Kusto CLI update operation is already in progress.");
        }

        UpdateState state;
        try
        {
            var existingState = _stateStore.Load();
            if (existingState.Status == UpdateStatus.Installing)
            {
                throw new UserFacingException(
                    "A detached Kusto CLI update installer is already running.");
            }

            state = new UpdateState
            {
                Status = UpdateStatus.Downloading,
                AvailableVersion = update.AvailableVersion,
                ReleaseTag = update.ReleaseTag,
                LastAttemptTime = DateTimeOffset.UtcNow
            };
            _stateStore.Save(state);
        }
        catch
        {
            _stateStore.ReleaseLock();
            throw;
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"kusto-update-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var assetName = GitHubReleaseService.GetPlatformAssetName();
            await _releaseService.DownloadReleaseAssetAsync(
                update.ReleaseTag,
                assetName,
                temporaryDirectory,
                cancellationToken);
            await _releaseService.DownloadReleaseAssetAsync(
                update.ReleaseTag,
                "checksums.txt",
                temporaryDirectory,
                cancellationToken);
            await _releaseService.DownloadReleaseAssetAsync(
                update.ReleaseTag,
                "release-metadata.json",
                temporaryDirectory,
                cancellationToken);

            var archivePath = Path.Combine(temporaryDirectory, assetName);
            var checksumsPath = Path.Combine(temporaryDirectory, "checksums.txt");
            var metadataPath = Path.Combine(temporaryDirectory, "release-metadata.json");
            ProvenanceVerifier.VerifyChecksum(archivePath, checksumsPath, assetName);
            var expectedHash = ProvenanceVerifier.GetExpectedSha256(checksumsPath, assetName);
            ProvenanceVerifier.ValidateReleaseMetadata(
                metadataPath,
                assetName,
                expectedHash,
                update.AvailableVersion);

            if (!OperatingSystem.IsWindows()
                && !update.IsDevBuild
                && !skipProvenance)
            {
                if (_releaseService.IsLocalSource)
                {
                    throw new UserFacingException(
                        $"Unix provenance verification requires a GitHub release source. Use --skip-provenance-checks only for a local source you trust.");
                }

                await _releaseService.DownloadReleaseAssetAsync(
                    update.ReleaseTag,
                    "attestations.jsonl",
                    temporaryDirectory,
                    cancellationToken);
                await _provenanceVerifier.VerifyArchiveAttestationAsync(
                    archivePath,
                    _releaseService.Repository,
                    $"refs/tags/{update.ReleaseTag}",
                    Path.Combine(temporaryDirectory, "attestations.jsonl"),
                    cancellationToken);
            }

            var extractedDirectory = Path.Combine(temporaryDirectory, "extracted");
            var extractedExecutable = GitHubReleaseService.ExtractReleaseArchive(
                archivePath,
                extractedDirectory);
            PayloadInstaller.ValidateManifest(extractedDirectory);

            if (OperatingSystem.IsWindows()
                && !update.IsDevBuild
                && !skipProvenance)
            {
                await _provenanceVerifier.VerifyWindowsPayloadAsync(
                    extractedDirectory,
                    cancellationToken);
            }

            await ValidateExtractedVersionAsync(
                extractedExecutable,
                update.AvailableVersion,
                cancellationToken);
            await PayloadInstaller.VerifyInstalledPayloadAsync(
                extractedDirectory,
                cancellationToken);

            var currentExecutable = Environment.ProcessPath!;
            var installDirectory = Path.GetDirectoryName(currentExecutable)!;
            var stageDirectory = Path.Combine(
                Path.GetDirectoryName(installDirectory)!,
                $".{Path.GetFileName(installDirectory)}.new-{Guid.NewGuid():N}");
            CopyDirectory(extractedDirectory, stageDirectory);

            state.Status = UpdateStatus.Staged;
            state.StagedDirectory = stageDirectory;
            state.InstallDirectory = installDirectory;
            state.BackupDirectory = Path.Combine(
                Path.GetDirectoryName(installDirectory)!,
                $".{Path.GetFileName(installDirectory)}.old-{Guid.NewGuid():N}");
            state.ErrorMessage = null;
            _stateStore.Save(state);
        }
        catch (Exception ex)
        {
            state.Status = UpdateStatus.Failed;
            state.ErrorMessage = ex.Message;
            _stateStore.Save(state);
            throw;
        }
        finally
        {
            _stateStore.ReleaseLock();
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public async Task<UpdateInstallResult> InstallStagedAsync(
        CancellationToken cancellationToken)
    {
        EnsureSelfUpdateSupported();
        if (!_stateStore.TryAcquireLock())
        {
            throw new UserFacingException(
                "Another Kusto CLI update operation is already in progress.");
        }

        try
        {
            var state = _stateStore.Load();
            if (state.Status == UpdateStatus.Installing)
            {
                throw new UserFacingException(
                    "A detached Kusto CLI update installer is already running.");
            }

            if (state.Status != UpdateStatus.Staged
                || string.IsNullOrWhiteSpace(state.StagedDirectory)
                || !Directory.Exists(state.StagedDirectory))
            {
                throw new UserFacingException("No verified update is staged for installation.");
            }

            if (OperatingSystem.IsWindows())
            {
                _ = PayloadInstaller.ScheduleWindowsInstall(state, _stateStore);
                return new UpdateInstallResult(Completed: false, Scheduled: true);
            }

            await PayloadInstaller.InstallOnUnixAsync(
                state,
                _stateStore,
                _logger,
                cancellationToken);
            return new UpdateInstallResult(Completed: true, Scheduled: false);
        }
        finally
        {
            _stateStore.ReleaseLock();
        }
    }

    private static async Task ValidateExtractedVersionAsync(
        string executablePath,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new UserFacingException(
                "Could not start the downloaded Kusto CLI to verify its version.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new UserFacingException(
                "The downloaded Kusto CLI did not report its version in time.");
        }
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new UserFacingException(
                $"The downloaded Kusto CLI could not report its version: {error}");
        }

        var candidate = output.Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => VersionHelper.TryParse(token, out _));
        if (!VersionHelper.TryParse(candidate, out var actual)
            || !VersionHelper.TryParse(expectedVersion, out var expected)
            || actual != expected)
        {
            throw new UserFacingException(
                $"The downloaded binary reported version '{output}', expected '{expectedVersion}'.");
        }
    }

    private static void EnsureSelfUpdateSupported()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                "Self-update is not supported from 'dotnet run'. Install a native Kusto CLI release first.");
        }
    }

    private static void ThrowIfDisabled()
    {
        var value = Environment.GetEnvironmentVariable(
            AppIdentity.DisableSelfUpdatesEnvVar);
        if (value is not null
            && value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on")
        {
            throw new UserFacingException(
                $"Self-update is disabled by {AppIdentity.DisableSelfUpdatesEnvVar}.");
        }
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var filePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }
}
