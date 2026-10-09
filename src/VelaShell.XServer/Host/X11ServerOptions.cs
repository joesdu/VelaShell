// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Net;

namespace VelaShell.XServer;

/// <summary>服务端选项。构造 <see cref="X11Server" /> 时校验一次(不合法抛 <see cref="ArgumentException" />),之后不再读取改动。</summary>
public sealed class X11ServerOptions
{
    /// <summary>显示号上限:TCP 端口 6000 + N 不能超过 65535。</summary>
    public const int MaxDisplayNumber = ushort.MaxValue - 6000;

    /// <summary>根窗口的边长上限:X 的坐标是 16 位有符号数。</summary>
    public const int MaxScreenSize = short.MaxValue;

    /// <summary>显示号 N(0 – <see cref="MaxDisplayNumber" />);TCP 端口 = 6000 + N。</summary>
    public int DisplayNumber { get; init; }

    /// <summary>监听地址。<b>默认只听本机</b>:SSH X11 转发过来的连接在本机看来就是 127.0.0.1。</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary>
    /// <see cref="X11Server.StartAsync" /> 是否监听 TCP 6000 + N。null(默认)= 配了 <see cref="AuthorizationCookie" /> 才听;
    /// true = 总是听;false = 不听(只用 Unix 套接字或 <see cref="X11Server.ServeAsync" /> 喂流)。
    /// 原先默认就听、又不要 cookie:<c>new X11Server()</c> + <c>StartAsync()</c> 的结果是本机任何用户都能经环回 TCP 连进来,
    /// 读窗口、记键盘、注入输入(TCP 上分不出对端是哪个用户)。显式设 true 而不配 cookie 时启动会记一行提醒。
    /// </summary>
    public bool? ListenTcp { get; init; }

    /// <summary>
    /// Unix 套接字路径:null = Windows 以外默认 <c>/tmp/.X11-unix/X{DisplayNumber}</c>(Linux 另在抽象命名空间里监听同名套接字);
    /// 空字符串 = 不监听。本机客户端用 <c>DISPLAY=:N</c> 连它。要放得进 Unix 套接字的地址(Linux 上 107 字节左右),否则构造时抛异常。
    /// </summary>
    public string? UnixSocketPath { get; init; }

    /// <summary>根窗口(虚拟桌面)宽,像素(1 – <see cref="MaxScreenSize" />)。宿主应当设成所有显示器合起来的范围。</summary>
    public int ScreenWidth { get; init; } = 3840;

    /// <summary>根窗口高,像素(1 – <see cref="MaxScreenSize" />)。</summary>
    public int ScreenHeight { get; init; } = 2160;

    /// <summary>
    /// 显示器布局;null = 一台显示器覆盖整个根窗口。每台都应当落在 <see cref="ScreenWidth" /> × <see cref="ScreenHeight" /> 之内,
    /// 最多 <see cref="X11Server.MaxMonitors" /> 台。运行中换布局用 <see cref="X11Server.SetScreenLayout" />。
    /// </summary>
    public IReadOnlyList<XMonitor>? Monitors { get; init; }

    /// <summary>每英寸像素数(≥ 1),用来换算屏幕的毫米尺寸(客户端据此选字号);经 XSETTINGS 与 RESOURCE_MANAGER 的 Xft.dpi 发布。</summary>
    public int Dpi { get; init; } = 96;

    /// <summary>整数缩放倍数(≥ 1,HiDPI):经 XSETTINGS 的 Gdk/WindowScalingFactor 告诉 GTK。运行中改用 <see cref="X11Server.SetDisplayScale" />。</summary>
    public int ScaleFactor { get; init; } = 1;

    /// <summary>
    /// 键盘布局名(XKB 的 layout,如 <c>us</c>、<c>de</c>),对应服务端内置的 US 键位表。
    /// 宿主换了键位表时经 <see cref="X11Server.SetKeymap" /> 连同布局名一起给出。
    /// </summary>
    public string KeyboardLayout { get; init; } = "us";

