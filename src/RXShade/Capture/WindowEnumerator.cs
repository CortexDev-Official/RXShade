using System.Diagnostics;
using System.Runtime.InteropServices;
using RXShade.Interop;

namespace RXShade.Capture;

/// <summary>
/// Builds the list of Roblox game windows.
///
/// RXShade only works with Roblox, so this deliberately lists nothing else.
/// That is not just branding: the whole tool is tuned for the Roblox client's
/// window shape, and offering an arbitrary-window picker implies a generality
/// the filters do not actually have.
///
/// This reads only what the window manager exposes about top-level windows:
/// handle, title, class name, visibility and style bits, plus the owning
/// process *name* (via the normal Process API - no handle to the process's
/// memory is ever opened). This is the same information Alt-Tab shows.
/// </summary>
public static class WindowEnumerator
{
    public static List<CaptureTarget> EnumerateTargets()
    {
        var windows = new List<CaptureTarget>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsCapturableWindow(hwnd)) return true;

            string className = NativeMethods.GetWindowClass(hwnd);
            string processName = TryGetProcessName(hwnd);

            if (!IsRoblox(className, processName)) return true;

            string title = NativeMethods.GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) title = "Roblox";

            windows.Add(new CaptureTarget
            {
                Handle = hwnd,
                Title = title,
                ClassName = className,
                ProcessName = processName
            });
            return true;
        }, IntPtr.Zero);

        windows.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return windows;
    }

    /// <summary>
    /// True when a top-level window belongs to a Roblox client.
    ///
    /// The process name (RobloxPlayerBeta / RobloxStudioBeta) is the reliable
    /// signal. The window class is a fallback for the rare case where the
    /// process could not be queried; the title is deliberately NOT used, since
    /// a browser tab mentioning Roblox would otherwise match.
    /// </summary>
    private static bool IsRoblox(string className, string processName)
    {
        if (!string.IsNullOrEmpty(processName))
            return processName.Contains("roblox", StringComparison.OrdinalIgnoreCase);

        return className.Equals("WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The standard "would this show in Alt-Tab" test. Windows.Graphics.Capture
    /// will reject anything else anyway, so filtering here keeps results honest.
    /// </summary>
    private static bool IsCapturableWindow(IntPtr hwnd)
    {
        if (hwnd == NativeMethods.GetShellWindow()) return false;
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return false;

        uint style = (uint)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_VISIBLE) == 0) return false;

        uint exStyle = (uint)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        // UWP/suspended windows linger as cloaked ghosts; capturing them yields black.
        if (NativeMethods.IsCloaked(hwnd)) return false;

        // A zero-area window has nothing to capture.
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        if (rect.Width <= 1 || rect.Height <= 1) return false;

        return true;
    }

    private static string TryGetProcessName(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return string.Empty;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            // Process exited between enumeration and lookup, or is protected.
            return string.Empty;
        }
    }

    /// <summary>Re-resolves the first Roblox window, e.g. after the game restarts.</summary>
    public static CaptureTarget? FindRobloxWindow()
        => EnumerateTargets().FirstOrDefault();
}
