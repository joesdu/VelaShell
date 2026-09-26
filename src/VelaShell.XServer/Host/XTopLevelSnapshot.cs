// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 一个顶层窗口某一刻的样子:几何、标题、ICCCM / EWMH / Motif 提示、形状。不可变 ——
/// 服务端每次变更都造一份新的,整份换进 <see cref="XTopLevelWindow.Snapshot" />。
/// </summary>
/// <remarks>
/// 宿主先把 <see cref="XTopLevelWindow.Snapshot" /> 取到局部变量里再读各字段,读到的就是同一时刻的值;
/// 分几次读 <see cref="XTopLevelWindow.Snapshot" /> 则可能跨两份快照(比如新宽度配旧高度)。
/// </remarks>
public sealed record XTopLevelSnapshot
{
    /// <summary>外框左上角在根窗口坐标里的位置。</summary>
    public int X { get; init; }

    /// <summary>外框左上角在根窗口坐标里的位置。</summary>
    public int Y { get; init; }

    /// <summary>内区宽。</summary>
    public int Width { get; init; }

    /// <summary>内区高。</summary>
    public int Height { get; init; }

    /// <summary>是否映射中。</summary>
    public bool IsMapped { get; init; }

    /// <summary>标题(<c>_NET_WM_NAME</c> 优先,否则 <c>WM_NAME</c>);没有为空串。</summary>
    public string Title { get; init; } = "";

    /// <summary><c>WM_CLASS</c> 的 class 部分;没有为空串。</summary>
    public string ClassName { get; init; } = "";

    /// <summary>override-redirect(菜单、提示框):宿主应画成无装饰、不抢焦点的弹出窗口。</summary>
    public bool OverrideRedirect { get; init; }

    /// <summary><c>WM_TRANSIENT_FOR</c> 指向的顶层窗口(对话框压在它上面);没有,或指向的不是顶层窗口时为 null。</summary>
    public XTopLevelWindow? TransientFor { get; init; }

    /// <summary>客户端支持 <c>WM_DELETE_WINDOW</c>(点关闭时应当礼貌地请它退出,而不是直接断开)。</summary>
    public bool SupportsDeleteWindow { get; init; }

    /// <summary>
    /// 窗口是 32 位 ARGB 视觉:<see cref="XTopLevelWindow.ReadPixels" /> 给出的像素高 8 位是(预乘的)alpha,宿主应当按透明窗口合成;
    /// 否则高 8 位无意义,窗口不透明。
    /// </summary>
    public bool HasAlpha { get; init; }

    /// <summary>窗口类型(<c>_NET_WM_WINDOW_TYPE</c>;没设时普通窗口是 Normal,有 <c>WM_TRANSIENT_FOR</c> 的是 Dialog)。</summary>
    public XWindowType WindowType { get; init; }

    /// <summary>窗口状态(<c>_NET_WM_STATE</c>)。映射前客户端可以先设好(比如一开始就全屏);之后由宿主经 <see cref="X11Server.SetTopLevelStates" /> 改。</summary>
    public XWindowStates States { get; init; }

    /// <summary>
    /// 要不要窗口管理器画装饰(标题栏、边框)。<c>_MOTIF_WM_HINTS</c> 要求无装饰时为 false ——
    /// GTK 的 HeaderBar、Electron 之类自绘标题栏的窗口都这样;宿主应当给它们一个无边框的原生窗口。
    /// </summary>
    public bool Decorated { get; init; } = true;

    /// <summary>最小尺寸(<c>WM_NORMAL_HINTS</c>);没有限制为 0。</summary>
    public int MinWidth { get; init; }

    /// <summary>最小尺寸;没有限制为 0。</summary>
    public int MinHeight { get; init; }

    /// <summary>最大尺寸;没有限制为 0。</summary>
    public int MaxWidth { get; init; }

    /// <summary>最大尺寸;没有限制为 0。</summary>
    public int MaxHeight { get; init; }

    /// <summary>尺寸步长(终端按字符格缩放);没有为 0。</summary>
    public int WidthIncrement { get; init; }

