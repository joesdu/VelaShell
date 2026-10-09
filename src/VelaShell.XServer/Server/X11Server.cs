// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(显示号与传输)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型)、§6(宿主接口)
//
//   这个文件是服务端的全部公开面:构造、生命周期、宿主注入。各方法只校验参数、把工作排进执行线程,
//   真正的处理在各领域的 partial 文件里(Apply* 系列);其余 partial 文件里没有公开成员。

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VelaShell.XServer.Input;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

/// <summary>
/// 一个可嵌入的 X11 服务端(rootless,软件绘图)。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个执行线程</b>执行全部客户端请求、宿主注入的输入与收尾工作 —— X 的语义是全局串行的,
/// 所有可变状态只在这个线程上被碰,因此不加锁(架构 §5)。唯一的例外是像素:宿主在别的线程上经
/// <see cref="XTopLevelWindow.ReadPixels" /> 读顶层窗口的像素,执行线程在执行一批工作项期间持有同一把像素锁。
/// </para>
/// <para>
/// 连接可以来自 TCP 与 Unix 套接字(<see cref="StartAsync" />),也可以直接交一条流进来
/// (<see cref="ServeAsync" />)—— 后者让宿主不必经本机端口:SSH 的 x11 通道本身就是一条双工流。
/// </para>
/// <para>
/// 宿主方法的命名:<c>Inject*</c> 是合成的用户输入;名字里带 <c>TopLevel</c>、第一个参数是 <see cref="XTopLevelWindow" /> 的,
/// 是宿主作为窗口管理器对那个顶层的动作,按「动词 + TopLevel + 宾语」起名(<c>MoveTopLevel</c>、<c>SetTopLevelStates</c>、
/// <c>ChangeTopLevelStates</c>、<c>KillTopLevelClient</c>);不带 <c>TopLevel</c> 的 <c>Set*</c> 是宿主那边的环境变了、换进服务端
/// (键位表、显示器布局、DPI、锁定键、剪贴板内容)。它们都可以在任意线程上调、立即返回,参数不合法时当场抛异常;
/// 指名的窗口在执行时已经不在(客户端刚销毁了它)时静默忽略。
/// </para>
/// <para>
/// 构造时执行线程就开始运行:构造出来的实例即使从没 <see cref="StartAsync" />,也要 <see cref="DisposeAsync" />。
/// </para>
/// </remarks>
public sealed partial class X11Server : IAsyncDisposable
{
    /// <summary>显示器上限:RANDR 的 CRTC / 输出 / 模式 ID 各占 16 个服务端 ID。</summary>
    public const int MaxMonitors = 16;

    internal const uint RootWindowId = 0x00000100;
    internal const uint DefaultColormapId = 0x00000020;
    internal const uint RootVisualId = 0x00000021;
    internal const uint ArgbVisualId = 0x00000022;

    private readonly X11ServerOptions _options;

    /// <summary>
    /// 构造时拷下来的 <see cref="X11ServerOptions.AuthorizationCookie" />:选项只持有调用方数组的引用,原先构造之后调用方改了那个数组,授权跟着变。
    /// </summary>
    private readonly byte[]? _cookie;

    /// <summary>对宿主的回调都经它排队,执行线程放锁之后再调(见 RunLoopAsync)。</summary>
    private readonly DeferredHost _host;

    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<uint, XResource> _resources = [];
    private readonly Dictionary<int, XClient> _clients = [];
    private readonly Keymap _keymap = new();
    private readonly Dictionary<XWindow, List<XRect>> _damage = [];
    private readonly Dictionary<XWindow, XTopLevelWindow> _topLevelHandles = [];

    /// <summary>像素锁与宿主的让行计数(见 <see cref="PixelGate" />)。</summary>
    private readonly PixelGate _pixelGate = new();

    /// <summary>GLX 扩展(自成一体的一个类;别的扩展还是 partial 文件)。</summary>
    private readonly GlxExtension _glx;

    private readonly Task _loopTask;
    private int _started;
    private int _disposed;

    /// <summary>StartAsync 开监听与 DisposeAsync 收监听互斥(两边同时跑时开起来的监听没人收)。</summary>
    private readonly Lock _listenGate = new();

