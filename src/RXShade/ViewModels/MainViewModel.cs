using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using RXShade.Capture;
using RXShade.Graphics;
using RXShade.Interop;
using RXShade.Models;
using RXShade.Views;

namespace RXShade.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly OverlayController _overlay;

    /// <summary>
    /// Captured at construction, on the UI thread.
    ///
    /// This must NOT be read as Dispatcher.CurrentDispatcher inside the engine
    /// callbacks: those fire on a Windows.Graphics.Capture worker thread, where
    /// CurrentDispatcher would create a brand-new dispatcher for that worker
    /// that nothing ever pumps - so the queued work would silently never run.
    /// </summary>
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// The display's refresh rate, shown in the footer. It is the hard ceiling
    /// on captured framerate, and having it visible makes it obvious when the
    /// game (not RXShade) is what is limiting the number.
    /// </summary>
    private readonly int _refreshRate = NativeMethods.GetPrimaryRefreshRate();

    private CaptureTarget? _selectedTarget;
    private bool _isRunning;
    private string _statusText = "Open Roblox to begin.";
    private string _statsText = string.Empty;
    private bool _isOnboardingOpen;
    private FilterPreset _activePreset = FilterPreset.Off;

    /// <summary>
    /// True while this view model is the one applying a preset. The settings
    /// object raises Changed for a preset too, and without this guard choosing
    /// a preset would immediately clear the chip that was just clicked.
    /// </summary>
    private bool _applyingPreset;

    /// <summary>Set by any change worth persisting; flushed by <see cref="_saveTimer"/>.</summary>
    private bool _preferencesDirty;

    public RenderEngine Engine { get; }
    public FilterSettings Settings => Engine.Settings;
    public AppPreferences Preferences { get; }
    public ObservableCollection<CaptureTarget> Targets { get; } = new();

    public RelayCommand RefreshTargetsCommand { get; }
    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand DismissOnboardingCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    public string VersionText { get; }

    /// <summary>
    /// True when the dark palette is active. The title-bar switch shows the
    /// theme it would switch TO, which is the convention for a two-state toggle.
    /// </summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        private set
        {
            if (!SetProperty(ref _isDarkTheme, value)) return;
            ThemeManager.Apply(value);
            _preferencesDirty = true;
        }
    }

    private bool _isDarkTheme;

    public MainViewModel()
    {
        Engine = new RenderEngine();
        Engine.TargetClosed += OnTargetClosed;
        Engine.RenderFailed += OnRenderFailed;

        _overlay = new OverlayController(Engine);
        _overlay.TargetLost += OnTargetClosed;

        Preferences = AppPreferences.Load();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        // The assembly version stays strictly numeric (1.0.2.0); the beta label
        // is presentation only.
        string numeric = version is null ? "1.0.2" : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionText = $"{numeric} Beta";

        // Restore the user's tuned filters and the preset chip they left lit.
        // The saved preset is read first so the chip starts correct, then the
        // stored values are layered on top: a preset would otherwise overwrite
        // the hand-edits the user made after choosing it.
        Settings.RestoreState(Preferences.LoadFilterState());
        _activePreset = Preferences.ActivePreset;

        // First launch: the welcome panel is the whole point of the screen.
        _isOnboardingOpen = !Preferences.HasCompletedOnboarding;

        RefreshTargetsCommand = new RelayCommand(RefreshTargets);
        ToggleCaptureCommand = new RelayCommand(ToggleCapture, () => SelectedTarget is not null);
        ShowHelpCommand = new RelayCommand(() => IsOnboardingOpen = true);
        DismissOnboardingCommand = new RelayCommand(() =>
        {
            Preferences.MarkOnboardingComplete();
            IsOnboardingOpen = false;
        });

        // Assigning the preset applies it and clears the chip, so a single
        // command covers both "return to neutral" and "no preset is active".
        ResetFiltersCommand = new RelayCommand(() => ActivePreset = FilterPreset.Off);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);

        // Applied before the window is shown, so startup never flashes the light
        // palette first. The setter is bypassed to avoid queueing a redundant
        // save for a value that came from disk.
        _isDarkTheme = Preferences.IsDarkTheme;
        ThemeManager.Apply(_isDarkTheme);

        Settings.Changed += OnSettingsChanged;

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        // A slider drag raises Changed per pixel of travel, so writes are
        // batched instead of hitting the disk on every frame of a drag.
        _saveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Start();

        RefreshTargets();
    }

    // ---- Bindable state ------------------------------------------------

    public CaptureTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            ToggleCaptureCommand.RaiseCanExecuteChanged();

            if (!IsRunning && value is not null)
                StatusText = "Roblox found. Ready to start.";
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(StartStopLabel));
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsRunning;

    public string StartStopLabel => IsRunning ? "Stop" : "Start";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatsText
    {
        get => _statsText;
        private set => SetProperty(ref _statsText, value);
    }

    /// <summary>
    /// True while the welcome / quick-start panel is covering the UI. Opened
    /// automatically on first launch, and reachable afterwards from the
    /// title-bar help button.
    /// </summary>
    public bool IsOnboardingOpen
    {
        get => _isOnboardingOpen;
        set => SetProperty(ref _isOnboardingOpen, value);
    }

    /// <summary>
    /// Selecting a preset immediately rewrites every filter value.
    ///
    /// The chip only stays lit while the values still describe the look it
    /// names. The moment the user moves a slider or a switch the preset no
    /// longer matches what is on screen and it drops back to Off, so the UI
    /// never claims "Vivid" while showing something else.
    /// </summary>
    public FilterPreset ActivePreset
    {
        get => _activePreset;
        set
        {
            if (!SetProperty(ref _activePreset, value)) return;

            _applyingPreset = true;
            try
            {
                Settings.ApplyPreset(value);
            }
            finally
            {
                _applyingPreset = false;
            }

            _preferencesDirty = true;
        }
    }

    /// <summary>True when no filter is doing anything, which the UI states plainly.</summary>
    public bool IsPassthrough => !Settings.AnyEnabled;

    public string AdapterName => Engine.AdapterDescription;

    // ---- Actions -------------------------------------------------------

    public void RefreshTargets()
    {
        var previous = SelectedTarget;

        Targets.Clear();
        foreach (var target in WindowEnumerator.EnumerateTargets())
            Targets.Add(target);

        // Keep the user's explicit choice across the refresh if that window is
        // still open, otherwise fall back to the first Roblox window.
        SelectedTarget =
            Targets.FirstOrDefault(t => t.Handle == previous?.Handle)
            ?? Targets.FirstOrDefault();

        if (SelectedTarget is null && !IsRunning)
            StatusText = "No Roblox window found. Open Roblox, then refresh.";
    }

    private void ToggleCapture()
    {
        if (IsRunning) StopCapture();
        else StartCapture();
    }

    public void StartCapture()
    {
        var target = SelectedTarget;
        if (target is null) return;

        if (!CaptureEngine.IsSupported)
        {
            StatusText = "Windows.Graphics.Capture is unavailable on this system.";
            return;
        }

        if (!target.IsAlive)
        {
            StatusText = "That window has closed. Refresh the list.";
            RefreshTargets();
            return;
        }

        try
        {
            Engine.StartCapture(target);
            _overlay.Start(target);

            StatusText = _overlay.UsesLegacySwapEffect
                ? "Overlay active (legacy swap effect — click-through layered window)."
                : "Overlay active.";

            IsRunning = true;
        }
        catch (Exception ex)
        {
            StopCapture();
            StatusText = $"Could not start: {ex.Message}";
        }
    }

    public void StopCapture()
    {
        _overlay.Stop();
        Engine.StopCapture();

        IsRunning = false;
        StatsText = string.Empty;
        StatusText = "Stopped.";
    }

    private void OnTargetClosed()
    {
        // Raised from a WGC worker thread as well as from the tracker timer.
        // BeginInvoke, never Invoke: the render thread can be holding the
        // engine's render lock while the UI thread is waiting to acquire it,
        // and a blocking call in that direction would deadlock the pair.
        _uiDispatcher.BeginInvoke(() =>
        {
            if (!IsRunning) return;
            StopCapture();
            StatusText = "The captured window closed.";
            RefreshTargets();
        });
    }

    private void OnRenderFailed(string message)
    {
        _uiDispatcher.BeginInvoke(() =>
        {
            StopCapture();
            StatusText = message;
        });
    }

    /// <summary>
    /// A preset is only accurate while the values still match it. Any manual
    /// edit - a slider or a filter switch - means the chip has to go out.
    /// </summary>
    private void OnSettingsChanged()
    {
        _preferencesDirty = true;

        if (_applyingPreset) return;

        if (_activePreset != FilterPreset.Off)
        {
            _activePreset = FilterPreset.Off;
            OnPropertyChanged(nameof(ActivePreset));
        }

        OnPropertyChanged(nameof(IsPassthrough));
    }

    private void UpdateStats()
    {
        if (!IsRunning)
        {
            StatsText = string.Empty;
            return;
        }

        var stats = Engine.GetStats();
        if (stats.Width == 0)
        {
            StatsText = "waiting for frames…";
            return;
        }

        string gpu = stats.PassCount > 0
            ? $"{stats.GpuMilliseconds:0.0} ms gpu"
            : "passthrough";

        // Always show rendered AND delivered. When they match, the ceiling is
        // upstream (the game or the compositor) and there is nothing to tune
        // here; when they diverge, RXShade is the bottleneck. Hiding the second
        // number made that impossible to tell apart.
        string fps = $"{stats.Fps:0}/{stats.DeliveredFps:0} fps";
        string refresh = _refreshRate > 0 ? $"{_refreshRate} Hz" : string.Empty;

        StatsText = string.Join("  ·  ",
            fps,
            $"{stats.Width}×{stats.Height}",
            gpu,
            $"{stats.CpuMilliseconds:0.0} ms cpu",
            refresh);
    }

    private void OnSaveTick(object? sender, EventArgs e) => FlushPreferences();

    /// <summary>
    /// Persists settings if anything changed. Runs on a timer during use and
    /// once more on shutdown, so a slider moved a moment before exit is kept.
    /// </summary>
    public void FlushPreferences()
    {
        if (!_preferencesDirty) return;
        _preferencesDirty = false;

        Preferences.ActivePreset = _activePreset;
        Preferences.IsDarkTheme = _isDarkTheme;
        Preferences.Save(Settings);
    }

    public void Dispose()
    {
        Settings.Changed -= OnSettingsChanged;

        _statsTimer.Stop();
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTick;

        FlushPreferences();

        _overlay.Dispose();
        Engine.Dispose();
    }
}
