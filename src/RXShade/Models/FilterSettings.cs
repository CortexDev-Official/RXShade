using System.Globalization;

namespace RXShade.Models;

/// <summary>Named one-click looks. Each maps to a full set of filter values.</summary>
public enum FilterPreset
{
    Off,
    Crisp,
    Vivid,
    Cinematic,
    Dreamy,
    Night,
    Mirror,
    Water,
    HDR
}

/// <summary>
/// Every user-facing filter parameter.
///
/// Slider values are stored in the 0-100 range the UI shows and normalised at
/// the point they are written into a constant buffer, so what the user sees is
/// exactly what is persisted. Reads happen on the render thread while writes
/// happen on the UI thread; the values are independent doubles, so a torn read
/// can at worst apply one stale parameter for a single frame.
///
/// WRITE ORDER MATTERS: every setter funnels through <see cref="Apply"/>, which
/// is what raises <see cref="Changed"/>. Do not assign to the backing fields
/// directly outside <see cref="RestoreState"/> and <see cref="ApplyPreset"/>,
/// or a change will go unpersisted and the preset chip will stay lit when it no
/// longer describes the live settings.
/// </summary>
public sealed class FilterSettings : ObservableObject
{
    /// <summary>
    /// Raised once per user-visible change. The view model uses it to persist
    /// settings and to drop a preset chip the moment the values no longer match
    /// the look it names.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// True while a bulk write is in progress (applying a preset or restoring
    /// saved state). Suppresses <see cref="Changed"/> so one click cannot queue
    /// forty saves.
    /// </summary>
    private bool _settling;

    private void Apply<T>(ref T field, T value, string name)
    {
        if (!SetProperty(ref field, value, name)) return;
        if (!_settling) Changed?.Invoke();
    }

    // ---- Sharpen (CAS) -------------------------------------------------
    private bool _sharpenEnabled;
    private double _sharpenIntensity = 45;

    public bool SharpenEnabled { get => _sharpenEnabled; set => Apply(ref _sharpenEnabled, value, nameof(SharpenEnabled)); }
    public double SharpenIntensity { get => _sharpenIntensity; set => Apply(ref _sharpenIntensity, value, nameof(SharpenIntensity)); }

    // ---- FXAA ----------------------------------------------------------
    private bool _fxaaEnabled;
    private double _fxaaIntensity = 75;

    public bool FxaaEnabled { get => _fxaaEnabled; set => Apply(ref _fxaaEnabled, value, nameof(FxaaEnabled)); }
    public double FxaaIntensity { get => _fxaaIntensity; set => Apply(ref _fxaaIntensity, value, nameof(FxaaIntensity)); }

    // ---- Bloom ---------------------------------------------------------
    private bool _bloomEnabled;
    private double _bloomIntensity = 35;
    private double _bloomThreshold = 65;

    public bool BloomEnabled { get => _bloomEnabled; set => Apply(ref _bloomEnabled, value, nameof(BloomEnabled)); }
    public double BloomIntensity { get => _bloomIntensity; set => Apply(ref _bloomIntensity, value, nameof(BloomIntensity)); }
    public double BloomThreshold { get => _bloomThreshold; set => Apply(ref _bloomThreshold, value, nameof(BloomThreshold)); }

    // ---- Fake reflection (stylised, not SSR) ---------------------------
    private bool _reflectionEnabled;
    private double _reflectionIntensity = 55;

    // Where the reflective surface starts, as a percentage down the screen.
    // This MUST line up with where objects meet the floor in the game, or the
    // effect mirrors floor onto floor and turns into a grey smear.
    private double _reflectionHorizon = 55;
    private double _reflectionWave = 35;

    // How much detail survives in the mirrored reflection. High = polished
    // marble, low = rough/wet concrete.
    private double _reflectionSharpness = 55;

    // Specular highlight boost applied to the objects themselves, which is
    // what makes them read as shiny rather than merely lit.
    private double _reflectionShine = 55;

    // Screen-wide glossy sheen: streaks light sources across walls, ceilings
    // and the floor. This is the component that makes a whole room look wet,
    // rather than just putting a mirror on the ground.
    private double _reflectionSheen = 60;

