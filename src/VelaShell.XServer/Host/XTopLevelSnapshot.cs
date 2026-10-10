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
    /// <summary>
    /// X 窗口边框外沿的左上角在根窗口坐标里的位置(核心协议的窗口位置);内容区(内区)在它右下 <see cref="BorderWidth" /> 处。
    /// <see cref="NeedsPlacement" /> 时是客户端请求的位置,还没按重力摆过。
    /// </summary>
    public int X { get; init; }

    /// <summary>边框外沿的左上角,见 <see cref="X" />。</summary>
    public int Y { get; init; }

    /// <summary>内区宽。</summary>
    public int Width { get; init; }

    /// <summary>内区高。</summary>
    public int Height { get; init; }

    /// <summary>
    /// X 窗口的边框宽度(<c>xterm -bw</c>、Xt / Motif 的菜单)。宿主不画 X 的边框:原生窗口的内容区对准 (X + BorderWidth, Y + BorderWidth),
    /// 原生窗口的外框就当作代替了它。
    /// </summary>
    public int BorderWidth { get; init; }

    /// <summary>
    /// <see cref="X" /> / <see cref="Y" /> 是客户端自己给的位置(建窗口、移动窗口、<c>_NET_MOVERESIZE_WINDOW</c> 以外的 ConfigureWindow),
    /// 窗口管理器还没摆过:宿主应当按 ICCCM §4.1.2.3 把原生窗口的<b>外框</b>(不是内容区)按 <see cref="WinGravity" /> 对准它 ——
    /// <see cref="PlaceInFrame" /> 给出摆好之后 X 窗口该在的位置 —— 再用 <see cref="X11Server.MoveTopLevel" /> 报回去(之后这一项为 false)。
    /// 不是用户指定的位置(<see cref="UserPosition" /> 为 false)而且是 (0, 0) 的,宿主可以替它选位置。
    /// override-redirect 窗口不归窗口管理器摆,恒为 false。
    /// </summary>
    public bool NeedsPlacement { get; init; }

    /// <summary>
    /// 按 <see cref="WinGravity" /> 套上四边宽 <paramref name="frame" /> 的外框之后,X 窗口(边框外沿的左上角)该在的根窗口坐标(ICCCM §4.1.2.3):
    /// 外框的参考点落在 X 窗口不套外框时参考点所在的位置;宿主不画 X 的边框,外框直接包着内容区。
    /// <see cref="XGravity.Static" /> 时内容区不动。原生窗口外框的左上角是结果加上 <see cref="BorderWidth" /> 再减去左边、上边的宽度。
    /// </summary>
    /// <param name="frame">原生窗口的装饰四边宽(物理像素;无装饰为 0)。</param>
    public (int X, int Y) PlaceInFrame(XFrameExtents frame)
    {
        (int dx, int dy) = GravityOffset(WinGravity, frame, BorderWidth);
        return (X + dx, Y + dy);
    }

    /// <summary>
    /// 按重力套外框时 X 窗口位置的偏移(<see cref="PlaceInFrame" /> 与 <c>_NET_MOVERESIZE_WINDOW</c> 共用):外框的外沿与 X 窗口(含边框)的外沿
    /// 按参考点对齐,内容区紧贴外框的内沿,X 窗口的位置 = 内容区 − 边框宽。
    /// </summary>
    internal static (int Dx, int Dy) GravityOffset(XGravity gravity, XFrameExtents frame, int borderWidth)
    {
        if (gravity == XGravity.Static)
        {
            return (0, 0);
        }
        // 外框比 X 窗口(含边框)宽出 left + right − 2·bw;参考点在左 / 中 / 右时外框的左沿分别挪 0、一半、全部。
        int column = gravity is XGravity.NorthWest or XGravity.West or XGravity.SouthWest ? 0
            : gravity is XGravity.North or XGravity.Center or XGravity.South ? 1 : 2;
        int row = gravity is XGravity.NorthWest or XGravity.North or XGravity.NorthEast ? 0
            : gravity is XGravity.West or XGravity.Center or XGravity.East ? 1 : 2;
        int frameX = -(column * (frame.Left + frame.Right - (2 * borderWidth)) / 2);
        int frameY = -(row * (frame.Top + frame.Bottom - (2 * borderWidth)) / 2);
        return (frameX + frame.Left - borderWidth, frameY + frame.Top - borderWidth);
    }

    /// <summary>是否映射中。</summary>
    public bool IsMapped { get; init; }

    /// <summary>标题(<c>_NET_WM_NAME</c> 优先,否则 <c>WM_NAME</c>);没有为空串。</summary>
    public string Title { get; init; } = "";

    /// <summary><c>WM_CLASS</c> 的 class 部分;没有为空串。</summary>
    public string ClassName { get; init; } = "";

    /// <summary><c>WM_CLASS</c> 的 instance 部分(同一个程序起的不同实例、<c>-name</c> 改过的名字);没有为空串。</summary>
    public string InstanceName { get; init; } = "";

    /// <summary>override-redirect(菜单、提示框):宿主应画成无装饰、不抢焦点的弹出窗口。</summary>
    public bool OverrideRedirect { get; init; }

    /// <summary><c>WM_TRANSIENT_FOR</c> 指向的顶层窗口(对话框压在它上面);没有,或指向的不是顶层窗口时为 null。</summary>
    public XTopLevelWindow? TransientFor { get; init; }

    /// <summary>客户端支持 <c>WM_DELETE_WINDOW</c>(点关闭时应当礼貌地请它退出,而不是直接断开)。</summary>
    public bool SupportsDeleteWindow { get; init; }

    /// <summary>所属客户端的编号(<see cref="XClientInfo.Id" />)。</summary>
    public int ClientId { get; init; }

    /// <summary>所属客户端的连接名(宿主在 <see cref="X11Server.ServeAuthenticatedAsync(System.IO.Stream, string?, System.Threading.CancellationToken)" /> 时给的);没给为 null。</summary>
    public string? ClientLabel { get; init; }

    /// <summary>
    /// InputOnly 窗口(GTK 的 GtkInvisible 之类,拿来占选区、接拖放):看不见、没有像素(<see cref="XTopLevelWindow.ReadPixels" /> 读不到),
    /// 宿主不应当为它开原生窗口。
    /// </summary>
    public bool InputOnly { get; init; }

    /// <summary>
    /// 窗口是 32 位 ARGB 视觉:<see cref="XTopLevelWindow.ReadPixels" /> 给出的像素高 8 位是(预乘的)alpha,宿主应当按透明窗口合成;
    /// 否则高 8 位无意义,窗口不透明。
    /// </summary>
    public bool HasAlpha { get; init; }

    /// <summary>窗口类型(<c>_NET_WM_WINDOW_TYPE</c>;没设时普通窗口是 Normal,有 <c>WM_TRANSIENT_FOR</c> 的是 Dialog)。</summary>
    public XWindowType WindowType { get; init; }

    /// <summary>
    /// 窗口状态(<c>_NET_WM_STATE</c>)。映射前客户端可以先设好(一开始就最大化、全屏),宿主显示原生窗口时应当照这个状态显示;
    /// <c>WM_HINTS</c> 的 initial_state 是 IconicState(<c>xterm -iconic</c>)时,服务端在映射时加上 <see cref="XWindowStates.Hidden" />。
    /// 之后由宿主经 <see cref="X11Server.SetTopLevelStates" /> 改。
    /// </summary>
    public XWindowStates States { get; init; }

    /// <summary>
    /// 要不要窗口管理器画装饰(标题栏、边框)。<c>_MOTIF_WM_HINTS</c> 要求无装饰时为 false ——
    /// GTK 的 HeaderBar、Electron 之类自绘标题栏的窗口都这样;宿主应当给它们一个无边框的原生窗口。
    /// </summary>
    public bool Decorated { get; init; } = true;

    /// <summary>
    /// 最小尺寸(<c>WM_NORMAL_HINTS</c>);没有限制为 0。没给最小尺寸而给了基准尺寸时按基准尺寸(ICCCM §4.1.2.3)。
    /// 尺寸类的值一律夹到 0–32767(X 的尺寸范围),客户端给的负数与超大值不会变成负的约束。
    /// </summary>
    public int MinWidth { get; init; }

    /// <summary>最小尺寸;没有限制为 0。</summary>
    public int MinHeight { get; init; }

    /// <summary>最大尺寸;没有限制为 0。</summary>
    public int MaxWidth { get; init; }

    /// <summary>最大尺寸;没有限制为 0。</summary>
    public int MaxHeight { get; init; }

    /// <summary>尺寸步长(终端按字符格缩放);没有为 0。合法的宽度是 <see cref="BaseWidth" /> + i × 步长。</summary>
    public int WidthIncrement { get; init; }

    /// <summary>尺寸步长;没有为 0。</summary>
    public int HeightIncrement { get; init; }

    /// <summary>
    /// 基准尺寸(<c>WM_NORMAL_HINTS</c> 的 base size):按步长缩放时从它算起(xterm 是滚动条与内边距的宽度)。
    /// 没给时按最小尺寸(ICCCM §4.1.2.3),两个都没给为 0。
    /// </summary>
    public int BaseWidth { get; init; }

    /// <summary>基准尺寸;见 <see cref="BaseWidth" />。</summary>
    public int BaseHeight { get; init; }

    /// <summary>宽高比(宽 / 高)的下限(<c>WM_NORMAL_HINTS</c> 的 min_aspect);没有为 0。</summary>
    public double MinAspect { get; init; }

    /// <summary>宽高比(宽 / 高)的上限(max_aspect);没有为 0。</summary>
    public double MaxAspect { get; init; }

    /// <summary>
    /// 窗口重力(<c>WM_NORMAL_HINTS</c> 的 win_gravity,ICCCM §4.1.2.3):外框加上去之后,窗口的哪个参考点停在客户端请求的位置上。
    /// 没给时是 <see cref="XGravity.NorthWest" />(外框左上角对准请求的坐标)。
    /// </summary>
    public XGravity WinGravity { get; init; } = XGravity.NorthWest;

    /// <summary>
    /// 位置是用户指定的(<c>WM_NORMAL_HINTS</c> 的 USPosition,比如 <c>xterm -geometry +0+0</c>):宿主应当照这个位置摆,哪怕是 (0, 0)。
    /// </summary>
    public bool UserPosition { get; init; }

    /// <summary>位置是程序自己定的(PPosition)。不少程序在 (0, 0) 也设它,宿主可以把那种当成「没给位置」。</summary>
    public bool ProgramPosition { get; init; }

    /// <summary>
    /// 尺寸是用户指定的(<c>WM_NORMAL_HINTS</c> 的 USSize,比如 <c>xterm -geometry 120x40</c>):宿主不该拿自己记住的尺寸盖掉它。
    /// </summary>
    public bool UserSize { get; init; }

    /// <summary>
    /// 窗口组的组长(<c>WM_HINTS</c> 的 window_group):同一个程序的各个顶层指向同一个组长(组长常常是一个不映射的窗口)。
    /// 没有,或指向的不是顶层窗口时为 null。
    /// </summary>
    public XTopLevelWindow? WindowGroup { get; init; }

    /// <summary>
    /// 窗口管理器可以对它做的操作(<c>_MOTIF_WM_HINTS</c> 的 functions):没说时全部可以。
    /// 宿主应当据此禁用对应的按钮(不能缩放的对话框、不让最小化的启动画面)。
    /// </summary>
    public XWindowFunctions Functions { get; init; } = XWindowFunctions.All;

    /// <summary>
    /// 图标(<c>_NET_WM_ICON</c>,可能有多种尺寸);没有时取 <c>WM_HINTS</c> 的 icon_pixmap(与 icon_mask)—— 老程序只给那个;
    /// 都没有为空列表。图标没变时各份快照共用同一个列表实例。
    /// </summary>
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

    /// <summary>
    /// 输入形状(SHAPE 1.1 的 Input 形状与边界形状的交集,内区坐标):指针只在这里面才落到这个窗口,以外的点击应当穿过去。
    /// null = 客户端没设输入形状(落在 <see cref="Shape" /> 里、没有形状时落在整个窗口里)。形状没变时各份快照共用同一个列表实例。
    /// </summary>
    public IReadOnlyList<XRect>? InputShape { get; init; }

    /// <summary>
    /// 停靠栏 / 面板要求保留的屏幕边缘(<c>_NET_WM_STRUT_PARTIAL</c>,没有时取 <c>_NET_WM_STRUT</c> 的左、右、上、下,根窗口坐标里距各边的像素);
    /// 没有为全 0。
    /// </summary>
    public XFrameExtents Strut { get; init; }
}

