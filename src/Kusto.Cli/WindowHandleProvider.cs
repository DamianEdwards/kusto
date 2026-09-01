using System.Runtime.InteropServices;

namespace Kusto.Cli;

/// <summary>
/// Supplies the parent window handle (HWND) the WAM broker anchors interactive
/// dialogs to. Implementations return <see cref="IntPtr.Zero"/> when no usable
/// handle exists; interactive login treats that as a hard failure while the silent
/// query-time path tolerates it (no UI is ever shown there).
/// </summary>
public interface IWindowHandleProvider
{
    IntPtr GetParentWindowHandle();
}

/// <summary>
/// Resolves a parent window handle using the foreground window first, then the
/// console and desktop windows as fallbacks. Uses source-generated <see cref="LibraryImportAttribute"/>
/// P/Invokes so the interop remains Native AOT friendly.
/// </summary>
public sealed partial class SystemWindowHandleProvider : IWindowHandleProvider
{
    public IntPtr GetParentWindowHandle()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return IntPtr.Zero;
        }

        // Pseudoconsole hosts can expose a non-zero but non-interactive console HWND.
        // WAM's documented desktop pattern uses the foreground top-level window.
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero)
        {
            return foregroundWindow;
        }

        var consoleWindow = GetConsoleWindow();
        return consoleWindow != IntPtr.Zero ? consoleWindow : GetDesktopWindow();
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDesktopWindow();
}
