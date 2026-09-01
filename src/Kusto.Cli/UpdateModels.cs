namespace Kusto.Cli;

internal enum UpdateStatus
{
    None,
    Downloading,
    Staged,
    Installing,
    Failed
}

internal sealed class UpdateState
{
    public UpdateStatus Status { get; set; }
    public string? AvailableVersion { get; set; }
    public string? ReleaseTag { get; set; }
    public string? StagedDirectory { get; set; }
    public string? InstallDirectory { get; set; }
    public string? BackupDirectory { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? LastAttemptTime { get; set; }
}

internal sealed class PayloadManifest
{
    public List<string> Files { get; set; } = [];
}

internal sealed record GitHubRelease(
    string TagName,
    string Version,
    bool IsPrerelease,
    bool IsDevBuild);

internal sealed record UpdateCheckResult(
    string CurrentVersion,
    string AvailableVersion,
    string ReleaseTag,
    bool IsDevBuild);

internal sealed record UpdateInstallResult(bool Completed, bool Scheduled);
