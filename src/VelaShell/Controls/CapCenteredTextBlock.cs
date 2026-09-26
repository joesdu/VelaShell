using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace VelaShell.Controls;

/// <summary>
/// 一行文字,把大写字母高度的中线放在自己布局框的正中 —— 状态栏这种一排紧挨着、各自垂直居中的
/// 文字块,靠它才落在同一条中线上。
/// </summary>
/// <remarks>
/// <para>
/// 普通 <see cref="TextBlock" /> 垂直居中的是它的行框,而行框的上下留白取这一行里<b>所有</b>字体的最大值
/// (Avalonia 12.1.3 <c>TextLineImpl</c> 逐个文字段取 ascent / descent 的最大值)。等宽字体里没有中文,
/// 中文回退到系统字体(微软雅黑 / 苹方 / Noto CJK),它们的留白比等宽字体大:于是只要一个文字块里混进中文,
/// 整行的基线就沉下去一截,连同一块里的英文数字一起,和隔壁的纯英文块对不齐(用户截图里差了约 1.5px)。
/// 设 <c>LineHeight</c> 也救不回来:基线仍按那组最大留白算。
/// </para>
/// <para>
/// 这里按字形对齐:用主字体排一个「H」量出大写字母高度,把文字平移到「基线 − 大写字母高度 / 2」正好落在
/// 布局框的中线上。英文、数字、中文共用一条基线,中文字形的中心与大写字母的中心本就接近,整排看起来就在一条线上;
/// 与字体回退到了谁无关,换平台也成立。平移走 <see cref="Visual.RenderTransform" />,不影响布局。
/// </para>
/// <para>
/// 布局框默认占满父容器给的整个高度(<see cref="Layoutable.VerticalAlignment" /> 为 Stretch),中线就是那一格的中线;
/// 只适合单行文字。
/// </para>
/// </remarks>
public sealed class CapCenteredTextBlock : TextBlock
{
    private static readonly ConcurrentDictionary<(Typeface Typeface, double Size), double> CapHeights = new();

    /// <summary>创建文字块;默认占满父容器给的高度,中线才是那一格的中线。</summary>
    public CapCenteredTextBlock() => VerticalAlignment = VerticalAlignment.Stretch;

    /// <summary>沿用 <see cref="TextBlock" /> 的样式(状态栏等处的 <c>TextBlock</c> 选择器照样命中)。</summary>
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        double offset = CenteringOffset(arranged.Height);
        if (offset != CurrentOffset)
        {
            RenderTransform = offset == 0 ? null : new TranslateTransform(0, offset);
        }
        return arranged;
    }

    /// <summary>当前生效的平移量(向下为正);没有平移时为 0。</summary>
    internal double CurrentOffset => (RenderTransform as TranslateTransform)?.Y ?? 0;

    /// <summary>把大写字母中线挪到布局框中线要平移多少(向下为正,已对齐到物理像素)。</summary>
    private double CenteringOffset(double height)
    {
        if (TextLayout.TextLines is not [TextLine line, ..] || height <= 0)
        {
            return 0;
        }
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        double capHeight = CapHeight(typeface, FontSize);
        if (capHeight <= 0)
        {
            return 0;
        }
        double scaling = LayoutHelper.GetLayoutScale(this);
        // 基线要落在「中线 + 大写字母高度的一半」处,并对齐到物理像素,字形边缘才不发虚。
        double targetBaseline = Math.Round((height / 2 + capHeight / 2) * scaling) / scaling;
        return targetBaseline - (Padding.Top + line.Baseline);
    }

    /// <summary>主字体里「H」的墨迹高度;按字体与字号缓存。</summary>
    internal static double CapHeight(Typeface typeface, double fontSize) =>
        CapHeights.GetOrAdd((typeface, fontSize), key =>
        {
            using var layout = new TextLayout("H", key.Typeface, key.Size, Brushes.Black);
            return layout.TextLines is [TextLine line, ..] ? line.Extent : 0;
        });
}