    /// <summary>收工做完(<see cref="Completion" />);第二个调 DisposeAsync 的等它。</summary>
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>用选项与宿主构造;宿主为 null 时不显示任何东西(无头,测试与诊断用)。</summary>
    /// <exception cref="ArgumentException">选项不合法(见 <see cref="X11ServerOptions" /> 各项的取值范围)。</exception>
    public X11Server(X11ServerOptions? options = null, IX11ServerHost? host = null)
    {
        _options = options ?? new X11ServerOptions();
        _options.Validate();
        _cookie = _options.AuthorizationCookie?.ToArray();
        _host = new DeferredHost(host ?? NullHost.Instance, Log);
        Root = CreateRootWindow();
        _resources[Root.Id] = Root;
        InitMonitors();
        RebuildRandRModes();
        InitRootful();
        _resources[DefaultColormapId] = new XColormap(DefaultColormapId, null, RootVisualId);
        InitAtoms();
        _glx = new GlxExtension(this);
        InitExtensions();
        InitXSettings();
        InitSystemTray();
        InitSyncCounters();
        PublishXkbRulesNames();
        InitEwmh();
        _pointerWindow = Root;
        _focus = Root;   // 初始焦点是 PointerRoot(与 X.Org 一致;窗口管理器 —— 这里是宿主 —— 之后再把焦点给具体的顶层)
        _loopTask = Task.Run(RunLoopAsync);
    }

    /// <summary>显示号 N(<see cref="X11ServerOptions.DisplayNumber" />)。</summary>
    public int DisplayNumber => _options.DisplayNumber;

    /// <summary>
    /// 给本机 X 客户端用的 <c>DISPLAY</c>:在 Unix 套接字上监听时是 <c>:N</c>(走套接字,MIT-SHM 可用),
    /// 只有 TCP 时是 <c>localhost:N.0</c>;<see cref="StartAsync" /> 之前、或者两种都没在监听(只经 <see cref="ServeAsync" /> 喂流)时为 null。
    /// </summary>
    public string? Display { get; private set; }

    /// <summary>TCP 监听的端口(6000 + 显示号);没监听时为 0。</summary>
    public int Port { get; private set; }

    internal XWindow Root { get; }

    internal X11ServerOptions Options => _options;

    /// <summary>服务端时间(毫秒,32 位回绕)—— 事件里的 time 字段。</summary>
    internal uint Now => unchecked((uint)_clock.ElapsedMilliseconds);

    // ================================================================== 生命周期