    /// <summary>
    /// <c>MIT-MAGIC-COOKIE-1</c> 授权 cookie。配置了就要求 TCP 连接(<b>包括环回</b>:本机别的进程、别的用户都连得到那个端口)
    /// 与没法确认是同一个用户的 Unix 套接字连接带上它;null = 不要求 cookie、只接受来自本机的连接,TCP 默认也不开(见 <see cref="ListenTcp" />)
    /// (与 X.Org 的主机访问控制行为一致)。不论配没配:Unix 套接字文件只有属主能连,Linux 抽象命名空间里的连接按 uid 只放行同一个用户;
    /// 宿主经 <see cref="X11Server.ServeAuthenticatedAsync(Stream, CancellationToken)" /> 喂进来的流不查授权。
    /// 长度 <see cref="MinAuthorizationCookieLength" /> – <see cref="MaxAuthorizationCookieLength" /> 字节(空数组曾让任何带空数据的
    /// MIT-MAGIC-COOKIE-1 都通过,比不配还宽);构造 <see cref="X11Server" /> 时拷一份,之后再改这个数组不影响授权。
    /// </summary>
    public byte[]? AuthorizationCookie { get; init; }

    /// <summary><see cref="AuthorizationCookie" /> 的最短长度(xauth 生成的 MIT-MAGIC-COOKIE-1 就是 16 字节)。</summary>
    public const int MinAuthorizationCookieLength = 16;

    /// <summary><see cref="AuthorizationCookie" /> 的最长长度(连接建立报文里授权数据的上限,更长的客户端根本发不进来)。</summary>
    public const int MaxAuthorizationCookieLength = 256;

    /// <summary>厂商字符串(连接建立回复里的 vendor)。</summary>
    public string Vendor { get; init; } = "VelaShell";

    /// <summary>与宿主的剪贴板互通(CLIPBOARD 选区)。关掉后 <see cref="IX11ServerHost.ClipboardChanged" /> 不再调用,
    /// <see cref="X11Server.SetClipboardText" /> 也不起作用。</summary>
    public bool SyncClipboard { get; init; } = true;

    /// <summary>PRIMARY 选区(X 里「选中即复制」)也参与互通。默认关:选中文字就改写系统剪贴板往往出人意料。</summary>
    public bool SyncPrimary { get; init; }

    /// <summary>
    /// 剪贴板跟着键盘焦点走:宿主的文本只给焦点所在的那个会话读 —— 焦点窗口的客户端,以及与它连接名相同的客户端(同一个 SSH 会话里的
    /// <c>xclip</c> / <c>xsel</c>,见 <see cref="X11Server.ServeAuthenticatedAsync(Stream, string?, CancellationToken)" />);X 这边的复制也只收那个会话的。
    /// 默认开:否则本机复制的密码在用户点一下任意 X 窗口后对所有会话可读,后台会话里的程序也能反复改写本机剪贴板(pastejacking)。
    /// 没有 X 窗口有焦点时谁都读不到。没有连接名的本机程序同属一个会话。
    /// <para>
    /// 开着时 PRIMARY、SECONDARY、CLIPBOARD 还按会话隔离:每个会话各有各的属主,别的会话看不到属主变化、收不到因此发的 SelectionClear
    /// 与 XFIXES 通知,也读不到另一个会话里的复制。跨会话的复制粘贴经宿主的剪贴板中转 —— 在 A 里复制、切到 B 再粘贴,
    /// 服务端在 B 拿到焦点时替宿主占有最新的文本(最近一次复制赢,不管它发生在哪个会话或本机)。同一会话里的程序之间照常互相复制粘贴。
    /// </para>
    /// 只有一个受信客户端的嵌入场景可以关掉(关掉后选区照协议全显示共享)。
    /// </summary>
    public bool ClipboardFollowsFocus { get; init; } = true;

