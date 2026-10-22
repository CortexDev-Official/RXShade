using System.IO;
using System.Reflection;

namespace RXShade.Models;

/// <summary>
/// Persisted user state, stored under
/// <c>%LOCALAPPDATA%\RXShade\preferences.txt</c> as plain key=value lines.
///
/// A full settings framework would be overkill here, and a registry entry would
/// be one more thing to clean up on uninstall. A small text file next to the
/// existing error log is the least surprising option: it is visible,
/// hand-editable, and deleting it restores first-run behaviour.
///
/// Filter values live here too. Before 1.0.2 they were not written at all, so
/// every restart silently threw away whatever the user had tuned.
///
/// Every operation is best-effort. A preferences file that cannot be read or
/// written must never stop the app from running.
/// </summary>
public sealed class AppPreferences
{
    private const string OnboardingKey = "onboarding.complete";
    private const string PresetKey = "preset.active";
    private const string ThemeKey = "theme.dark";
    private const string FilterPrefix = "filter.";

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RXShade");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "preferences.txt");

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False on a fresh install, true once the welcome panel is dismissed.</summary>
    public bool HasCompletedOnboarding { get; private set; }

    /// <summary>
    /// The preset the user last chose, or <see cref="FilterPreset.Off"/> when
    /// they never chose one or have since edited the values by hand.
    /// </summary>
    public FilterPreset ActivePreset { get; set; } = FilterPreset.Off;

    /// <summary>
    /// Dark palette. Read before the first window is shown so the app never
    /// flashes light-then-dark on startup.
    /// </summary>
    public bool IsDarkTheme { get; set; }

    public static AppPreferences Load()
    {
        var preferences = new AppPreferences();

        try
        {
            if (!File.Exists(FilePath)) return preferences;

            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                // First occurrence wins, so a hand-added duplicate cannot
                // override what the app itself wrote.
                preferences._values.TryAdd(key, value);
            }

            preferences.HasCompletedOnboarding =
                preferences._values.TryGetValue(OnboardingKey, out string? done) &&
                bool.TryParse(done, out bool parsed) && parsed;

            if (preferences._values.TryGetValue(PresetKey, out string? preset) &&
                Enum.TryParse(preset, ignoreCase: true, out FilterPreset parsedPreset))
            {
                preferences.ActivePreset = parsedPreset;
            }

            if (preferences._values.TryGetValue(ThemeKey, out string? theme) &&
                bool.TryParse(theme, out bool dark))
            {
                preferences.IsDarkTheme = dark;
            }
        }
        catch (Exception)
        {
            // Unreadable or malformed: fall back to first-run defaults. Values
            // already parsed are kept rather than discarded.
        }

        return preferences;
    }

    /// <summary>
    /// Version of the build that wrote the file. Written for support: a user
    /// reporting a problem can be asked to read one line instead of guessing
    /// which release they are on.
    /// </summary>
    private static string WriterVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "unknown";

    /// <summary>Reads the saved filter values, if any were written by a previous run.</summary>
    public Dictionary<string, string> LoadFilterState()
    {
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _values)
        {
            if (pair.Key.StartsWith(FilterPrefix, StringComparison.OrdinalIgnoreCase))
                state[pair.Key[FilterPrefix.Length..]] = pair.Value;
        }

        return state;
    }

    public void MarkOnboardingComplete()
    {
        HasCompletedOnboarding = true;
        Save();
    }

    /// <summary>
    /// Writes the current state out. The whole file is rewritten each time, so
    /// a value that is no longer produced simply disappears.
    /// </summary>
    public void Save(FilterSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);

            var lines = new List<string>
            {
                $"# RXShade preferences (written by {WriterVersion})",
                $"{OnboardingKey}={HasCompletedOnboarding.ToString().ToLowerInvariant()}",
                $"{ThemeKey}={IsDarkTheme.ToString().ToLowerInvariant()}",
                $"{PresetKey}={ActivePreset}"
            };

            foreach (var pair in settings.SaveState())
                lines.Add($"{FilterPrefix}{pair.Key}={pair.Value}");

            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception)
        {
            // Persistence is a convenience; never fatal.
        }
    }

    /// <summary>
    /// Overload for the paths that only touch onboarding state, which must not
    /// clobber filter values that have not been loaded into a settings object.
    /// </summary>
    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);

            var lines = new List<string>
            {
                $"# RXShade preferences (written by {WriterVersion})",
                $"{OnboardingKey}={HasCompletedOnboarding.ToString().ToLowerInvariant()}",
                $"{ThemeKey}={IsDarkTheme.ToString().ToLowerInvariant()}",
                $"{PresetKey}={ActivePreset}"
            };

            foreach (var pair in _values)
            {
                if (pair.Key.StartsWith(FilterPrefix, StringComparison.OrdinalIgnoreCase))
                    lines.Add($"{pair.Key}={pair.Value}");
            }

            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception)
        {
            // Persistence is a convenience; never fatal.
        }
    }
}
