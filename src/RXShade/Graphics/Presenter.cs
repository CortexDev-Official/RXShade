using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RXShade.Graphics;

/// <summary>How a presenter's swap chain reaches the screen.</summary>
public enum PresentPath
{
    /// <summary>Flip-model swap chain on a normal HWND. Fastest, used for preview/thumbnail.</summary>
    FlipModel,

    /// <summary>
    /// Flip-model swap chain composited by DirectComposition. Used for the
    /// click-through overlay: DWM treats it as a hardware layer.
    /// </summary>
    Composition,

    /// <summary>
    /// Legacy bitblt on a WS_EX_LAYERED window. Last-resort fallback only -
    /// DWM has to read and alpha-blend a redirection surface every frame, which
    /// throttles both us and the game underneath.
    /// </summary>
    LayeredBitblt
}

/// <summary>
/// A swap chain bound to one HWND. RXShade renders the filter chain once and
/// then blits the result into every active presenter (thumbnail, preview
/// window, overlay), so extra output surfaces cost one fullscreen blit each
/// rather than a whole extra pipeline run.
/// </summary>
public sealed class Presenter : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _backBufferRtv;
    private bool _disposed;

    // DirectComposition objects, only used on the Composition path. They must
    // stay alive for as long as the swap chain is being presented.
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;

    public IntPtr Hwnd { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Which presentation path this presenter ended up on.</summary>
    public PresentPath Path { get; }

    /// <summary>True only on the slow legacy layered/bitblt fallback.</summary>
    public bool UsesLegacySwapEffect => Path == PresentPath.LayeredBitblt;

    /// <summary>
    /// Defaults to 0 (present immediately) on purpose.
    ///
    /// Blocking on vblank here does not prevent tearing - the SOURCE is already
    /// vsynced by the game, and we only ever present when a new captured frame
    /// arrives, which is itself paced by the compositor. All waiting achieves
    /// is stalling the capture callback thread, and any present that overruns
    /// its vblank then costs a full extra refresh, pinning the output at half
    /// the display's rate.
    /// </summary>
    public int SyncInterval { get; set; }

    /// <param name="useComposition">
    /// True for the click-through overlay. Presents through DirectComposition,
    /// which DWM handles as a hardware layer instead of reading back a
    /// redirection surface every frame.
    /// </param>
    public Presenter(GraphicsDevice gfx, IntPtr hwnd, int width, int height, bool useComposition = false)
    {
        _gfx = gfx;
        Hwnd = hwnd;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        if (useComposition && TryCreateCompositionSwapChain(out var composed))
        {
            _swapChain = composed!;
            Path = PresentPath.Composition;
        }
        else if (!useComposition && TryCreateSwapChain(SwapEffect.FlipDiscard, 2, out var flip))
        {
            _swapChain = flip!;
            Path = PresentPath.FlipModel;
        }
        else
        {
            // Last resort. DXGI refuses flip-model swap chains on WS_EX_LAYERED
            // windows, and this bitblt path makes DWM alpha-composite an
            // intermediate bitmap every frame.
            //
            // TWO buffers, not one: with a single buffer there is nothing to
            // render into while the previous present is still being blitted, so
            // any present that overruns its vblank costs a whole extra refresh,
            // locking output to exactly half the display's rate.
            if (!TryCreateSwapChain(SwapEffect.Discard, 2, out var blt))
                throw new InvalidOperationException("Unable to create a swap chain for the target window.");
            _swapChain = blt!;
            Path = PresentPath.LayeredBitblt;
        }

        // Stop DXGI from hijacking Alt+Enter / PrintScreen on our windows.
        _gfx.Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        CreateBackBufferView();
    }

    /// <summary>
    /// Builds a composition swap chain and hangs it off a DirectComposition
    /// visual bound to our HWND.
    ///
    /// This is the modern overlay path: no redirection bitmap, flip-model swap
    /// chain, and DWM composites the result as a layer. The layered/bitblt
    /// alternative forces a full read-and-blend of an intermediate surface on
    /// every frame, which throttles the game underneath as well as us.
    /// </summary>
    private bool TryCreateCompositionSwapChain(out IDXGISwapChain1? swapChain)
    {
        swapChain = null;

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            // The overlay fully replaces the game's picture, so no per-pixel
            // alpha is needed and DWM can skip blending entirely.
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        try
        {
            swapChain = _gfx.Factory.CreateSwapChainForComposition(_gfx.Device, desc);

            using var dxgiDevice = _gfx.Device.QueryInterface<IDXGIDevice>();
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

            _compositionDevice.CreateTargetForHwnd(Hwnd, true, out IDCompositionTarget target);
            _compositionTarget = target;
            _compositionVisual = _compositionDevice.CreateVisual();
            _compositionVisual.SetContent(swapChain);
            _compositionTarget.SetRoot(_compositionVisual);
            _compositionDevice.Commit();

            return true;
        }
        catch (SharpGenException)
        {
            DisposeComposition();
            swapChain?.Dispose();
            swapChain = null;
            return false;
        }
    }

    private void DisposeComposition()
    {
        _compositionVisual?.Dispose(); _compositionVisual = null;
        _compositionTarget?.Dispose(); _compositionTarget = null;
        _compositionDevice?.Dispose(); _compositionDevice = null;
    }

    private bool TryCreateSwapChain(SwapEffect effect, int bufferCount, out IDXGISwapChain1? swapChain)
    {
        swapChain = null;
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = (uint)bufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = effect,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        try
        {
            swapChain = _gfx.Factory.CreateSwapChainForHwnd(_gfx.Device, Hwnd, desc);
            return swapChain is not null;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    private void CreateBackBufferView()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = _gfx.Device.CreateRenderTargetView(backBuffer);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;

        Width = width;
        Height = height;

        _gfx.Context.UnsetRenderTargets();
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;

        _swapChain.ResizeBuffers(0, (uint)Width, (uint)Height, Format.Unknown, SwapChainFlags.None);
        CreateBackBufferView();
    }

    public ID3D11RenderTargetView? BackBuffer => _backBufferRtv;

    /// <summary>
    /// Viewport that fits <paramref name="srcW"/>x<paramref name="srcH"/> into this
    /// surface without distorting it (letterbox / pillarbox).
    /// </summary>
    public Viewport GetAspectFitViewport(int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0) return new Viewport(0, 0, Width, Height, 0, 1);

        float scale = MathF.Min((float)Width / srcW, (float)Height / srcH);
        float w = srcW * scale;
        float h = srcH * scale;
        return new Viewport((Width - w) * 0.5f, (Height - h) * 0.5f, w, h, 0, 1);
    }

    public void Present()
    {
        // DXGI_STATUS_OCCLUDED is normal when the target window is minimised or
        // fully covered; it is not an error, just skip the wait next time.
        _swapChain.Present((uint)SyncInterval, PresentFlags.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backBufferRtv?.Dispose();
        DisposeComposition();
        _swapChain.Dispose();
    }
}