    /// <summary>
    /// 限制经 <see cref="X11Server.ServeAuthenticatedAsync(Stream, string?, CancellationToken)" /> 进来的连接(SSH 转发来的远端程序):
    /// 看不到 XTEST(伪造的输入与真实键盘无从区分,被攻破的远端机能往别的会话的 xterm 注入命令)、收不到 XI2 的原始按键事件
    /// (不抢焦点就能记下所有 X 窗口里敲的键)、不能 XIChangeHierarchy(能让物理输入设备失效)。
    /// 默认关:远端的 xdotool 之类的工具靠 XTEST 工作。
    /// </summary>
    public bool RestrictForwardedClients { get; init; }

    /// <summary>
    /// 告诉客户端「窗口管理器支持客户端自绘阴影」(在 <c>_NET_SUPPORTED</c> 里列出 <c>_GTK_FRAME_EXTENTS</c>)。
    /// 打开后 GTK 的自绘标题栏窗口会在四周画半透明阴影,宿主必须能显示带 alpha 的窗口并按
    /// <see cref="XTopLevelSnapshot.ClientFrameExtents" /> 处理;默认关,GTK 于是画无阴影的窗口。
    /// 关着时边缘缩放照样可用:GTK 在窗口自己的内沿留一圈约 4 像素(乘 GTK 的缩放倍数)的缩放区,按下就发
    /// <c>_NET_WM_MOVERESIZE</c>(上、下、左、右与四个角),宿主经 <see cref="XMoveResizeRequest" /> 开始原生的缩放
    /// (gtk3-widget-factory 实测)。
    /// </summary>
    public bool ClientSideShadows { get; init; }

    /// <summary>
    /// 当系统托盘(freedesktop System Tray Protocol):服务端占住 <c>_NET_SYSTEM_TRAY_S0</c>,X 程序的托盘图标按 XEmbed 嵌进服务端的嵌入窗口,
    /// 经 <see cref="IX11ServerHost.SystemTrayIconAdded" /> 交给宿主画成宿主自己的托盘图标。默认关:宿主得真把它们显示出来 ——
    /// 有托盘时程序会「关闭到托盘」,宿主不显示的话关掉的窗口就再也找不回来。
    /// </summary>
    public bool SystemTray { get; init; }

    /// <summary>
    /// 单窗口(rootful)模式:整个根窗口作为一个顶层交给宿主(<see cref="X11Server.Screen" />),宿主开一个原生窗口显示整块桌面,
    /// 远端的窗口管理器照常管理顶层 —— 跑完整的远端桌面(xfce、MATE)或图形安装器时用。服务端不再当窗口管理器(不占 <c>WM_S0</c>、
    /// 不写 <c>_NET_SUPPORTED</c> 这些,根窗口的 SubstructureRedirect 让给客户端),也不当 XSETTINGS 管理器与托盘(<see cref="SystemTray" /> 不起作用),
    /// 那些归远端桌面自己的守护进程。默认关(rootless:每个顶层一个原生窗口)。
    /// </summary>
    public bool Rootful { get; init; }

    /// <summary>托盘图标嵌入窗口的边长(像素,16–128,默认 24)。图标程序照这个尺寸画。</summary>
    public int SystemTrayIconSize { get; init; } = 24;

    /// <summary>
    /// 窗口管理器的名字(<c>_NET_SUPPORTING_WM_CHECK</c> 窗口上的 <c>_NET_WM_NAME</c>)。默认 <c>LG3D</c>:服务端占着 <c>WM_S0</c> 与根窗口的
    /// SubstructureRedirect,Java(AWT / Swing)据此认定有窗口管理器,再按这个名字决定它套不套外框 —— 不认得的名字一律当成会套外框,
    /// 于是一直等 ReparentNotify、不理 ConfigureNotify(最大化、改尺寸之后内容不重排,假定有 25 像素的标题栏)。<c>LG3D</c> 是 Java 认得的
    /// 「不套外框」的名字(实测 OpenJDK 17:识别为 LookingGlass,边距 0,最大化与改尺寸都照常重排)。改名之前先用 Swing 程序验一遍。
    /// </summary>
    public string WindowManagerName { get; init; } = "LG3D";

