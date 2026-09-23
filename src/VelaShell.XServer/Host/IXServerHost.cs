// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Net;
using VelaShell.XServer.Drawing;

namespace VelaShell.XServer.Host;

/// <summary>
/// 宿主:把服务端的顶层窗口显示成原生窗口(rootless,架构 §6)。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>全部回调都在服务端的执行线程上调用</b>,宿主自己切到 UI 线程;回调里不要阻塞,
/// 也不要同步调回服务端的注入方法(那些方法只是把工作项排进同一个执行线程,同步等它会死锁)。
/// </para>
/// <para>
/// 像素通过 <see cref="XTopLevelWindow.CopyPixels" /> 读,它与绘图共用一把锁,可以在任意线程上调。
/// </para>
/// </remarks>
public interface IXServerHost
{
    /// <summary>一个顶层窗口映射了(该创建原生窗口并显示)。</summary>
    void TopLevelMapped(XTopLevelWindow window);

    /// <summary>一个顶层窗口取消映射或销毁了(该隐藏 / 关闭原生窗口)。</summary>
    void TopLevelUnmapped(XTopLevelWindow window);

    /// <summary>几何(客户端 ConfigureWindow)或标题、类名等属性变了。</summary>
    void TopLevelChanged(XTopLevelWindow window);

    /// <summary>有内容画进了顶层窗口;<paramref name="damage" /> 是顶层内区坐标里的矩形。</summary>
    void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage);

    /// <summary>
    /// 指针所在位置该显示的光标变了。<paramref name="cursorGlyph" /> 是 cursor 字体的字形号
    /// (68 = left_ptr、152 = xterm、…);−1 表示默认箭头或位图光标。
    /// </summary>
    void CursorChanged(XTopLevelWindow? window, int cursorGlyph);

    /// <summary>响铃。</summary>
    void Bell(int percent);
}

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

/// <summary>服务端选项。</summary>
public sealed record XServerOptions
{
    /// <summary>显示号 N;TCP 端口 = 6000 + N。</summary>
    public int DisplayNumber { get; init; }

    /// <summary>监听地址。<b>默认只听本机</b>:SSH X11 转发过来的连接在本机看来就是 127.0.0.1。</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary>根窗口(虚拟桌面)宽,像素。宿主应当设成所有显示器合起来的范围。</summary>
    public int ScreenWidth { get; init; } = 3840;

    /// <summary>根窗口高,像素。</summary>
    public int ScreenHeight { get; init; } = 2160;

    /// <summary>每英寸像素数,用来换算屏幕的毫米尺寸(客户端据此选字号)。</summary>
    public int Dpi { get; init; } = 96;

    /// <summary>
    /// <c>MIT-MAGIC-COOKIE-1</c> 授权 cookie;null = 不要求授权、只接受来自本机的连接(与 X.Org 的主机访问控制行为一致)。
    /// </summary>
    public byte[]? AuthorizationCookie { get; init; }

    /// <summary>厂商字符串(连接建立回复里的 vendor)。</summary>
    public string Vendor { get; init; } = "VelaShell";

    /// <summary>
    /// 诊断日志:连接进出、每条发给客户端的协议错误(带操作码)、未实现的请求。null = 不记。
    /// 在执行线程上调用,不要在里面阻塞。
    /// </summary>
    public Action<string>? Log { get; init; }
}
