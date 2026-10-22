using System.Globalization;
using System.Windows.Data;

namespace RXShade.Views;

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
/// Picks one of two strings from a bool: true gives the first parameter, false
/// gives the second. Keeps short label flips (Live / No filters) out of the
/// view model, where they would need an extra property and a change notification
/// for every flag they depend on.
/// </summary>
public sealed class BooleanChoiceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string[] parts = (parameter as string ?? string.Empty).Split('|');
        if (parts.Length != 2) return string.Empty;
        return value is true ? parts[0] : parts[1];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
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