    /// <summary>
    /// 一个客户端能占的内存上限(字节):像素图、顶层窗口的缓冲、DOUBLE-BUFFER 的后缓冲、属性值、RENDER 字形、XFIXES 区域,
    /// 以及每个资源的一份固定开销。超了的请求回 BadAlloc,而不是让服务端(连同宿主进程)耗尽内存。默认 1 GiB。
    /// </summary>
    public long MaxClientMemory { get; init; } = 1L << 30;

    /// <summary>全部客户端合计的内存上限(字节,口径同 <see cref="MaxClientMemory" />)。默认 2 GiB。</summary>
    public long MaxTotalMemory { get; init; } = 2L << 30;

    /// <summary>
    /// 诊断日志:连接进出、每条发给客户端的协议错误(带操作码与最近几条请求)、未实现的请求、服务端内部与宿主回调的异常。
    /// null = 不记。服务端的全部诊断只走这一个出口。在执行线程或连接的读写线程上调用,不要在里面阻塞。
    /// </summary>
    public Action<string>? Log { get; init; }

    /// <summary>校验各项取值;不合法抛 <see cref="ArgumentException" />(参数名为 <c>options</c>)。</summary>
    internal void Validate()
    {
        const string param = "options";
        Require(DisplayNumber is >= 0 and <= MaxDisplayNumber, $"{nameof(DisplayNumber)} 必须在 0–{MaxDisplayNumber} 之间。");
        Require(ListenAddress is not null, $"{nameof(ListenAddress)} 不能为 null。");
        Require(ScreenWidth is >= 1 and <= MaxScreenSize && ScreenHeight is >= 1 and <= MaxScreenSize,
            $"{nameof(ScreenWidth)} / {nameof(ScreenHeight)} 必须在 1–{MaxScreenSize} 之间。");
        Require(Dpi >= 1, $"{nameof(Dpi)} 必须 ≥ 1。");
        Require(ScaleFactor >= 1, $"{nameof(ScaleFactor)} 必须 ≥ 1。");
        Require(SystemTrayIconSize is >= 16 and <= 128, $"{nameof(SystemTrayIconSize)} 必须在 16–128 之间。");
        Require(!string.IsNullOrEmpty(KeyboardLayout), $"{nameof(KeyboardLayout)} 不能为空。");
        Require(Vendor is not null && WindowManagerName is not null, $"{nameof(Vendor)} / {nameof(WindowManagerName)} 不能为 null。");
        Require(MaxClientMemory >= 1 && MaxTotalMemory >= 1, $"{nameof(MaxClientMemory)} / {nameof(MaxTotalMemory)} 必须 ≥ 1。");
        Require(AuthorizationCookie is null or { Length: >= MinAuthorizationCookieLength and <= MaxAuthorizationCookieLength },
            $"{nameof(AuthorizationCookie)} 的长度必须在 {MinAuthorizationCookieLength}–{MaxAuthorizationCookieLength} 字节之间。");
        Require(UnixSocketPath is null or "" || FitsSocketAddress(UnixSocketPath),
            $"{nameof(UnixSocketPath)} 太长,放不进 Unix 套接字的地址(连同 Linux 抽象命名空间里的同名套接字)。");

        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new ArgumentException(message, param);
            }
        }
    }

    /// <summary>
    /// 路径放得进 sockaddr_un(Linux 上连同抽象命名空间里多一个前导 NUL 的同名套接字)。原先到 StartAsync 才抛
    /// ArgumentOutOfRangeException,宿主只接几类异常,服务端不释放、状态卡在「启动中」。不支持 Unix 套接字的系统上不查。
    /// </summary>
    private static bool FitsSocketAddress(string path)
    {
        if (!System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
        {
            return true;
        }
        try
        {
            _ = new System.Net.Sockets.UnixDomainSocketEndPoint(path);
            if (OperatingSystem.IsLinux())
            {
                _ = new System.Net.Sockets.UnixDomainSocketEndPoint("\0" + path);
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