/// <summary><see cref="IX11ServerHost.TopLevelChanged" /> 报告的变化:快照里哪几组字段跟上一份不同。</summary>
[Flags]
public enum XTopLevelChanges
{
    /// <summary>没有变化。</summary>
    None = 0,

    /// <summary>
    /// <see cref="XTopLevelSnapshot.X" />、<see cref="XTopLevelSnapshot.Y" />、宽、高、<see cref="XTopLevelSnapshot.BorderWidth" />、
    /// <see cref="XTopLevelSnapshot.NeedsPlacement" />。
    /// </summary>
    Geometry = 1 << 0,

    /// <summary><see cref="XTopLevelSnapshot.Title" />、<see cref="XTopLevelSnapshot.ClassName" /> 或 <see cref="XTopLevelSnapshot.InstanceName" />。</summary>
    Title = 1 << 1,

    /// <summary><see cref="XTopLevelSnapshot.States" />。</summary>
    States = 1 << 2,

    /// <summary><see cref="XTopLevelSnapshot.Icons" />。</summary>
    Icons = 1 << 3,

    /// <summary><see cref="XTopLevelSnapshot.Shape" /> 或 <see cref="XTopLevelSnapshot.InputShape" />。</summary>
    Shape = 1 << 4,

    /// <summary>其余提示:类型、装饰、尺寸约束、位置提示与重力、焦点、紧急、不透明度、客户端边距、瞬态父窗口、窗口组、alpha、进程信息等。</summary>
    Hints = 1 << 5,

