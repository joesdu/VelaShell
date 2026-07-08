using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PulseTerm.App.Converters;

/// <summary>标签条溢出判定(设计 nunbT 的 Tab Overflow Controls 仅在放不下时出现):
/// 输入 [Extent.Width, Viewport.Width],内容比视口宽(留 0.5px 容差)时为 true。</summary>
public sealed class WidthOverflowConverter : IMultiValueConverter
{
    public static readonly WidthOverflowConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is double extent && values[1] is double viewport)
            return extent > viewport + 0.5;

        return false;
    }
}
