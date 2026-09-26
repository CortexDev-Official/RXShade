using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RXShade.Interop;

/// <summary>
/// Bridges the WinRT <c>Windows.Graphics.Capture</c> projection to raw
/// Direct3D11 (Vortice) objects.
///
/// Two COM interfaces are needed that have no WinRT projection:
///  * <c>IGraphicsCaptureItemInterop</c> - creates a capture item from an HWND
///    or HMONITOR (the non-picker path; the picker UI is a WinRT-only API).
///  * <c>IDirect3DDxgiInterfaceAccess</c> - unwraps an <see cref="IDirect3DSurface"/>
///    back into the underlying ID3D11Texture2D so we can bind it as an SRV.
///
/// The activation factory is fetched via <c>RoGetActivationFactory</c> directly
/// rather than through a CsWinRT helper, because the helper's shape has moved
/// around between CsWinRT versions and this is stable ABI.
/// </summary>
internal static class WinRtInterop
{
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_GraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        IntPtr hstring = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(className, className.Length, out hstring);
            var iid = IID_GraphicsCaptureItemInterop;
            RoGetActivationFactory(hstring, ref iid, out factoryPtr);
            return (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                factoryPtr, typeof(IGraphicsCaptureItemInterop));
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    /// <summary>Creates a capture item for a top-level window handle.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GetCaptureItemInterop();
        var iid = IID_GraphicsCaptureItem;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
        }
    }

    /// <summary>
    /// Wraps our Vortice D3D11 device as the WinRT <see cref="IDirect3DDevice"/>
    /// the frame pool requires. Capture frames then land directly on the same
    /// device we render with - no cross-device copy, no CPU round-trip.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr devicePtr);
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);
        }
        finally
        {
            if (devicePtr != IntPtr.Zero) Marshal.Release(devicePtr);
        }
    }

    /// <summary>
    /// Unwraps a capture frame's surface into the underlying ID3D11Texture2D.
    /// The returned texture is a new reference - the caller disposes it.
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = (IDirect3DDxgiInterfaceAccess)WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
        var iid = IID_ID3D11Texture2D;
        IntPtr texturePtr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(texturePtr);
    }
}
