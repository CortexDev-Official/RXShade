using System.Windows.Threading;
using RXShade.Capture;
using RXShade.Graphics;
using RXShade.Interop;

namespace RXShade.Views;

/// <summary>
/// Manages the borderless click-through overlay and keeps it glued to the
/// target window.
///
/// INPUT: the overlay is created WS_EX_TRANSPARENT | WS_EX_LAYERED and answers
/// WM_NCHITTEST with HTTRANSPARENT, so the window manager routes every click
/// and keystroke to whatever is underneath. RXShade installs no keyboard or
/// mouse hook and reads no input at all - input simply never reaches it.
/// </summary>
public sealed class OverlayController : IDisposable
{
    // 30 Hz tracking. The overlay only follows window moves and resizes, which
    // a user cannot perform anywhere near this fast; polling harder just burns
    // CPU and DWM time that the game could be using.
    private static readonly TimeSpan TrackInterval = TimeSpan.FromMilliseconds(33);

    private readonly RenderEngine _engine;
    private readonly DispatcherTimer _tracker;

    private Win32Window? _window;
    private CaptureTarget? _target;
    private int _outputId = -1;
    private bool _visible;
    private bool _disposed;

    /// <summary>Raised when the target window disappears while the overlay is up.</summary>
    public event Action? TargetLost;

    public bool IsActive => _window is not null;

    /// <summary>True when DXGI refused a flip-model swap chain on the layered window.</summary>
    public bool UsesLegacySwapEffect { get; private set; }

    public OverlayController(RenderEngine engine)
    {
        _engine = engine;
        _tracker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TrackInterval };
        _tracker.Tick += (_, _) => Track();
    }

    public void Start(CaptureTarget target)
    {
        Stop();
        _target = target;

        if (!NativeMethods.TryGetClientRectOnScreen(target.Handle, out var rect) || rect.Width < 2 || rect.Height < 2)
            throw new InvalidOperationException("The target window has no visible client area to cover.");

        // MUST stay on the WS_EX_LAYERED path, even though it is the slower one.
        //
        // Reliable click-through needs WS_EX_LAYERED: WS_EX_TRANSPARENT alone on
        // a non-layered top-level window does not pass mouse input through, and
        // WM_NCHITTEST -> HTTRANSPARENT is not enough on its own either.
        // DirectComposition would be considerably faster but requires
        // WS_EX_NOREDIRECTIONBITMAP, which is mutually exclusive with
        // WS_EX_LAYERED - and an overlay that swallows every click makes the
        // game unplayable, which is far worse than a slower present path.
        _window = new Win32Window(
            "RXShade Overlay",
            rect.Left, rect.Top, rect.Width, rect.Height,
            clickThrough: true, topMost: true, toolWindow: true, forComposition: false);

        // preferFlipModel: false - WS_EX_LAYERED windows cannot host a
        // flip-model swap chain, DXGI returns DXGI_ERROR_INVALID_CALL.
        // syncInterval 0: never block the capture thread on vblank. See
        // Presenter.SyncInterval - waiting here pins output to half the
        // display's refresh rate without preventing any tearing.
        _outputId = _engine.AddOutput(
            _window.Handle, rect.Width, rect.Height,
            aspectFit: false, useComposition: false, syncInterval: 0);

        UsesLegacySwapEffect = _engine.OutputUsesLegacySwapEffect(_outputId);

        _lastBounds = rect;
        _window.Show();
        _visible = true;
        _tracker.Start();
    }

    public void Stop()
    {
        _tracker.Stop();

        if (_outputId >= 0)
        {
            _engine.RemoveOutput(_outputId);
            _outputId = -1;
        }

        _window?.Dispose();
        _window = null;
        _target = null;
        _visible = false;
    }

    private void Track()
    {
        if (_window is null || _target is null) return;

        if (!_target.IsAlive)
        {
            TargetLost?.Invoke();
            return;
        }

        // Minimised: nothing to cover, and the client rect goes off-screen.
        if (NativeMethods.IsIconic(_target.Handle))
        {
            SetVisible(false);
            return;
        }

        // Only sit on top while the game (or RXShade itself) is the active
        // app. Without this the overlay would cover a browser after Alt-Tab.
        if (!IsTargetOrOwnAppActive())
        {
            SetVisible(false);
            return;
        }

        if (!NativeMethods.TryGetClientRectOnScreen(_target.Handle, out var rect)) return;
        if (rect.Width < 2 || rect.Height < 2)
        {
            SetVisible(false);
            return;
        }

        // ONLY reposition when the target actually moved or resized.
        //
        // Calling SetWindowPos with HWND_TOPMOST every tick makes DWM
        // re-evaluate z-order and recomposite the desktop 60 times a second
        // for no reason. That is pure overhead charged to the whole system -
        // including the game we are sitting on top of.
        if (rect.Left != _lastBounds.Left || rect.Top != _lastBounds.Top ||
            rect.Width != _lastBounds.Width || rect.Height != _lastBounds.Height)
        {
            bool resized = _window.SetBounds(rect.Left, rect.Top, rect.Width, rect.Height, topMost: true);
            _lastBounds = rect;

            if (resized)
                _engine.ResizeOutput(_outputId, rect.Width, rect.Height);
        }

        SetVisible(true);
    }

    private NativeMethods.RECT _lastBounds;

    private bool IsTargetOrOwnAppActive()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (_target is not null && foreground == _target.Handle) return true;

        NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void SetVisible(bool visible)
    {
        if (_window is null || visible == _visible) return;
        _visible = visible;

        if (visible) _window.Show();
        else _window.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
