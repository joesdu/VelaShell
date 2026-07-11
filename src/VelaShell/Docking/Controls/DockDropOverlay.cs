using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 拖拽指示覆盖层:铺满工作区、不参与命中测试。拖到组内容区时高亮目标区域
/// (中心 = 并入,半区 = 拆分方向);拖到标签条时画 2px 插入线。
/// </summary>
public sealed class DockDropOverlay : Control
{
    private Rect? _region;
    private Rect? _insertion;

    /// <summary>高亮一个放置区域(工作区坐标系)。</summary>
    public void ShowRegion(Rect region)
    {
        _region = region;
        _insertion = null;
        InvalidateVisual();
    }

    /// <summary>显示标签插入位置线(工作区坐标系)。</summary>
    public void ShowInsertion(Rect line)
    {
        _insertion = line;
        _region = null;
        InvalidateVisual();
    }

    public void Hide()
    {
        _region = null;
        _insertion = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        Color accent = this.TryFindResource("VelaAccent", ActualThemeVariant, out object? value) && value is ISolidColorBrush brush
                           ? brush.Color
                           : Colors.DodgerBlue;
        if (_region is { } region)
        {
            context.FillRectangle(new ImmutableSolidColorBrush(accent, 0.18), region);
            context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(accent), 2), region.Deflate(1));
        }
        if (_insertion is { } line)
        {
            context.FillRectangle(new ImmutableSolidColorBrush(accent), line);
        }
    }
}
