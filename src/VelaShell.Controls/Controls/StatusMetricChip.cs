using Avalonia;
using Avalonia.Controls.Primitives;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 状态栏上的指标胶囊控件:成对呈现一个说明标签(<see cref="Label" />)
/// 与其对应的数值(<see cref="Value" />)。
/// </summary>
public sealed class StatusMetricChip : TemplatedControl
{
    /// <summary>标识 <see cref="Label" /> 样式化属性。</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<StatusMetricChip, string?>(nameof(Label));

    /// <summary>标识 <see cref="Value" /> 样式化属性。</summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<StatusMetricChip, string?>(nameof(Value));

    /// <summary>指标的说明标签文本。</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>指标的数值文本。</summary>
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
