using System.Runtime.InteropServices;
using System.Windows.Interop;
using RXShade.Interop;

namespace RXShade.Views;

/// <summary>
/// Hosts a raw Win32 child window inside WPF so Direct3D can present into it.
///
/// WPF's own D3DImage would force the frame through a shared surface and an
/// extra copy on the UI thread every frame; a child HWND with its own swap
/// chain lets the render thread present directly, and WM_SIZE arrives in
/// physical pixels so DPI scaling is handled for free.
/// </summary>
public sealed class D3DHost : HwndHost
{
    private Win32Window? _surface;

    public IntPtr SurfaceHandle => _surface?.Handle ?? IntPtr.Zero;
    public int SurfaceWidth => _surface?.Width ?? 0;
    public int SurfaceHeight => _surface?.Height ?? 0;

    /// <summary>Raised once the child HWND exists and can back a swap chain.</summary>
    public event Action<IntPtr, int, int>? SurfaceReady;

    /// <summary>Raised on resize, in physical pixels.</summary>
    public event Action<int, int>? SurfaceResized;

    /// <summary>Raised just before the child HWND is destroyed.</summary>
    public event Action? SurfaceDestroying;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // WPF has not laid us out yet, so start at 1x1; the first WM_SIZE
        // brings it to the real size before anything is presented.
        _surface = Win32Window.CreateChild(hwndParent.Handle, 1, 1);
        _surface.Resized += OnSurfaceResized;

        SurfaceReady?.Invoke(_surface.Handle, _surface.Width, _surface.Height);
        return new HandleRef(this, _surface.Handle);
    }

    private void OnSurfaceResized(int width, int height) => SurfaceResized?.Invoke(width, height);

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        SurfaceDestroying?.Invoke();

        if (_surface is not null)
        {
            _surface.Resized -= OnSurfaceResized;
            _surface.Dispose();
            _surface = null;
        }
    }
}
