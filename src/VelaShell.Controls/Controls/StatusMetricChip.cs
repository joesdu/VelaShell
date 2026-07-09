using Avalonia;
using Avalonia.Controls.Primitives;

namespace VelaShell.Controls.Controls;

public sealed class StatusMetricChip : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<StatusMetricChip, string?>(nameof(Label));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<StatusMetricChip, string?>(nameof(Value));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
