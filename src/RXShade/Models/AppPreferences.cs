using System.IO;

namespace RXShade.Models;

/// <summary>
/// Tiny persisted user state, stored under
/// <c>%LOCALAPPDATA%\RXShade\preferences.txt</c> as plain key=value lines.
///
/// A full settings framework would be overkill for a single boolean, and a
/// registry entry would be one more thing to clean up on uninstall. A small
/// text file next to the existing error log is the least surprising option:
/// it is visible, deletable, and removing it restores first-run behaviour.
///
/// Every operation is best-effort. A preferences file that cannot be read or
/// written must never stop the app from running.
/// </summary>
public sealed class AppPreferences
{
    private const string OnboardingKey = "onboarding.complete";

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RXShade");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "preferences.txt");

    /// <summary>False on a fresh install, true once the welcome panel is dismissed.</summary>
    public bool HasCompletedOnboarding { get; private set; }

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

                if (key.Equals(OnboardingKey, StringComparison.OrdinalIgnoreCase))
                    preferences.HasCompletedOnboarding = bool.TryParse(value, out bool done) && done;
            }
        }
        catch (Exception)
        {
            // Unreadable or malformed: fall back to first-run defaults.
        }

        return preferences;
    }

    public void MarkOnboardingComplete()
    {
        HasCompletedOnboarding = true;
        Save();
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath,
                $"# RXShade preferences{Environment.NewLine}" +
                $"{OnboardingKey}={HasCompletedOnboarding.ToString().ToLowerInvariant()}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Persistence is a convenience; never fatal.
        }
    }
}
