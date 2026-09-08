namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 侧栏几何:各部件(时间戳 / 行号 / 命令标记列 / 折叠列 / 空白,按左→右顺序)的像素宽度、
/// x 偏移与命中区间。纯计算(只依赖单元格宽 + 五个开关),与控件解耦,可单测 ——
/// 折叠点击与命令标记点击是否落在各自的列即由此判定。
/// </summary>
/// <remarks>按单元格宽与时间戳/行号/命令标记/折叠/空白五个开关计算各列的像素宽度。</remarks>
public readonly struct GutterLayout(
    double cellWidth,
    bool showTimestamp,
    bool showNumber,
    bool showFold,
    bool blank,
    bool showCommandMark = false)
{
    /// <summary>行号列固定 5 位(右对齐):默认 1 万行 scrollback 的最大行号约 5 位,宽度全程恒定。</summary>
    public const int NumberDigits = 5;

    /// <summary>“空白”部件:侧栏与正文间的固定间隔(px)。</summary>
    public const double BlankPixels = 5.0;

    /// <summary>时间戳列宽(px);关闭时为 0。</summary>
    public double TimeWidth { get; } = showTimestamp ? 11 * cellWidth : 0;                // "[HH:mm:ss] " = 11 cells
    /// <summary>行号列宽(px);关闭时为 0。</summary>
    public double NumberWidth { get; } = showNumber ? (NumberDigits + 1) * cellWidth : 0; // "NNNNN " = 6 cells

    /// <summary>命令标记列宽(px);没有 OSC 133 标记时为 0(整列不占地方)。</summary>
    /// <remarks>
    /// 比折叠列窄一点:它只画一个小三角,而折叠列要放得下方框标记。
    /// </remarks>
    public double CommandMarkWidth { get; } = showCommandMark ? Math.Ceiling(cellWidth * 1.2) : 0;

    /// <summary>折叠列宽(px);关闭时为 0。</summary>
    public double FoldWidth { get; } = showFold ? Math.Ceiling(cellWidth * 1.6) : 0;
    /// <summary>侧栏与正文间空白宽(px);关闭时为 0。</summary>
    public double BlankWidth { get; } = blank ? BlankPixels : 0;

    /// <summary>行号列左边缘 x。</summary>
    public double NumberLeft => TimeWidth;

    /// <summary>命令标记列左边缘 x。</summary>
    public double CommandMarkLeft => TimeWidth + NumberWidth;

    /// <summary>折叠列左边缘 x。</summary>
    public double FoldLeft => TimeWidth + NumberWidth + CommandMarkWidth;

    /// <summary>侧栏总宽(全部部件关时为 0)。</summary>
    public double TotalWidth => TimeWidth + NumberWidth + CommandMarkWidth + FoldWidth + BlankWidth;

    /// <summary>是否有任一部件开启(需绘制侧栏)。</summary>
    public bool Enabled => TotalWidth > 0;

    /// <summary>控件坐标 x 是否落在侧栏区域内。</summary>
    public bool ContainsX(double x) => Enabled && x < TotalWidth;

    /// <summary>控件坐标 x 是否落在命令标记列。</summary>
    /// <remarks>
    /// 与 <see cref="IsFoldColumnHit" /> 不同,这里<b>不</b>把右侧空白算进命中区 ——
    /// 那段空白紧挨着折叠列,归折叠列(它更需要放宽命中,方框标记本身就小)。
    /// 两列相邻,判定必须互斥,否则点一下会同时触发折叠与选中输出。
    /// </remarks>
    public bool IsCommandMarkHit(double x) =>
        CommandMarkWidth > 0 && x >= CommandMarkLeft && x < CommandMarkLeft + CommandMarkWidth;

    /// <summary>控件坐标 x 是否落在折叠列的可点击区间(折叠列 + 其右侧空白,便于命中)。</summary>
    public bool IsFoldColumnHit(double x) => FoldWidth > 0 && x >= FoldLeft && x < TotalWidth;
}
