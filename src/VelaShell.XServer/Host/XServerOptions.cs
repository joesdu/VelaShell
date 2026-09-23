// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Net;

namespace VelaShell.XServer.Host;

/// <summary>服务端选项。</summary>
public sealed record XServerOptions
{
    /// <summary>显示号 N;TCP 端口 = 6000 + N。</summary>
    public int DisplayNumber { get; init; }

    /// <summary>监听地址。<b>默认只听本机</b>:SSH X11 转发过来的连接在本机看来就是 127.0.0.1。</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary><see cref="Server.X11Server.StartAsync" /> 是否监听 TCP 6000 + N。只用 Unix 套接字或 <c>ServeAsync</c> 喂流的宿主可以关掉。</summary>
    public bool ListenTcp { get; init; } = true;

    /// <summary>
    /// Unix 套接字路径:null = Windows 以外默认 <c>/tmp/.X11-unix/X{DisplayNumber}</c>(Linux 另在抽象命名空间里监听同名套接字);
    /// 空字符串 = 不监听。本机客户端用 <c>DISPLAY=:N</c> 连它。
    /// </summary>
    public string? UnixSocketPath { get; init; }

    /// <summary>根窗口(虚拟桌面)宽,像素。宿主应当设成所有显示器合起来的范围。</summary>
    public int ScreenWidth { get; init; } = 3840;

    /// <summary>根窗口高,像素。</summary>
    public int ScreenHeight { get; init; } = 2160;

    /// <summary>
    /// 显示器布局;null = 一台显示器覆盖整个根窗口。每台都应当落在 <see cref="ScreenWidth" /> × <see cref="ScreenHeight" /> 之内。
    /// 运行中换布局用 <see cref="Server.X11Server.SetScreenLayout" />。
    /// </summary>
    public IReadOnlyList<XMonitor>? Monitors { get; init; }

    /// <summary>每英寸像素数,用来换算屏幕的毫米尺寸(客户端据此选字号);经 XSETTINGS 与 RESOURCE_MANAGER 的 Xft.dpi 发布。</summary>
    public int Dpi { get; init; } = 96;

    /// <summary>整数缩放倍数(HiDPI):经 XSETTINGS 的 Gdk/WindowScalingFactor 告诉 GTK。运行中改用 <see cref="Server.X11Server.SetDisplayScale" />。</summary>
    public int ScaleFactor { get; init; } = 1;

    /// <summary>键盘布局名(XKB 的 layout,如 <c>us</c>、<c>de</c>);键位表本身由 <see cref="Server.X11Server.SetKeyboardMapping" /> 给出。</summary>
    public string KeyboardLayout { get; init; } = "us";

    /// <summary>
    /// <c>MIT-MAGIC-COOKIE-1</c> 授权 cookie;null = 不要求授权、只接受来自本机的连接(与 X.Org 的主机访问控制行为一致)。
    /// </summary>
    public byte[]? AuthorizationCookie { get; init; }

    /// <summary>厂商字符串(连接建立回复里的 vendor)。</summary>
    public string Vendor { get; init; } = "VelaShell";

    /// <summary>与宿主的剪贴板互通(CLIPBOARD 选区)。关掉后 <see cref="IXServerHost.ClipboardChanged" /> 不再调用,
    /// <see cref="Server.X11Server.SetClipboardText" /> 也不起作用。</summary>
    public bool SyncClipboard { get; init; } = true;

    /// <summary>PRIMARY 选区(X 里「选中即复制」)也参与互通。默认关:选中文字就改写系统剪贴板往往出人意料。</summary>
    public bool SyncPrimary { get; init; }

    /// <summary>
    /// 告诉客户端「窗口管理器支持客户端自绘阴影」(在 <c>_NET_SUPPORTED</c> 里列出 <c>_GTK_FRAME_EXTENTS</c>)。
    /// 打开后 GTK 的自绘标题栏窗口会在四周画半透明阴影,宿主必须能显示带 alpha 的窗口并按
    /// <see cref="XTopLevelWindow.ClientFrameExtents" /> 处理;默认关,GTK 于是画无阴影的窗口。
    /// </summary>
    public bool ClientSideShadows { get; init; }

    /// <summary>窗口管理器的名字(<c>_NET_SUPPORTING_WM_CHECK</c> 窗口上的 <c>_NET_WM_NAME</c>)。</summary>
    public string WindowManagerName { get; init; } = "VelaShell";

    /// <summary>
    /// 诊断日志:连接进出、每条发给客户端的协议错误(带操作码)、未实现的请求。null = 不记。
    /// 在执行线程上调用,不要在里面阻塞。
    /// </summary>
    public Action<string>? Log { get; init; }
}