    public bool ReflectionEnabled { get => _reflectionEnabled; set => Apply(ref _reflectionEnabled, value, nameof(ReflectionEnabled)); }
    public double ReflectionIntensity { get => _reflectionIntensity; set => Apply(ref _reflectionIntensity, value, nameof(ReflectionIntensity)); }
    public double ReflectionSheen { get => _reflectionSheen; set => Apply(ref _reflectionSheen, value, nameof(ReflectionSheen)); }
    public double ReflectionHorizon { get => _reflectionHorizon; set => Apply(ref _reflectionHorizon, value, nameof(ReflectionHorizon)); }
    public double ReflectionWave { get => _reflectionWave; set => Apply(ref _reflectionWave, value, nameof(ReflectionWave)); }
    public double ReflectionSharpness { get => _reflectionSharpness; set => Apply(ref _reflectionSharpness, value, nameof(ReflectionSharpness)); }
    public double ReflectionShine { get => _reflectionShine; set => Apply(ref _reflectionShine, value, nameof(ReflectionShine)); }

    // ---- Focus (radial fake depth of field) ----------------------------
    private bool _focusEnabled;
    private double _focusStrength = 50;
    private double _focusRadius = 45;

    public bool FocusEnabled { get => _focusEnabled; set => Apply(ref _focusEnabled, value, nameof(FocusEnabled)); }
    public double FocusStrength { get => _focusStrength; set => Apply(ref _focusStrength, value, nameof(FocusStrength)); }
    public double FocusRadius { get => _focusRadius; set => Apply(ref _focusRadius, value, nameof(FocusRadius)); }

    // ---- HDR tone mapping ----------------------------------------------
    private bool _hdrEnabled;
    private double _hdrExposure = 50;   // 50 == neutral
    private double _hdrPunch = 45;      // local contrast
    private double _hdrHighlights = 40; // highlight recovery
    private double _hdrShadows = 35;    // shadow lift
    private double _hdrVibrance = 45;

    public bool HdrEnabled { get => _hdrEnabled; set => Apply(ref _hdrEnabled, value, nameof(HdrEnabled)); }
    public double HdrExposure { get => _hdrExposure; set => Apply(ref _hdrExposure, value, nameof(HdrExposure)); }
    public double HdrPunch { get => _hdrPunch; set => Apply(ref _hdrPunch, value, nameof(HdrPunch)); }
    public double HdrHighlights { get => _hdrHighlights; set => Apply(ref _hdrHighlights, value, nameof(HdrHighlights)); }
    public double HdrShadows { get => _hdrShadows; set => Apply(ref _hdrShadows, value, nameof(HdrShadows)); }
    public double HdrVibrance { get => _hdrVibrance; set => Apply(ref _hdrVibrance, value, nameof(HdrVibrance)); }

    // ---- Colour grading ------------------------------------------------
    private bool _colorGradeEnabled;
    private double _saturation = 50;   // 50 == neutral
    private double _contrast = 50;     // 50 == neutral
    private double _temperature = 50;  // 50 == neutral
    private double _vignette;

    public bool ColorGradeEnabled { get => _colorGradeEnabled; set => Apply(ref _colorGradeEnabled, value, nameof(ColorGradeEnabled)); }
    public double Saturation { get => _saturation; set => Apply(ref _saturation, value, nameof(Saturation)); }
    public double Contrast { get => _contrast; set => Apply(ref _contrast, value, nameof(Contrast)); }
    public double Temperature { get => _temperature; set => Apply(ref _temperature, value, nameof(Temperature)); }
    public double Vignette { get => _vignette; set => Apply(ref _vignette, value, nameof(Vignette)); }

    // ---- Chromatic aberration ------------------------------------------
    private bool _chromaticEnabled;
    private double _chromaticIntensity = 25;

    public bool ChromaticEnabled { get => _chromaticEnabled; set => Apply(ref _chromaticEnabled, value, nameof(ChromaticEnabled)); }
    public double ChromaticIntensity { get => _chromaticIntensity; set => Apply(ref _chromaticIntensity, value, nameof(ChromaticIntensity)); }

    // ---- Film grain ----------------------------------------------------
    private bool _grainEnabled;
    private double _grainIntensity = 30;
    private double _grainCoarseness = 35;

    public bool GrainEnabled { get => _grainEnabled; set => Apply(ref _grainEnabled, value, nameof(GrainEnabled)); }
    public double GrainIntensity { get => _grainIntensity; set => Apply(ref _grainIntensity, value, nameof(GrainIntensity)); }
    public double GrainCoarseness { get => _grainCoarseness; set => Apply(ref _grainCoarseness, value, nameof(GrainCoarseness)); }

