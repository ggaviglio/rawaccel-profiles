using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace RawAccelProfiles.Converters;

/// <summary>Display converter for nullable bool EPP selection: null = Skip, true = Enable, false = Disable.</summary>
public class NullableBoolDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null  => "(Skip)",
            true  => "Enable",
            false => "Disable",
            _     => value.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value; // Selection binding stays on the original bool? items.
}
