using System.Globalization;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>True when the bound int equals the converter parameter (drives settings page visibility).</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    /// <summary>Shared singleton for use directly from XAML.</summary>
    public static readonly IntEqualsConverter Instance = new();

    /// <summary>Returns true when the bound int equals the int parsed from the string parameter.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && parameter is string s && int.TryParse(s, out int p) && i == p;

    /// <summary>Reverse conversion is unsupported and always throws <see cref="NotSupportedException" />.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