    /// <summary>
    /// When false the pipeline short-circuits to a single blit, which is the
    /// true zero-cost passthrough path.
    /// </summary>
    public bool AnyEnabled =>
        SharpenEnabled || FxaaEnabled || BloomEnabled || ReflectionEnabled ||
        FocusEnabled || HdrEnabled || ColorGradeEnabled || ChromaticEnabled || GrainEnabled;

    /// <summary>
    /// Kept as a guard so a future filter cannot be added to the UI and then
    /// silently dropped by the pipeline's passthrough short-circuit.
    /// </summary>
    public static bool AnyEnabledIn(in FilterSnapshot snapshot) => snapshot.AnyEnabled;

    public void ResetToDefaults() => ApplyPreset(FilterPreset.Off);

    /// <summary>
    /// Applies a complete look in one go.
    ///
    /// Every preset writes EVERY field, including the disables. A preset that
    /// only set the values it cared about would silently inherit whatever the
    /// previous preset left switched on, and the same button would then give
    /// different results depending on click order.
    /// </summary>
    public void ApplyPreset(FilterPreset preset)
    {
        _settling = true;
        try
        {
            ApplyPresetCore(preset);
        }
        finally
        {
            _settling = false;
        }

        Changed?.Invoke();
    }

    private void ApplyPresetCore(FilterPreset preset)
    {
        // Neutral baseline first.
        SharpenEnabled = false; SharpenIntensity = 45;
        FxaaEnabled = false; FxaaIntensity = 75;
        BloomEnabled = false; BloomIntensity = 35; BloomThreshold = 65;
        ReflectionEnabled = false; ReflectionIntensity = 55; ReflectionSheen = 60;
        ReflectionHorizon = 55; ReflectionWave = 35;
        ReflectionSharpness = 55; ReflectionShine = 55;
        FocusEnabled = false; FocusStrength = 50; FocusRadius = 45;
        HdrEnabled = false; HdrExposure = 50; HdrPunch = 45;
        HdrHighlights = 40; HdrShadows = 35; HdrVibrance = 45;
        ColorGradeEnabled = false; Saturation = 50; Contrast = 50; Temperature = 50; Vignette = 0;
        ChromaticEnabled = false; ChromaticIntensity = 25;
        GrainEnabled = false; GrainIntensity = 30; GrainCoarseness = 35;

        switch (preset)
        {
            case FilterPreset.Off:
                break;

            // Clean and legible. Aimed at actually playing: no colour shift, no
            // blur, nothing that could hide a detail you need to see.
            case FilterPreset.Crisp:
                FxaaEnabled = true; FxaaIntensity = 60;
                SharpenEnabled = true; SharpenIntensity = 68;
                ColorGradeEnabled = true; Saturation = 56; Contrast = 55;
                break;

            // Punchy and saturated - the "screenshot" look.
            case FilterPreset.Vivid:
                SharpenEnabled = true; SharpenIntensity = 42;
                BloomEnabled = true; BloomIntensity = 32; BloomThreshold = 70;
                ColorGradeEnabled = true; Saturation = 74; Contrast = 60;
                Temperature = 55; Vignette = 14;
                break;

            // Film emulation: desaturated, contrasty, soft edges, grain.
            case FilterPreset.Cinematic:
                FxaaEnabled = true; FxaaIntensity = 72;
                BloomEnabled = true; BloomIntensity = 26; BloomThreshold = 68;
                FocusEnabled = true; FocusStrength = 46; FocusRadius = 44;
                ColorGradeEnabled = true; Saturation = 43; Contrast = 63;
                Temperature = 44; Vignette = 38;
                ChromaticEnabled = true; ChromaticIntensity = 18;
                GrainEnabled = true; GrainIntensity = 32; GrainCoarseness = 42;
                break;

            // Heavy glow and a shallow sharp centre.
            case FilterPreset.Dreamy:
                BloomEnabled = true; BloomIntensity = 72; BloomThreshold = 44;
                FocusEnabled = true; FocusStrength = 64; FocusRadius = 30;
                ColorGradeEnabled = true; Saturation = 62; Contrast = 46;
                Temperature = 60; Vignette = 26;
                ChromaticEnabled = true; ChromaticIntensity = 26;
                break;

            // For dark games. Contrast below 50 pulls values towards mid-grey,
            // which lifts crushed shadows instead of just raising brightness.
            case FilterPreset.Night:
                FxaaEnabled = true; FxaaIntensity = 60;
                SharpenEnabled = true; SharpenIntensity = 52;
                BloomEnabled = true; BloomIntensity = 24; BloomThreshold = 76;
                ColorGradeEnabled = true; Saturation = 46; Contrast = 41;
                Temperature = 43; Vignette = 18;
                break;

            // A polished floor: a pure crisp mirror with no sheen and no ripple.
            // Values dialled in by hand in-game rather than derived from the
            // synthetic test scene, which is what actually reads correctly on
            // Doors' hard interiors.
            case FilterPreset.Mirror:
                FxaaEnabled = true; FxaaIntensity = 65;

                ReflectionEnabled = true;
                ReflectionSheen = 0;          // mirror only; streaks off
                ReflectionIntensity = 100;    // mirrored band at full
                ReflectionHorizon = 55;
                ReflectionShine = 65;
                ReflectionWave = 0;           // dead flat surface
                ReflectionSharpness = 100;    // fully crisp reflection

                BloomEnabled = true; BloomIntensity = 30; BloomThreshold = 40;

                HdrEnabled = true;
                HdrExposure = 26;
                HdrPunch = 0;
                HdrHighlights = 19;
                HdrShadows = 0;
                HdrVibrance = 37;
                break;

            // The same mirror, but moving and softened: ripple on the reflection
            // and sheen across the surfaces, which is what separates standing
            // water from polished stone.
            case FilterPreset.Water:
                FxaaEnabled = true; FxaaIntensity = 65;

                ReflectionEnabled = true;
                ReflectionSheen = 55;         // light streaked across surfaces
                ReflectionIntensity = 85;
                ReflectionHorizon = 55;
                ReflectionShine = 60;
                ReflectionWave = 58;          // visible movement in the reflection
                ReflectionSharpness = 38;     // softened, not a hard mirror

                BloomEnabled = true; BloomIntensity = 34; BloomThreshold = 44;

                ColorGradeEnabled = true; Saturation = 58; Contrast = 52;
                Temperature = 43;             // cool cast, as water tends to read
                Vignette = 16;
                break;

            // Tone-mapped, punchy and deep. The closest thing here to what a
            // shader mod's "HDR" preset looks like.
            case FilterPreset.HDR:
                FxaaEnabled = true; FxaaIntensity = 65;
                SharpenEnabled = true; SharpenIntensity = 38;
                BloomEnabled = true; BloomIntensity = 34; BloomThreshold = 68;
                HdrEnabled = true; HdrExposure = 54; HdrPunch = 60;
                HdrHighlights = 52; HdrShadows = 42; HdrVibrance = 58;
                break;
        }
    }

