// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Host;

/// <summary>一个顶层窗口在宿主眼里的样子。属性值由服务端线程更新,宿主只读。</summary>
public sealed class XTopLevelWindow
{
    private readonly object _pixelLock;
    private readonly Func<(uint[] Pixels, int Width, int Height)?> _pixels;

    internal XTopLevelWindow(uint id, object pixelLock, Func<(uint[] Pixels, int Width, int Height)?> pixels)
    {
        Id = id;
        _pixelLock = pixelLock;
        _pixels = pixels;
    }

    /// <summary>窗口 ID(XID)。宿主注入输入、移动 / 缩放 / 关闭时用它指名。</summary>
    public uint Id { get; }

    /// <summary>外框左上角在根窗口坐标里的位置。</summary>
    public int X { get; internal set; }

    /// <summary>外框左上角在根窗口坐标里的位置。</summary>
    public int Y { get; internal set; }

    /// <summary>内区宽。</summary>
    public int Width { get; internal set; }

    /// <summary>内区高。</summary>
    public int Height { get; internal set; }

    /// <summary>标题(<c>_NET_WM_NAME</c> 优先,否则 <c>WM_NAME</c>)。</summary>
    public string Title { get; internal set; } = "";

    /// <summary><c>WM_CLASS</c> 的 class 部分。</summary>
    public string ClassName { get; internal set; } = "";

    /// <summary>override-redirect(菜单、提示框):宿主应画成无装饰、不抢焦点的弹出窗口。</summary>
    public bool OverrideRedirect { get; internal set; }

    /// <summary><c>WM_TRANSIENT_FOR</c> 指向的顶层窗口 ID;没有为 0。</summary>
    public uint TransientFor { get; internal set; }

    /// <summary>客户端支持 <c>WM_DELETE_WINDOW</c>(点关闭时应当礼貌地请它退出,而不是直接断开)。</summary>
    public bool SupportsDeleteWindow { get; internal set; }

    /// <summary>是否映射中。</summary>
    public bool IsMapped { get; internal set; }

    /// <summary>
    /// 窗口是 32 位 ARGB 视觉:<see cref="CopyPixels" /> 给出的像素高 8 位是(预乘的)alpha,宿主应当按透明窗口合成;
    /// 否则高 8 位无意义,窗口不透明。
    /// </summary>
    public bool HasAlpha { get; internal set; }

    /// <summary>窗口类型(<c>_NET_WM_WINDOW_TYPE</c>;没设时普通窗口是 Normal,有 <c>WM_TRANSIENT_FOR</c> 的是 Dialog)。</summary>
    public XWindowType WindowType { get; internal set; }

    /// <summary>窗口状态(<c>_NET_WM_STATE</c>)。映射前客户端可以先设好(比如一开始就全屏);之后由宿主经 <c>SetTopLevelStates</c> 改。</summary>
    public XWindowStates States { get; internal set; }

    /// <summary>
    /// 要不要窗口管理器画装饰(标题栏、边框)。<c>_MOTIF_WM_HINTS</c> 要求无装饰时为 false ——
    /// GTK 的 HeaderBar、Electron 之类自绘标题栏的窗口都这样;宿主应当给它们一个无边框的原生窗口。
    /// </summary>
    public bool Decorated { get; internal set; } = true;

    /// <summary>最小尺寸(<c>WM_NORMAL_HINTS</c>);没有限制为 0。</summary>
    public int MinWidth { get; internal set; }

    /// <summary>最小尺寸;没有限制为 0。</summary>
    public int MinHeight { get; internal set; }

    /// <summary>最大尺寸;没有限制为 0。</summary>
    public int MaxWidth { get; internal set; }

    /// <summary>最大尺寸;没有限制为 0。</summary>
    public int MaxHeight { get; internal set; }

    /// <summary>尺寸步长(终端按字符格缩放);没有为 0。</summary>
    public int WidthIncrement { get; internal set; }

    /// <summary>尺寸步长;没有为 0。</summary>
    public int HeightIncrement { get; internal set; }

    /// <summary>图标(<c>_NET_WM_ICON</c>,可能有多种尺寸);没有为空列表。</summary>
    public IReadOnlyList<XWindowIcon> Icons { get; internal set; } = [];

    /// <summary>解析出 <see cref="Icons" /> 的那份属性值(属性不可变、改动总是整份换掉:同一个引用就不必重新解析)。</summary>
    internal object? IconSource { get; set; }

    /// <summary>要求引起注意(<c>WM_HINTS</c> 的 urgency 或 <c>_NET_WM_STATE_DEMANDS_ATTENTION</c>)。</summary>
    public bool Urgent { get; internal set; }

    /// <summary>接受键盘焦点(<c>WM_HINTS</c> 的 input;没设时为 true)。</summary>
    public bool AcceptsFocus { get; internal set; } = true;

    /// <summary>不透明度(<c>_NET_WM_WINDOW_OPACITY</c>),0–1。</summary>
    public double Opacity { get; internal set; } = 1;

    /// <summary>
    /// 客户端自绘的阴影 / 边距(<c>_GTK_FRAME_EXTENTS</c>:左、右、上、下)。只有启用
    /// <see cref="XServerOptions.ClientSideShadows" /> 时客户端才会画;这部分应当透明且不算窗口的「身体」。
    /// </summary>
    public (int Left, int Right, int Top, int Bottom) ClientFrameExtents { get; internal set; }

    /// <summary>客户端进程号(<c>_NET_WM_PID</c>,在 <see cref="ClientMachine" /> 那台机器上);没有为 0。</summary>
    public int ProcessId { get; internal set; }

    /// <summary>客户端所在的机器名(<c>WM_CLIENT_MACHINE</c>)—— 经 SSH 转发时是远端主机。</summary>
    public string ClientMachine { get; internal set; } = "";

    /// <summary>窗口角色(<c>WM_WINDOW_ROLE</c>),同一个程序的不同窗口靠它区分(用于记住位置之类)。</summary>
    public string Role { get; internal set; } = "";

    /// <summary>
    /// 窗口形状(SHAPE 扩展的边界形状与内区的交集,内区坐标);null = 普通矩形窗口。
    /// 宿主应当让形状以外的部分透明、且不接收鼠标(xeyes 的两只眼睛、不规则弹层)。
    /// </summary>
    public IReadOnlyList<XRect>? Shape { get; internal set; }

    /// <summary>
    /// 拷贝当前像素(<c>0x00RRGGBB</c>,行优先,宽 × 高)。<paramref name="destination" /> 不够大时只拷能放下的部分。
    /// </summary>
    /// <returns>实际拷贝时的 (宽, 高);窗口已没有缓冲时为 (0, 0)。</returns>
    public (int Width, int Height) CopyPixels(Span<uint> destination)
    {
        lock (_pixelLock)
        {
            if (_pixels() is not { } buffer)
            {
                return (0, 0);
            }
            int count = Math.Min(destination.Length, buffer.Width * buffer.Height);
            buffer.Pixels.AsSpan(0, count).CopyTo(destination);
            return (buffer.Width, buffer.Height);
        }
    }
}
