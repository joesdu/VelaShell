using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>UTC 时间 → 本地时间文本(云同步版本历史)。</summary>
public sealed class UtcToLocalTextConverter : IValueConverter
{
    public static readonly UtcToLocalTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime utc && utc != DateTime.MinValue
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "";

    // 单向展示转换器:Run.Text 等绑定在控件卸载等时机会尝试回写,
    // 返回 DoNothing 静默忽略,不能抛异常(会冒泡成运行时错误)。
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>长版本号(Gist revision SHA)截为前 10 位展示。</summary>
public sealed class ShortShaConverter : IValueConverter
{
    public static readonly ShortShaConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string sha && sha.Length > 10 ? sha[..10] : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
