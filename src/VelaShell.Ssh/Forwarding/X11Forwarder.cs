// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.3.1  x11-req
//   RFC 4254 §6.3.2  x11 通道
//   行为规格:        velashell-docs/zh/ssh/spec/07-forwarding.md §7.5

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Forwarding;

/// <summary>把服务端开回来的 <c>x11</c> 通道接到本机的 X 显示上。</summary>
/// <remarks>
/// <para>
/// <b>只做转发那一端，不做 X server。</b>（架构 §12「明确不做」）
/// </para>
/// <para>
/// 安全核心是<b>假 cookie</b>：发给服务端的永远是一个随机生成的假 cookie，
/// 远端 X 客户端拿它连过来之后，我们核对、再换成本机真实的 cookie。
/// 核对不过就拒绝。详见 <c>velashell-docs/zh/ssh/spec/07-forwarding.md</c> §7.5.2。
/// </para>
/// <para>
/// 同一条连接上可以有多个转发（每个会话一个，各有各的假 cookie）——
/// <c>x11</c> 通道按 cookie 分给对应的那个（<c>velashell-docs/zh/ssh/spec/07</c> §7.5.4）。
/// </para>
/// </remarks>
public sealed class X11Forwarder : IAsyncDisposable
{
    /// <summary><c>xauth</c> 最多跑多久。</summary>
    private static readonly TimeSpan XAuthTimeout = TimeSpan.FromSeconds(30);

    private readonly X11ChannelRouter _router;
    private readonly X11ForwardOptions _options;
    private readonly byte[] _fakeCookie;
    private readonly byte[] _realCookie;
    private readonly SemaphoreSlim _slots;
    private readonly long _expiresAtTicks;

    private long _acceptedChannels;
    private long _rejectedChannels;

    /// <summary>单连接模式下那唯一的名额是否已被认领（0 / 1）。</summary>
    private int _singleConnectionClaimed;
    private int _disposed;

    private X11Forwarder(
        X11ChannelRouter router,
        X11ForwardOptions options,
        X11Display display,
        byte[] fakeCookie,
        byte[] realCookie)
    {
        _router = router;
        _options = options;
        Display = display;
        _fakeCookie = fakeCookie;
        _realCookie = realCookie;
        _slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);

