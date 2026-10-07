using System;
using System.Globalization;
using Avalonia.Data.Converters;
using RawAccelProfiles.Services;

namespace RawAccelProfiles.Converters;

public class WinPointerSpeedDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return "(Skip)";

        if (value is int speed && speed >= 1 && speed <= 20)
        {
            var mult = ProfileService.WinMultipliers[speed - 1];
            return $"{speed} ({mult:0.0###}x)";
        }

        return value?.ToString() ?? "(Skip)";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Display-only converter; selection binding stays on the original int? items.
        return value;
    }
}
