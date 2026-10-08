using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RXShade.Views;

/// <summary>
/// Tints the background of an enabled settings row. Deliberately a whole-row
/// wash rather than a coloured strip down one edge, which is the single most
/// recognisable generated-interface tell.
///
/// The colours mirror AccentMuted / BorderSoft in Theme.xaml. Kept here rather
/// than as nine per-card DataTriggers in XAML, which would be ~80 lines of
/// markup for the same result.
/// </summary>
public sealed class ActiveBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Active = CreateFrozen(0xF2, 0xF7, 0xFE);
    private static readonly SolidColorBrush Inactive = CreateFrozen(0x00, 0x00, 0x00, 0x00);

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Active : Inactive;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Lets the two mode radio buttons share one bool: Overlay binds to it
/// directly, Preview binds to its inverse.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// Binds a group of radio buttons to a single enum property. Each chip passes
/// its own enum name as ConverterParameter and is checked when it matches.
///
/// ConvertBack returns Binding.DoNothing for the un-checking half of a radio
/// group change, so the outgoing chip does not race the incoming one and
/// overwrite the new value.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null
           && parameter is string name
           && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name && Enum.TryParse(targetType, name, out object? result))
            return result!;

        return Binding.DoNothing;
    }
}
