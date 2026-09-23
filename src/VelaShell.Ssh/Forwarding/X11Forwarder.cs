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

namespace VelaShell.Ssh.Forwarding;

/// <summary>X11 转发的选项。</summary>
/// <remarks>
/// <para>
/// <b>默认是非受信模式</b>（对应 <c>ssh -X</c>）。受信模式（<c>ssh -Y</c>）
/// 把本机显示的完全控制权交给远端 —— X11 没有客户端隔离，
/// 连上同一个显示的任何客户端都能<b>读别人的按键、抓别人的窗口</b>。
/// </para>
/// </remarks>
public sealed record X11ForwardOptions
{
    /// <summary>用哪个显示；<see langword="null"/> 取 <c>DISPLAY</c>。</summary>
    public X11Display? Display { get; init; }

    /// <summary>受信模式（<c>ssh -Y</c>）。</summary>
    /// <remarks>
    /// <b>默认 <see langword="false"/>。</b> 打开它等于把本机所有图形会话
    /// 的输入输出交给远端，要有明确的理由。
    /// <para>
    /// 非受信模式需要本机有 <c>xauth</c>、且 X server 支持 SECURITY 扩展；
    /// Windows 上通常两者都没有，那里只能用受信模式。
    /// </para>
    /// </remarks>
    public bool Trusted { get; init; }

    /// <summary>转发的有效期。默认 20 分钟；<see cref="TimeSpan.Zero"/> 表示不过期。</summary>
    /// <remarks>
    /// <para>
    /// 过期之后新的 <c>x11</c> 通道一律拒绝（已经建好的不受影响）。
    /// </para>
    /// <para>
    /// 〔与 OpenSSH 的一处**有意差异**〕OpenSSH 的 <c>ForwardX11Timeout</c>
    /// 只管非受信模式。我们<b>两种模式都管</b> —— 因为「有效期只在某一种模式下
    /// 起作用」是一个会让人栽跟头的 API：受信模式恰恰是危险得多的那个，
    /// 却反而没有期限，说不通。
    /// </para>
    /// <para>
    /// 长会话要一直用的话，显式设成 <see cref="TimeSpan.Zero"/>。
    /// 非受信模式下这个值也会传给 <c>xauth generate ... timeout</c>；
    /// 设成 Zero 时那里退回 20 分钟（<c>xauth</c> 需要一个具体的数）。
    /// </para>
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary><c>.Xauthority</c> 的路径；<see langword="null"/> 走默认。</summary>
    /// <remarks>
    /// 受信模式从这里读真 cookie；非受信模式下它是 <c>xauth</c> 连本机显示时用的授权
    /// （通过 <c>XAUTHORITY</c> 传给它）—— <b>这个文件本身永远不会被写</b>。
    /// </remarks>
    public string? XAuthorityPath { get; init; }

    /// <summary><c>xauth</c> 可执行文件的位置；<see langword="null"/> 用 <c>xauth</c>。</summary>
    public string? XAuthLocation { get; init; }

    /// <summary>只允许一条 X11 连接。</summary>
    /// <remarks>
    /// 默认 <see langword="false"/>：一个远端会话常常开多个 X 客户端，
    /// 设成 <see langword="true"/> 的话第二个就连不上了。
    /// 这一条同时发给服务端（<c>x11-req</c> 的 single connection 字段）
    /// <b>并在本端强制</b> —— 不把安全约束寄托在对端身上。
    /// </remarks>
    public bool SingleConnection { get; init; }

    /// <summary>同时允许的 X11 通道数上限。</summary>
    public int MaxConcurrentChannels { get; init; } = 16;

