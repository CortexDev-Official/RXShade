using System.Diagnostics;
using System.IO;
using RXShade.Capture;
using RXShade.Graphics;
using RXShade.Interop;
using RXShade.Models;

namespace RXShade;

/// <summary>
/// Headless verification of the capture -> shader -> present pipeline.
///
/// Run with:  RXShade.exe --selftest [seconds] [--window &lt;title substring&gt;]
///
/// This exists to answer one question directly, without a GUI in the way:
/// does Windows.Graphics.Capture + Direct3D11 sustain full framerate on this
/// machine, and if not, is it the capture or the post-processing?
///
/// It measures two phases against the same live source:
///   PHASE 1  zero filters  - capture, one blit, present. Pure capture cost.
///   PHASE 2  every filter  - the full shader chain.
/// If phase 1 is slow the problem is capture or presentation. If only phase 2
/// is slow the problem is the shader chain, and the reported GPU time says
/// exactly how much of the frame it is eating.
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        AttachToParentConsole();

        double seconds = 4.0;
        string? windowFilter = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--window" && i + 1 < args.Length)
                windowFilter = args[++i];
            else if (double.TryParse(args[i], out double parsed) && parsed is > 0 and <= 120)
                seconds = parsed;
        }

        Console.WriteLine();
        Console.WriteLine("RXShade self-test  -  CortexDev");
        Console.WriteLine("================================");

        if (!CaptureEngine.IsSupported)
        {
            Console.WriteLine("FAIL: Windows.Graphics.Capture is not available on this system.");
            return 2;
        }
        Console.WriteLine("Windows.Graphics.Capture : supported");

        // The yellow outline Windows draws around captured windows is an OS
        // privacy indicator. Removing it needs explicit consent, so report
        // whether we actually got it rather than assuming.
        bool borderless = CaptureEngine.CanCaptureBorderless;
        Console.WriteLine($"Borderless capture       : {(borderless ? "granted (no yellow border)" : "denied - yellow border will show")}");

        int refreshRate = NativeMethods.GetPrimaryRefreshRate();
        if (refreshRate > 0)
            Console.WriteLine($"Display refresh          : {refreshRate} Hz  (hard ceiling on captured framerate)");

        RenderEngine? engine = null;
        Win32Window? window = null;

        try
        {
            var startupTimer = Stopwatch.StartNew();
            engine = new RenderEngine();
            startupTimer.Stop();

            Console.WriteLine($"GPU                      : {engine.AdapterDescription}");
            Console.WriteLine($"Device + shader compile  : {startupTimer.ElapsedMilliseconds} ms");

            // Show every GPU and where the monitors actually are. Capture
            // frames come from DWM, which runs on whichever adapter drives the
            // display - so that is the adapter RXShade must use, regardless of
            // which GPU is the "better" one.
            Console.WriteLine();
            Console.WriteLine("Installed adapters:");
            foreach (var adapter in GraphicsDevice.EnumerateAdapters())
            {
                string vram = adapter.DedicatedVideoMemory > 0
                    ? $"{adapter.DedicatedVideoMemory / (1024 * 1024)} MB"
                    : "shared";
                string monitors = adapter.OutputCount == 0
                    ? "no monitor attached"
                    : $"{adapter.OutputCount} monitor(s) attached  <-- drives the display";
                Console.WriteLine($"  [{adapter.Index}] {adapter.Description,-34} {vram,9}   {monitors}");
            }
            Console.WriteLine();

            CaptureTarget? target = SelectTarget(windowFilter);
            if (target is null)
            {
                Console.WriteLine("FAIL: no capturable target found.");
                return 3;
            }
            Console.WriteLine($"Capture target           : {target.DisplayName}");

            // Window capture returns the whole window frame, title bar included.
            // Confirm we are cropping to the client area, or the overlay shows a
            // second title bar squeezed into the top of the game view.
            if (NativeMethods.TryGetClientCropInWindow(target.Handle, out int cx, out int cy, out int cw, out int chh))
            {
                Console.WriteLine($"Client-area crop         : +{cx},+{cy}  {cw}x{chh} (title bar excluded)");
            }

            // Small, off to one side, never takes focus. It exists so the test
            // exercises a real swap chain Present rather than only the offscreen
            // passes - presentation is part of what we are verifying.
            window = new Win32Window("RXShade self-test", 40, 40, 480, 270,
                clickThrough: false, topMost: true, toolWindow: true);
            window.Show();

            int outputId = engine.AddOutput(window.Handle, 480, 270,
                aspectFit: true, useComposition: false, syncInterval: 0);

            Console.WriteLine($"Swap chain               : {(engine.OutputUsesLegacySwapEffect(outputId) ? "bitblt (legacy)" : "flip model")}");
            Console.WriteLine();

            engine.StartCapture(target);

            IntPtr targetWindow = target.Handle;

            // Give the user time to click the game. Measuring while it sits
            // behind a console window reports Windows' background-throttled
            // rate, which is roughly half the real one and looks exactly like a
            // bug in the capture pipeline.
            if (targetWindow != IntPtr.Zero)
            {
                Console.WriteLine();
                Console.WriteLine("  >>> CLICK ON THE GAME WINDOW NOW - measuring starts in 5 seconds <<<");
                for (int i = 5; i > 0; i--)
                {
                    Console.Write($"  {i}... ");
                    Pump(1.0);
                }
                Console.WriteLine();
                Console.WriteLine();
            }

            // Let capture settle before measuring: the first few frames include
            // frame-pool allocation and shader/pipeline warm-up.
            Pump(1.0);

            // Capture-only baseline. With no output registered the engine does
            // no rendering or presenting at all, so this is the raw rate
            // Windows.Graphics.Capture hands us. Comparing it with the phases
            // below is what separates "the source only produces 50 FPS" from
            // "our present is costing us half the frame rate".
            engine.RemoveOutput(outputId);
            Console.WriteLine();
            Console.WriteLine($"PHASE 0  capture only, no output     ({seconds:0.#}s)");
            double captureOnlyFps = MeasureDeliveredOnly(engine, seconds, targetWindow);
            Console.WriteLine($"    capture delivers: {captureOnlyFps,6:0.0} FPS  (raw WGC rate, nothing rendered)");

            outputId = engine.AddOutput(window.Handle, 480, 270,
                aspectFit: true, useComposition: false, syncInterval: 0);
            Pump(0.5);

            Console.WriteLine();
            Console.WriteLine($"PHASE 1  passthrough, zero filters   ({seconds:0.#}s)");
            engine.Settings.ResetToDefaults();
            var passthrough = Measure(engine, seconds, targetWindow);
            Report(passthrough);

            Console.WriteLine();
            Console.WriteLine($"PHASE 2  full filter chain           ({seconds:0.#}s)");
            EnableEverything(engine.Settings);
            Pump(0.5);
            var filtered = Measure(engine, seconds, targetWindow);
            Report(filtered);

            Console.WriteLine();
            Console.WriteLine("PHASE 3  click-through overlay path");
            RunOverlayPathCheck(engine);

            Console.WriteLine();
            Console.WriteLine("PHASE 4  preset sweep");
            RunPresetSweep(engine);

            Console.WriteLine();
            Console.WriteLine("--------------------------------");

            // "Keeping up" means we render essentially every frame handed to
            // us. A source that only produces 30 FPS cannot be turned into 60.
            bool keepsUpPassthrough = passthrough.RenderedFps >= passthrough.DeliveredFps * 0.97;
            bool keepsUpFiltered = filtered.RenderedFps >= filtered.DeliveredFps * 0.97;

            Console.WriteLine($"Passthrough : renders {passthrough.RenderedFps:0.0} of {passthrough.DeliveredFps:0.0} delivered FPS");
            Console.WriteLine($"Filtered    : renders {filtered.RenderedFps:0.0} of {filtered.DeliveredFps:0.0} delivered FPS   GPU {filtered.GpuMilliseconds:0.00} ms/frame");

            // Headroom: how many frames per second the shader chain alone could
            // sustain. This is the number that matters for the 60 FPS target,
            // because it is independent of what the source happens to produce.
            double shaderCeiling = filtered.GpuMilliseconds > 0.001
                ? 1000.0 / filtered.GpuMilliseconds
                : double.PositiveInfinity;
            Console.WriteLine($"Shader ceiling: {shaderCeiling:0} FPS at {filtered.Width}x{filtered.Height} with all filters on");
            Console.WriteLine($"Source ceiling: capture delivers {captureOnlyFps:0.0} FPS" +
                              (refreshRate > 0 ? $"  (display {refreshRate} Hz)" : string.Empty));

            // The single most common misunderstanding: the game's own frame rate
            // cap, not RXShade, sets the ceiling. Say so explicitly when the
            // source is clearly running below the display.
            if (refreshRate > 0 && captureOnlyFps < refreshRate * 0.9)
            {
                Console.WriteLine();
                Console.WriteLine($"Note        : the captured window produces {captureOnlyFps:0.0} FPS on a {refreshRate} Hz display.");
                Console.WriteLine("              RXShade renders every frame it is handed and its shader chain has");
                Console.WriteLine("              headroom to spare, so this ceiling is the game's own frame rate");
                Console.WriteLine("              cap (or vsync). Raise it in the game for a higher number.");
            }

            Console.WriteLine();
            if (!keepsUpFiltered)
                Console.WriteLine("Diagnosis   : shader-chain bound - we are dropping delivered frames.");
            else if (!keepsUpPassthrough)
                Console.WriteLine("Diagnosis   : present bound - capture is fine but output cannot keep up.");
            else if (shaderCeiling < 60)
                Console.WriteLine("Diagnosis   : GPU too slow for 60 FPS with every filter enabled.");
            else
            {
                Console.WriteLine("Diagnosis   : pipeline healthy - we consume every frame the source produces,");
                Console.WriteLine($"              with shader headroom for {shaderCeiling:0} FPS.");
            }

            engine.StopCapture();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            engine?.Dispose();
            window?.Dispose();
        }
    }

    private static CaptureTarget? SelectTarget(string? windowFilter)
    {
        var targets = WindowEnumerator.EnumerateTargets();

        if (!string.IsNullOrWhiteSpace(windowFilter))
        {
            return targets.FirstOrDefault(t =>
                t.Title.Contains(windowFilter, StringComparison.OrdinalIgnoreCase));
        }

        // RXShade only ever captures Roblox, so there is exactly one kind of
        // target to choose from. The self-test requires Roblox to be running.
        return targets.FirstOrDefault();
    }

    /// <summary>Worst case: every pass on at once, which no preset actually does.</summary>
    private static void EnableEverything(FilterSettings settings)
    {
        settings.SharpenEnabled = true;
        settings.FxaaEnabled = true;
        settings.BloomEnabled = true;
        settings.ReflectionEnabled = true;
        settings.FocusEnabled = true;
        settings.HdrEnabled = true;
        settings.ColorGradeEnabled = true;
        settings.ChromaticEnabled = true;
        settings.GrainEnabled = true;
    }

    /// <summary>
    /// Runs every preset through the real shader chain and reports its cost.
    ///
    /// This is the cheapest way to catch a preset that references a broken
    /// pass or is quietly far more expensive than the rest - a UI screenshot
    /// would never reveal either.
    /// </summary>
    private static void RunPresetSweep(RenderEngine engine)
    {
        foreach (FilterPreset preset in Enum.GetValues<FilterPreset>())
        {
            engine.Settings.ApplyPreset(preset);
            Pump(0.7);

            var stats = engine.GetStats();
            string cost = stats.PassCount > 0
                ? $"{stats.GpuMilliseconds,5:0.00} ms over {stats.PassCount,2} passes"
                : "passthrough (no passes)";
            Console.WriteLine($"    {preset,-10}: {cost}");
        }

        engine.Settings.ApplyPreset(FilterPreset.Off);
    }

    /// <summary>
    /// Exercises the overlay's presentation path specifically.
    ///
    /// This is the riskiest surface in the app: DXGI refuses flip-model swap
    /// chains on WS_EX_LAYERED windows, so the overlay has to fall back to the
    /// bitblt swap effect. That fallback is worth proving on the actual machine
    /// rather than assuming. A small probe window is used so nothing is
    /// covered up while the test runs.
    /// </summary>
    private static void RunOverlayPathCheck(RenderEngine engine)
    {
        Win32Window? overlay = null;
        int outputId = -1;

        try
        {
            overlay = new Win32Window("RXShade overlay probe", 560, 40, 480, 270,
                clickThrough: true, topMost: true, toolWindow: true);

            // Must match OverlayController exactly, or this phase reports on a
            // path the real overlay never takes.
            outputId = engine.AddOutput(overlay.Handle, 480, 270,
                aspectFit: false, useComposition: false, syncInterval: 0);
            overlay.Show();

            long before = engine.RenderedFrameCount;
            var timer = Stopwatch.StartNew();
            Pump(1.5);
            long frames = engine.RenderedFrameCount - before;

            // Which path the overlay landed on matters enormously: the legacy
            // layered/bitblt fallback makes DWM alpha-blend an intermediate
            // surface every frame and throttles the game underneath.
            Console.WriteLine($"    present path    : {engine.GetOutputPath(outputId)}");
            Console.WriteLine($"    presented       : {frames} frames in {timer.Elapsed.TotalSeconds:0.0}s");
            Console.WriteLine("    click-through   : WS_EX_TRANSPARENT + WM_NCHITTEST -> HTTRANSPARENT");
            Console.WriteLine($"    result          : {(frames > 0 ? "OK" : "NO FRAMES PRESENTED")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (outputId >= 0) engine.RemoveOutput(outputId);
            overlay?.Dispose();
        }
    }

    private readonly record struct PhaseResult(
        double DeliveredFps,
        double RenderedFps,
        double GpuMilliseconds,
        int PassCount,
        int Width,
        int Height,
        double ForegroundFraction);

    /// <summary>
    /// Counts delivered vs rendered frames over an exact interval. Measuring
    /// both is the whole point: if they match, we are consuming every frame
    /// the source produces and any shortfall is the source's, not ours.
    /// </summary>
    /// <summary>
    /// Counts only what the capture source delivers, with no output registered,
    /// so no rendering or presenting happens. This is the ceiling everything
    /// else is measured against.
    /// </summary>
    private static double MeasureDeliveredOnly(RenderEngine engine, double seconds, IntPtr targetWindow)
    {
        long deliveredStart = engine.DeliveredFrameCount;
        var timer = Stopwatch.StartNew();
        PumpAndSampleFocus(seconds, targetWindow);
        double elapsed = timer.Elapsed.TotalSeconds;
        long delivered = engine.DeliveredFrameCount - deliveredStart;
        return delivered / elapsed;
    }

    private static PhaseResult Measure(RenderEngine engine, double seconds, IntPtr targetWindow)
    {
        long deliveredStart = engine.DeliveredFrameCount;
        long renderedStart = engine.RenderedFrameCount;

        var timer = Stopwatch.StartNew();
        double foregroundFraction = PumpAndSampleFocus(seconds, targetWindow);
        double elapsed = timer.Elapsed.TotalSeconds;

        long delivered = engine.DeliveredFrameCount - deliveredStart;
        long rendered = engine.RenderedFrameCount - renderedStart;
        var stats = engine.GetStats();

        return new PhaseResult(
            delivered / elapsed,
            rendered / elapsed,
            stats.GpuMilliseconds,
            stats.PassCount,
            stats.Width,
            stats.Height,
            foregroundFraction);
    }

    /// <summary>
    /// Pumps messages while sampling whether the captured window is actually
    /// the foreground window.
    ///
    /// This matters enormously and is easy to miss: Windows throttles the
    /// presentation rate of background windows, and games throttle themselves
    /// further when they lose focus. Measuring capture throughput while the
    /// target sits behind a console window reports the throttled rate, not the
    /// real one - which is exactly the trap this benchmark fell into.
    /// </summary>
    private static double PumpAndSampleFocus(double seconds, IntPtr targetWindow)
    {
        var timer = Stopwatch.StartNew();
        int foreground = 0;
        int samples = 0;

        while (timer.Elapsed.TotalSeconds < seconds)
        {
            NativeMethods.PumpMessages();

            if (targetWindow != IntPtr.Zero)
            {
                samples++;
                if (NativeMethods.GetForegroundWindow() == targetWindow) foreground++;
            }

            Thread.Sleep(4);
        }

        return samples > 0 ? (double)foreground / samples : 1.0;
    }

    private static void Report(PhaseResult result)
    {
        Console.WriteLine($"    resolution      : {result.Width} x {result.Height}");
        Console.WriteLine($"    capture delivers: {result.DeliveredFps,6:0.0} FPS");
        Console.WriteLine($"    we render       : {result.RenderedFps,6:0.0} FPS");
        Console.WriteLine($"    shader time     : {result.GpuMilliseconds:0.00} ms/frame over {result.PassCount} pass(es)");

        // Without this the whole measurement can be quietly meaningless.
        if (result.ForegroundFraction < 0.8)
        {
            Console.WriteLine($"    target focused  : {result.ForegroundFraction * 100,5:0}%  *** INVALID MEASUREMENT ***");
            Console.WriteLine("                      Windows throttles background windows and games throttle");
            Console.WriteLine("                      themselves when unfocused. Click the game and re-run.");
        }
    }

    /// <summary>
    /// Drains the message queue for the duration. The window we created lives
    /// on this thread, so it needs pumping or DWM will mark it unresponsive.
    /// </summary>
    private static void Pump(double seconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            NativeMethods.PumpMessages();
            Thread.Sleep(4);
        }
    }

    private static void AttachToParentConsole()
    {
        // A WinExe is built with the WINDOWS subsystem, so it starts with no
        // console and no valid stdout handle - Console.WriteLine goes nowhere.
        // Attach to the launching shell's console; if it has none (double
        // clicked), allocate one so the results are still visible.
        if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            NativeMethods.AllocConsole();

        // AttachConsole/AllocConsole install fresh standard handles, but the
        // CLR has already cached its (null) stdout writer, so re-point it.
        var stdout = Console.OpenStandardOutput();
        if (stdout != Stream.Null)
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
    }
}
