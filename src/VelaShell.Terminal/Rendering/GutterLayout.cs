namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 侧栏几何:各部件(时间戳 / 行号 / 命令标记列 / 折叠列 / 空白,按左→右顺序)的像素宽度、
/// x 偏移与命中区间。纯计算(只依赖单元格宽 + 各部件开关),与控件解耦,可单测 ——
/// 折叠点击与命令标记点击是否落在各自的列即由此判定。
/// </summary>
/// <remarks>
/// 折叠列里画两样东西:竖直分隔线(<paramref name="showGuideLine" />)与折叠方框(<paramref name="showFold" />),
/// 两个开关各自独立(#586)。只开分隔线时整列照样占位,但不响应点击 —— 只想要一条线把侧栏和正文隔开的人,
/// 选整行时不会再被折叠误伤。两者共用同一列宽,来回切换折叠时正文不会左右跳。
/// </remarks>
public readonly struct GutterLayout(
    double cellWidth,
    bool showTimestamp,
    bool showNumber,
    bool showFold,
    bool blank,
    bool showCommandMark = false,
    bool showTimestampMillis = true,
    bool showGuideLine = false)
{
    /// <summary>行号列固定 5 位(右对齐):默认 1 万行 scrollback 的最大行号约 5 位,宽度全程恒定。</summary>
    public const int NumberDigits = 5;

    /// <summary>“空白”部件:侧栏与正文间的固定间隔(px)。</summary>
    public const double BlankPixels = 5.0;

    /// <summary>时间戳列宽(px);关闭时为 0。</summary>
    public double TimeWidth { get; } = showTimestamp ? (showTimestampMillis ? 15 : 11) * cellWidth : 0; // "[HH:mm:ss.fff] " = 15 cells / "[HH:mm:ss] " = 11 cells
    /// <summary>行号列宽(px);关闭时为 0。</summary>
    public double NumberWidth { get; } = showNumber ? (NumberDigits + 1) * cellWidth : 0; // "NNNNN " = 6 cells

    /// <summary>命令标记列宽(px);没有 OSC 133 标记时为 0(整列不占地方)。</summary>
    /// <remarks>
    /// 比折叠列窄一点:它只画一个小三角,而折叠列要放得下方框标记。
    /// </remarks>
    public double CommandMarkWidth { get; } = showCommandMark ? Math.Ceiling(cellWidth * 1.2) : 0;

    /// <summary>折叠列宽(px);折叠与分隔线都关时为 0。</summary>
    /// <remarks>
    /// 两个单元格宽:方框加大后(见 <see cref="FoldBoxSize" />)左右仍各留几像素,
    /// 命中区也不再借用右侧空白(见 <see cref="IsFoldColumnHit" />),列本身得宽一点才好点。
    /// </remarks>
    public double FoldWidth { get; } = showFold || showGuideLine ? Math.Ceiling(cellWidth * 2) : 0;
    /// <summary>侧栏与正文间空白宽(px);关闭时为 0。</summary>
    public double BlankWidth { get; } = blank ? BlankPixels : 0;

    /// <summary>折叠是否开启:决定方框标记的绘制与折叠列是否响应点击。</summary>
    public bool FoldEnabled { get; } = showFold;

    /// <summary>折叠列里的竖直分隔线是否开启。</summary>
    public bool GuideLineEnabled { get; } = showGuideLine;

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
    /// 与折叠列相邻,判定必须与 <see cref="IsFoldColumnHit" /> 互斥,否则点一下会同时触发折叠与选中输出。
    /// </remarks>
    public bool IsCommandMarkHit(double x) =>
        CommandMarkWidth > 0 && x >= CommandMarkLeft && x < CommandMarkLeft + CommandMarkWidth;

    /// <summary>控件坐标 x 是否落在折叠列的可点击区间;只开分隔线、没开折叠时恒为 false。</summary>
    /// <remarks>
    /// 只认折叠列本身,<b>不</b>含右侧的空白。那段空白紧贴正文第一列,从左往右拖选整行时指针最容易
    /// 落在那里;以前它也算折叠命中,一按下去就把上方输出整段折掉(#586)。现在它就是名副其实的间隔。
    /// </remarks>
    public bool IsFoldColumnHit(double x) => FoldEnabled && x >= FoldLeft && x < FoldLeft + FoldWidth;

    /// <summary>
    /// 折叠方框边长(px):随行高缩放,夹在 9–13px,且左右至少各给折叠列留 1px;取奇数,
    /// 保证 ± 符号有精确的单像素中心。
    /// </summary>
    public int FoldBoxSize(double cellHeight)
    {
        int box = (int)Math.Clamp(Math.Floor(cellHeight * 0.65), 9, 13);
        box = Math.Min(box, (int)FoldWidth - 2);
        if (box % 2 == 0)
        {
            box--;
        }
        return Math.Max(box, 5);
    }
}
