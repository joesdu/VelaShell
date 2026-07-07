using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PulseTerm.App.Converters;

/// <summary>True when the bound int equals the converter parameter (drives settings page visibility).</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && parameter is string s && int.TryParse(s, out var p) && i == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