    /// <summary>尺寸步长;没有为 0。</summary>
    public int HeightIncrement { get; init; }

    /// <summary>图标(<c>_NET_WM_ICON</c>,可能有多种尺寸);没有为空列表。图标没变时各份快照共用同一个列表实例。</summary>
    public IReadOnlyList<XWindowIcon> Icons { get; init; } = [];

    /// <summary>要求引起注意(<c>WM_HINTS</c> 的 urgency 或 <c>_NET_WM_STATE_DEMANDS_ATTENTION</c>)。</summary>
    public bool Urgent { get; init; }

    /// <summary>接受键盘焦点(<c>WM_HINTS</c> 的 input;没设时为 true)。</summary>
    public bool AcceptsFocus { get; init; } = true;

    /// <summary>不透明度(<c>_NET_WM_WINDOW_OPACITY</c>),0–1。</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>
    /// 客户端自绘的阴影 / 边距(<c>_GTK_FRAME_EXTENTS</c>)。只有启用 <see cref="X11ServerOptions.ClientSideShadows" />
    /// 时客户端才会画;这部分应当透明且不算窗口的「身体」。
    /// </summary>
    public XFrameExtents ClientFrameExtents { get; init; }

    /// <summary>客户端进程号(<c>_NET_WM_PID</c>,在 <see cref="ClientMachine" /> 那台机器上);没有为 0。</summary>
    public int ProcessId { get; init; }

    /// <summary>客户端所在的机器名(<c>WM_CLIENT_MACHINE</c>)—— 经 SSH 转发时是远端主机;没有为空串。</summary>
    public string ClientMachine { get; init; } = "";

    /// <summary>窗口角色(<c>WM_WINDOW_ROLE</c>),同一个程序的不同窗口靠它区分(用于记住位置之类);没有为空串。</summary>
    public string Role { get; init; } = "";

    /// <summary>
    /// 窗口形状(SHAPE 扩展的边界形状与内区的交集,内区坐标);null = 普通矩形窗口。形状没变时各份快照共用同一个列表实例。
    /// 宿主应当让形状以外的部分透明、且不接收鼠标(xeyes 的两只眼睛、不规则弹层)。
    /// </summary>
    public IReadOnlyList<XRect>? Shape { get; init; }
}

/// <summary><see cref="IX11ServerHost.TopLevelChanged" /> 报告的变化:快照里哪几组字段跟上一份不同。</summary>
[Flags]
public enum XTopLevelChanges
{
    /// <summary>没有变化。</summary>
    None = 0,

    /// <summary><see cref="XTopLevelSnapshot.X" />、<see cref="XTopLevelSnapshot.Y" />、宽、高。</summary>
    Geometry = 1 << 0,

    /// <summary><see cref="XTopLevelSnapshot.Title" /> 或 <see cref="XTopLevelSnapshot.ClassName" />。</summary>
    Title = 1 << 1,

    /// <summary><see cref="XTopLevelSnapshot.States" />。</summary>
    States = 1 << 2,

    /// <summary><see cref="XTopLevelSnapshot.Icons" />。</summary>
    Icons = 1 << 3,

    /// <summary><see cref="XTopLevelSnapshot.Shape" />。</summary>
    Shape = 1 << 4,

    /// <summary>其余提示:类型、装饰、尺寸约束、焦点、紧急、不透明度、客户端边距、瞬态父窗口、alpha、进程信息等。</summary>
    Hints = 1 << 5,

    /// <summary>全部。</summary>
    All = Geometry | Title | States | Icons | Shape | Hints,
}

/// <summary>窗口四边的宽度,像素(<c>_NET_FRAME_EXTENTS</c> / <c>_GTK_FRAME_EXTENTS</c> 的左、右、上、下)。</summary>
/// <param name="Left">左边。</param>
/// <param name="Right">右边。</param>
/// <param name="Top">上边。</param>
/// <param name="Bottom">下边。</param>
public readonly record struct XFrameExtents(int Left, int Right, int Top, int Bottom);
