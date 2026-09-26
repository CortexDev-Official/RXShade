using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RXShade.Graphics;

/// <summary>
/// Owns the single D3D11 device used for both capture and post-processing.
///
/// Capture frames are delivered *on this device*, so the whole pipeline stays
/// in VRAM: WGC writes the frame, our shaders read it, the swap chain presents
/// it. There is no CPU readback anywhere in the hot path.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public FeatureLevel FeatureLevel { get; }
    public string AdapterDescription { get; }

    public ID3D11SamplerState LinearClamp { get; }
    public ID3D11SamplerState PointClamp { get; }
    public ID3D11BlendState AlphaBlend { get; }
    public ID3D11BlendState Opaque { get; }

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context)
    {
        Device = device;
        Context = context;
        FeatureLevel = device.FeatureLevel;

        // WGC delivers frames on a different thread than the one that created
        // the device, and WinRT touches the device internally. Multithread
        // protection lets the driver serialise that safely.
        using (var multithread = device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        AdapterDescription = adapter.Description.Description.Trim();
        Factory = adapter.GetParent<IDXGIFactory2>();

        LinearClamp = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        PointClamp = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        Opaque = device.CreateBlendState(BlendDescription.Opaque);
        AlphaBlend = device.CreateBlendState(BlendDescription.NonPremultiplied);
    }

    /// <summary>
    /// Creates the device on the DEFAULT adapter, deliberately.
    ///
    /// On a hybrid machine it is tempting to force the discrete GPU, but that
    /// would be wrong here: capture frames come from the Desktop Window
    /// Manager, which runs on whichever adapter drives the display. Picking a
    /// different adapter would force every frame through a cross-adapter copy
    /// over PCIe. The default adapter is the display adapter, so the capture
    /// surface, the shader passes and the swap chain all stay on one GPU -
    /// even when the game itself is rendering on the other one.
    /// </summary>
    public static GraphicsDevice Create()
    {
        // BgraSupport is mandatory for WinRT / Direct3D interop surfaces.
        var flags = DeviceCreationFlags.BgraSupport;

        FeatureLevel[] levels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        Result result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, flags, levels,
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);

        if (result.Failure || device is null || context is null)
        {
            // WARP keeps the app usable on machines with a broken/absent GPU
            // driver; it will not hit 60 FPS but it will not crash either.
            result = D3D11.D3D11CreateDevice(
                null, DriverType.Warp, flags, levels,
                out device, out _, out context);
            result.CheckError();
        }

        return new GraphicsDevice(device!, context!);
    }

    /// <summary>One installed GPU, and whether a monitor is actually plugged into it.</summary>
    public readonly record struct AdapterInfo(
        int Index,
        string Description,
        ulong DedicatedVideoMemory,
        int OutputCount);

    /// <summary>
    /// Lists every DXGI adapter with its attached-monitor count.
    ///
    /// This is the authoritative answer to "which GPU is RXShade using and
    /// why". The adapter with outputs is the one driving the display, which is
    /// where DWM composites and therefore where capture frames are produced.
    /// An adapter with 0 outputs has no monitor cable in it.
    /// </summary>
    public static List<AdapterInfo> EnumerateAdapters()
    {
        var results = new List<AdapterInfo>();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; ; index++)
        {
            if (factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Failure || adapter is null)
                break;

            using (adapter)
            {
                int outputCount = 0;
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                        break;
                    output.Dispose();
                    outputCount++;
                }

                var description = adapter.Description1;

                // DedicatedVideoMemory is pointer-sized; the implicit
                // conversion to a 32-bit type overflows on any card with more
                // than 4 GB, so go through ulong explicitly.
                ulong videoMemory = (ulong)(nuint)description.DedicatedVideoMemory;

                results.Add(new AdapterInfo(
                    (int)index,
                    description.Description.Trim(),
                    videoMemory,
                    outputCount));
            }
        }

        return results;
    }

    public void Dispose()
    {
        Opaque.Dispose();
        AlphaBlend.Dispose();
        PointClamp.Dispose();
        LinearClamp.Dispose();
        Factory.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
