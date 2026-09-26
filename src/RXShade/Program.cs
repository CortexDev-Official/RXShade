namespace RXShade;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless pipeline verification, used to prove capture + shaders hit
        // framerate without a GUI in the measurement path.
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run(args);

        int testIndex = Array.FindIndex(args, a => a.Equals("--rendertest", StringComparison.OrdinalIgnoreCase));
        if (testIndex >= 0 && testIndex + 1 < args.Length)
            return RenderTest.Run(args[testIndex + 1]);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
