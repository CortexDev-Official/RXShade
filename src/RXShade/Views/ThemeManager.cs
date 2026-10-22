using System.Reflection;
using System.Windows;

namespace RXShade.Views;

/// <summary>
/// Swaps the active colour palette at runtime.
///
/// The whole interface reads its colours through DynamicResource, so replacing
/// the single dictionary that holds them repaints every control without
/// rebuilding any window. Styles, templates and layout are untouched by a theme
/// change, which is what keeps the two palettes consistent by construction
/// rather than by two parallel sets of markup.
///
/// Theme.xaml is itself a merged dictionary, so the palette is not at the top
/// level of Application.Resources: it is entry 0 of the dictionary that actually
/// defines "BgBase". That entry is replaced in place, by index, because mutating
/// the merged list of a dictionary already in the live tree is what makes
/// existing DynamicResource references re-resolve.
/// </summary>
public static class ThemeManager
{
    /// <summary>True once the dark palette is the active one.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Assembly-qualified pack Uri for a palette file.
    ///
    /// The ";component/" form is required. A bare relative Uri such as
    /// "Theme.Dark.xaml" is resolved against the *entry* assembly's resource
    /// table, which happens to be RXShade in the shipped app but is not
    /// guaranteed to be - and when it is not, the swap throws
    /// "Cannot locate resource 'theme.dark.xaml'" at startup. Naming the
    /// assembly explicitly makes the lookup independent of who is hosting us.
    /// </summary>
    private static Uri Palette(string fileName) => new(
        $"/{Assembly.GetExecutingAssembly().GetName().Name};component/{fileName}",
        UriKind.Relative);

    public static void Apply(bool dark)
    {
        IsDark = dark;

        ResourceDictionary? owner = FindPaletteOwner(
            Application.Current?.Resources, new HashSet<ResourceDictionary>());

        if (owner is null || owner.MergedDictionaries.Count == 0) return;

        owner.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = Palette(dark ? "Theme.Dark.xaml" : "Theme.Light.xaml")
        };
    }

    /// <summary>
    /// Finds the dictionary whose own keys include a palette colour. Uses a
    /// visited set because a merged dictionary can be referenced from more than
    /// one place, and a cycle would otherwise recurse forever during startup.
    /// </summary>
    private static ResourceDictionary? FindPaletteOwner(
        ResourceDictionary? root, HashSet<ResourceDictionary> visited)
    {
        if (root is null || !visited.Add(root)) return null;
        if (root.Contains("BgBase")) return root;

        foreach (ResourceDictionary merged in root.MergedDictionaries)
        {
            ResourceDictionary? found = FindPaletteOwner(merged, visited);
            if (found is not null) return found;
        }

        return null;
    }
}