    /// <summary>全部。</summary>
    All = Geometry | Title | States | Icons | Shape | Hints,
}

/// <summary>X 的重力(协议「CreateWindow」的 win-gravity 取值;ICCCM §4.1.2.3 的 win_gravity 用 1–10)。</summary>
public enum XGravity
{
    /// <summary>左上角(默认)。</summary>
    NorthWest = 1,
    /// <summary>上边中点。</summary>
    North = 2,
    /// <summary>右上角。</summary>
    NorthEast = 3,
    /// <summary>左边中点。</summary>
    West = 4,
    /// <summary>中心。</summary>
    Center = 5,
    /// <summary>右边中点。</summary>
    East = 6,
    /// <summary>左下角。</summary>
    SouthWest = 7,
    /// <summary>下边中点。</summary>
    South = 8,
    /// <summary>右下角。</summary>
    SouthEast = 9,
    /// <summary>内区不动:请求的坐标就是内区左上角(外框长在它外面)。</summary>
    Static = 10,
}

/// <summary>窗口管理器可以对窗口做的操作(<c>_MOTIF_WM_HINTS</c> 的 functions)。</summary>
[Flags]
public enum XWindowFunctions
{
    /// <summary>什么都不行。</summary>
    None = 0,
    /// <summary>改尺寸。</summary>
    Resize = 1 << 0,
    /// <summary>移动。</summary>
    Move = 1 << 1,
    /// <summary>最小化。</summary>
    Minimize = 1 << 2,
    /// <summary>最大化。</summary>
    Maximize = 1 << 3,
    /// <summary>关闭。</summary>
    Close = 1 << 4,
    /// <summary>全部。</summary>
    All = Resize | Move | Minimize | Maximize | Close,
}

/// <summary>窗口四边的宽度,像素(<c>_NET_FRAME_EXTENTS</c> / <c>_GTK_FRAME_EXTENTS</c> 的左、右、上、下)。</summary>
/// <param name="Left">左边。</param>
/// <param name="Right">右边。</param>
/// <param name="Top">上边。</param>
/// <param name="Bottom">下边。</param>
public readonly record struct XFrameExtents(int Left, int Right, int Top, int Bottom);
