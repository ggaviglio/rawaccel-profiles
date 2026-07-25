using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MouseApp.Converters;

public class DpiDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && int.TryParse(s, out int dpi) && dpi > 0)
        {
            int percent = (int)Math.Round(dpi / 96.0 * 100);
            return $"{percent}% ({dpi})";
        }

        return value?.ToString() ?? "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Display-only converter; Text binding stays on the raw number string.
        return value;
    }
}
