using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;
using RXShade.Interop;
using RXShade.ViewModels;
using RXShade.Views;

namespace RXShade;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private int _thumbnailOutputId = -1;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        ThumbnailHost.SurfaceReady += OnThumbnailReady;
        ThumbnailHost.SurfaceResized += OnThumbnailResized;
        ThumbnailHost.SurfaceDestroying += OnThumbnailDestroying;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            NativeMethods.ApplyWindowChrome(source.Handle);
    }

    // ---- Live thumbnail -------------------------------------------------
    // The thumbnail is just another output surface, so it costs one extra
    // fullscreen blit rather than a second pipeline run - and crucially no
    // CPU readback, which is what a WriteableBitmap preview would have cost.

    private void OnThumbnailReady(IntPtr hwnd, int width, int height)
    {
        // Capped at 30 FPS. A small preview gains nothing from running at full
        // capture rate, and every Present is CPU time plus a DWM composite
        // charged against the game we are sitting on top of.
        _thumbnailOutputId = _viewModel.Engine.AddOutput(
            hwnd, width, height,
            aspectFit: true, useComposition: false, syncInterval: 0, maxFps: 30);
    }

    private void OnThumbnailResized(int width, int height)
    {
        if (_thumbnailOutputId >= 0)
            _viewModel.Engine.ResizeOutput(_thumbnailOutputId, width, height);
    }

    private void OnThumbnailDestroying()
    {
        if (_thumbnailOutputId >= 0)
        {
            _viewModel.Engine.RemoveOutput(_thumbnailOutputId);
            _thumbnailOutputId = -1;
        }
    }

    /// <summary>
    /// Re-scan windows every time the picker is opened, so launching RXShade
    /// before Roblox is the normal order of events rather than a dead end.
    /// </summary>
    private void OnSourceDropDownOpened(object sender, EventArgs e) => _viewModel.RefreshTargets();

    // ---- Window chrome --------------------------------------------------

    /// <summary>Opens the developer's site in the user's default browser.</summary>
    private void OnCreditNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A missing browser association must not take the app down.
            App.Log(ex);
        }

        e.Handled = true;
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
        Application.Current?.Shutdown();
    }
}