    /// <summary>Snapshot taken once per frame so a slider drag cannot change values mid-pipeline.</summary>
    public FilterSnapshot Snapshot() => new()
    {
        SharpenEnabled = SharpenEnabled,
        SharpenIntensity = (float)(SharpenIntensity / 100.0),
        FxaaEnabled = FxaaEnabled,
        FxaaIntensity = (float)(FxaaIntensity / 100.0),
        BloomEnabled = BloomEnabled,
        BloomIntensity = (float)(BloomIntensity / 100.0),
        BloomThreshold = (float)(BloomThreshold / 100.0),
        ReflectionEnabled = ReflectionEnabled,
        ReflectionIntensity = (float)(ReflectionIntensity / 100.0),
        ReflectionSheen = (float)(ReflectionSheen / 100.0),
        ReflectionHorizon = (float)(ReflectionHorizon / 100.0),
        ReflectionWave = (float)(ReflectionWave / 100.0),
        ReflectionSharpness = (float)(ReflectionSharpness / 100.0),
        ReflectionShine = (float)(ReflectionShine / 100.0),
        FocusEnabled = FocusEnabled,
        FocusStrength = (float)(FocusStrength / 100.0),
        FocusRadius = (float)(FocusRadius / 100.0),
        HdrEnabled = HdrEnabled,
        HdrExposure = (float)(HdrExposure / 100.0),
        HdrPunch = (float)(HdrPunch / 100.0),
        HdrHighlights = (float)(HdrHighlights / 100.0),
        HdrShadows = (float)(HdrShadows / 100.0),
        HdrVibrance = (float)(HdrVibrance / 100.0),
        ColorGradeEnabled = ColorGradeEnabled,
        Saturation = (float)(Saturation / 100.0),
        Contrast = (float)(Contrast / 100.0),
        Temperature = (float)(Temperature / 100.0),
        Vignette = (float)(Vignette / 100.0),
        ChromaticEnabled = ChromaticEnabled,
        ChromaticIntensity = (float)(ChromaticIntensity / 100.0),
        GrainEnabled = GrainEnabled,
        GrainIntensity = (float)(GrainIntensity / 100.0),
        GrainCoarseness = (float)(GrainCoarseness / 100.0)
    };

