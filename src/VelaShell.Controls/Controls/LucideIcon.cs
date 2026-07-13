using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>
/// Renders a lucide icon exactly as the design uses them: a 24×24 stroke-based geometry drawn
/// with a 2px round-cap/round-join pen, uniformly scaled to the control size. Avalonia's
/// <c>PathIcon</c> fills geometry, which breaks stroke-style icon sets — this control strokes.
/// Geometry data lives in <c>Themes/Icons.axaml</c> keyed as <c>Icon.&lt;lucide-name&gt;</c>.
/// </summary>
public class LucideIcon : Control
{
    /// <summary>The lucide path geometry in its native 24×24 view box.</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    /// <summary>Stroke brush (maps to the design's icon fill token).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Foreground));

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty);
    }

    /// <summary>要绘制的 lucide 路径几何(以其原生 24×24 视图框为准)。</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>描边画刷(对应设计中的图标填充令牌)。</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Measures the control, falling back to the design's default icon size when unconstrained.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Default to the design's most common icon size when unconstrained.
        double w = double.IsFinite(Width) ? Width : 12;
        double h = double.IsFinite(Height) ? Height : 12;
        return new(w, h);
    }

    /// <summary>Draws the icon geometry with the current foreground brush.</summary>
    public override void Render(DrawingContext context)
    {
        Geometry? geometry = Data;
        IBrush? brush = Foreground;
        if (geometry is null || brush is null)
        {
            return;
        }
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // Uniform scale from the 24×24 lucide view box, centered in the bounds. The pen scales
        // with the transform, so the stroke keeps lucide's 2/24 weight ratio at any size.
        double scale = Math.Min(w, h) / 24.0;
        var offset = new Point((w - 24 * scale) / 2, (h - 24 * scale) / 2);
        var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
