using RXShade.Graphics;
using RXShade.Interop;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace RXShade.Capture;

/// <summary>
/// Frame delivered by Windows.Graphics.Capture, already resident in VRAM.
/// </summary>
/// <param name="Texture">The frame pool's texture. Valid only for the duration of the callback.</param>
/// <param name="ContentWidth">Live content width; may be smaller than the texture.</param>
/// <param name="ContentHeight">Live content height; may be smaller than the texture.</param>
public readonly record struct CapturedFrame(
    ID3D11Texture2D Texture,
    int CropX,
    int CropY,
    int ContentWidth,
    int ContentHeight);

/// <summary>
/// Wraps a Windows.Graphics.Capture session.
///
/// ============================ HOW THIS WORKS ============================
/// This is the same mechanism OBS, Xbox Game Bar and the NVIDIA overlay use.
/// The Desktop Window Manager already owns the composited pixels of every
/// window; WGC asks DWM for a copy of them. RXShade therefore:
///   * does NOT inject any DLL into the target process
///   * does NOT open, read or write the target process's memory
///   * does NOT hook any API inside the target process
///   * does NOT touch any file belonging to the target application
/// It receives a picture. That is the entire extent of the interaction.
/// =======================================================================
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    /// <summary>
    /// Frame pool depth.
    ///
    /// Three, not two. We render and present inside the FrameArrived callback,
    /// so while we hold a buffer the compositor needs a free one to write the
    /// next frame into. With only two, any frame where our work overruns the
    /// refresh interval leaves nowhere to write, WGC skips that composition
    /// cycle, and delivery locks to exactly half the display's rate. The third
    /// buffer gives the slack to absorb a slow frame without halving.
    /// </summary>
    private const int FramePoolBuffers = 3;

    private readonly object _lock = new();
    private readonly IDirect3DDevice _winrtDevice;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private bool _disposed;

    /// <summary>The Roblox window whose client-area crop is applied each frame.</summary>
    private IntPtr _targetWindow;

    /// <summary>
    /// Raised on a WGC worker thread (the frame pool is free-threaded), once per
    /// captured frame. The handler runs synchronously; the texture is released
    /// as soon as it returns.
    /// </summary>
    public event Action<CapturedFrame>? FrameArrived;

    /// <summary>Raised when the captured window goes away.</summary>
    public event Action? TargetClosed;

    public SizeInt32 ContentSize { get; private set; }
    public bool IsRunning => _session is not null;

    /// <summary>
    /// Frames WGC has handed us since Start, counted before any rendering
    /// happens. Comparing this against rendered frames separates "the source
    /// is not producing frames" from "we cannot keep up with the source".
    /// </summary>
    public long DeliveredFrameCount => Interlocked.Read(ref _deliveredFrames);

    private long _deliveredFrames;

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

    public CaptureEngine(GraphicsDevice gfx)
    {
        _winrtDevice = WinRtInterop.CreateDirect3DDevice(gfx.Device);
    }

    public void Start(CaptureTarget target, bool captureCursor, bool drawCaptureBorder)
    {
        lock (_lock)
        {
            StopCore();

            _item = target.CreateCaptureItem();
            _targetWindow = target.Handle;
            _poolSize = _item.Size;
            ContentSize = _poolSize;

            _item.Closed += OnItemClosed;

            // CreateFreeThreaded => FrameArrived fires on a WGC worker thread
            // instead of requiring a DispatcherQueue. That keeps the render loop
            // completely off the WPF UI thread.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBuffers,
                _poolSize);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            TrySetCursorCapture(_session, captureCursor);
            TrySetBorderRequired(_session, drawCaptureBorder);
            _session.StartCapture();
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => TargetClosed?.Invoke();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;

        bool needsRecreate = false;
        SizeInt32 newSize = default;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var contentSize = frame.ContentSize;
            ContentSize = contentSize;

            // WGC keeps handing back pool-sized textures after the window
            // shrinks, with stale pixels outside the live region. We crop to
            // ContentSize now and resize the pool after releasing the frame.
            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                needsRecreate = true;
                newSize = contentSize;
            }

            if (contentSize.Width <= 0 || contentSize.Height <= 0) return;

            Interlocked.Increment(ref _deliveredFrames);

            // Window capture returns the whole window frame, title bar included.
            // Crop to the client area so the overlay - which covers exactly the
            // client rect - shows the game and not a second title bar.
            int cropX = 0, cropY = 0;
            int width = contentSize.Width, height = contentSize.Height;

            if (_targetWindow != IntPtr.Zero &&
                NativeMethods.TryGetClientCropInWindow(_targetWindow, out int ox, out int oy, out int cw, out int ch) &&
                ox + cw <= contentSize.Width && oy + ch <= contentSize.Height)
            {
                cropX = ox;
                cropY = oy;
                width = cw;
                height = ch;
            }

            using var texture = WinRtInterop.GetTexture(frame.Surface);
            FrameArrived?.Invoke(new CapturedFrame(texture, cropX, cropY, width, height));
        }
        catch (ObjectDisposedException)
        {
            // Session torn down mid-callback; nothing to do.
        }
        finally
        {
            if (needsRecreate && !_disposed)
            {
                lock (_lock)
                {
                    if (_framePool is not null && !_disposed)
                    {
                        _poolSize = newSize;
                        _framePool.Recreate(
                            _winrtDevice,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized,
                            FramePoolBuffers,
                            newSize);
                    }
                }
            }
        }
    }

    /// <summary>
    /// THE YELLOW BORDER.
    ///
    /// Windows draws a yellow outline around anything being captured. It is an
    /// OS privacy indicator, not something the capturing app draws, and it is
    /// the same one OBS and Game Bar trigger. Setting IsBorderRequired = false
    /// only works AFTER the process has been granted borderless capture -
    /// without this request the assignment silently does nothing, which is
    /// exactly why the border kept appearing.
    ///
    /// Requested once per process and cached. Requires Windows 11 (22000+);
    /// on Windows 10 the border cannot be removed by any application.
    /// </summary>
    private static readonly Lazy<bool> BorderlessAccessGranted = new(() =>
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return false;

            var status = GraphicsCaptureAccess
                .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();

            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception)
        {
            // Denied by policy, timed out, or the API is unavailable. Not
            // fatal - we simply capture with the border showing.
            return false;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when this process may capture without the yellow border.</summary>
    public static bool CanCaptureBorderless => BorderlessAccessGranted.Value;

    private static void TrySetCursorCapture(GraphicsCaptureSession session, bool enabled)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled = enabled;
            }
        }
        catch (Exception)
        {
            // Older build; capture with the default cursor behaviour.
        }
    }

    private static void TrySetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

            // Asking to keep the border always works; removing it needs consent.
            if (required || BorderlessAccessGranted.Value)
                session.IsBorderRequired = required;
        }
        catch (Exception)
        {
            // Policy refused it. The border stays, which is only cosmetic.
        }
    }

    public void Stop()
    {
        lock (_lock) StopCore();
    }

    private void StopCore()
    {
        if (_session is not null)
        {
            _session.Dispose();
            _session = null;
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        _targetWindow = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) StopCore();
    }
}