    // ---- Persistence ---------------------------------------------------
    //
    // Written as flat key=value pairs so the preferences file stays readable
    // and hand-editable, matching the rest of AppPreferences. Values are stored
    // in the same 0-100 range the UI shows.

    private static readonly string[] ToggleNames =
    {
        nameof(SharpenEnabled), nameof(FxaaEnabled), nameof(BloomEnabled),
        nameof(ReflectionEnabled), nameof(FocusEnabled), nameof(HdrEnabled),
        nameof(ColorGradeEnabled), nameof(ChromaticEnabled), nameof(GrainEnabled)
    };

    private static readonly string[] ValueNames =
    {
        nameof(SharpenIntensity), nameof(FxaaIntensity),
        nameof(BloomIntensity), nameof(BloomThreshold),
        nameof(ReflectionIntensity), nameof(ReflectionSheen), nameof(ReflectionHorizon),
        nameof(ReflectionWave), nameof(ReflectionSharpness), nameof(ReflectionShine),
        nameof(FocusStrength), nameof(FocusRadius),
        nameof(HdrExposure), nameof(HdrPunch), nameof(HdrHighlights), nameof(HdrShadows), nameof(HdrVibrance),
        nameof(Saturation), nameof(Contrast), nameof(Temperature), nameof(Vignette),
        nameof(ChromaticIntensity), nameof(GrainIntensity), nameof(GrainCoarseness)
    };

    public Dictionary<string, string> SaveState()
    {
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in ToggleNames)
            state[name] = ((bool)GetType().GetProperty(name)!.GetValue(this)!).ToString();

        foreach (string name in ValueNames)
        {
            double value = (double)GetType().GetProperty(name)!.GetValue(this)!;
            state[name] = value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return state;
    }

    /// <summary>
    /// Restores a previously saved state. Unknown or unparsable keys are
    /// ignored individually, so a file written by an older build still restores
    /// whatever it does contain instead of being discarded wholesale.
    /// </summary>
    public void RestoreState(IReadOnlyDictionary<string, string> state)
    {
        _settling = true;
        try
        {
            foreach (string name in ToggleNames)
            {
                if (state.TryGetValue(name, out string? raw) && bool.TryParse(raw, out bool flag))
                    GetType().GetProperty(name)!.SetValue(this, flag);
            }

            foreach (string name in ValueNames)
            {
                if (!state.TryGetValue(name, out string? raw)) continue;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;

                GetType().GetProperty(name)!.SetValue(this, Math.Clamp(value, 0, 100));
            }
        }
        finally
        {
            _settling = false;
        }
    }
}

/// <summary>Immutable per-frame copy of <see cref="FilterSettings"/>, all values normalised to 0..1.</summary>
public readonly record struct FilterSnapshot
{
    public bool SharpenEnabled { get; init; }
    public float SharpenIntensity { get; init; }

    public bool FxaaEnabled { get; init; }
    public float FxaaIntensity { get; init; }

    public bool BloomEnabled { get; init; }
    public float BloomIntensity { get; init; }
    public float BloomThreshold { get; init; }

    public bool ReflectionEnabled { get; init; }
    public float ReflectionIntensity { get; init; }
    public float ReflectionSheen { get; init; }
    public float ReflectionHorizon { get; init; }
    public float ReflectionWave { get; init; }
    public float ReflectionSharpness { get; init; }
    public float ReflectionShine { get; init; }

    public bool FocusEnabled { get; init; }
    public float FocusStrength { get; init; }
    public float FocusRadius { get; init; }

    public bool HdrEnabled { get; init; }
    public float HdrExposure { get; init; }
    public float HdrPunch { get; init; }
    public float HdrHighlights { get; init; }
    public float HdrShadows { get; init; }
    public float HdrVibrance { get; init; }

    public bool ColorGradeEnabled { get; init; }
    public float Saturation { get; init; }
    public float Contrast { get; init; }
    public float Temperature { get; init; }
    public float Vignette { get; init; }

    public bool ChromaticEnabled { get; init; }
    public float ChromaticIntensity { get; init; }

    public bool GrainEnabled { get; init; }
    public float GrainIntensity { get; init; }
    public float GrainCoarseness { get; init; }

    public bool AnyEnabled =>
        SharpenEnabled || FxaaEnabled || BloomEnabled || ReflectionEnabled ||
        FocusEnabled || HdrEnabled || ColorGradeEnabled || ChromaticEnabled || GrainEnabled;
}
