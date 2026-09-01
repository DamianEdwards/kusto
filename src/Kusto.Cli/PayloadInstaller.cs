using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

internal static class PayloadInstaller
{
    private const string ManifestFileName = "payload-manifest.json";

    public static IReadOnlyList<string> ValidateManifest(string payloadDirectory)
    {
        var manifestPath = Path.Combine(payloadDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new UserFacingException(
                $"The update payload does not contain {ManifestFileName}.");
        }

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            KustoJsonSerializerContext.Default.PayloadManifest)
            ?? throw new UserFacingException(
                $"The update payload contains an invalid {ManifestFileName}.");
        if (manifest.Files.Count == 0)
        {
            throw new UserFacingException(
                $"The update payload contains an empty {ManifestFileName}.");
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var normalized = NormalizeRelativePath(payloadDirectory, entry);
            if (!declared.Add(normalized))
            {
                throw new UserFacingException(
                    $"{ManifestFileName} contains duplicate path '{entry}'.");
            }

            if (!File.Exists(Path.Combine(payloadDirectory, normalized)))
            {
                throw new UserFacingException(
                    $"The update payload is missing declared file '{entry}'.");
            }
        }

        var actual = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(payloadDirectory, path))
            .Where(path => !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!declared.SetEquals(actual))
        {
            var undeclared = actual.Except(declared, StringComparer.OrdinalIgnoreCase).ToArray();
            throw new UserFacingException(
                undeclared.Length > 0
                    ? $"The update payload contains undeclared file(s): {string.Join(", ", undeclared)}."
                    : $"The update payload manifest does not match the extracted archive.");
        }

        var executableFileName = AppIdentity.GetExecutableFileName();
        if (!declared.Contains(executableFileName))
        {
            throw new UserFacingException(
                $"The update payload manifest does not contain required file '{executableFileName}'.");
        }

        return declared.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task InstallOnUnixAsync(
        UpdateState state,
        UpdateStateStore stateStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.StagedDirectory)
            || string.IsNullOrWhiteSpace(state.InstallDirectory))
        {
            throw new UserFacingException("The staged update does not contain install paths.");
        }

        var stagedDirectory = state.StagedDirectory;
        var installDirectory = state.InstallDirectory;
        var backupDirectory = state.BackupDirectory
            ?? Path.Combine(
                Path.GetDirectoryName(installDirectory)!,
                $".{Path.GetFileName(installDirectory)}.old-{Guid.NewGuid():N}");
        state.BackupDirectory = backupDirectory;
        state.Status = UpdateStatus.Installing;
        state.LastAttemptTime = DateTimeOffset.UtcNow;
        stateStore.Save(state);

        var newFiles = ValidateManifest(stagedDirectory);
        var managedFiles = ReadManagedFiles(installDirectory)
            .Concat(newFiles)
            .Append(ManifestFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var mutationStarted = false;
        try
        {
            Directory.CreateDirectory(installDirectory);
            Directory.CreateDirectory(backupDirectory);
            BackupFiles(installDirectory, backupDirectory, managedFiles);
            mutationStarted = true;
            DeleteFiles(installDirectory, managedFiles);
            MoveFiles(stagedDirectory, installDirectory, newFiles.Append(ManifestFileName));
            await VerifyInstalledPayloadAsync(installDirectory, cancellationToken);

            Directory.Delete(backupDirectory, recursive: true);
            Directory.Delete(stagedDirectory, recursive: true);
            stateStore.Clear();
            logger.LogInformation(
                "Installed Kusto CLI update {Version}",
                state.AvailableVersion);
        }
        catch
        {
            if (mutationStarted)
            {
                RollbackFiles(installDirectory, backupDirectory, managedFiles);
            }
            else if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }

            state.Status = UpdateStatus.Failed;
            state.ErrorMessage = "The update failed and the previous payload was restored.";
            stateStore.Save(state);
            throw;
        }
    }

    public static Process ScheduleWindowsInstall(
        UpdateState state,
        UpdateStateStore stateStore)
    {
        if (string.IsNullOrWhiteSpace(state.StagedDirectory)
            || string.IsNullOrWhiteSpace(state.InstallDirectory)
            || string.IsNullOrWhiteSpace(state.BackupDirectory))
        {
            throw new UserFacingException("The staged update does not contain install paths.");
        }

        var helperPath = Path.Combine(
            Path.GetTempPath(),
            $"kusto-update-{Guid.NewGuid():N}.ps1");
        var resultPath = Path.Combine(
            AppPaths.GetAppHomeDirectory(),
            "update-result.json");
        Directory.CreateDirectory(AppPaths.GetAppHomeDirectory());
        File.WriteAllText(helperPath, WindowsUpdateScript);

        state.Status = UpdateStatus.Installing;
        state.LastAttemptTime = DateTimeOffset.UtcNow;
        state.ErrorMessage = null;
        stateStore.Save(state);

        var startInfo = new ProcessStartInfo
        {
            FileName = GetWindowsPowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(helperPath);
        startInfo.ArgumentList.Add("-ParentPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-InstallDirectory");
        startInfo.ArgumentList.Add(state.InstallDirectory);
        startInfo.ArgumentList.Add("-StagedDirectory");
        startInfo.ArgumentList.Add(state.StagedDirectory);
        startInfo.ArgumentList.Add("-BackupDirectory");
        startInfo.ArgumentList.Add(state.BackupDirectory);
        startInfo.ArgumentList.Add("-StatePath");
        startInfo.ArgumentList.Add(stateStore.StatePath);
        startInfo.ArgumentList.Add("-LockPath");
        startInfo.ArgumentList.Add(stateStore.LockPath);
        startInfo.ArgumentList.Add("-ResultPath");
        startInfo.ArgumentList.Add(resultPath);

        return Process.Start(startInfo)
            ?? throw new UserFacingException(
                "Could not start the detached Windows update installer.");
    }

    public static async Task VerifyInstalledPayloadAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var executablePath = Path.Combine(
            installDirectory,
            AppIdentity.GetExecutableFileName());
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"kusto-update-smoke-{Guid.NewGuid():N}.png");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("_diag");
            startInfo.ArgumentList.Add("chart-self-test");
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo)
                ?? throw new UserFacingException(
                    "Could not start the updated Kusto CLI for validation.");
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new UserFacingException(
                    "The updated Kusto CLI did not pass its startup check in time.");
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var stderr = await stderrTask;
                throw new UserFacingException(
                    $"The updated Kusto CLI failed its packaged chart check: {stderr.Trim()}");
            }
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static IEnumerable<string> ReadManagedFiles(string installDirectory)
    {
        var manifestPath = Path.Combine(installDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            KustoJsonSerializerContext.Default.PayloadManifest)
            ?? throw new UserFacingException(
                $"The installed {ManifestFileName} is invalid.");
        return manifest.Files
            .Select(entry => NormalizeRelativePath(installDirectory, entry))
            .ToArray();
    }

    private static void BackupFiles(
        string sourceDirectory,
        string backupDirectory,
        IEnumerable<string> files)
    {
        foreach (var relativePath in files)
        {
            var sourcePath = Path.Combine(sourceDirectory, relativePath);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var backupPath = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(sourcePath, backupPath, overwrite: true);
        }
    }

    private static void DeleteFiles(
        string directory,
        IEnumerable<string> files)
    {
        foreach (var relativePath in files)
        {
            var path = Path.Combine(directory, relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void MoveFiles(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<string> files)
    {
        foreach (var relativePath in files)
        {
            var sourcePath = Path.Combine(sourceDirectory, relativePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void RollbackFiles(
        string installDirectory,
        string backupDirectory,
        IEnumerable<string> managedFiles)
    {
        foreach (var relativePath in managedFiles)
        {
            var installedPath = Path.Combine(installDirectory, relativePath);
            if (File.Exists(installedPath))
            {
                File.Delete(installedPath);
            }
        }

        if (Directory.Exists(backupDirectory))
        {
            foreach (var backupPath in Directory.EnumerateFiles(
                         backupDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(backupDirectory, backupPath);
                var destinationPath = Path.Combine(installDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Move(backupPath, destinationPath, overwrite: true);
            }
        }
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new UserFacingException(
                $"{ManifestFileName} contains invalid path '{path}'.");
        }

        var normalized = path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var rootPath = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"{ManifestFileName} contains path outside the payload: '{path}'.");
        }

        return Path.GetRelativePath(root, fullPath);
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

    private const string WindowsUpdateScript = """
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][int]$ParentPid,
            [Parameter(Mandatory)][string]$InstallDirectory,
            [Parameter(Mandatory)][string]$StagedDirectory,
            [Parameter(Mandatory)][string]$BackupDirectory,
            [Parameter(Mandatory)][string]$StatePath,
            [Parameter(Mandatory)][string]$LockPath,
            [Parameter(Mandatory)][string]$ResultPath
        )

        $ErrorActionPreference = 'Stop'
        $manifestName = 'payload-manifest.json'

        function Get-SafeFiles([string]$Root)
        {
            $manifestPath = Join-Path $Root $manifestName
            $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
            $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
            $files = @()
            foreach ($entry in @($manifest.files))
            {
                if ([string]::IsNullOrWhiteSpace($entry) -or [System.IO.Path]::IsPathRooted($entry))
                {
                    throw "Invalid payload path '$entry'."
                }
                $fullPath = [System.IO.Path]::GetFullPath((Join-Path $Root $entry))
                if (-not $fullPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase))
                {
                    throw "Payload path '$entry' escapes its root."
                }
                $files += $entry.Replace('/', '\')
            }
            return @($files)
        }

        function Move-PayloadFiles([string]$Source, [string]$Destination, [string[]]$Files)
        {
            foreach ($relativePath in $Files)
            {
                $sourcePath = Join-Path $Source $relativePath
                if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
                {
                    continue
                }
                $destinationPath = Join-Path $Destination $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
                Move-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
            }
        }

        function Copy-PayloadFiles([string]$Source, [string]$Destination, [string[]]$Files)
        {
            foreach ($relativePath in $Files)
            {
                $sourcePath = Join-Path $Source $relativePath
                if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
                {
                    continue
                }
                $destinationPath = Join-Path $Destination $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
                Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
            }
        }

        function Remove-PayloadFiles([string]$Root, [string[]]$Files)
        {
            foreach ($relativePath in $Files)
            {
                Remove-Item -LiteralPath (Join-Path $Root $relativePath) -Force -ErrorAction SilentlyContinue
            }
        }

        $newFiles = @(Get-SafeFiles $StagedDirectory)
        $oldFiles = @()
        if (Test-Path -LiteralPath (Join-Path $InstallDirectory $manifestName))
        {
            $oldFiles = @(Get-SafeFiles $InstallDirectory)
        }
        $managedFiles = @($newFiles + $oldFiles + $manifestName | Sort-Object -Unique)
        $mutationStarted = $false

        try
        {
            try { Wait-Process -Id $ParentPid -ErrorAction Stop } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }

            $lockDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
            $lockStream = $null
            while ($null -eq $lockStream -and [DateTimeOffset]::UtcNow -lt $lockDeadline)
            {
                try
                {
                    $lockStream = [System.IO.FileStream]::new(
                        $LockPath,
                        [System.IO.FileMode]::OpenOrCreate,
                        [System.IO.FileAccess]::ReadWrite,
                        [System.IO.FileShare]::None)
                }
                catch [System.IO.IOException]
                {
                    Start-Sleep -Milliseconds 250
                }
            }
            if ($null -eq $lockStream)
            {
                throw "Timed out waiting for the Kusto CLI update lock."
            }

            New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
            New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
            Copy-PayloadFiles $InstallDirectory $BackupDirectory $managedFiles
            $mutationStarted = $true
            Remove-PayloadFiles $InstallDirectory $managedFiles
            Move-PayloadFiles $StagedDirectory $InstallDirectory @($newFiles + $manifestName)

            $smokePath = Join-Path ([System.IO.Path]::GetTempPath()) ("kusto-update-smoke-" + [guid]::NewGuid().ToString('N') + '.png')
            try
            {
                $smokeProcess = Start-Process `
                    -FilePath (Join-Path $InstallDirectory 'kusto.exe') `
                    -ArgumentList @('_diag', 'chart-self-test', '--output', "`"$smokePath`"") `
                    -WindowStyle Hidden `
                    -PassThru
                if (-not $smokeProcess.WaitForExit(60000))
                {
                    $smokeProcess.Kill()
                    if (-not $smokeProcess.WaitForExit(5000))
                    {
                        throw "The timed-out Kusto CLI packaged chart check could not be terminated."
                    }
                    throw "The updated Kusto CLI packaged chart check timed out."
                }
                if ($smokeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $smokePath))
                {
                    throw "The updated Kusto CLI failed its packaged chart check."
                }
            }
            finally
            {
                Remove-Item -LiteralPath $smokePath -Force -ErrorAction SilentlyContinue
            }

            Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $StagedDirectory -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
            @{ success = $true } | ConvertTo-Json -Compress | Set-Content -Path $ResultPath
        }
        catch
        {
            if ($mutationStarted)
            {
                Remove-PayloadFiles $InstallDirectory $managedFiles
                Copy-PayloadFiles $BackupDirectory $InstallDirectory $managedFiles
            }
            else
            {
                Remove-Item -LiteralPath $BackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }

            $message = $_.Exception.Message
            if (Test-Path -LiteralPath $StatePath)
            {
                $state = Get-Content -Path $StatePath -Raw | ConvertFrom-Json
                $state.status = 4
                $state.errorMessage = $message
                $state | ConvertTo-Json -Depth 10 | Set-Content -Path $StatePath
            }
            @{ success = $false; error = $message } | ConvertTo-Json -Compress | Set-Content -Path $ResultPath
            exit 1
        }
        finally
        {
            if ($null -ne $lockStream)
            {
                $lockStream.Dispose()
                Remove-Item -LiteralPath $LockPath -Force -ErrorAction SilentlyContinue
            }
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;
}