        // 有效期与受信与否无关 —— 见 X11ForwardOptions.Timeout 上的说明。
        _expiresAtTicks = options.Timeout <= TimeSpan.Zero
            ? long.MaxValue
            : Environment.TickCount64 + (long)options.Timeout.TotalMilliseconds;
    }

    /// <summary>接受了几条 X11 通道。</summary>
    public long AcceptedChannels => Volatile.Read(ref _acceptedChannels);

    /// <summary>拒绝了几条 —— cookie 不对、过期、或者超出并发上限。</summary>
    /// <remarks>
    /// <b>这个数不该是 0 以外的值。</b> 非零意味着有人拿着错的 cookie 在敲门，
    /// 值得让使用者看见。
    /// </remarks>
    public long RejectedChannels => Volatile.Read(ref _rejectedChannels);

    /// <summary>转发用的本机显示。</summary>
    public X11Display Display { get; }

    /// <summary>这条转发是否已经过期。</summary>
    internal bool IsExpired => Environment.TickCount64 > _expiresAtTicks;

    /// <summary>在一条 session 通道上请求 X11 转发。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="channel">要在哪条 session 通道上请求（<c>pty-req</c> 之后、<c>env</c> 之前）。</param>
    /// <param name="options">选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshForwardException">拿不到本机显示 / cookie，或服务端拒绝。</exception>
    /// <remarks>
    /// <c>internal</c>：对外的入口是 <see cref="Channels.SshSessionRequestOptions.X11Forwarding"/> ——
    /// <c>x11-req</c> 要夹在 <c>pty-req</c> 与 <c>env</c> 之间发（velashell-docs/zh/ssh/spec/07 §7.5.3），
    /// 让调用方自己在一条裸通道上调它，那个时序就交给了调用方。X11 转发默认是关的，而且没有「全局打开」的开关。
    /// </remarks>
    internal static async ValueTask<X11Forwarder> RequestAsync(
        Session.SshConnection connection,
        SshChannel channel,
        X11ForwardOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channel);

        X11ForwardOptions effective = options ?? X11ForwardOptions.Default;

        X11Display display = effective.Display
            ?? X11Display.FromEnvironment()
            ?? throw new SshForwardException(SshFailureReason.ForwardSetupFailed,
                "拿不到本机的 X 显示：DISPLAY 没设或者格式不认识。" +
                "可以在 X11ForwardOptions.Display 里显式指定。");

        if (effective.LocalConnector is not null && !effective.Trusted)
        {
            throw new SshForwardException(SshFailureReason.ForwardSetupFailed,
                "本机显示经连接器接入时只支持受信模式:非受信模式要 xauth 连上本机显示签受限 cookie,连接器后面没有可供它去连的显示。");
        }

        byte[] realCookie = effective.LocalConnector is not null
            ? effective.LocalCookie.ToArray()
            : await ResolveRealCookieAsync(display, effective, cancellationToken).ConfigureAwait(false);

        // ⚠️ **发给服务端的永远是假 cookie。** 真 cookie 一步都不能离开本机。
        byte[] fakeCookie = X11SetupMessage.CreateFakeCookie();

        X11Forwarder forwarder = new(connection.X11Router, effective, display, fakeCookie, realCookie);

        // 先登记再发请求：服务端可能在应答之后立刻开通道。
        forwarder._router.Add(forwarder);

        try
        {
            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteBoolean(effective.SingleConnection);
            writer.WriteUtf8String(XAuthority.MitMagicCookie1);

            // ⚠️ cookie 字段是**十六进制文本**，不是原始字节（RFC 4254 §6.3.1）。
            writer.WriteUtf8String(X11SetupMessage.ToHex(fakeCookie));
            writer.WriteUInt32((uint)display.Screen);

            bool accepted = await channel.SendRequestAsync(
                SshProtocolNames.RequestX11, payload.WrittenMemory,
                wantReply: true, cancellationToken).ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshForwardException(SshFailureReason.ForwardRejected,
                    "服务端拒绝了 X11 转发请求。常见原因是 sshd_config 里 X11Forwarding no，" +
                    "或者服务端没装 xauth。");
            }

            return forwarder;
        }
        catch (Exception)
        {
            forwarder._router.Remove(forwarder);
            throw;
        }
    }

    // ------------------------------------------------------------ 由路由调用

    /// <summary>核对假 cookie 并换成真的；不是这个转发的 cookie 时返回 <see langword="false"/>。</summary>
    internal bool TryRewrite(byte[] original, in X11SetupMessage.Parsed setup, out byte[] rewritten) =>
        X11SetupMessage.TryRewriteCookie(original, setup, _fakeCookie, _realCookie, out rewritten);

    internal void NoteRejected() => Interlocked.Increment(ref _rejectedChannels);

    private void ReleaseSingle(bool claimed)
    {
        if (claimed)
        {
            Volatile.Write(ref _singleConnectionClaimed, 0);
        }
    }

    /// <summary>cookie 已经对上了：连本机显示，把换好的建立报文发过去，然后对搬。</summary>
    internal async Task RelayAsync(SshChannel channel, byte[] rewrittenSetup, CancellationToken cancellationToken)
    {
        // 〔velashell-docs/zh/ssh/spec/07 §7.5.7〕通道是在过期之前开的，但建立报文到的时候已经过期 —— 照样拒。
        if (IsExpired || Volatile.Read(ref _disposed) != 0)
        {
            NoteRejected();
            return;
        }

        // 单连接模式：本端强制，不指望服务端。
        //
        // ⚠️ 名额要**原子地认领**。以前是「看 _acceptedChannels 是否 > 0」，而计数要等建立报文
        // 发给本机显示之后才加一 —— 两条 x11 通道挨着到达时，第二条看到的还是 0，两条都被放行。
        bool claimedSingle = false;
        if (_options.SingleConnection)
        {
            if (Interlocked.Exchange(ref _singleConnectionClaimed, 1) != 0)
            {
                NoteRejected();
                return;
            }
            claimedSingle = true;
        }

        if (!_slots.Wait(0, CancellationToken.None))
        {
            ReleaseSingle(claimedSingle);
            NoteRejected();
            return;
        }

        Socket? local = null;
        Stream? stream = null;
        bool accepted = false;
        try
        {
            Action? shutdownSend = null;
            Action? abort = null;
            if (_options.LocalConnector is { } connector)
            {
                stream = await ConnectViaConnectorAsync(connector, cancellationToken).ConfigureAwait(false);

                // 〔velashell-docs/zh/ssh/spec/07 §7.5.9〕远端的 EOF 也要让 X server 读到:能单向关写端就关写端,
                // 不能就关整条流。不做的话远端程序退出后连接与窗口一直挂到 SSH 会话结束。
                shutdownSend = stream switch
                {
                    InMemoryDuplexStream duplex => duplex.CompleteWrites,
                    { } other => other.Dispose,
                    null => null,
                };
            }
            else
            {
                local = await ConnectDisplayAsync(cancellationToken).ConfigureAwait(false);
                if (local is not null)
                {
                    stream = new NetworkStream(local, ownsSocket: false);
                    Socket socket = local;
                    shutdownSend = () => StreamRelayEndpoint.ShutdownSend(socket);
                    abort = () => StreamRelayEndpoint.Reset(socket);
                }
            }
            if (stream is null)
            {
                return;   // 本机显示连不上 —— 通道随后由会话关掉；名额在 finally 里退回
            }

            // 显示连上了就算接纳，**在建立报文上线之前**计数：
            // X server 一收到报文，外面就可能来读这个计数。
            Interlocked.Increment(ref _acceptedChannels);
            accepted = true;

            // 先把换好的建立报文发过去，再进入对搬。
            await stream.WriteAsync(rewrittenSetup, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 流交给端点,由它释放;这里不再重复释放。
            Stream owned = stream;
            stream = null;
            await using StreamRelayEndpoint localEnd = new(owned, shutdownSend, ownsStream: true, abort);
            ChannelRelayEndpoint remoteEnd = new(channel);

            await DuplexRelay.RunAsync(
                remoteEnd, localEnd,
                onBytesFromLeft: static _ => { },
                onBytesFromRight: static _ => { },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            local?.Dispose();
            _slots.Release();

            // 没接纳（显示连不上、连的时候出错）就等于什么都没转发出去 —— 单连接的名额退回。
            if (!accepted)
            {
                ReleaseSingle(claimedSingle);
            }
        }
    }

    /// <summary>经连接器拿本机显示的流;连接器那一端不可用(已停、已释放)时返回 <see langword="null" />。</summary>
    private static async ValueTask<Stream?> ConnectViaConnectorAsync(
        Func<CancellationToken, ValueTask<Stream>> connector, CancellationToken cancellationToken)
    {
        try
        {
            return await connector(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>挨个候选端点去连本机显示。</summary>
    private async ValueTask<Socket?> ConnectDisplayAsync(CancellationToken cancellationToken)
    {
        foreach (EndPoint endPoint in Display.GetCandidateEndPoints())
        {
            Socket socket = endPoint is UnixDomainSocketEndPoint
                ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                : new Socket(SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();   // 换下一个候选
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
        }

        return null;
    }

    // ------------------------------------------------------------ 真 cookie

    /// <summary>拿到本机显示真实的 cookie。</summary>
    private static async ValueTask<byte[]> ResolveRealCookieAsync(
        X11Display display, X11ForwardOptions options, CancellationToken cancellationToken)
    {
        if (options.Trusted)
        {
            // 〔velashell-docs/zh/ssh/spec/07 §7.5.7〕受信模式**不跑外部程序** —— 读文件就够了，
            // 少跑一个外部程序就少一条攻击面。
            IReadOnlyList<XAuthorityEntry> entries =
                await XAuthority.LoadAsync(options.XAuthorityPath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<IPAddress> addresses =
                await XAuthority.ResolveHostAddressesAsync(display, cancellationToken).ConfigureAwait(false);

            // 找不到就用随机数据（与 OpenSSH 一致）：让 X server 去拒绝，
            // 比我们在这里猜一个「大概对」的 cookie 好。
            return XAuthority.FindCookie(entries, display, hostAddresses: addresses) ?? X11SetupMessage.CreateFakeCookie();
        }

        return await GenerateUntrustedCookieAsync(display, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>非受信模式：让 <c>xauth</c> 生成一个受限 cookie。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>生成的 cookie 写进一个临时文件（<c>xauth -f</c>），绝不写进使用者的
    /// <c>.Xauthority</c>。</b>后者里装的是本机显示的<b>完全授权</b> cookie；
    /// 不带 <c>-f</c> 的 <c>xauth generate</c> 会用受限 cookie 把它覆盖掉 ——
    /// 有效期一到，使用者自己本机的 X 程序就再也连不上自己的显示了。
    /// </para>
    /// <para>
    /// 临时目录只有当前用户可访问，用完即删。
    /// </para>
    /// </remarks>
    private static async ValueTask<byte[]> GenerateUntrustedCookieAsync(
        X11Display display, X11ForwardOptions options, CancellationToken cancellationToken)
    {
        string xauth = options.XAuthLocation ?? "xauth";

        int timeoutSeconds = XAuthTimeoutSeconds(options.Timeout);

        // CreateTempSubdirectory 在类 Unix 上建的是 0700 目录。
        // 建不了（临时目录满了、没有权限）也是「本机这一侧准备失败」：报成 SshForwardException，
        // 尽力而为时会话才会照常启动（spec/07 §7.5.8）；原样抛 IOException 的话，整个会话都起不来。
        DirectoryInfo scratch;
        try
        {
            scratch = Directory.CreateTempSubdirectory("velashell-x11-");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SshForwardException(SshFailureReason.ForwardSetupFailed, $"建不了给 {xauth} 用的临时目录：{ex.Message}", ex);
        }

        string scratchFile = Path.Combine(scratch.FullName, "xauthfile");

        try
        {
            ProcessStartInfo start = new(xauth)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string argument in BuildGenerateArguments(scratchFile, display, timeoutSeconds))
            {
                start.ArgumentList.Add(argument);
            }

            // xauth 要先以完全授权连上本机显示，才能请它签一个受限 cookie ——
            // 那份授权从哪里读，跟着我们的选项走。
            if (options.XAuthorityPath is { } authority)
            {
                start.Environment["XAUTHORITY"] = authority;
            }

            await RunXAuthAsync(start, xauth, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<XAuthorityEntry> entries =
                await XAuthority.LoadAsync(scratchFile, cancellationToken).ConfigureAwait(false);

            // 临时文件里只有刚生成的那一条；按显示匹配不上时（显示名的写法与
            // 地址族的对应并不总是一目了然）就取其中唯一的 MIT-MAGIC-COOKIE-1。
            return XAuthority.FindCookie(entries, display)
                ?? entries.FirstOrDefault(static e => e.Name == XAuthority.MitMagicCookie1)?.Data
                ?? throw new SshForwardException(SshFailureReason.ForwardSetupFailed,
                    $"{xauth} generate 跑完了，但没有生成 {display.XAuthName} 的 cookie。");
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (Exception)
            {
                // 删不掉只是留下一个只有本用户能读的临时目录；不值得为它让转发失败。
            }
        }
    }

    /// <summary>交给 <c>xauth generate ... timeout</c> 的秒数。</summary>
    /// <remarks>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.7〕X 的 SECURITY 扩展：受限授权在「没有任何连接在用它」的状态
    /// 持续这么多秒之后被 X server 清掉，0 表示永不过期（不写时默认 60 秒）。
    /// </para>
    /// <para>
    /// <b>有效期再加 60 秒。</b>两边各自计时 —— X server 从生成那一刻算起，我们从请求转发时算起；
    /// 两者相等时，我们刚接下一条 <c>x11</c> 通道、X server 恰好已经清掉授权，那条连接就被拒了。
    /// 曾经传的就是有效期本身。
    /// </para>
    /// <para>
    /// <b>有效期为 0（不过期）时传 0。</b>曾经退回 20 分钟：X server 空闲 20 分钟就清掉授权，
    /// 我们却还在接受新的 <c>x11</c> 通道，之后的 X 程序一律被 X server 拒绝。
    /// </para>
    /// </remarks>
    internal static int XAuthTimeoutSeconds(TimeSpan validity)
    {
        if (validity <= TimeSpan.Zero)
        {
            return 0;
        }

        const int margin = 60;
        double seconds = Math.Ceiling(validity.TotalSeconds) + margin;
        return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }

    /// <summary><c>xauth generate</c> 的参数。</summary>
    /// <remarks>
    /// ⚠️ <b><c>-f</c> 必须在最前面，而且必须指向临时文件。</b>见
    /// <see cref="GenerateUntrustedCookieAsync"/> 的说明 —— 少了它，
    /// 受限 cookie 会覆盖使用者 <c>.Xauthority</c> 里的完全授权 cookie。
    /// </remarks>
    internal static IReadOnlyList<string> BuildGenerateArguments(
        string scratchFile, X11Display display, int timeoutSeconds) =>
    [
        "-f", scratchFile,
        "generate", display.XAuthName, XAuthority.MitMagicCookie1,
        "untrusted",
        "timeout", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];

    /// <summary>跑 <c>xauth</c>，有期限，超时就杀掉。</summary>
    private static async ValueTask RunXAuthAsync(ProcessStartInfo start, string xauth, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new SshForwardException(SshFailureReason.ForwardSetupFailed, $"启动 {xauth} 失败。");
        }
        catch (SshForwardException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SshForwardException(SshFailureReason.ForwardSetupFailed,
                $"跑不起来 {xauth}：{ex.Message}。" +
                "Windows 上通常没有 xauth —— 那里请用受信模式（Trusted = true）。", ex);
        }

        using (process)
        {
            // 两条输出管道**同时**读 —— 只等进程退出而不读的话，输出一多就会
            // 把管道写满，进程卡在写上、永远不退出。
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var limit =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(XAuthTimeout);

            try
            {
                // ⚠️ **必须有期限。** xauth 可能卡在一个没响应的 X server 上，
                //    而那会让整个请求跟着挂住。
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时或调用方取消：**把它杀掉**，不留一个挂着的子进程。
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 恰好在这时退出了。
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new SshForwardException(SshFailureReason.ForwardSetupFailed, $"{xauth} generate 超过 {XAuthTimeout.TotalSeconds:0} 秒没有返回。");
            }

            _ = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new SshForwardException(SshFailureReason.ForwardSetupFailed,
                    $"{xauth} generate 失败（退出码 {process.ExitCode}）：{error.Trim()}。" +
                    "非受信 X11 转发需要本机有 xauth、且 X server 支持 SECURITY 扩展；" +
                    "都没有的话请显式用受信模式（Trusted = true），但要清楚那等于把本机显示完全交给远端。");
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只摘掉<b>这一个</b>转发 —— 同一条连接上别的会话的 X11 转发照常工作。
    /// 已经建好的 X11 连接不受影响，它们随各自的通道结束。
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _router.Remove(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>这条转发用的假 cookie（诊断与测试用）。</summary>
    /// <remarks>
    /// <c>internal</c>，给测试验证「发出去的确实不是真 cookie」——
    /// 而不是为了让它被当成凭据用。
    /// </remarks>
    internal ReadOnlySpan<byte> FakeCookie => _fakeCookie;
}
