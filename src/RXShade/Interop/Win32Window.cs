using System.Runtime.InteropServices;
using static RXShade.Interop.NativeMethods;

namespace RXShade.Interop;

/// <summary>
/// A minimal raw Win32 window used as a Direct3D presentation surface.
///
/// WPF is not used for the render surfaces because WPF works in
/// device-independent units and would fight us over DPI scaling: the overlay
/// has to line up with the target window to the physical pixel.
/// </summary>
public sealed class Win32Window : IDisposable
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_CLOSE = 0x0010;
    private const uint WS_CHILD = 0x40000000;
    private static readonly IntPtr HTTRANSPARENT = new(-1);

    private static readonly object ClassLock = new();
    private static ushort _classAtom;
    private static WndProc? _wndProcDelegate;   // must outlive every window
    private static IntPtr _classNamePtr;

    private readonly bool _clickThrough;
    private readonly bool _isLayered;
    private IntPtr _hwnd;
    private bool _disposed;

    public IntPtr Handle => _hwnd;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Raised when the user closes the window (preview mode only).</summary>
    public event Action? Closed;

    /// <summary>Raised on WM_SIZE with the new size in physical pixels.</summary>
    public event Action<int, int>? Resized;

    private static readonly Dictionary<IntPtr, Win32Window> Instances = new();

    private Win32Window(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        Width = width;
        Height = height;
        _clickThrough = false;
        lock (ClassLock) Instances[hwnd] = this;
    }

    /// <summary>
    /// Creates a child render surface inside a WPF window (used by the live
    /// thumbnail and the preview window).
    /// </summary>
    public static Win32Window CreateChild(IntPtr parent, int width, int height)
    {
        EnsureClassRegistered();

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        IntPtr hwnd = CreateWindowEx(
            0,
            new IntPtr(_classAtom),
            null,
            WS_CHILD | WS_VISIBLE,
            0, 0, width, height,
            parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx (child) failed (0x{Marshal.GetLastWin32Error():X8}).");

        return new Win32Window(hwnd, width, height);
    }

    /// <param name="forComposition">
    /// True builds the window for DirectComposition: no redirection surface, so
    /// DWM composites our swap chain directly as a layer. This is dramatically
    /// cheaper than WS_EX_LAYERED, which forces DWM to read and alpha-blend an
    /// intermediate bitmap every single frame and drags the game's own
    /// presentation rate down with it.
    /// </param>
    public Win32Window(
        string title, int x, int y, int width, int height,
        bool clickThrough, bool topMost, bool toolWindow, bool forComposition = false)
    {
        _clickThrough = clickThrough;
        _isLayered = clickThrough && !forComposition;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        EnsureClassRegistered();

        uint exStyle = 0;
        if (clickThrough)
        {
            // WS_EX_TRANSPARENT makes hit-testing fall through to whatever is
            // underneath, so every click and key goes straight to the game.
            // RXShade installs no hooks and reads no input - the input simply
            // never arrives here in the first place.
            exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;

            // LAYERED and NOREDIRECTIONBITMAP are mutually exclusive.
            exStyle |= forComposition ? WS_EX_NOREDIRECTIONBITMAP : WS_EX_LAYERED;
        }
        if (topMost) exStyle |= WS_EX_TOPMOST;
        if (toolWindow) exStyle |= WS_EX_TOOLWINDOW;   // keeps it out of Alt-Tab

        _hwnd = CreateWindowEx(
            exStyle,
            new IntPtr(_classAtom),
            title,
            WS_POPUP,
            x, y, Width, Height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (0x{Marshal.GetLastWin32Error():X8}).");

        lock (ClassLock) Instances[_hwnd] = this;

        if (_isLayered)
        {
            // Fully opaque. The overlay replaces the game's picture rather than
            // tinting it, so there is no per-pixel alpha to composite.
            // Only valid on the WS_EX_LAYERED fallback path; a
            // NOREDIRECTIONBITMAP window has no layered attributes to set.
            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        }
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classAtom != 0) return;

            _wndProcDelegate = StaticWndProc;
            _classNamePtr = Marshal.StringToHGlobalUni("RXShadeRenderSurface");

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_HREDRAW | CS_VREDRAW | CS_OWNDC,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = GetModuleHandle(null),
                hbrBackground = IntPtr.Zero,   // we always paint via D3D
                lpszClassName = _classNamePtr
            };

            _classAtom = RegisterClassEx(ref wc);
            if (_classAtom == 0)
                throw new InvalidOperationException($"RegisterClassEx failed (0x{Marshal.GetLastWin32Error():X8}).");
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32Window? window;
        lock (ClassLock) Instances.TryGetValue(hWnd, out window);

        switch (msg)
        {
            case WM_NCHITTEST when window is { _clickThrough: true }:
                // Belt and braces alongside WS_EX_TRANSPARENT.
                return HTTRANSPARENT;

            case WM_ERASEBKGND:
                return new IntPtr(1);   // suppress the background wipe; D3D owns every pixel

            case WM_SIZE when window is not null:
                // lParam carries the new client size in PHYSICAL pixels, which
                // is exactly what the swap chain needs - no DPI conversion.
                int width = (int)(lParam.ToInt64() & 0xFFFF);
                int height = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (width > 0 && height > 0)
                {
                    window.Width = width;
                    window.Height = height;
                    window.Resized?.Invoke(width, height);
                }
                return IntPtr.Zero;

            case WM_CLOSE:
                window?.Closed?.Invoke();
                return IntPtr.Zero;

            case WM_DESTROY:
                if (window is not null)
                {
                    lock (ClassLock) Instances.Remove(hWnd);
                }
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Show()
    {
        // SW_SHOWNOACTIVATE: never steal focus from the game.
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public void Hide() => ShowWindow(_hwnd, SW_HIDE);

    /// <summary>Moves/resizes without activating. Returns true if the size changed.</summary>
    public bool SetBounds(int x, int y, int width, int height, bool topMost)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool resized = width != Width || height != Height;

        Width = width;
        Height = height;

        SetWindowPos(
            _hwnd,
            topMost ? HWND_TOPMOST : IntPtr.Zero,
            x, y, width, height,
            SWP_NOACTIVATE | (topMost ? 0 : SWP_NOZORDER));

        return resized;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            lock (ClassLock) Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
