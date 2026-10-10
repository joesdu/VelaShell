// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」与附录 B「Connection Setup」
//   (客户端开场 12 字节 + 授权名 / 数据;成功回复的定长部分、FORMAT、SCREEN、DEPTH、VISUALTYPE 的布局;失败回复)
//   BIG-REQUESTS Extension(请求长度字段为 0 时后跟 4 字节的扩展长度;BigReqEnable,次操作码 0:回复 maximum-request-length)
//   第 10 节「Connection Close」(CloseDownMode = Destroy 时释放该连接的全部资源、选区、抓取)
//   请求「SetCloseDownMode」「ChangeHosts」「ListHosts」「SetAccessControl」(访问控制策略固定,不可改)

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>核心协议允许的最大请求长度(以 4 字节计)。</summary>
    internal const ushort MaxRequestLength = 65535;

    /// <summary>BIG-REQUESTS 打开后的最大请求长度(以 4 字节计,16 MB)。</summary>
    internal const uint MaxBigRequestLength = 4 * 1024 * 1024;

    /// <summary>读端缓冲。一批典型的绘图请求(几十到几百条)一次读进来。</summary>
    private const int InputBufferSize = 64 * 1024;

    /// <summary>写出端拼包缓冲。</summary>
    private const int OutputBufferSize = 64 * 1024;

    private int _nextClientIndex = 1;

    /// <summary>
    /// 以 RetainPermanent / RetainTemporary 收尾的客户端:资源还留在资源表里(协议第 10 节),它的编号不分给新连接 ——
    /// 否则新客户端的资源 ID 与留下来的撞上。KillClient 销毁这些资源之后编号才放回去。
    /// </summary>
    private readonly Dictionary<int, XClient> _retainedClients = [];

    /// <summary>
    /// 同时以 Retain 模式留着资源的客户端上限,超了的断开时照 Destroy 处理。保留的客户端占着编号:原先不设限,
    /// 任何已授权的客户端循环「连上 → RetainPermanent → 断开」254 次(不到一秒),之后所有新连接都收到「客户端已满」,
    /// 连不上就发不了 KillClient,只能重启服务端。真实用途(xsetroot 留下根窗口的像素图、会话管理器)只要一两个。
    /// </summary>
    internal const int MaxRetainedClients = 16;

    /// <summary>经 TCP / Unix 套接字接进来的连接(收工时等它们结束)。</summary>
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    private TcpListener? _listener;
    private Task? _acceptTask;

    // ------------------------------------------------------------------ TCP

    /// <summary>这次要不要听 TCP:<see cref="X11ServerOptions.ListenTcp" /> 没给时看配没配 cookie(零值取安全值)。</summary>
    private bool ListensOnTcp => _options.ListenTcp ?? _cookie is not null;

    private void StartTcpListener()
    {
        if (_cookie is null)
        {
            Log($"listening on TCP {_options.ListenAddress}:{6000 + _options.DisplayNumber} without a cookie: "
                + "any local user can connect, read windows and inject input");
        }
        TcpListener listener = new(_options.ListenAddress, 6000 + _options.DisplayNumber);
        if (OperatingSystem.IsWindows())
        {
            // SO_EXCLUSIVEADDRUSE:没设的话,同一个用户的别的进程(包括低完整性的)用 SO_REUSEADDR 绑更具体的地址照样绑得上 ——
            // 听 0.0.0.0 时它绑 127.0.0.1:6000+N,本机的连接就都落到它那里(实测 Windows 11)。别的系统上 SO_REUSEADDR 本来就绑不上正在听的端口。
            listener.ExclusiveAddressUse = true;
        }
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(listener, _lifetime.Token);
    }

    /// <summary>TCP 监听独占着地址(Windows 的 SO_EXCLUSIVEADDRUSE,见 <see cref="StartTcpListener" />);测试用。</summary>
    internal bool TcpListenerIsExclusive => _listener?.ExclusiveAddressUse == true;

    /// <summary>关掉全部监听(收工,或 <see cref="StartAsync" /> 半途失败时撤回已经开起来的)。接受循环随之结束。</summary>
    private void StopListeners()
    {
        _listener?.Stop();
        _listener = null;
        Port = 0;
        StopUnixListeners();
        ReleaseDisplayLock();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;   // 收工,或者监听已经关了(TcpListener 停了之后再 Accept 抛 InvalidOperationException)
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _listener), listener))
                {
                    return;
                }
                await AcceptFailedAsync("TCP", ex, cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                tcp.NoDelay = true;
                bool local = tcp.Client.RemoteEndPoint is IPEndPoint { Address: var address } && IPAddress.IsLoopback(address);
                TrackConnection(ServeAndDisposeAsync(tcp, local, cancellationToken));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                tcp.Dispose();   // 刚接进来对端就复位了:只丢这一条
            }
        }
    }

    /// <summary>
    /// 接受连接失败了,但监听还在:记一行(限流),按原因退避一下再接着接。原先接受循环遇到任何 SocketException 就永久退出 ——
    /// fd 用完(EMFILE / ENFILE)、accept 之前对端就复位(Windows 的 ConnectionReset、BSD 的 ECONNABORTED)都是暂时的,
    /// 之后本机 X 程序却再也连不进来,一行日志都没有(经 SSH 连接器来的不受影响,所以很难察觉)。
    /// </summary>
    private async Task AcceptFailedAsync(string transport, SocketException error, CancellationToken cancellationToken)
    {
        SocketError code = error.SocketErrorCode;
        Post(null, () =>
        {
            if (ShouldLogFrequent())
            {
                LogFrequent($"{transport} accept failed ({code}); still listening");
            }
        });
        if (code is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            return;   // 只是那一条连接没了
        }
        // fd 用完之类:立刻再 accept 还是失败,原地打转就是占满一个核。
        try
        {
            await Task.Delay(AcceptRetryDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收工:下一轮循环看到取消就退出。
        }
    }

    /// <summary>接受连接失败(fd 用完之类)后等这么久再试。</summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private async Task ServeAndDisposeAsync(TcpClient tcp, bool local, CancellationToken cancellationToken)
    {
        using (tcp)
        {
            try
            {
                await ServeCoreAsync(tcp.GetStream(), new Peer(local, SameHost: false, Uid: null, LocalUser: false, Authenticated: false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 服务端正在收工。
            }
        }
    }

    /// <summary>登记一个接进来的连接任务,结束时自动摘掉。</summary>
    private void TrackConnection(Task connection)
    {
        _connections.TryAdd(connection, 0);
        _ = connection.ContinueWith(t => _connections.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>收工时:接进来的连接随 _lifetime 取消而收工,给它们一点时间关掉套接字。</summary>
    private async Task WaitForConnectionsAsync()
    {
        try
        {
            await Task.WhenAll(_connections.Keys).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 收工阶段的异常不关心。
        }
    }

    // ------------------------------------------------------------------ 一条连接

    /// <summary>连接的对端:服务端对它知道多少(授权检查与 MIT-SHM 用)。</summary>
    /// <param name="IsLocal">来自本机(环回 TCP、Unix 套接字、进程内的流)。没配置 cookie 时只接受本机连接。</param>
    /// <param name="SameHost">经 Unix 套接字连进来、与服务端在同一个 IPC 命名空间里的:MIT-SHM 对它可见。</param>
    /// <param name="Uid">对端的 uid(Linux 上经 SO_PEERCRED,macOS / FreeBSD 上经 getpeereid);取不到为 null。</param>
    /// <param name="LocalUser">能确定对端就是运行服务端的这个用户(权限 0600 的套接字文件,或 uid 与本进程相同)。</param>
    /// <param name="Authenticated">调用方已经验过身份(<see cref="ServeAuthenticatedAsync(Stream, CancellationToken)" />),不再查授权。</param>
    /// <param name="Label">宿主给这条连接起的名字(比如它来自哪个 SSH 会话);进日志与 <see cref="XClientInfo" />。</param>
    /// <param name="Untrusted">宿主指明这条连接非受信(<see cref="XClientTrust.Untrusted" />)。</param>
    /// <param name="Pid">对端进程的 pid(Unix 套接字:Linux 经 SO_PEERCRED,macOS 经 LOCAL_PEERPID);不知道为 0。X-Resource 的 LocalClientPid 用。</param>
    internal readonly record struct Peer(bool IsLocal, bool SameHost, uint? Uid, bool LocalUser, bool Authenticated, string? Label = null,
        bool Untrusted = false, int Pid = 0);
    /// <summary>
    /// 连接建立的时限:读连接建立报文(12 字节的头与授权名 / 数据)、回失败,都要在这之内做完。
    /// 对端连上来却迟迟不发完(卡住的,或者故意占着不放的),到点就断开 —— 否则每个这样的连接都一直占着一个套接字和一个任务,
    /// 而 <see cref="MaxClients" /> 只数已经建立的客户端,拦不住它们。握手只是一个往返,走 SSH 转发的慢链路也绰绰有余。
    /// </summary>
    internal TimeSpan SetupTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 同时处在连接建立阶段(还没登记成客户端)的连接上限,超了新来的当场关掉。<see cref="MaxClients" /> 只数已经建立的客户端:
    /// 本机任何用户不带 cookie 开几万条连接、每条只发个头就挂着,每条占一个套接字、一个任务和缓冲,原先没有任何上限。
    /// </summary>
    internal const int MaxPendingSetups = 32;

    /// <summary>授权名与授权数据各自的长度上限(与 SSH 侧转发的 X11SetupMessage 一致;MIT-MAGIC-COOKIE-1 只要 16 字节)。</summary>
    internal const int MaxAuthFieldLength = 256;

    private int _pendingSetups;

    internal async Task ServeCoreAsync(Stream stream, Peer peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken ct = linked.Token;
        CancellationTokenSource? connection = null;
        // 连接建立阶段的读写用它:到了 SetupTimeout 还没发完就取消。等执行线程登记客户端那一步不计在内 ——
        // 那一步半途取消的话,执行线程照样登记了,却没人再用这个客户端。
        if (Interlocked.Increment(ref _pendingSetups) > MaxPendingSetups)
        {
            Interlocked.Decrement(ref _pendingSetups);
            XServerMetrics.RefusedConnections.Add(1, new KeyValuePair<string, object?>("reason", "too_many_setups"));
            return;   // 正在握手的连接太多:当场关掉(流由调用方释放)
        }
        bool pending = true;
        void SetupDone()
        {
            if (pending)
            {
                pending = false;
                Interlocked.Decrement(ref _pendingSetups);
            }
        }
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        setup.CancelAfter(SetupTimeout);

        XClient? client = null;
        Task? writer = null;
        try
        {
            byte[] head = new byte[12];
            await stream.ReadExactlyAsync(head, setup.Token).ConfigureAwait(false);
            bool bigEndian = head[0] switch
            {
                (byte)'B' => true,
                (byte)'l' => false,
                _ => throw new InvalidDataException("连接建立报文的字节序标记非法。"),
            };
            ushort major = Read16(head.AsSpan(2), bigEndian);
            int nameLength = Read16(head.AsSpan(6), bigEndian);
            int dataLength = Read16(head.AsSpan(8), bigEndian);
            if (nameLength > MaxAuthFieldLength || dataLength > MaxAuthFieldLength)
            {
                // 先按客户端给的长度分配的话,每条连接各 128 KB(进大对象堆);真实的授权数据只有几十字节。
                XServerMetrics.RefusedConnections.Add(1, new KeyValuePair<string, object?>("reason", "bad_setup"));
                await SendSetupFailureAsync(stream, bigEndian, "Authorization data too long", setup.Token).ConfigureAwait(false);
                return;
            }
            byte[] rest = new byte[XWire.Pad(nameLength) + XWire.Pad(dataLength)];
            await stream.ReadExactlyAsync(rest, setup.Token).ConfigureAwait(false);
            string authName = XWire.Latin1.GetString(rest, 0, nameLength);
            byte[] authData = rest.AsSpan(XWire.Pad(nameLength), dataLength).ToArray();

            if (major != 11)
            {
                XServerMetrics.RefusedConnections.Add(1, new KeyValuePair<string, object?>("reason", "bad_setup"));
                await SendSetupFailureAsync(stream, bigEndian, "Protocol version mismatch", setup.Token).ConfigureAwait(false);
                return;
            }
            // 不是服务端自己的 cookie、而 SECURITY 签过授权时,cookie 可能是签出来的那种:授权表只在执行线程上读写,
            // 留到登记时在那里核对(见 RegisterClient)。
            string? refused = Authorize(authName, authData, peer);
            byte[]? generated = null;
            if (refused is not null && authName == "MIT-MAGIC-COOKIE-1" && Volatile.Read(ref _authorizationCount) > 0)
            {
                (generated, refused) = (authData, null);
            }
            if (refused is { } reason)
            {
                XServerMetrics.RefusedConnections.Add(1, new KeyValuePair<string, object?>("reason", "authorization"));
                Post(null, () =>
                {
                    if (ShouldLogFrequent())
                    {
                        LogFrequent($"connection refused: {reason}");
                    }
                });
                await SendSetupFailureAsync(stream, bigEndian, reason, setup.Token).ConfigureAwait(false);
                return;
            }

            setup.CancelAfter(Timeout.InfiniteTimeSpan);   // 报文收齐了:下面等执行线程登记,不计时
            Task<(XClient? Client, string? Refused)> registering = InvokeAsync(() => RegisterClient(bigEndian, peer, generated));
            string? refusedAtRegistration;
            try
            {
                (client, refusedAtRegistration) = await registering.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 等的时候调用方取消了:登记照样会在执行线程上执行,登记成了就当场断开 —— 原先没人管它,永久占着一个编号。
                _ = registering.ContinueWith(t =>
                {
                    if (t.Result.Client is { } orphan)
                    {
                        Post(null, () =>
                        {
                            DisconnectClient(orphan);
                            orphan.Abort();
                            orphan.Dispose();
                        });
                    }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
            if (client is null)
            {
                setup.CancelAfter(SetupTimeout);
                await SendSetupFailureAsync(stream, bigEndian, refusedAtRegistration ?? "Maximum number of clients reached", setup.Token)
                    .ConfigureAwait(false);
                return;
            }
            SetupDone();   // 登记成了客户端:不再占「正在握手」的名额
            // 连接的读写还要跟着「服务端主动断开这个客户端」一起停。
            connection = CancellationTokenSource.CreateLinkedTokenSource(ct, client.Aborted);
            ct = connection.Token;
            writer = PumpOutputAsync(client, stream, ct);
            // 读端单独套一层缓冲:X 请求又小又密(常见 8–40 字节),不缓冲就是每条请求两次系统调用。
            // ⚠️ 只经它读、从不经它写 —— BufferedStream 读写共用一块缓冲,在不可寻址的流上混用会抛异常;
            // 写出端直接写底层流。
            BufferedStream input = new(stream, InputBufferSize);
            await ReadRequestsAsync(client, input, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            // 对端走了、乱发、连接建立超时,或者服务端在收工。
            if (client is null && setup.IsCancellationRequested && !linked.IsCancellationRequested)
            {
                Post(null, () =>
                {
                    if (ShouldLogFrequent())
                    {
                        LogFrequent("connection setup timed out");
                    }
                });
            }
        }
        finally
        {
            SetupDone();
            if (client is not null)
            {
                XClient gone = client;
                PostAfterRequests(gone, () => DisconnectClient(gone));   // 排在它还没执行的请求之后:发完请求就关连接的客户端,请求照样生效
                gone.Output.Writer.TryComplete();
            }
            if (writer is not null)
            {
                // 对端已经不读了:别再等积压的输出写完(对端半关闭时那会永远等下去)。
                connection?.Cancel();
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // 写出端跟着收工。
                }
            }
            connection?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>
    /// 授权检查;通过返回 null,否则返回给客户端看的原因。依次:
    /// ① 调用方已经验过身份的流(<see cref="ServeAuthenticatedAsync(Stream, CancellationToken)" />)放行;
    /// ② 带了对的 MIT-MAGIC-COOKIE-1 放行;
    /// ③ 能确定对端就是运行服务端的这个用户(取得到对端 uid 时 uid 相同;取不到时连的是权限 0600 的套接字文件)放行;
    /// ④ 知道对端 uid 而它是别的用户:拒 —— Linux 抽象命名空间里的套接字没有文件权限可言,不看 uid 的话
    ///    本机任何用户都能连进来读窗口、记键盘、经 XTEST 注入输入;
    /// ⑤ 配置了 cookie 时其余一律拒(环回 TCP 也一样:本机别的进程、别的用户都连得到那个端口);
    ///    没配置时与 X.Org 的主机访问控制一致,只放行本机。
    /// </summary>
    internal string? Authorize(string name, byte[] data, Peer peer)
    {
        if (peer.Authenticated)
        {
            return null;
        }
        // ⚠️ 常数时间比较:逐字节短路会泄漏「前几个字节对了几个」。
        if (_cookie is { } cookie && name == "MIT-MAGIC-COOKIE-1" && CryptographicOperations.FixedTimeEquals(data, cookie))
        {
            return null;
        }
        if (peer.LocalUser)
        {
            return null;
        }
        if (peer.Uid is not null)
        {
            return "Authorization required: the connecting user does not own this display";
        }
        if (_cookie is not null)
        {
            return "Authorization required, but no authorization protocol specified";
        }
        return peer.IsLocal ? null : "No protocol specified: only local connections are accepted";
    }

    // ------------------------------------------------------------------ 主机访问控制(协议「ChangeHosts」「ListHosts」「SetAccessControl」)
    //
    // 访问策略是固定的(见 Authorize):带对的 cookie,或者能确定是本机 / 本用户;没有可增删的主机清单。原先 ListHosts 报 Disabled ——
    // xhost 据此显示「access control disabled, clients can connect from any host」,实际谁也不能不带 cookie 从别处连进来 ——
    // ChangeHosts / SetAccessControl 又静默成功,xhost +host 看起来生效了、其实什么也没变。

    /// <summary>ListHosts:访问控制开着(Enabled),主机清单是空的。</summary>
    private static void ListHosts(XClient c) => c.Reply(1, static w => w.U16(0).Zero(22));

    /// <summary>ChangeHosts:主机清单改不了 —— 合法的请求回 BadAccess(协议允许服务端不让改)。</summary>
    private static void ChangeHosts(XRequestReader r)
    {
        if (r.Data > 1)
        {
            throw new XProtocolError(XErrorCode.Value, r.Data);   // mode:0 Insert、1 Delete
        }
        throw new XProtocolError(XErrorCode.Access);
    }

    /// <summary>SetAccessControl:访问控制本来就开着,Enable 什么也不做;Disable 回 BadAccess。</summary>
    private static void SetAccessControl(XRequestReader r)
    {
        switch (r.Data)
        {
            case 1:   // Enable
                return;
            case 0:   // Disable
                throw new XProtocolError(XErrorCode.Access);
            default:
                throw new XProtocolError(XErrorCode.Value, r.Data);
        }
    }

    private static async Task SendSetupFailureAsync(Stream stream, bool bigEndian, string reason, CancellationToken ct)
    {
        byte[] text = XWire.Latin1.GetBytes(reason);
        XWriter w = new(bigEndian);
        w.U8(0).U8((byte)Math.Min(255, text.Length)).U16(11).U16(0).U16((ushort)(XWire.Pad(text.Length) / 4));
        w.Bytes(text).Pad4();
        await stream.WriteAsync(w.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 同时连着的客户端上限。资源 ID 的顶上三位恒为 0(协议第 8 节),每个客户端占低 21 位(<see cref="XClient.ResourceMask" />),
    /// 客户端编号就只剩 8 位;编号 0 是服务端自己的资源(根窗口、默认颜色表)。
    /// </summary>
    internal const int MaxClients = 255;

    /// <summary>
    /// 分一个空闲的客户端编号并发出连接建立回复;编号用完了返回 null。<paramref name="generated" /> 不为 null 时这条连接带的是
    /// 服务端自己那个以外的 cookie:在 SECURITY 签过的授权里找,找不到就拒,返回给客户端看的原因。
    /// </summary>
    private (XClient? Client, string? Refused) RegisterClient(bool bigEndian, Peer peer, byte[]? generated = null)
    {
        SecurityAuthorization? authorization = null;
        if (generated is not null && (authorization = FindAuthorization(generated)) is null)
        {
            if (ShouldLogFrequent())
            {
                LogFrequent("connection refused: invalid MIT-MAGIC-COOKIE-1 key");
            }
            return (null, "Invalid MIT-MAGIC-COOKIE-1 key");
        }
        int index = _nextClientIndex;
        for (int tried = 0; tried < MaxClients; tried++, index = index >= MaxClients ? 1 : index + 1)
        {
            if (_clients.ContainsKey(index) || _retainedClients.ContainsKey(index))
            {
                continue;
            }
            _nextClientIndex = index >= MaxClients ? 1 : index + 1;
            // 对端的身份在执行线程上随登记一起写进去(之后不再变):原先连接线程在登记之后才写,违反 XClient「只在执行线程上读写」的约定,
            // 只是恰好靠工作队列的先后关系(写完才开始读请求)没出事。
            XClient client = new(index, bigEndian)
            {
                Label = peer.Label,
                SameHost = peer.SameHost,
                Forwarded = peer.Authenticated,
                PeerUid = peer.Uid,
                Untrusted = peer.Untrusted || authorization is { Untrusted: true },
                Authorization = authorization,
                PeerPid = peer.Pid,
            };
            _clients[index] = client;
            if (authorization is not null)
            {
                authorization.Connections++;
            }
            XServerMetrics.ActiveClients.Add(1);
            client.Send(BuildSetupReply(client));
            if (ShouldLogFrequent())
            {
                LogFrequent($"{client} connected ({(bigEndian ? "MSB" : "LSB")} first{(client.Untrusted ? ", untrusted" : "")})");
            }
            return (client, null);
        }
        XServerMetrics.RefusedConnections.Add(1, new KeyValuePair<string, object?>("reason", "too_many_clients"));
        if (ShouldLogFrequent())
        {
            LogFrequent($"connection refused: {MaxClients} clients already connected");
        }
        return (null, null);
    }

    /// <summary>连接建立成功回复:一块屏幕、深度 24 的 TrueColor 视觉(外加深度 32 与深度 1)。</summary>
    private byte[] BuildSetupReply(XClient client)
    {
        byte[] vendor = XWire.Latin1.GetBytes(_options.Vendor);
        XWriter w = client.Writer(256);
        w.U8(1).U8(0).U16(11).U16(0).U16(0);        // success、主版本 11、次版本 0、长度(回填)
        w.U32(12101000);                             // release-number
        w.U32(client.ResourceBase).U32(XClient.ResourceMask);
        w.U32(0);                                    // motion-buffer-size:不保存移动历史(GetMotionEvents 回空)
        w.U16((ushort)vendor.Length).U16(MaxRequestLength);
        w.U8(1);                                     // 屏幕数
        w.U8(7);                                     // FORMAT 数
        w.U8(0);                                     // image-byte-order:LSBFirst
        w.U8(0);                                     // bitmap-format-bit-order:LeastSignificant
        w.U8(32).U8(32);                             // bitmap scanline unit / pad
        w.U8(Input.Keymap.MinKeycode).U8(Input.Keymap.MaxKeycode);
        w.Zero(4);
        w.Bytes(vendor).Pad4();

        // FORMAT:depth、bits-per-pixel、scanline-pad、5 字节空
        foreach ((byte depth, byte bpp) in ((byte, byte)[])[(1, 1), (4, 8), (8, 8), (15, 16), (16, 16), (24, 32), (32, 32)])
        {
            w.U8(depth).U8(bpp).U8(32).Zero(5);
        }

        // SCREEN
        (int mmW, int mmH) = ScreenMillimeters();
        w.U32(Root.Id).U32(DefaultColormapId).U32(0xFFFFFF).U32(0x000000);
        w.U32(Root.AllEventMasks);
        w.U16((ushort)Root.Width).U16((ushort)Root.Height).U16((ushort)mmW).U16((ushort)mmH);
        w.U16(1).U16(1);                             // min / max installed maps
        w.U32(RootVisualId);
        w.U8(0);                                     // backing-stores:Never(我们另有顶层缓冲,不对客户端承诺)
        w.Bool(false);                               // save-unders
        w.U8(24);                                    // root-depth
        w.U8(7);                                     // DEPTH 数

        // DEPTH 24:一个 TrueColor 视觉
        w.U8(24).U8(0).U16(1).Zero(4);
        WriteVisual(w, RootVisualId);
        // DEPTH 1 / 4 / 8 / 15 / 16:没有视觉(只能做像素图 —— 协议只允许在列出的深度上建像素图)
        foreach (byte depth in (byte[])[1, 4, 8, 15, 16])
        {
            w.U8(depth).U8(0).U16(0).Zero(4);
        }
        // DEPTH 32:一个 TrueColor 视觉(ARGB,RENDER 用)
        w.U8(32).U8(0).U16(1).Zero(4);
        WriteVisual(w, ArgbVisualId);

        byte[] bytes = w.ToArray();
        ushort extra = (ushort)((bytes.Length - 8) / 4);
        if (client.BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), extra);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), extra);
        }
        return bytes;

        static void WriteVisual(XWriter w, uint id) =>
            // visual-id、class(4 = TrueColor)、bits-per-rgb、colormap-entries、红绿蓝掩码、4 字节空
            w.U32(id).U8(4).U8(8).U16(256).U32(0xFF0000).U32(0x00FF00).U32(0x0000FF).Zero(4);
    }

    private async Task ReadRequestsAsync(XClient client, Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        byte[] extended = new byte[4];
        while (!ct.IsCancellationRequested && !client.Closed)
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            uint units = Read16(header.AsSpan(2), client.BigEndian);
            int headerSize = 4;
            if (units == 0)
            {
                // BIG-REQUESTS:长度字段为 0,后面 4 字节才是真长度(含这 8 字节头)。
                // 没打开扩展就收到 0 长度 —— 按协议是 BadLength,这里直接断开,免得后面整条流错位。
                if (!client.BigRequestsEnabled)
                {
                    throw new InvalidDataException("收到长度为 0 的请求,但客户端没有打开 BIG-REQUESTS。");
                }
                await stream.ReadExactlyAsync(extended, ct).ConfigureAwait(false);
                units = client.BigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(extended)
                    : BinaryPrimitives.ReadUInt32LittleEndian(extended);
                headerSize = 8;
                if (units is < 2 or > MaxBigRequestLength)
                {
                    throw new InvalidDataException("BIG-REQUESTS 长度越界。");
                }
            }
            // 交给执行线程的请求统一是「4 字节头 + 正文」:扩展长度字段剥掉,正文紧接在头后。
            int size = 4 + (int)(units * 4) - headerSize;
            // 背压:已读进来、还没执行的请求条数或字节数到了上限,就先等执行线程消化,再分配、再读(同步完成的快路径不分配)。
            await client.PendingRequests.WaitAsync(ct).ConfigureAwait(false);
            await client.ReserveRequestBytesAsync(size, ct).ConfigureAwait(false);
            // 缓冲从池里租、执行完还回去(ExecuteRequest):整窗 PutImage 一帧就是几 MB,每条都新分配就是每条都进大对象堆。
            // 租来的数组可能比请求长,也不清零 —— 前 size 字节都会被读进来的字节盖掉,读不满就整条连接收工、这块数组随之丢弃。
            byte[] request = ArrayPool<byte>.Shared.Rent(size);
            header.CopyTo(request, 0);
            await stream.ReadExactlyAsync(request.AsMemory(4, size - 4), ct).ConfigureAwait(false);
            PostRequest(client, request, size);
        }
    }

    /// <summary>
    /// 写出端:把已经排队的回复 / 事件 / 错误拼进一块缓冲再一次写出 —— 事件动辄几十条一批,
    /// 每条单独写就是每条一次系统调用(TCP 上还可能每条一个包)。单条超过缓冲的(GetImage 的大回复)直接写。
    /// </summary>
    private static async Task PumpOutputAsync(XClient client, Stream stream, CancellationToken ct)
    {
        ChannelReader<byte[]> reader = client.Output.Reader;
        byte[] batch = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                int used = 0;
                while (reader.TryRead(out byte[]? message))
                {
                    // 取出来就不再算「排队」。整批写完才减的话,单条大回复(GetImage 71 MB 起)在写出去的
                    // 整个过程中都把计数顶在上限上:客户端读完它之后紧接着发的那条请求,回它时会被当成
                    // 「客户端不读了」而把连接判死 —— 回复永远发不出去(见 XClient.Send)。
                    client.NoteWritten(message.Length);
                    if (used + message.Length > batch.Length)
                    {
                        if (used > 0)
                        {
                            await stream.WriteAsync(batch.AsMemory(0, used), ct).ConfigureAwait(false);
                            used = 0;
                        }
                        if (message.Length > batch.Length)
                        {
                            await stream.WriteAsync(message, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    message.CopyTo(batch, used);
                    used += message.Length;
                }
                if (used > 0)
                {
                    await stream.WriteAsync(batch.AsMemory(0, used), ct).ConfigureAwait(false);
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 写不出去:对端已经断了。读端这时可能正卡在背压上(这个客户端的请求被 SYNC Await、XTEST 的延迟、别人的 GrabServer 挂着,
            // 未执行的请求到了上限),根本没去读套接字,察觉不到 —— 原先连接就一直挂着,窗口成了关不掉的僵尸。主动断开它。
            client.Abort();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batch);
        }
    }

    private static ushort Read16(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);

    /// <summary>
    /// 客户端断开(协议第 10 节「Connection Close」):事件选择、抓取、选区一律放掉;CloseDownMode 为 Destroy(默认)时销毁它的全部资源,
    /// 为 RetainPermanent / RetainTemporary 时资源留着,等 KillClient 来销毁。
    /// </summary>
    private void DisconnectClient(XClient client)
    {
        // 按对象比对再摘:KillClient / 关窗当场调一次,连接收尾时再排一次;中间这个编号可能已经分给了新客户端,
        // 原先按编号摘,第二次就把新客户端摘了 —— 它照样收发请求,编号却又能分给下一个,两个客户端的资源 ID 范围撞在一起。
        if (!_clients.TryGetValue(client.Index, out XClient? current) || !ReferenceEquals(current, client))
        {
            return;
        }
        _clients.Remove(client.Index);
        XServerMetrics.ActiveClients.Add(-1);
        client.Closed = true;
        if (ShouldLogFrequent())
        {
            LogFrequent($"{client} disconnected");
        }
        try
        {
            ProcessSaveSet(client);   // 先还回别人的窗口,再销毁资源(协议「Connection Close」)
        }
        catch (Exception ex)
        {
            if (ShouldLogFrequent())
            {
                LogFailure($"save-set of {client} failed:", "save-set", ex);
            }
        }
        try
        {
            if (client.CloseDownMode is 1 or 2 && _retainedClients.Count < MaxRetainedClients)
            {
                ReleaseConnectionState(client);
                _retainedClients[client.Index] = client;
            }
            else
            {
                if (client.CloseDownMode is 1 or 2 && ShouldLogFrequent())
                {
                    LogFrequent($"{client} asked to retain its resources, but {MaxRetainedClients} clients already do: destroying them");
                }
                CleanupClient(client);
            }
        }
        catch (Exception ex)
        {
            if (ShouldLogFrequent())
            {
                LogFailure($"cleanup of {client} failed:", "cleanup", ex);
            }
        }
        if (ReferenceEquals(_serverGrabber, client))
        {
            ReleaseServerGrab();
        }
    }

    /// <summary>SetCloseDownMode(协议「SetCloseDownMode」):0 Destroy、1 RetainPermanent、2 RetainTemporary,别的值是 BadValue。</summary>
    private static void SetCloseDownMode(XClient c, XRequestReader r)
    {
        if (r.Data > 2)
        {
            throw new XProtocolError(XErrorCode.Value, r.Data);   // 原先照单全收,3–255 断开时也按 Destroy 处理
        }
        c.CloseDownMode = r.Data;
    }

    /// <summary>以 RetainPermanent / RetainTemporary 收尾的客户端的资源:KillClient 指到它们时销毁,编号随之放回。</summary>
    private void DestroyRetainedClient(XClient client)
    {
        if (_retainedClients.Remove(client.Index))
        {
            // 连接状态在断开时已经清过(ReleaseConnectionState),这里只销毁留下的资源。
            DestroyClientResources(client);
            NotifyClientResourcesDestroyed(client);
            UpdatePointerWindow();
            UpdateCursor();
        }
    }

    /// <summary>KillClient(AllTemporary):销毁所有以 RetainTemporary 收尾的客户端的资源。</summary>
    private void DestroyRetainedTemporaryClients()
    {
        foreach (XClient client in _retainedClients.Values.Where(c => c.CloseDownMode == 2).ToArray())
        {
            DestroyRetainedClient(client);
        }
    }

    /// <summary>这个客户端已经以 Retain 模式断开、资源还留着。</summary>
    private bool IsRetained(XClient client) => _retainedClients.TryGetValue(client.Index, out XClient? retained) && ReferenceEquals(retained, client);

    /// <summary>KillClient 语义:还连着的断开;已经以 Retain 模式断开的,销毁它留下的全部资源。</summary>
    private void KillClientOf(XClient client)
    {
        if (IsRetained(client))
        {
            DestroyRetainedClient(client);
            return;
        }
        if (!client.Closed)
        {
            XServerMetrics.Disconnects.Add(1, new KeyValuePair<string, object?>("reason", "killed"));
        }
        client.Abort();
        DisconnectClient(client);
    }

    /// <summary>见 <see cref="GetClientsAsync" />。</summary>
    private IReadOnlyList<XClientInfo> SnapshotClients()
    {
        Dictionary<XClient, int> resources = [];
        foreach (XResource resource in _resources.Values)
        {
            if (resource.Owner is { } owner)
            {
                resources[owner] = resources.GetValueOrDefault(owner) + 1;
            }
        }
        return
        [
            .. _clients.Values.Select(c => (Client: c, Retained: false))
                .Concat(_retainedClients.Values.Select(c => (Client: c, Retained: true)))
                .OrderBy(e => e.Client.Index)
                .Select(e => new XClientInfo(e.Client.Index, e.Client.Label, e.Retained, resources.GetValueOrDefault(e.Client),
                    e.Client.MemoryInUse,
                    [.. _topLevelHandles.Where(p => ReferenceEquals(p.Key.Owner, e.Client)).Select(p => p.Value)],
                    e.Client.Untrusted ? XClientTrust.Untrusted : XClientTrust.Trusted,
                    ReferenceEquals(_serverGrabber, e.Client))),
        ];
    }

    /// <summary>连接收尾时与资源无关的那一半:选区、抓取、别人窗口上的事件选择与被动抓取、各扩展的每连接状态。</summary>
    private void ReleaseConnectionState(XClient client)
    {
        ReleaseSelectionsAndGrabs(client);
        ReleaseEventSelections(client);
    }

    /// <summary>
    /// 以 Destroy 模式断开的客户端:释放它的选区、抓取、资源与事件选择,再让各扩展清掉自己的那份状态
    /// (<see cref="Extension.ClientClosed" /> 与 <see cref="Extension.ClientResourcesDestroyed" />,各一次)。
    /// </summary>
    private void CleanupClient(XClient client)
    {
        ReleaseSelectionsAndGrabs(client);
        DestroyClientResources(client);
        ReleaseEventSelections(client);
        NotifyClientResourcesDestroyed(client);
    }

    /// <summary>客户端的资源刚销毁完:各扩展清挂在那些资源上的状态(<see cref="Extension.ClientResourcesDestroyed" />)。</summary>
    private void NotifyClientResourcesDestroyed(XClient client)
    {
        foreach (Extension extension in _extensionList)
        {
            extension.ClientResourcesDestroyed?.Invoke(client);
        }
    }

    private void ReleaseSelectionsAndGrabs(XClient client)
    {
        foreach ((SelectionSlot slot, (XWindow Window, XClient? Client, uint Time) owner) in _selections.ToArray())
        {
            if (ReferenceEquals(owner.Client, client))
            {
                _selections.Remove(slot);
                NotifySelectionChange(slot.Atom, 2, 0, owner.Time, client => InScope(client, slot));
                OnSelectionOwnerLost(slot);
            }
        }
        DropOrphanedFetches();   // 正在取的选区,属主走了
        if (ReferenceEquals(PointerGrab?.Client, client))
        {
            PointerGrab = null;
        }
        if (ReferenceEquals(KeyboardGrab?.Client, client))
        {
            KeyboardGrab = null;
        }
    }

    private void DestroyClientResources(XClient client)
    {
        // 资源表只扫一遍:分出它的窗口与其余资源。先销毁「挂在别人窗口下」的那些(连同子窗口),再清其余资源。
        List<XWindow> windows = [];
        List<XResource> others = [];
        foreach (XResource resource in _resources.Values)
        {
            if (!ReferenceEquals(resource.Owner, client))
            {
                continue;
            }
            if (resource is XWindow window)
            {
                windows.Add(window);
            }
            else
            {
                others.Add(resource);
            }
        }
        foreach (XWindow window in windows)
        {
            if (_resources.ContainsKey(window.Id) && window.Parent is { } parent && !ReferenceEquals(parent.Owner, client))
            {
                Destroy(window);
            }
        }
        foreach (XWindow window in windows)
        {
            Destroy(window);   // 已随上级销毁的会在里面直接返回
        }
        DetachShmSegments(others);
        foreach (XResource resource in others)
        {
            RemoveResource(resource.Id);
            if (resource is XPixmap pixmap)
            {
                CleanupDamage(pixmap);   // 客户端走了,它的像素图随之销毁:别的客户端建在上面的 Damage 一并销毁
            }
        }
    }

    private void ReleaseEventSelections(XClient client)
    {
        // 它在别人窗口上选的事件、登记的被动抓取一并摘掉。
        foreach (XResource resource in _resources.Values)
        {
            if (resource is XWindow window)
            {
                window.EventSelections.Remove(client);
                window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, client));
                window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, client));
                window.ShapeSelections.Remove(client);
            }
        }
        foreach (Extension extension in _extensionList)
        {
            extension.ClientClosed?.Invoke(client);
        }
        UpdatePointerWindow();
        UpdateCursor();
    }

    // ------------------------------------------------------------------ BIG-REQUESTS

    private static void BigRequests(XClient c, XRequestReader r)
    {
        if (r.Data != 0)
        {
            throw new XProtocolError(XErrorCode.Request);
        }
        c.BigRequestsEnabled = true;
        c.Reply(0, w => w.U32(MaxBigRequestLength).Zero(20));
    }
}
