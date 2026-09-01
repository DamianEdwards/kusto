using System.Runtime.InteropServices;

namespace Kusto.Cli;

/// <summary>
/// Abstracts the host operating system so WAM's Windows-only requirement can be
/// exercised through an injectable seam in tests without a real platform check.
/// </summary>
public interface IPlatform
{
    bool IsWindows { get; }
}

public sealed class SystemPlatform : IPlatform
{
    public static SystemPlatform Instance { get; } = new();

    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
