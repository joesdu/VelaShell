using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VelaShell.App.Converters;

/// <summary>把 "#RRGGBB" 字符串转换为画刷(设置外观页色板/ANSI 调色板)。</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
