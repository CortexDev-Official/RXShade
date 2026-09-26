using System.Numerics;
using System.Runtime.InteropServices;
using RXShade.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RXShade.Graphics;

/// <summary>Result of a pipeline run: what to blit, and how to sample it.</summary>
public readonly record struct PipelineOutput(
    ID3D11ShaderResourceView Srv,
    Vector2 UvScale,
    Vector2 UvOffset,
    int Width,
    int Height,
    int PassCount);

/// <summary>
/// The GPU post-processing chain.
///
/// PASS ORDER (and why):
///   0. Ingest    - crop the capture texture to the live region, convert to
///                  the float16 working format. Skipped entirely when no
///                  filter is on, which is the passthrough fast path.
///   1. FXAA      - before sharpening. Sharpening first would harden the very
///                  aliasing FXAA is trying to find.
///   2. Sharpen   - CAS, after AA so it sharpens the cleaned-up edges.
///   3. Bloom      - reads the assembled image, adds light on top.
///   4. Reflection - a compositing effect; needs the scene already assembled.
///   5. Focus      - lens defocus, applied to the completed scene.
///   5b HDR        - tone mapping. Belongs after everything that ADDS light
///                   (bloom, reflections) and before the creative grade, which
///                   is exactly where a real imaging pipeline puts it.
///   6. Colour     - the "look" is applied to the finished composite.
///   7. Chromatic  - a lens artefact, so it goes on the final image.
///   8. Grain      - film/sensor grain is the very last thing in a real
///                   imaging chain. Anything applied after it would blur or
///                   sharpen the grain itself and stop it reading as grain.
///
/// Everything is a fullscreen triangle into a ping-pong pair of float16
/// targets. Bloom runs at quarter resolution. There is no CPU readback at any
/// point - the only data leaving the GPU is the timestamp query.
/// </summary>
public sealed class FilterPipeline : IDisposable
{
    // Working format. float16 rather than 8-bit unorm because bloom sums
    // multiple bright samples and contrast can push values past 1.0; banding
    // and clipping in the intermediate targets would be visible otherwise.
    private const Format WorkingFormat = Format.R16G16B16A16_Float;
    private const int BloomDivisor = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Vector2 SourceSize;
        public Vector2 InvSourceSize;
        public Vector2 UvScale;
        public Vector2 UvOffset;
        public float Time;
        public float Aspect;
        public Vector2 Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EffectConstants
    {
        public Vector4 Params0;
        public Vector4 Params1;
        public Vector4 Params2;
        public Vector4 Params3;
    }

    private readonly GraphicsDevice _gfx;
    private readonly ShaderLibrary _shaders;
    private readonly ID3D11Buffer _frameConstantBuffer;
    private readonly ID3D11Buffer _effectConstantBuffer;
    private readonly GpuTimer _timer;

    private RenderTexture? _rtA;
    private RenderTexture? _rtB;
    private RenderTexture? _bloomA;
    private RenderTexture? _bloomB;
    private int _width;
    private int _height;

    private int _passCount;

    public double LastGpuMilliseconds => _timer.LastMilliseconds;

    public FilterPipeline(GraphicsDevice gfx, ShaderLibrary shaders)
    {
        _gfx = gfx;
        _shaders = shaders;
        _timer = new GpuTimer(gfx);

        _frameConstantBuffer = CreateConstantBuffer(Marshal.SizeOf<FrameConstants>());
        _effectConstantBuffer = CreateConstantBuffer(Marshal.SizeOf<EffectConstants>());
    }