    /// <summary>
    /// 开始监听:TCP 6000 + N(<see cref="X11ServerOptions.ListenTcp" />)与 Unix 套接字(<see cref="X11ServerOptions.UnixSocketPath" />)。
    /// 类 Unix 上同时按 Xserver(1) 的约定持有 <c>/tmp/.X{N}-lock</c>(Xvfb、<c>xvfb-run -a</c> 挑显示号时看它),收工时删掉。
    /// 别的服务端持着这个锁、TCP 端口被占用、或者 Unix 套接字的名字被别人占着(Linux 的抽象名有人 bind 了、套接字文件后面有人在听或删不掉)时抛
    /// <see cref="SocketException" />(<see cref="SocketError.AddressAlreadyInUse" />),已经开起来的监听一并关掉 —— 这个显示号不能用,换一个;
    /// Unix 套接字因别的原因建不起来(目录建不了、属主不可信)只记日志 —— 但配置了要监听、结果一种传输也没开起来时抛 <see cref="IOException" />
    /// (原先照常返回,<see cref="Display" /> 是 null,调用方以为开起来了)。两种都没配置(只经 <see cref="ServeAsync" /> 喂流)时什么也不做。
    /// 失败之后已经开起来的监听一并关掉,可以再调一次(比如换个显示号之前先等占用的程序退出)。
    /// </summary>
    /// <exception cref="InvalidOperationException">已经开始监听了。</exception>
    /// <exception cref="ObjectDisposedException">服务端已经释放,或者正在释放(与 <see cref="DisposeAsync" /> 同时调)。</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("服务端已经在监听了。");
        }
        lock (_listenGate)
        {
            // 与 DisposeAsync 收监听互斥:那边先收了这边才开,开起来的监听器、套接字文件与显示号锁就没人收了。
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                ClaimDisplayLock();
                if (ListensOnTcp)
                {
                    StartTcpListener();
                }
                StartUnixListeners(_lifetime.Token);
                if (_listener is null && _unixListeners.Count == 0 && (ListensOnTcp || UnixSocketPath is not null))
                {
                    throw new IOException("没有一种传输监听起来(Unix 套接字建不起来的原因见日志)。");
                }
            }
            catch
            {
                StopListeners();
                Volatile.Write(ref _started, 0);   // 原先失败之后再调报「已经在监听了」,这个实例就再也开不起来
                throw;
            }
        }
        Display = DisplayAddress();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 在一条已经建立的双工流上服务一个 X 客户端,直到它断开或服务端收工。按 <see cref="X11ServerOptions.AuthorizationCookie" /> 查授权。
    /// </summary>
    /// <param name="stream">双工流(TCP、Unix 套接字……)。服务端<b>不释放</b>它:任务结束后由调用方释放。</param>
    /// <param name="isLocal">
    /// 对端算不算本机连接,只在<b>没配置</b> cookie 时起作用(那时只接受本机连接)。配置了 cookie 时一律要客户端带上它 ——
    /// 已经由别的环节验过身份的流(比如 SSH 转发已核对过假 cookie)用 <see cref="ServeAuthenticatedAsync(Stream, CancellationToken)" />。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> 为 null(当场抛,不是放进返回的任务里)。</exception>
    /// <exception cref="ObjectDisposedException">服务端已经释放(当场抛)。</exception>
    public Task ServeAsync(Stream stream, bool isLocal, CancellationToken cancellationToken = default)
    {
        CheckServable(stream);
        return ServeCoreAsync(stream, new Peer(isLocal, SameHost: false, Uid: null, LocalUser: false, Authenticated: false), cancellationToken);
    }

    /// <summary>
    /// 在一条<b>调用方已经验过身份</b>的双工流上服务一个 X 客户端:不再查授权(不看 cookie,也不看是不是本机)。
    /// </summary>
    /// <param name="stream">
    /// 只能是进程内接过来的流,比如 SSH 的 x11 通道经连接器接进来 —— 转发层已经核对过远端给的假 cookie。
    /// 服务端<b>不释放</b>它:任务结束后由调用方释放。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> 为 null(当场抛)。</exception>
    /// <exception cref="ObjectDisposedException">服务端已经释放(当场抛)。</exception>
    public Task ServeAuthenticatedAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ServeAuthenticatedAsync(stream, label: null, cancellationToken);

    /// <summary>同 <see cref="ServeAuthenticatedAsync(Stream, CancellationToken)" />,并给这条连接起个名字(比如 <c>user@host:22</c>)。</summary>
    /// <param name="stream">见 <see cref="ServeAuthenticatedAsync(Stream, CancellationToken)" />。</param>
    /// <param name="label">
    /// 连接的来历,进日志与 <see cref="GetClientsAsync" />、<see cref="XTopLevelSnapshot.ClientLabel" /> —— 宿主据此说得出「哪个会话的程序」。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> 为 null(当场抛)。</exception>
    /// <exception cref="ObjectDisposedException">服务端已经释放(当场抛)。</exception>
    public Task ServeAuthenticatedAsync(Stream stream, string? label, CancellationToken cancellationToken = default) =>
        ServeAuthenticatedAsync(stream, label, XClientTrust.Trusted, cancellationToken);

    /// <summary>
    /// 同 <see cref="ServeAuthenticatedAsync(Stream, string?, CancellationToken)" />,并指明这条连接的信任级别:
    /// <see cref="XClientTrust.Untrusted" /> 的客户端按 SECURITY 扩展的非受信语义受限(<c>ssh -X</c> 那一档)——
    /// 进程内的连接器用不着 <c>xauth generate</c> 去签受限 cookie,直接在这里说明。
    /// </summary>
    /// <param name="stream">见 <see cref="ServeAuthenticatedAsync(Stream, CancellationToken)" />。</param>
    /// <param name="label">见 <see cref="ServeAuthenticatedAsync(Stream, string?, CancellationToken)" />。</param>
    /// <param name="trust">信任级别。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> 为 null(当场抛)。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="trust" /> 不是定义了的值(当场抛)。</exception>
    /// <exception cref="ObjectDisposedException">服务端已经释放(当场抛)。</exception>
    public Task ServeAuthenticatedAsync(Stream stream, string? label, XClientTrust trust, CancellationToken cancellationToken = default)
    {
        CheckServable(stream);
        if (trust is not (XClientTrust.Trusted or XClientTrust.Untrusted))
        {
            throw new ArgumentOutOfRangeException(nameof(trust), trust, "未定义的信任级别。");
        }
        return ServeCoreAsync(stream, new Peer(IsLocal: true, SameHost: false, Uid: null, LocalUser: false, Authenticated: true, label,
            Untrusted: trust == XClientTrust.Untrusted), cancellationToken);
    }

    /// <summary>
    /// 连着的 X 客户端,连同以 Retain 模式断开、资源还留着的:编号、宿主给的名字、资源数、记在账上的内存、映射着的顶层。
    /// 宿主拿它列「谁连着、来自哪个会话、占多少内存」,停服前说得出「会断开 N 个程序」。
    /// </summary>
    public Task<IReadOnlyList<XClientInfo>> GetClientsAsync() => InvokeAsync(SnapshotClients);

    /// <summary>
    /// 断开编号为 <paramref name="clientId" /> 的客户端(<see cref="XClientInfo.Id" />,KillClient 语义):以 Retain 模式断开过的,
    /// 销毁它留下的资源。没有这个编号时什么也不做。
    /// </summary>
    public void DisconnectClient(int clientId) => Post(null, () =>
    {
        if ((_clients.GetValueOrDefault(clientId) ?? _retainedClients.GetValueOrDefault(clientId)) is { } client)
        {
            KillClientOf(client);
        }
    });

    /// <summary>
    /// 收工做完时完成(<see cref="DisposeAsync" /> 断开了所有客户端、收掉了监听与执行线程)。宿主可以拿它知道服务端什么时候真正停了。
    /// </summary>
    public Task Completion => _shutdown.Task;

    /// <summary>
    /// 收工:关监听、断开所有客户端、停执行线程。可以多次调、可以并发调:后来的调用等第一次收完才返回(原先立即返回,
    /// 调用方以为已经停了,其实还在收)。
    /// </summary>
    /// <remarks>
    /// 收工时<b>不</b>对还映射着的顶层发 <see cref="IX11ServerHost.TopLevelUnmapped" />:宿主自己收掉它的原生窗口
    /// (先脱离、再释放服务端)。之后句柄的 <see cref="XTopLevelWindow.IsAlive" /> 都是 false,再用它们调服务端的方法会被忽略或抛
    /// <see cref="ObjectDisposedException" />(ServeAsync 一族、StartAsync)。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _shutdown.Task.ConfigureAwait(false);   // 第二个调用者等第一个收完
            return;
        }
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            _shutdown.TrySetResult();   // 收尾半途抛了也要放后来的调用者走
        }
    }

    /// <summary><see cref="DisposeAsync" /> 的收尾,只走一次。</summary>
    private async Task ShutdownAsync()
    {
        lock (_listenGate)
        {
            StopListeners();
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        foreach (Task? task in (Task?[])[_acceptTask, _loopTask])
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 收工(OperationCanceledException);别的异常也不在这里重抛 —— 重抛就跳过了下面断开客户端、摘共享内存段的收尾。
            }
        }
        foreach (XClient client in _clients.Values)
        {
            client.Abort();
        }
        foreach (XTopLevelWindow handle in _topLevelHandles.Values)
        {
            handle.Retire();   // 执行线程已经停了:句柄都不再指着活着的窗口
        }
        DetachShmSegments(_resources.Values);
        await WaitForConnectionsAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // ================================================================== 宿主注入:输入

    /// <summary>
    /// 指针在顶层窗口里移动(内区坐标,物理像素;可以为负或超出窗口 —— 拖动时指针被捕获在窗口外)。
    /// 坐标与 <see cref="MoveTopLevel" /> 一样按 X 的 16 位范围核对。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">坐标超出 −32768…32767。</exception>
    public void InjectPointerMotion(XTopLevelWindow window, int x, int y)
    {
        CheckHandle(window);
        CheckCoordinate(x, nameof(x));
        CheckCoordinate(y, nameof(y));
        Post(null, () =>
        {
            if (InputTarget(window) is { } top)
            {
                ApplyPointerMotion(top, x, y);
            }
        });
    }

    /// <summary>
    /// 按钮按下 / 松开(内区坐标)。1 左、2 中、3 右;滚轮向上 4、向下 5、向左 6、向右 7(宿主应当为每格滚动注入一次按下 + 松开);
    /// 8、9 是后退 / 前进侧键。窗口已经不在时,按下照例忽略,松开照样生效(不挪指针)—— 按下之后窗口没了(弹出菜单一点就关),
    /// 松开要是也丢了,X 这边那个按钮就一直按着、自动抓取也不解除。X 这边并没按着这个按钮时松开不投递。
    /// 坐标的范围同 <see cref="InjectPointerMotion" />。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">坐标超出 −32768…32767,或按钮不在 1–255。</exception>
    public void InjectPointerButton(XTopLevelWindow window, int x, int y, int button, bool pressed)
    {
        CheckHandle(window);
        CheckCoordinate(x, nameof(x));
        CheckCoordinate(y, nameof(y));
        ArgumentOutOfRangeException.ThrowIfLessThan(button, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(button, 255);
        Post(null, () =>
        {
            if (InputTarget(window) is { } top)
            {
                ApplyPointerButton(top, x, y, button, pressed);
            }
            else if (!pressed)
            {
                ApplyPointerButtonRelease(button);
            }
        });
    }

    /// <summary>指针离开了所有顶层窗口(移到了宿主的其他窗口或桌面上)。</summary>
    public void InjectPointerLeave() => Post(null, ApplyPointerLeave);

    /// <summary>
    /// 宿主那边拖着东西(本机的文件、文本)在这个顶层的内区 (x, y) 上:服务端替宿主扮演 XDND 的源(freedesktop XDND 第 5 版),
    /// 给指针所在、声明了 XdndAware 的 X 窗口发 XdndEnter / XdndPosition。<paramref name="types" /> 是放下时能给的数据类型
    /// (MIME 类型或 <c>UTF8_STRING</c> 这类 X 的目标名,文件给 <c>text/uri-list</c>);目标接不接受见 <see cref="IsDragAccepted" />。
    /// 指针每动一下调一次;离开时调 <see cref="InjectDragLeave" />,松手时调 <see cref="InjectDrop" />。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="types" /> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">坐标超出 X 的 16 位范围。</exception>
    public void InjectDragOver(XTopLevelWindow window, int x, int y, IReadOnlyList<string> types)
    {
        CheckHandle(window);
        CheckCoordinate(x, nameof(x));
        CheckCoordinate(y, nameof(y));
        ArgumentNullException.ThrowIfNull(types);
        string[] copy = [.. types];
        Post(null, () =>
        {
            if (InputTarget(window) is { } top)
            {
                ApplyDragOver(top, x, y, copy);
            }
        });
    }

    /// <summary>拖着的东西离开了 X 窗口,或宿主那边取消了拖放:给目标发 XdndLeave。没在拖时什么也不做。</summary>
    public void InjectDragLeave() => Post(null, ApplyDragLeave);

    /// <summary>
    /// 在这个顶层的内区 (x, y) 上松手:目标最后说接受就发 XdndDrop,目标经 XdndSelection 取 <paramref name="data" />(类型 → 字节,
    /// 类型的写法同 <see cref="InjectDragOver" />);不接受、或放在不接受拖放的地方,就发 XdndLeave。最后一条 XdndStatus 还没回来时先等它
    /// (至多 3 秒)。宿主的文件要先传到远端、把远端路径写成 <c>text/uri-list</c> 再调这个 —— 目标拖着时取不到数据。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">坐标超出 X 的 16 位范围。</exception>
    public void InjectDrop(XTopLevelWindow window, int x, int y, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> data)
    {
        CheckHandle(window);
        CheckCoordinate(x, nameof(x));
        CheckCoordinate(y, nameof(y));
        ArgumentNullException.ThrowIfNull(data);
        Dictionary<string, ReadOnlyMemory<byte>> copy = data.ToDictionary(p => p.Key, p => (ReadOnlyMemory<byte>)p.Value.ToArray());
        Post(null, () =>
        {
            if (InputTarget(window) is { } top)
            {
                ApplyDrop(top, x, y, copy);
            }
            else
            {
                ApplyDragLeave();
            }
        });
    }

    /// <summary>
    /// 宿主那边的拖放(<see cref="InjectDragOver" />)此刻的目标说会接受:宿主据此显示「可以放」的光标。异步更新(目标回 XdndStatus 之后),
    /// 任何线程上都可以读。
    /// </summary>
    public bool IsDragAccepted => _dragAccepted;

    /// <summary>按键按下 / 松开(X 键码,见 <see cref="XKeycodes" />)。按键送往当前的键盘焦点(<see cref="FocusTopLevel" />)。</summary>
    public void InjectKey(byte keycode, bool pressed) => InjectKey(keycode, pressed, repeat: false);

    /// <summary>
    /// 同 <see cref="InjectKey(byte, bool)" />;<paramref name="repeat" /> 为真时是宿主的自动重复(键一直按着,系统又报了一次按下 ——
    /// 重复的节奏由宿主定)。服务端按 X 的语义处理:自动重复关了(<c>xset r off</c>、这个键不重复、它是修饰键)就丢掉;否则客户端收到
    /// 一个 KeyPress,没开 XKB DetectableAutoRepeat 的核心客户端先收一个 KeyRelease(「按下、松开、按下……」),XI2 的 KeyPress 带 KeyRepeat 标志。
    /// 服务端认为这个键没按着时当普通的按下。宿主的松开照常用 <paramref name="pressed" /> 为假、<paramref name="repeat" /> 为假报。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="repeat" /> 为真而 <paramref name="pressed" /> 为假。</exception>
    public void InjectKey(byte keycode, bool pressed, bool repeat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keycode, XKeymap.MinKeycode);
        if (repeat && !pressed)
        {
            throw new ArgumentException("自动重复只有按下,没有松开。", nameof(repeat));
        }
        Post(null, () => ApplyKey(keycode, pressed, repeat));
    }

    /// <summary><see cref="InjectText" /> 一次至多这么多 UTF-16 码元(输入法一次上屏的字远少于此)。</summary>
    public const int MaxInjectedTextLength = 4096;

    /// <summary>
    /// 输入一串字(宿主的输入法组好、上屏的文字):送往当前的键盘焦点,与用户在 X 窗口里按键一样。X 程序只认键码,每个字找一个空着的键码、
    /// 把它的键值改成这个字的 Unicode 键值再按下松开(客户端各收到一次 MappingNotify);键位表里本来就有、不按修饰键就打得出来的字直接按那个键。
    /// 远端不用装输入法框架,所有工具包都能收到;没有预编辑,候选框由本机的输入法自己显示。换行按 Return、制表按 Tab,其余控制字符不输入。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text" /> 超过 <see cref="MaxInjectedTextLength" /> 个 UTF-16 码元。</exception>
    public void InjectText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxInjectedTextLength)
        {
            throw new ArgumentException($"一次至多 {MaxInjectedTextLength} 个字符。", nameof(text));
        }
        if (text.Length != 0)
        {
            Post(null, () => ApplyInjectText(text, 0));
        }
    }

    /// <summary>
    /// 宿主的锁定键状态(CapsLock、NumLock)换进服务端,不合成按键:客户端收到 XKB 的 StateNotify,之后的按键按它解释。
    /// 宿主在 X 窗口得到焦点时按系统的真实状态推一次 —— 服务端起步时两个都关着,用户在别的程序里切过也不会知道;
    /// 原先 Windows 上开着 NumLock,小键盘在 X 里却是方向键。
    /// </summary>
    public void SetLockState(bool capsLock, bool numLock) => Post(null, () => ApplyLockState(capsLock, numLock));

    /// <summary>
    /// 用户在宿主自己的界面里有动静(在本机终端里打字、点鼠标):空闲计时归零,与 X 窗口里的输入一样 —— 远端程序经 MIT-SCREEN-SAVER /
    /// SYNC 的 IDLETIME 看到的空闲时间不再只按 X 输入算(原先用户整小时在本机终端里打字,远端的「离开」状态、空闲锁屏照样触发)。
    /// 不产生任何输入事件。可以在任意线程上调,调得再频繁也只是每 250 毫秒至多排一个工作项。
    /// </summary>
    public void NoteUserActivity()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastHostActivity);
        if (now - last < 250 || Interlocked.CompareExchange(ref _lastHostActivity, now, last) != last)
        {
            return;
        }
        Post(null, NoteInputActivity);
    }

    private long _lastHostActivity = long.MinValue / 2;

    // ================================================================== 宿主注入:窗口管理器

    /// <summary>宿主让某个顶层窗口得到键盘焦点(用户激活了它的原生窗口);null = 所有顶层都失去焦点。</summary>
    public void FocusTopLevel(XTopLevelWindow? window)
    {
        if (Rootful)
        {
            return;   // 单窗口模式:焦点归远端的窗口管理器(没有窗口管理器时是 PointerRoot),宿主窗口失去焦点不改 X 的焦点
        }
        if (window is null)
        {
            Post(null, () => ApplyFocus(null));
            return;
        }
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyFocus(top);
            }
        });
    }

    /// <summary>用户移动了原生窗口:外框左上角移到根窗口坐标 (<paramref name="x" />, <paramref name="y" />),并按 ICCCM 发一条合成的 ConfigureNotify。</summary>
    public void MoveTopLevel(XTopLevelWindow window, int x, int y)
    {
        CheckHandle(window);
        CheckCoordinate(x, nameof(x));
        CheckCoordinate(y, nameof(y));
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyMove(top, x, y);
            }
        });
    }

    /// <summary>用户缩放了原生窗口:改内区尺寸(客户端收到 ConfigureNotify 与 Expose,重画)。</summary>
    public void ResizeTopLevel(XTopLevelWindow window, int width, int height)
    {
        CheckHandle(window);
        CheckSize(width, nameof(width));
        CheckSize(height, nameof(height));
        Post(null, () =>
        {
            if (ReferenceEquals(window, _screenHandle))
            {
                ApplyScreenResize(width, height);   // 单窗口模式:缩放屏幕窗口就是改屏幕尺寸
            }
            else if (LiveTopLevel(window) is { } top)
            {
                ApplyResize(top, width, height);
            }
        });
    }

    /// <summary>
    /// 用户点了原生窗口的关闭按钮:客户端声明了 WM_DELETE_WINDOW 就礼貌地请它自己关(ICCCM §4.2.8),
    /// 否则断开该客户端(与窗口管理器的 XKillClient 一致)。override-redirect 的窗口(弹出菜单、提示框)不归窗口管理器管,忽略。
    /// </summary>
    public void CloseTopLevel(XTopLevelWindow window)
    {
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyClose(top);
            }
        });
    }

    /// <summary>
    /// 强制结束这个顶层所属的客户端(KillClient 语义):断开它的连接;以 Retain 模式断开过、只剩资源的,把它留下的资源全部销毁。
    /// 用在客户端卡死的时候 —— 声明了 WM_DELETE_WINDOW 的程序卡住了,<see cref="CloseTopLevel" /> 关不掉它;通常在
    /// <see cref="XNotRespondingRequest" /> 之后、用户确认了才调。会连同这个客户端的其它窗口一起关掉。
    /// </summary>
    public void KillTopLevelClient(XTopLevelWindow window)
    {
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { Owner: { } owner })
            {
                KillClientOf(owner);
            }
        });
    }

    /// <summary>
    /// 卡住时的恢复手段:解除一切指针 / 键盘抓取(核心、XI2、被动抓取激活的)并解冻设备,放开 GrabServer,把浮动的从设备挂回虚拟核心设备。
    /// 远端程序的菜单开着时 SSH 断网、笔记本睡眠或远端进程被 SIGSTOP —— 连接没断,抓取就一直在,所有会话的所有 X 窗口点不动、打不了字,
    /// 直到 SSH 保活超时;任何客户端执行一次 <c>xinput float</c>,核心鼠标在所有 X 程序里失效,断开也不恢复。
    /// 客户端照常收到 mode 为 Ungrab 的 crossing / 焦点事件与 HierarchyChanged,就像抓取自己解除了一样;被动抓取的登记不动。
    /// </summary>
    public void BreakGrabs() => Post(null, ApplyBreakGrabs);

    /// <summary>
    /// 宿主(窗口管理器)设定了窗口状态 —— 通常是照办了一个 <see cref="XStateChangeRequest" />,或用户点了原生窗口的最大化按钮。
    /// 服务端写 <c>_NET_WM_STATE</c> 与 <c>WM_STATE</c>,客户端据此更新外观。<see cref="XWindowStates.Focused" /> 由服务端按焦点维护,这里给的会被忽略。
    /// 整组覆盖:没给的状态都会去掉 —— 只改宿主管的那几个(最大化、全屏、最小化……)用 <see cref="ChangeTopLevelStates" />。
    /// </summary>
    public void SetTopLevelStates(XTopLevelWindow window, XWindowStates states)
    {
        CheckHandle(window);
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyStates(top, states);
            }
        });
    }

    /// <summary>
    /// 宿主(窗口管理器)改了窗口状态的一部分:加上 <paramref name="add" />、去掉 <paramref name="remove" />,其余状态原样保留 ——
    /// 客户端映射前自己设的 SkipTaskbar、Modal、Sticky、Below、DemandsAttention 不会因为用户点了一下最大化就被清掉
    /// (<see cref="SetTopLevelStates" /> 是整组覆盖)。服务端写 <c>_NET_WM_STATE</c> 与 <c>WM_STATE</c>;<see cref="XWindowStates.Focused" /> 由服务端维护,这里给的会被忽略。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="add" /> 与 <paramref name="remove" /> 有重叠的位。</exception>
    public void ChangeTopLevelStates(XTopLevelWindow window, XWindowStates add, XWindowStates remove)
    {
        CheckHandle(window);
        if ((add & remove & ~XWindowStates.Focused) != 0)
        {
            throw new ArgumentException("同一个状态不能既加又去。", nameof(remove));
        }
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyStates(top, (ReadNetWmStates(top) | add) & ~remove);
            }
        });
    }

    /// <summary>宿主给窗口加的装饰有多宽(<c>_NET_FRAME_EXTENTS</c>,物理像素,不能为负)。客户端据此计算外框位置。</summary>
    public void SetTopLevelFrameExtents(XTopLevelWindow window, XFrameExtents extents)
    {
        CheckHandle(window);
        if (extents.Left < 0 || extents.Right < 0 || extents.Top < 0 || extents.Bottom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extents), extents, "外框宽度不能为负。");
        }
        Post(null, () =>
        {
            if (LiveTopLevel(window) is { } top)
            {
                ApplyFrameExtents(top, extents);
            }
        });
    }

    // ================================================================== 宿主注入:配置

    /// <summary>
    /// 换键位表(宿主的键盘布局变了):<paramref name="keymap" /> 里列出的键码连同布局名、右 Alt 的角色一次换掉,
    /// XKB 描述随之重新推出。客户端各收到一次 MappingNotify(键盘,修饰键表变了时再加一次修饰键)与 XKB 的 MapNotify。
    /// </summary>
    public void SetKeymap(XKeymap keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var change = KeymapChange.From(keymap);
        Post(null, () => ApplyKeymap(change));
    }

    /// <summary>
    /// 宿主的显示器布局变了:把根窗口(虚拟桌面)改成 <paramref name="width" /> × <paramref name="height" />,
    /// 显示器换成 <paramref name="monitors" />(null 或空 = 一台覆盖全部;最多 <see cref="MaxMonitors" /> 台)。
    /// 客户端收到根窗口的 ConfigureNotify 与 RANDR 的 ScreenChangeNotify / RRNotify。
    /// </summary>
    public void SetScreenLayout(int width, int height, IReadOnlyList<XMonitor>? monitors = null)
    {
        CheckSize(width, nameof(width));
        CheckSize(height, nameof(height));
        IReadOnlyList<XMonitor> normalized = NormalizeMonitors(monitors, width, height, nameof(monitors));
        Post(null, () => ApplyScreenLayout(width, height, normalized));
    }

    /// <summary>
    /// 宿主的 DPI / 缩放变了(窗口挪到了另一台显示器、用户改了系统缩放):更新 XSETTINGS(Xft/DPI、Gdk/WindowScalingFactor、
    /// Gdk/UnscaledDPI)与根窗口的 RESOURCE_MANAGER(Xft.dpi)。GTK 立刻按新值重排;Xlib / Xft 与 Qt 程序在下次启动时生效。
    /// </summary>
    /// <param name="dpi">每英寸像素数(实际像素,如 2 倍缩放的 192),≥ 1。</param>
    /// <param name="scale">整数缩放倍数(GTK 的窗口缩放),≥ 1。</param>
    public void SetDisplayScale(int dpi, int scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        Post(null, () => ApplyDisplayScale(dpi, scale));
    }

    /// <summary>
    /// 剪贴板文本的上限(UTF-8 字节):宿主交来的(<see cref="SetClipboardText" />)超过它当场拒绝,从 X 客户端取来的超过它不交给宿主。
    /// </summary>
    public const int MaxClipboardBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 宿主的剪贴板有了新文本:服务端占有 CLIPBOARD(<see cref="X11ServerOptions.SyncPrimary" /> 时连同 PRIMARY),
    /// 之后 X 客户端粘贴拿到的就是它(大的分块按 INCR 交)。与刚交给宿主的文本相同时什么也不做(那是宿主把我们给的写回来了)。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">文本按 UTF-8 超过 <see cref="MaxClipboardBytes" />。</exception>
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxClipboardBytes || System.Text.Encoding.UTF8.GetByteCount(text) > MaxClipboardBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(text), text.Length, $"剪贴板文本超过 {MaxClipboardBytes} 字节(UTF-8)。");
        }
        Post(null, () => ApplyClipboardText(text));
    }

    // ================================================================== 参数校验

    /// <summary>
    /// ServeAsync 一族的参数在调用时就核对(与别的宿主方法一样当场抛):原先 null 与已释放都放进返回的任务里才报,
    /// 调用方不 await 就看不到。
    /// </summary>
    private void CheckServable(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void CheckHandle(XTopLevelWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.BelongsTo(_pixelGate))
        {
            throw new ArgumentException("这个窗口不是本服务端的。", nameof(window));
        }
    }

    private static void CheckCoordinate(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, short.MinValue, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, short.MaxValue, name);
    }

    private static void CheckSize(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, X11ServerOptions.MaxScreenSize, name);
    }

    /// <summary>句柄指的窗口此刻还是它自己的那个顶层窗口(没被销毁、没被 reparent 走)时返回窗口,否则 null。只在执行线程上调。</summary>
    private XWindow? LiveTopLevel(XTopLevelWindow handle) =>
        handle.Window is { IsTopLevel: true } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? current)
        && ReferenceEquals(current, handle)
            ? top
            : null;

    /// <summary>注入输入的落点:普通顶层同 <see cref="LiveTopLevel" />;单窗口模式的屏幕句柄是根窗口(坐标就是根坐标)。只在执行线程上调。</summary>
    private XWindow? InputTarget(XTopLevelWindow handle) => ReferenceEquals(handle, _screenHandle) ? Root : LiveTopLevel(handle);

    /// <summary>当前监听着的传输对应的 DISPLAY(见 <see cref="Display" />)。</summary>
    private string? DisplayAddress()
    {
        int n = _options.DisplayNumber;
        if (_unixListeners.Count != 0)
        {
            return $":{n}";
        }
        if (_listener is null)
        {
            return null;
        }
        IPAddress address = _options.ListenAddress;
        bool anyOrLoopback = IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        return $"{(anyOrLoopback ? "localhost" : address.ToString())}:{n}.0";
    }
}
