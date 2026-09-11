using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 以设计稿中的方式渲染一个 lucide 图标:基于 24×24 描边几何路径绘制,
/// 使用 2px 圆头/圆角画笔,并等比缩放到控件尺寸。Avalonia 的
/// <c>PathIcon</c> 会填充几何路径,这会破坏描边风格的图标集 —— 本控件默认改用描边方式。
/// 几何数据存放在 <c>Themes/Icons.axaml</c> 中,以 <c>Icon.&lt;lucide-name&gt;</c> 为键。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Fill" /> 与 <see cref="ViewBoxSize" /> 两个口子是为**插件带来的品牌图标**开的:
/// 那些 logo 通常是实心填充、而且视图框不是 24 —— 用描边去画一个实心 logo,得到的是它的
/// 轮廓线,一团糊。宿主自己的图标集一律不碰这两个属性,保持 lucide 的描边语言。
/// </para>
/// </remarks>
public class LucideIcon : Control
{
    /// <summary>lucide 路径几何(以其原生 24×24 视图框为准)。</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    /// <summary>描边画刷(对应设计中的图标填充令牌)。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Foreground));

    /// <summary>填充画刷;非空时改为填充而不描边(实心品牌图标用)。</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Fill));

    /// <summary>几何数据的原生视图框边长;lucide 恒为 24。</summary>
    public static readonly StyledProperty<double> ViewBoxSizeProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(ViewBoxSize), 24d);

    static LucideIcon() =>
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty, FillProperty, ViewBoxSizeProperty);

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

    /// <summary>
    /// 填充画刷。**置了它就不再描边** —— 实心图标(插件的品牌 logo)与描边图标是两种画法,
    /// 同时上会让实心块再套一圈 2px 轮廓,比只描边更糟。
    /// </summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>
    /// 几何数据的原生视图框边长,默认 24(lucide)。品牌 logo 的视图框往往是 1024 之类,
    /// 按 24 缩放会把它放大四十多倍 —— 那种"图标没显示"的现象,原因通常在这里。
    /// <para>非正数按 24 处理:一个写错的值不该让整个标签栏的图标消失。</para>
    /// </summary>
    public double ViewBoxSize
    {
        get => GetValue(ViewBoxSizeProperty);
        set => SetValue(ViewBoxSizeProperty, value);
    }

    /// <summary>测量控件尺寸,在无约束时回退到设计稿的默认图标大小。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        // 无约束时默认使用设计稿中最常见的图标尺寸。
        double w = double.IsFinite(Width) ? Width : 12;
        double h = double.IsFinite(Height) ? Height : 12;
        return new(w, h);
    }

    private readonly PenCache _pens = new();

    /// <summary>使用当前前景画刷绘制图标几何(置了 <see cref="Fill" /> 则改为填充)。</summary>
    public override void Render(DrawingContext context)
    {
        Geometry? geometry = Data;
        IBrush? fill = Fill;
        IBrush? brush = fill ?? Foreground;
        if (geometry is null || brush is null)
        {
            return;
        }
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // 从原生视图框等比缩放,并在边界内居中。画笔随变换一起缩放,
        // 因此描边在任何尺寸下都保持 lucide 2/24 的粗细比例。
        double box = ViewBoxSize is > 0 and < double.PositiveInfinity ? ViewBoxSize : 24d;
        double scale = Math.Min(w, h) / box;
        var offset = new Point((w - box * scale) / 2, (h - box * scale) / 2);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (fill is not null)
            {
                // 实心图标:只填充。再描一圈边等于给每个色块套上 2px 轮廓,比不描更糟。
                context.DrawGeometry(fill, null, geometry);
                return;
            }
            // 画笔按 (颜色, 线宽, 端点, 拐角) 复用:图标在工具栏/会话树/文件浏览器里成百上千个,
            // 每个每次重绘都新建一支可变 Pen(框架还要再快照一次)纯属白扔。
            IPen pen = _pens.Get(brush, 2, PenLineCap.Round, PenLineJoin.Round);
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