    private ID3D11Buffer CreateConstantBuffer(int size)
    {
        // Constant buffers must be a multiple of 16 bytes.
        int aligned = (size + 15) & ~15;
        return _gfx.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)aligned,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    /// <summary>
    /// Runs the enabled filters. Returns the SRV that should be presented.
    /// When nothing is enabled this returns the capture SRV untouched, so the
    /// passthrough path costs exactly one blit per output window.
    /// </summary>
    /// <param name="cropX">Left edge of the wanted region inside the capture texture, in pixels.</param>
    /// <param name="cropY">Top edge of the wanted region inside the capture texture, in pixels.</param>
    /// <param name="contentWidth">Width of the wanted region (the window's CLIENT area).</param>
    /// <param name="contentHeight">Height of the wanted region.</param>
    public PipelineOutput Process(
        ID3D11ShaderResourceView captureSrv,
        int textureWidth, int textureHeight,
        int cropX, int cropY,
        int contentWidth, int contentHeight,
        in FilterSnapshot settings,
        float time)
    {
        float invTexW = textureWidth > 0 ? 1f / textureWidth : 0f;
        float invTexH = textureHeight > 0 ? 1f / textureHeight : 0f;

        var cropScale = new Vector2(contentWidth * invTexW, contentHeight * invTexH);
        var cropOffset = new Vector2(cropX * invTexW, cropY * invTexH);

        if (!settings.AnyEnabled)
            return new PipelineOutput(captureSrv, cropScale, cropOffset, contentWidth, contentHeight, 0);

        EnsureTargets(contentWidth, contentHeight);
        _passCount = 0;

        _timer.Begin();
        BindCommonState();

        // ---- Pass 0: ingest + crop ------------------------------------
        SetFrameConstants(contentWidth, contentHeight, cropScale, cropOffset, time);
        DrawPass(ShaderNames.Blit, _rtA!, captureSrv);

        // Everything downstream is exactly content-sized, so no more cropping.
        SetFrameConstants(contentWidth, contentHeight, Vector2.One, Vector2.Zero, time);

        RenderTexture current = _rtA!;
        RenderTexture spare = _rtB!;

        if (settings.FxaaEnabled)
        {
            SetEffectConstants(new Vector4(settings.FxaaIntensity, 0, 0, 0));
            DrawPass(ShaderNames.Fxaa, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.SharpenEnabled)
        {
            SetEffectConstants(new Vector4(settings.SharpenIntensity, 0, 0, 0));
            DrawPass(ShaderNames.Sharpen, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.BloomEnabled)
        {
            current = RunBloom(current, spare, contentWidth, contentHeight, settings, time, out spare);
        }

        if (settings.ReflectionEnabled)
        {
            // Horizon spans 25%..85% down the frame, which covers everything
            // from a high overhead camera to a near-ground third-person one.
            float horizon = 0.25f + settings.ReflectionHorizon * 0.60f;

            SetEffectConstants(
                new Vector4(
                    horizon,
                    settings.ReflectionIntensity * 0.95f,
                    // Roughness is the inverse of the Sharpness control.
                    26.0f * (1.0f - settings.ReflectionSharpness * 0.85f),
                    settings.ReflectionWave * 0.007f),      // wave amplitude in UV
                new Vector4(
                    52.0f,                                  // wave frequency
                    1.5f,                                   // wave speed
                    1.15f,                                  // fresnel falloff exponent
                    settings.ReflectionSharpness),
                new Vector4(
                    // Screen-wide sheen: streaks light sources across walls,
                    // ceiling and floor. This is the component that makes a
                    // whole room look wet rather than just the ground.
                    settings.ReflectionSheen * 1.7f,
                    // Streak length. Rougher surfaces smear further, so this
                    // runs opposite to sharpness.
                    0.14f + (1.0f - settings.ReflectionSharpness) * 0.17f,
                    // What counts as a light source. Stays well above mid-grey
                    // so ordinary lit surfaces never register.
                    0.60f,
                    // Surfaces mostly reflect what is above them.
                    0.62f),
                new Vector4(
                    // Specular shine on the objects themselves.
                    settings.ReflectionShine,
                    0, 0, 0));
            DrawPass(ShaderNames.FakeReflection, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.FocusEnabled)
        {
            SetEffectConstants(new Vector4(
                settings.FocusStrength,
                // Map 0..1 onto 0.12..0.75 so even the tightest setting keeps
                // a usable sharp area around the player.
                0.12f + settings.FocusRadius * 0.63f,
                0, 0));
            DrawPass(ShaderNames.Focus, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.HdrEnabled)
        {
            SetEffectConstants(
                new Vector4(
                    0.55f + settings.HdrExposure * 0.9f,   // 0.5 slider == 1.0 neutral
                    settings.HdrPunch,
                    settings.HdrHighlights,
                    settings.HdrShadows),
                new Vector4(
                    settings.HdrVibrance,
                    1.6f,   // local-contrast radius in texels
                    0, 0));
            DrawPass(ShaderNames.Hdr, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.ColorGradeEnabled)
        {
            SetEffectConstants(
                new Vector4(
                    settings.Saturation * 2.0f,          // 0.5 slider == 1.0 neutral
                    0.5f + settings.Contrast,            // 0.5 slider == 1.0 neutral
                    settings.Temperature * 2.0f - 1.0f,  // 0.5 slider == 0 neutral
                    0.0f),                               // tint not exposed in the UI
                new Vector4(
                    settings.Vignette,
                    1.0f,    // vignette radius
                    0.6f,    // vignette softness
                    1.0f));  // exposure
            DrawPass(ShaderNames.ColorGrade, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.ChromaticEnabled)
        {
            SetEffectConstants(new Vector4(settings.ChromaticIntensity, 0, 0, 0));
            DrawPass(ShaderNames.ChromaticAberration, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        if (settings.GrainEnabled)
        {
            SetEffectConstants(new Vector4(settings.GrainIntensity, settings.GrainCoarseness, 0, 0));
            DrawPass(ShaderNames.FilmGrain, spare, current.Srv);
            (current, spare) = (spare, current);
        }

        _timer.End();

        return new PipelineOutput(current.Srv, Vector2.One, Vector2.Zero, contentWidth, contentHeight, _passCount);
    }

    private RenderTexture RunBloom(
        RenderTexture current, RenderTexture spare,
        int width, int height,
        in FilterSnapshot settings, float time,
        out RenderTexture newSpare)
    {
        int bw = _bloomA!.Width;
        int bh = _bloomA.Height;

        // Bright-pass, downsampling 4x in the same pass.
        SetFrameConstants(bw, bh, Vector2.One, Vector2.Zero, time);
        SetEffectConstants(new Vector4(settings.BloomThreshold, 0.5f, 0, 0));
        DrawPass(ShaderNames.BloomPrefilter, _bloomA, current.Srv);

        // Three separable gaussian iterations at widening radius. Stacking
        // several narrow blurs approximates a very wide kernel far more
        // smoothly than one big one, and gives bloom the long, soft tail real
        // lens glare has instead of a hard-edged disc. All at quarter res, so
        // each extra iteration costs ~1/16 of a full-res pass.
        RenderTexture src = _bloomA;
        RenderTexture dst = _bloomB!;
        foreach (float radius in stackalloc float[] { 1.0f, 2.4f, 5.2f })
        {
            SetEffectConstants(new Vector4(radius / bw, 0, 0, 0));
            DrawPass(ShaderNames.BloomBlur, dst, src.Srv);
            (src, dst) = (dst, src);

            SetEffectConstants(new Vector4(0, radius / bh, 0, 0));
            DrawPass(ShaderNames.BloomBlur, dst, src.Srv);
            (src, dst) = (dst, src);
        }

        // Composite back at full resolution.
        SetFrameConstants(width, height, Vector2.One, Vector2.Zero, time);
        SetEffectConstants(new Vector4(settings.BloomIntensity * 1.6f, 0, 0, 0));
        DrawPass(ShaderNames.BloomComposite, spare, current.Srv, src.Srv);

        newSpare = current;
        return spare;
    }

    /// <summary>
    /// Copies a finished frame into a swap chain back buffer, letterboxing if
    /// the destination has a different aspect ratio.
    /// </summary>
    public void BlitToPresenter(Presenter presenter, in PipelineOutput output, bool aspectFit)
    {
        var rtv = presenter.BackBuffer;
        if (rtv is null) return;

        Viewport viewport;
        bool clear = false;

        if (aspectFit)
        {
            viewport = presenter.GetAspectFitViewport(output.Width, output.Height);
            // Only the letterbox bars need clearing, but a full clear is one
            // fast path on every GPU and avoids stale edges on resize.
            clear = true;
        }
        else
        {
            viewport = new Viewport(0, 0, presenter.Width, presenter.Height, 0, 1);
        }

        BlitTo(rtv, viewport, output, clear);
    }

    /// <summary>Blits a pipeline result into an arbitrary render target.</summary>
    public void BlitTo(ID3D11RenderTargetView target, Viewport viewport, in PipelineOutput output, bool clear)
    {
        var ctx = _gfx.Context;

        BindCommonState();
        SetFrameConstants(output.Width, output.Height, output.UvScale, output.UvOffset, 0f);

        ctx.OMSetRenderTargets(target);
        if (clear) ctx.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 1f));

        ctx.RSSetViewport(viewport);
        ctx.PSSetShader(_shaders.GetPixelShader(ShaderNames.Blit));
        ctx.PSSetShaderResource(0, output.Srv);
        ctx.Draw(3, 0);

        ctx.PSSetShaderResource(0, null!);
        ctx.UnsetRenderTargets();
    }

    private void BindCommonState()
    {
        var ctx = _gfx.Context;

        // No vertex or index buffer: the fullscreen triangle is generated from
        // SV_VertexID, so the input assembler has nothing to fetch.
        ctx.IASetInputLayout(null!);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_shaders.FullscreenVertexShader);
        ctx.GSSetShader(null);
        ctx.HSSetShader(null);
        ctx.DSSetShader(null);
        ctx.CSSetShader(null);

        ctx.PSSetSampler(0, _gfx.LinearClamp);
        ctx.PSSetSampler(1, _gfx.PointClamp);
        ctx.PSSetConstantBuffer(0, _frameConstantBuffer);
        ctx.PSSetConstantBuffer(1, _effectConstantBuffer);

        ctx.OMSetBlendState(_gfx.Opaque);
        ctx.OMSetDepthStencilState(null);
        ctx.RSSetState(null);
    }

    private void DrawPass(
        string shaderName,
        RenderTexture target,
        ID3D11ShaderResourceView source,
        ID3D11ShaderResourceView? aux = null)
    {
        var ctx = _gfx.Context;

        ctx.OMSetRenderTargets(target.Rtv);
        ctx.RSSetViewport(new Viewport(0, 0, target.Width, target.Height, 0, 1));
        ctx.PSSetShader(_shaders.GetPixelShader(shaderName));
        ctx.PSSetShaderResource(0, source);
        if (aux is not null) ctx.PSSetShaderResource(1, aux);

        ctx.Draw(3, 0);

        // Unbind before the next pass: a texture cannot be an SRV and an RTV
        // at the same time, and D3D silently drops one of the bindings if it is.
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
        ctx.UnsetRenderTargets();

        _passCount++;
    }

    private void SetFrameConstants(int width, int height, Vector2 uvScale, Vector2 uvOffset, float time)
    {
        var constants = new FrameConstants
        {
            SourceSize = new Vector2(width, height),
            InvSourceSize = new Vector2(1f / Math.Max(width, 1), 1f / Math.Max(height, 1)),
            UvScale = uvScale,
            UvOffset = uvOffset,
            Time = time,
            Aspect = height > 0 ? (float)width / height : 1f
        };
        _gfx.Context.UpdateSubresource(constants, _frameConstantBuffer);
    }

    private void SetEffectConstants(
        Vector4 params0, Vector4 params1 = default,
        Vector4 params2 = default, Vector4 params3 = default)
    {
        var constants = new EffectConstants
        {
            Params0 = params0,
            Params1 = params1,
            Params2 = params2,
            Params3 = params3
        };
        _gfx.Context.UpdateSubresource(constants, _effectConstantBuffer);
    }

    private void EnsureTargets(int width, int height)
    {
        if (_rtA is not null && width == _width && height == _height) return;

        DisposeTargets();

        _width = width;
        _height = height;

        _rtA = new RenderTexture(_gfx.Device, width, height, WorkingFormat);
        _rtB = new RenderTexture(_gfx.Device, width, height, WorkingFormat);

        int bw = Math.Max(1, width / BloomDivisor);
        int bh = Math.Max(1, height / BloomDivisor);
        _bloomA = new RenderTexture(_gfx.Device, bw, bh, WorkingFormat);
        _bloomB = new RenderTexture(_gfx.Device, bw, bh, WorkingFormat);
    }

    private void DisposeTargets()
    {
        _rtA?.Dispose(); _rtA = null;
        _rtB?.Dispose(); _rtB = null;
        _bloomA?.Dispose(); _bloomA = null;
        _bloomB?.Dispose(); _bloomB = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        _timer.Dispose();
        _effectConstantBuffer.Dispose();
        _frameConstantBuffer.Dispose();
    }
}
