using System.Diagnostics;
using RXShade.Capture;
using RXShade.Models;
using SharpGen.Runtime;
using Vortice.Direct3D11;

namespace RXShade.Graphics;

public readonly record struct FrameStats(
    double Fps,
    double DeliveredFps,
    double GpuMilliseconds,
    double CpuMilliseconds,
    int PassCount,
    int Width,
    int Height,
    int OutputCount)
{
    /// <summary>
    /// True when we are rendering essentially every frame the source hands us,
    /// i.e. RXShade is not the thing limiting the framerate.
    /// </summary>
    public bool KeepingUp => DeliveredFps <= 0 || Fps >= DeliveredFps * 0.97;
}

/// <summary>
/// Ties capture, filtering and presentation together.
///
/// The whole render loop runs on the Windows.Graphics.Capture worker thread:
/// a frame arrives, it goes through the shader chain, and the result is
/// blitted into every registered output window. The WPF UI thread is never
/// on the critical path, so dragging a slider cannot drop a frame.
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private sealed class Output : IDisposable
    {
        public required int Id { get; init; }
        public required Presenter Presenter { get; init; }
        public required bool AspectFit { get; init; }

        /// <summary>0 = present every frame. Otherwise the minimum gap between presents.</summary>
        public long MinIntervalTicks { get; init; }

        private long _nextPresentTicks;

        /// <summary>
        /// Rate-limits an output. The live thumbnail does not need to run at
        /// full capture rate, and every extra Present is CPU time and a DWM
        /// composite charged against the game.
        /// </summary>
        public bool ShouldPresent(long nowTicks)
        {
            if (MinIntervalTicks <= 0) return true;
            if (nowTicks < _nextPresentTicks) return false;

            _nextPresentTicks = nowTicks + MinIntervalTicks;
            return true;
        }

        public void Dispose() => Presenter.Dispose();
    }

    private readonly object _renderLock = new();
    private readonly Dictionary<int, Output> _outputs = new();
    private readonly Dictionary<IntPtr, ID3D11ShaderResourceView> _srvCache = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly GraphicsDevice _gfx;
    private readonly ShaderLibrary _shaders;
    private readonly FilterPipeline _pipeline;
    private readonly CaptureEngine _capture;

    private int _nextOutputId = 1;
    private int _lastContentWidth;
    private int _lastContentHeight;
    private int _lastPassCount;
    private bool _disposed;
    private bool _faulted;

    // FPS accounting over a sliding ~500 ms window. Both numbers are tracked:
    // rendered tells the user what they see, delivered tells them whether the
    // ceiling is ours or the source's.
    private long _windowStartTicks;
    private long _windowStartDelivered;
    private int _windowFrames;
    private double _fps;
    private double _deliveredFps;
    private double _cpuMilliseconds;

    public FilterSettings Settings { get; } = new();
    public string AdapterDescription => _gfx.AdapterDescription;
    public bool IsCapturing => _capture.IsRunning;

    /// <summary>Frames WGC delivered (before rendering).</summary>
    public long DeliveredFrameCount => _capture.DeliveredFrameCount;

    /// <summary>Frames we fully rendered and presented.</summary>
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrames);

    private long _renderedFrames;

    /// <summary>Raised when the captured window disappears.</summary>
    public event Action? TargetClosed;

    /// <summary>Raised on an unrecoverable device error; the engine has already stopped.</summary>
    public event Action<string>? RenderFailed;

    public RenderEngine()
    {
        _gfx = GraphicsDevice.Create();
        _shaders = new ShaderLibrary(_gfx);

        // Compile everything up front so the first frame after a filter is
        // switched on does not hitch while d3dcompiler runs.
        _shaders.PrecompileAll();

        _pipeline = new FilterPipeline(_gfx, _shaders);
        _capture = new CaptureEngine(_gfx);
        _capture.FrameArrived += OnFrameArrived;
        _capture.TargetClosed += () => TargetClosed?.Invoke();
    }

    // ---- Capture control ----------------------------------------------

    public void StartCapture(CaptureTarget target, bool captureCursor = false, bool showCaptureBorder = false)
    {
        lock (_renderLock)
        {
            ClearSrvCache();
            _faulted = false;
            _capture.Start(target, captureCursor, showCaptureBorder);
            _windowStartTicks = _clock.ElapsedTicks;
            _windowStartDelivered = _capture.DeliveredFrameCount;
            _windowFrames = 0;
        }
    }

    public void StopCapture()
    {
        _capture.Stop();
        lock (_renderLock)
        {
            ClearSrvCache();
            _fps = 0;
            _windowFrames = 0;
        }
    }

    // ---- Output surfaces ----------------------------------------------

    /// <summary>
    /// Registers a window to receive the filtered frame.
    /// </summary>
    /// <param name="useComposition">
    /// True for the click-through overlay, which presents through
    /// DirectComposition so DWM handles it as a hardware layer.
    /// </param>
    /// <param name="maxFps">0 for unlimited; otherwise cap this output's present rate.</param>
    public int AddOutput(
        IntPtr hwnd, int width, int height,
        bool aspectFit, bool useComposition, int syncInterval, int maxFps = 0)
    {
        lock (_renderLock)
        {
            var presenter = new Presenter(_gfx, hwnd, width, height, useComposition)
            {
                SyncInterval = syncInterval
            };

            int id = _nextOutputId++;
            _outputs[id] = new Output
            {
                Id = id,
                Presenter = presenter,
                AspectFit = aspectFit,
                MinIntervalTicks = maxFps > 0 ? Stopwatch.Frequency / maxFps : 0
            };
            return id;
        }
    }

    public void ResizeOutput(int id, int width, int height)
    {
        lock (_renderLock)
        {
            if (_outputs.TryGetValue(id, out var output))
                output.Presenter.Resize(width, height);
        }
    }

    public void RemoveOutput(int id)
    {
        lock (_renderLock)
        {
            if (_outputs.Remove(id, out var output))
                output.Dispose();
        }
    }

    /// <summary>Which presentation path an output ended up using.</summary>
    public string GetOutputPath(int id)
    {
        lock (_renderLock)
        {
            if (!_outputs.TryGetValue(id, out var output)) return "unknown";
            return output.Presenter.Path switch
            {
                PresentPath.Composition => "DirectComposition (hardware layer)",
                PresentPath.FlipModel => "flip model",
                _ => "layered bitblt (SLOW fallback)"
            };
        }
    }

    public bool OutputUsesLegacySwapEffect(int id)
    {
        lock (_renderLock)
            return _outputs.TryGetValue(id, out var output) && output.Presenter.UsesLegacySwapEffect;
    }

    // ---- Render loop ---------------------------------------------------

    private void OnFrameArrived(CapturedFrame frame)
    {
        if (_disposed) return;

        lock (_renderLock)
        {
            if (_disposed || _faulted || _outputs.Count == 0) return;

            // Time spent inside the capture callback. If this approaches one
            // refresh interval we are the reason frames are being missed; if it
            // is small and delivery is still low, the limit is upstream of us.
            long callbackStart = Stopwatch.GetTimestamp();

            try
            {
                // WGC hands back pool-sized textures; the pipeline crops to the
                // live content region using the ratio between the two.
                var description = frame.Texture.Description;

                if (frame.ContentWidth != _lastContentWidth || frame.ContentHeight != _lastContentHeight)
                {
                    // Pool was recreated, so the old textures (and the SRVs
                    // keeping them alive) are no longer referenced.
                    ClearSrvCache();
                    _lastContentWidth = frame.ContentWidth;
                    _lastContentHeight = frame.ContentHeight;
                }

                var srv = GetOrCreateSrv(frame.Texture);
                var snapshot = Settings.Snapshot();

                // Wrapped at an hour so the wave phase never loses precision
                // in a long session.
                float time = (float)(_clock.Elapsed.TotalSeconds % 3600.0);

                PipelineOutput output = _pipeline.Process(
                    srv,
                    (int)description.Width, (int)description.Height,
                    frame.CropX, frame.CropY,
                    frame.ContentWidth, frame.ContentHeight,
                    snapshot,
                    time);

                _lastPassCount = output.PassCount;

                long nowTicks = _clock.ElapsedTicks;
                foreach (var target in _outputs.Values)
                {
                    if (!target.ShouldPresent(nowTicks)) continue;

                    _pipeline.BlitToPresenter(target.Presenter, output, target.AspectFit);
                    target.Presenter.Present();
                }

                Interlocked.Increment(ref _renderedFrames);

                double elapsedMs = (Stopwatch.GetTimestamp() - callbackStart) * 1000.0 / Stopwatch.Frequency;
                _cpuMilliseconds = _cpuMilliseconds * 0.9 + elapsedMs * 0.1;

                TickFps();
            }
            catch (SharpGenException ex)
            {
                // Typically device-removed (driver reset / GPU hot-swap).
                //
                // We are on a WGC worker thread, inside its FrameArrived
                // callback. Tearing the capture session down from in here can
                // deadlock inside WGC, so instead we latch a fault flag (which
                // makes every subsequent frame a no-op) and let the UI thread
                // perform the actual shutdown.
                _faulted = true;
                RenderFailed?.Invoke($"Direct3D error: {ex.Message}");
            }
        }
    }

    private ID3D11ShaderResourceView GetOrCreateSrv(ID3D11Texture2D texture)
    {
        // The frame pool rotates a small fixed set of textures, so caching by
        // native pointer means we create two SRVs per session instead of one
        // per frame. The cached SRV holds a reference, which also guarantees
        // the pointer cannot be recycled underneath us.
        IntPtr key = texture.NativePointer;
        if (_srvCache.TryGetValue(key, out var cached))
            return cached;

        var srv = _gfx.Device.CreateShaderResourceView(texture);
        _srvCache[key] = srv;
        return srv;
    }

    private void ClearSrvCache()
    {
        foreach (var srv in _srvCache.Values)
            srv.Dispose();
        _srvCache.Clear();
    }

    private void TickFps()
    {
        _windowFrames++;
        long now = _clock.ElapsedTicks;
        double elapsed = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 0.5) return;

        long deliveredNow = _capture.DeliveredFrameCount;

        _fps = _windowFrames / elapsed;
        _deliveredFps = (deliveredNow - _windowStartDelivered) / elapsed;

        _windowFrames = 0;
        _windowStartDelivered = deliveredNow;
        _windowStartTicks = now;
    }

    public FrameStats GetStats()
    {
        lock (_renderLock)
        {
            return new FrameStats(
                _fps,
                _deliveredFps,
                _pipeline.LastGpuMilliseconds,
                _cpuMilliseconds,
                _lastPassCount,
                _lastContentWidth,
                _lastContentHeight,
                _outputs.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _capture.FrameArrived -= OnFrameArrived;
        _capture.Dispose();

        lock (_renderLock)
        {
            foreach (var output in _outputs.Values)
                output.Dispose();
            _outputs.Clear();

            ClearSrvCache();
            _pipeline.Dispose();
            _shaders.Dispose();
            _gfx.Dispose();
        }
    }
}
