using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RXShade;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RXShade", "error.log");

    private bool _errorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A failure inside a WGC worker thread or a D3D call should surface as
        // a readable message and a log entry, not a silent exit.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log(ex);
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;

        // Only ever show one dialog: a fault in the render path can repeat
        // every frame, and a stack of identical message boxes helps nobody.
        if (_errorShown) return;
        _errorShown = true;

        MessageBox.Show(
            $"{e.Exception.GetType().Name}\n\n{e.Exception.Message}\n\nDetails written to:\n{LogPath}",
            "RXShade — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    internal static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never itself take the app down (IO, permissions,
            // a full disk, or a malformed path all fail silently here).
        }
    }
}
