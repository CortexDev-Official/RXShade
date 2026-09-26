using System.Runtime.InteropServices;

namespace RXShade.Interop;

/// <summary>
/// Win32 P/Invoke surface used by RXShade.
///
/// NOTE ON SCOPE: everything here operates on *our own* windows or reads
/// public window metadata (title / bounds / styles) of other top-level
/// windows. RXShade never opens a handle to another process, never reads or
/// writes another process's memory, and never injects code. See
/// <see cref="WindowEnumerator"/> for the enumeration policy.
/// </summary>
internal static class NativeMethods
{
    // ---- Window styles -------------------------------------------------
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;

    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_APPWINDOW = 0x00040000;

    /// <summary>
    /// Required for DirectComposition. The window gets no redirection surface
    /// at all, so DWM composites our swap chain directly as a layer instead of
    /// copying it through an intermediate bitmap every frame.
    /// Mutually exclusive with WS_EX_LAYERED.
    /// </summary>
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    // ---- SetWindowPos --------------------------------------------------
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOREDRAW = 0x0008;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // ---- ShowWindow ----------------------------------------------------
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    // ---- Layered windows -----------------------------------------------
    public const uint LWA_ALPHA = 0x00000002;

    // ---- Misc ----------------------------------------------------------
    public const uint GA_ROOT = 2;
    public const int DWMWA_CLOAKED = 14;
    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_OWNDC = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    /// <summary>Drains the message queue for the calling thread.</summary>
    public static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---- Enumeration / metadata ----------------------------------------

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    // Same native export as DwmGetWindowAttribute, but typed for the
    // attributes that return a RECT rather than an int.
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeRect(IntPtr hWnd, int attribute, out RECT value, int size);

    /// <summary>
    /// The window's VISIBLE frame, excluding the invisible resize border that
    /// GetWindowRect includes. This is the rectangle Windows.Graphics.Capture
    /// actually hands back, so it is the correct origin for cropping a captured
    /// window frame down to its client area.
    /// </summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// Where the client area sits inside the captured window image, in pixels.
    ///
    /// Capturing a window yields its ENTIRE frame including the title bar. The
    /// overlay only covers the client area, so without this offset the captured
    /// title bar gets squeezed into the top of the client region and the user
    /// sees the title bar twice.
    /// </summary>
    public static bool TryGetClientCropInWindow(IntPtr hWnd, out int offsetX, out int offsetY, out int width, out int height)
    {
        offsetX = offsetY = width = height = 0;

        // A minimised window reports a nonsense client rect (Windows parks it
        // off-screen at title-bar size). Cropping to that would shrink the
        // whole capture down to a sliver.
        if (IsIconic(hWnd))
            return false;

        if (DwmGetWindowAttributeRect(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
            return false;
        if (!TryGetClientRectOnScreen(hWnd, out RECT client))
            return false;
        if (client.Width <= 0 || client.Height <= 0)
            return false;

        // The client area is always the bulk of a window. If it is not, the
        // rects disagree (mid-animation, DPI change) and cropping would be wrong.
        if (frame.Width > 0 && frame.Height > 0 &&
            (client.Width * 2 < frame.Width || client.Height * 2 < frame.Height))
            return false;

        offsetX = Math.Max(0, client.Left - frame.Left);
        offsetY = Math.Max(0, client.Top - frame.Top);
        width = client.Width;
        height = client.Height;
        return true;
    }

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Matches the DWM frame to RXShade's light UI and asks for the standard
    /// rounded window corners. Both attributes are Windows 11 era; on older
    /// builds DwmSetWindowAttribute just returns a failure HRESULT and the
    /// window keeps square corners, which is fine.
    /// </summary>
    public static void ApplyWindowChrome(IntPtr hWnd)
    {
        int darkMode = 0;
        DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int cornerPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    // ---- Geometry ------------------------------------------------------

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    // ---- Window lifetime -----------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint exStyle, IntPtr classNameAtom, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    private const int ENUM_CURRENT_SETTINGS = -1;

    /// <summary>
    /// Current refresh rate of the primary display, in Hz.
    ///
    /// This is the hard ceiling on anything RXShade can output: captured frames
    /// are produced by the compositor, which runs at the display's refresh
    /// rate. A game rendering at 360 FPS is only doing so because it bypasses
    /// the compositor entirely.
    /// </summary>
    public static int GetPrimaryRefreshRate()
    {
        var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode)
            ? (int)mode.dmDisplayFrequency
            : 0;
    }

    // ---- Console (for --selftest on a WinExe) ---------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // ---- Helpers -------------------------------------------------------

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var buffer = new char[len + 1];
        int copied = GetWindowText(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    public static string GetWindowClass(IntPtr hWnd)
    {
        var buffer = new char[256];
        int copied = GetClassName(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    public static bool IsCloaked(IntPtr hWnd)
        => DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// Screen-space rectangle of a window's *client* area, in physical pixels.
    /// Used to align the overlay: we want to cover the rendered content, not
    /// the title bar / drop shadow.
    /// </summary>
    public static bool TryGetClientRectOnScreen(IntPtr hWnd, out RECT rect)
    {
        rect = default;
        if (!GetClientRect(hWnd, out var client)) return false;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref origin)) return false;
        rect = new RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Width,
            Bottom = origin.Y + client.Height
        };
        return true;
    }
}
