using System.Globalization;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>把分组深度转换为树行左内边距。</summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    /// <summary>供 XAML 绑定使用的共享实例。</summary>
    public static readonly DepthToIndentConverter Instance = new();

    /// <summary>把分组深度转换为分组或会话行的左内边距。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is string { } mode && string.Equals(mode, "session", StringComparison.OrdinalIgnoreCase))
        {
            double left = value is int level and > 0 ? 36 + (level - 1) * 16 : 12;
            return new Avalonia.Thickness(left, 0, 20, 0);
        }
        int depth = value is int groupDepth ? Math.Max(0, groupDepth) : 0;
        double step = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 16;
        return new Avalonia.Thickness(11 + depth * step, 0, 0, 0);
    }

    /// <summary>该转换只用于显示，不支持反向转换。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