    /// <summary>
    /// 尽力而为：开会话时 X11 设置失败就不开 X11、会话照常启动，而不是抛异常。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>默认 <see langword="false"/>（严格）。</b>〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕
    /// 调用方在这一次执行上显式要求的 X11，失败就抛 —— 他明确要 X11，静默降级等于骗他。
    /// </para>
    /// <para>
    /// 只有 X11 是由<b>连接级开关</b>打开的时候（比如 <c>ssh_config</c> 里的 <c>ForwardX11 yes</c>，
    /// 见 <see cref="Config.SshHostConfig.ApplyToShell"/>）才设成 <see langword="true"/>：
    /// 否则一份存量配置会让这台主机上的所有会话都起不来。
    /// </para>
    /// <para>
    /// 只在 <see cref="Session.SshConnectionSessions.OpenShellAsync"/> /
    /// <see cref="Session.SshConnectionSessions.ExecuteAsync"/> 里起作用：失败的原因放在
    /// <see cref="Channels.SshShell.X11SetupFailure"/> / <see cref="Channels.SshCommand.X11SetupFailure"/> 上，
    /// 并计入 <see cref="ForwardMetrics.Errors"/>。取消照常抛出。
    /// 直接调 <see cref="X11Forwarder.RequestAsync"/> 的话这一项不起作用 —— 那本身就是显式请求。
    /// </para>
    /// </remarks>
    public bool BestEffort { get; init; }

    /// <summary>默认选项。</summary>
    public static X11ForwardOptions Default { get; } = new();
}

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
        _slots = new SemaphoreSlim(options.MaxConcurrentChannels, options.MaxConcurrentChannels);

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
    /// <b>必须由使用者显式调用。</b> X11 转发默认是关的，而且没有「全局打开」
    /// 的开关 —— 它是逐通道的决定。
    /// </remarks>
    public static async ValueTask<X11Forwarder> RequestAsync(
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
            ?? throw new SshForwardException(
                "拿不到本机的 X 显示：DISPLAY 没设或者格式不认识。" +
                "可以在 X11ForwardOptions.Display 里显式指定。");

        byte[] realCookie = await ResolveRealCookieAsync(display, effective, cancellationToken)
            .ConfigureAwait(false);

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
                SshAlgorithmNames.RequestX11, payload.WrittenMemory,
                wantReply: true, cancellationToken).ConfigureAwait(false);

            if (!accepted)
            {
                throw new SshForwardException(
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
        bool accepted = false;
        try
        {
            local = await ConnectDisplayAsync(cancellationToken).ConfigureAwait(false);
            if (local is null)
            {
                return;   // 本机显示连不上 —— 通道随后由会话关掉；名额在 finally 里退回
            }

            // 显示连上了就算接纳，**在建立报文上线之前**计数：
            // X server 一收到报文，外面就可能来读这个计数。
            Interlocked.Increment(ref _acceptedChannels);
            accepted = true;

            NetworkStream stream = new(local, ownsSocket: false);

            // 先把换好的建立报文发过去，再进入对搬。
            await stream.WriteAsync(rewrittenSetup, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            Socket socket = local;
            await using StreamRelayEndpoint localEnd = new(
                stream, () => SafeShutdownSend(socket), ownsStream: true);
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
            local?.Dispose();
            _slots.Release();

            // 没接纳（显示连不上、连的时候出错）就等于什么都没转发出去 —— 单连接的名额退回。
            if (!accepted)
            {
                ReleaseSingle(claimedSingle);
            }
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

        // xauth 要一个具体的秒数；调用方把有效期关掉时退回 20 分钟。
        int timeoutSeconds = options.Timeout <= TimeSpan.Zero
            ? (int)TimeSpan.FromMinutes(20).TotalSeconds
            : Math.Max(60, (int)options.Timeout.TotalSeconds);

        // CreateTempSubdirectory 在类 Unix 上建的是 0700 目录。
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("velashell-x11-");
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
                ?? throw new SshForwardException(
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
            process = Process.Start(start) ?? throw new SshForwardException($"启动 {xauth} 失败。");
        }
        catch (SshForwardException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SshForwardException(
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

                throw new SshForwardException($"{xauth} generate 超过 {XAuthTimeout.TotalSeconds:0} 秒没有返回。");
            }

            _ = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new SshForwardException(
                    $"{xauth} generate 失败（退出码 {process.ExitCode}）：{error.Trim()}。" +
                    "非受信 X11 转发需要本机有 xauth、且 X server 支持 SECURITY 扩展；" +
                    "都没有的话请显式用受信模式（Trusted = true），但要清楚那等于把本机显示完全交给远端。");
            }
        }
    }

    private static void SafeShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // 对面已经走了。
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
    /// 交出来是为了让使用者能验证「发出去的确实不是真 cookie」——
    /// 而不是为了让它被当成凭据用。
    /// </remarks>
    internal ReadOnlySpan<byte> FakeCookie => _fakeCookie;
}
