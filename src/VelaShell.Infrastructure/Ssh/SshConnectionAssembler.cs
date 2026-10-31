using System.Runtime.Versioning;
using System.Security.Cryptography;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把一份 <see cref="VelaConnectionInfo" /> 装配成一条可用的 SSH 连接。
/// </summary>
/// <remarks>
/// <para>
/// 这里集中了原先散在 <c>InfrastructureServiceCollectionExtensions</c> 里的
/// <c>BuildSshClientSettings</c> / <c>AddCredential</c> / <c>BuildProxyChain</c> /
/// <c>AddHostAuthentication</c> 四段 —— 它们本来就是同一件事的四个部分,
/// 放在 DI 注册里只是历史原因,而那让「怎么连上去」这件事读起来要在两百行里跳着找。
/// </para>
/// <para>
/// <b>跳板链的方向</b>:<see cref="VelaConnectionInfo.JumpHost" /> 是「要先连谁」,
/// 所以最内层(没有自己跳板的那一跳)才是真正出网的那一跳 ——
/// 网络代理只作用于它,其余各跳都跑在 SSH 通道里。
/// </para>
/// </remarks>
internal static class SshConnectionAssembler
{
    /// <summary>每跳认证期限(用户可能要去掏手机看动态码);Tailscale 跳是收到验证链接之前的守卫期。</summary>
    internal static readonly TimeSpan DefaultAuthenticationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Tailscale 跳收到验证链接之后的等待上限:与服务端等用户批准的时长相当(约三十分钟)。</summary>
    internal static readonly TimeSpan TailscaleCheckTimeout = TimeSpan.FromMinutes(30);

    /// <summary>拼进失败消息的服务端说明最多多少字符。</summary>
    private const int MaxServerMessageLength = 300;

    /// <summary>
    /// 装配结果:连接工厂与建链超时。
    /// </summary>
    /// <remarks>
    /// 跳板链上的跳板连接不在这里:它们归各自拨出来的流所有,外层连接释放时逐层一起断开
    /// (见 <see cref="DialerChain.Jump(SshEndPoint, Func{SshJumpContext, CancellationToken, ValueTask{SshConnection}})" />)。
    /// </remarks>
    /// <param name="Connect">连接工厂。</param>
    /// <param name="ConnectTimeout">建链超时。</param>
    /// <param name="Banners">认证时服务端发来的横幅(链上每一跳都收),开 shell 时作为提示写进终端。</param>
    /// <param name="Route">这次连接实际经过的代理与跳板(连接信息面板要列);每次 <paramref name="Connect" /> 开始时清空重记。</param>
    internal readonly record struct Assembled(
        Func<CancellationToken, ValueTask<SshConnection>> Connect,
        TimeSpan ConnectTimeout,
        SshServerBanners Banners,
        SshRouteRecorder Route);

    /// <summary>按连接信息装配。</summary>
    /// <param name="info">连接信息(含跳板链)。</param>
    /// <param name="hostKey">主机指纹信任库;<see langword="null" /> 时不校验(测试用)。</param>
    /// <param name="settings">设置。</param>
    /// <param name="prompt">主机指纹的人工确认。</param>
    /// <param name="alerts">安全告警。</param>
    /// <param name="proxyResolver">出站代理。</param>
    /// <param name="keyboardPrompt">
    /// keyboard-interactive(2FA / OTP)的弹框;<see langword="null" /> 时不应答动态码,
    /// 只保留库的「密码兼答 keyboard-interactive」。
    /// </param>
    /// <param name="tailscaleCheckPrompt">验证窗口显示/更新/结束回调；显示只等待 UI 操作，不能等待验证完成。</param>
    /// <param name="authenticationTimeout">
    /// 每跳认证期限(Tailscale 跳是收到验证链接之前的守卫期);生产沿用两分钟,仅测试覆盖短期限。
    /// </param>
    /// <param name="tailscaleCheckTimeout">Tailscale 跳收到验证链接之后的等待上限;生产三十分钟,仅测试覆盖短期限。</param>
    public static Assembled Create(
        VelaConnectionInfo info,
        IHostKeyService? hostKey,
        ISettingsService? settings,
        IHostKeyPrompt? prompt,
        ISecurityAlertService? alerts,
        IProxyResolver? proxyResolver,
        IKeyboardInteractivePrompt? keyboardPrompt = null,
        Func<TailscaleSshCheckNotification, Task>? tailscaleCheckPrompt = null,
        TimeSpan? authenticationTimeout = null,
        TimeSpan? tailscaleCheckTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        // 没人在等(自动重连、健康巡检):要人裁决的主机指纹直接拒绝,Tailscale 复核也不开验证窗。
        // 巡检更进一步,动态码框也不弹(见 ConnectionInfo.IsUnattended):换成一个谁都不问、直接谢绝的应答界面 ——
        // 认得出的口令提示照样用已存的密码代答,动态码、改密码那一问谢绝、按「要人来」报回。
        // 不能干脆不挂应答器:那样库自带的「密码兼答」会把密码也填进验证码那一轮,白白记一次动态码失败。
        bool nobodyWaiting = info.IsAutomaticReconnect || info.IsUnattended;
        if (info.IsUnattended)
        {
            keyboardPrompt = UnattendedKeyboardPrompt.Instance;
        }

        // 链上每一跳共用同一个策略实例 —— 跳板与终点走同一套信任判定,自动重连时也一起不弹窗。
        IHostKeyPolicy policy = hostKey is null
            ? new DangerousAcceptAnyHostKeyPolicy()
            : new VelaHostKeyPolicy(hostKey, settings, prompt, alerts, nobodyWaiting, unattended: info.IsUnattended);

        TimeSpan connectTimeout = ConnectTimeout(settings);
        SshServerBanners banners = new();
        SshRouteRecorder route = new();

        // 最内层跳板真正出网,代理装在它身上;外层每一跳用库的跳板拨号器包住内层。
        // 每一跳的连接由这里现建(要先连 agent、按跳准备凭据),所以用回调那一种。
        ISshTransportDialer dialer = new ProxyTransportDialer(proxyResolver, route);

        List<VelaConnectionInfo> chain = JumpChainInnerToOuter(info);
        for (int i = 0; i < chain.Count; i++)
        {
            VelaConnectionInfo hop = chain[i];
            // 离终点几跳:终点的跳板是 1(连接配置里的「跳板主机」),它的跳板是 2……协商失败的一键补救据此找到要改哪条配置。
            int depth = chain.Count - i;
            ISshTransportDialer inner = dialer;
            dialer = DialerChain.Jump(
                new SshEndPoint(hop.Host, hop.Port),
                (jump, ct) => ConnectAsync(hop, policy, settings, inner, connectTimeout, keyboardPrompt, banners,
                    tailscaleCheckPrompt, authenticationTimeout, tailscaleCheckTimeout, nobodyWaiting, jump, depth, route,
                    info.PacketTrace, ct));
        }

        ISshTransportDialer finalDialer = dialer;
        return new Assembled(
            ct =>
            {
                // 跳板按连上的先后记(最内一跳最先连上,也就是从近到远);重连时从头记。
                route.Begin();
                return ConnectAsync(info, policy, settings, finalDialer, connectTimeout, keyboardPrompt, banners,
                    tailscaleCheckPrompt, authenticationTimeout, tailscaleCheckTimeout, nobodyWaiting, jump: null, hopDepth: 0, route,
                    info.PacketTrace, ct);
            },
            connectTimeout,
            banners,
            route);
    }

    /// <summary>
    /// 没人看着的探测(<see cref="VelaConnectionInfo.IsUnattended" />)用的 keyboard-interactive 界面:谁都不问,一律谢绝。
    /// </summary>
    /// <remarks>
    /// 应答器照旧代答认得出的口令提示;轮到动态码、改密码这类要人回答的,它收到 <see langword="null" />(等同用户点了取消),
    /// 库以 Aborted 结束,装配处报成「要你亲自连一次」。
    /// </remarks>
    private sealed class UnattendedKeyboardPrompt : IKeyboardInteractivePrompt
    {
        public static readonly UnattendedKeyboardPrompt Instance = new();

        public Task<IReadOnlyList<string>?> AskAsync(KeyboardInteractiveRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>?>(null);
    }

    /// <summary>
    /// 跳板链,**从最内层往外**。
    /// </summary>
    /// <remarks>
    /// <c>info.JumpHost</c> 是「连 info 之前要先连的那台」,所以链表本身是由外到内的;
    /// 装配拨号器要反过来 —— 先有最内层那条真实出站,才谈得上在它上面开通道。
    /// </remarks>
    private static List<VelaConnectionInfo> JumpChainInnerToOuter(VelaConnectionInfo info)
    {
        List<VelaConnectionInfo> hops = [];
        for (VelaConnectionInfo? hop = info.JumpHost; hop is not null; hop = hop.JumpHost)
        {
            hops.Add(hop);
        }
        hops.Reverse();
        return hops;
    }

    private static async ValueTask<SshConnection> ConnectAsync(
        VelaConnectionInfo info,
        IHostKeyPolicy policy,
        ISettingsService? settings,
        ISshTransportDialer dialer,
        TimeSpan connectTimeout,
        IKeyboardInteractivePrompt? keyboardPrompt,
        SshServerBanners banners,
        Func<TailscaleSshCheckNotification, Task>? tailscaleCheckPrompt,
        TimeSpan? authenticationTimeout,
        TimeSpan? tailscaleCheckTimeout,
        bool isAutomaticReconnect,
        SshJumpContext? jump,
        int hopDepth,
        SshRouteRecorder route,
        SshPacketTrace? packetTrace,
        CancellationToken cancellationToken)
    {
        bool isTailscale = info.AuthMethod == AuthMethod.Tailscale;
        using CancellationTokenSource? attempt = isTailscale
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        CancellationToken connectToken = attempt?.Token ?? cancellationToken;
        // Tailscale 跳的认证期限由这里管(见 TailscaleAuthDeadline),库的认证计时在这一跳关掉。
        TailscaleAuthDeadline? deadline = isTailscale
            ? new(policy, attempt!, authenticationTimeout ?? DefaultAuthenticationTimeout, tailscaleCheckTimeout ?? TailscaleCheckTimeout)
            : null;
        Guid attemptId = isTailscale ? Guid.NewGuid() : Guid.Empty;
        string target = HostAddress.FormatTarget(info.Username, info.Host, info.Port);
        object? checkGate = isTailscale ? new object() : null;
        bool completed = false;
        bool userCancelled = false;
        bool checkSeen = false;
        // 认证期间最近一段不带验证链接的横幅:服务端拒绝时多半先用它说明原因,再断开。
        string? lastBanner = null;
        Uri? currentUri = null;
        CancellationTokenSource? uiLifetime = null;

        string SafeFailure(string key, SshException? failure = null) =>
            $"{Strings.Get(key)}\n{target}" + (failure is null ? "" : $" [{failure.Reason} @ {failure.Phase}]");

        // 服务端说明拼进失败消息:去掉全部链接、净化控制字符、压成一行并限长 —— 它来自未认证的对端。
        string ServerMessage()
        {
            string summary = lastBanner is null
                ? ""
                : KeyboardInteractiveResponder.Clean(TailscaleSshCheckUri.RemoveLinks(lastBanner), MaxServerMessageLength)
                    .ReplaceLineEndings(" ");
            return summary.Length == 0 ? "" : "\n" + Strings.Format("SshErr_TailscaleServerMessage", summary);
        }

        void CancelCheck()
        {
            lock (checkGate!)
            {
                if (completed || userCancelled)
                {
                    return;
                }
                userCancelled = true;
                // 与结束标记共用边界；过期按钮永远不会访问已释放的 CTS。
                attempt!.Cancel();
            }
        }

        async ValueTask OnAuthenticationBannerAsync(string text, CancellationToken authToken)
        {
            await banners.OnBannerAsync(text, authToken).ConfigureAwait(false);
            if (!TailscaleSshCheckUri.TryGetVerificationUri(text, out Uri? uri))
            {
                lastBanner = text;
                return;
            }

            // 链接之前的横幅(法律声明之类)不是拒绝的理由。
            lastBanner = null;
            checkSeen = true;
            if (isAutomaticReconnect)
            {
                throw new VelaSshTailscaleAuthenticationException(SafeFailure("SshErr_TailscaleCheckRequired"));
            }
            if (tailscaleCheckPrompt is null)
            {
                throw new VelaSshTailscaleAuthenticationException(SafeFailure("SshErr_TailscaleCheckUiUnavailable"));
            }
            if (currentUri?.OriginalString == uri.OriginalString)
            {
                return;
            }

            authToken.ThrowIfCancellationRequested();
            // 服务端会等用户批准很久,守卫期到这里放宽到等待上限。
            DateTimeOffset? expiresAt = deadline!.ExtendForCheck();
            uiLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(authToken);
            currentUri = uri;
            try
            {
                // 仅等待显示/更新，不等待窗口关闭；认证器必须继续读 SUCCESS。
                await tailscaleCheckPrompt(new(attemptId, target, uri, CancelCheck, uiLifetime.Token) { ExpiresAt = expiresAt })
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                authToken.ThrowIfCancellationRequested();
                throw new VelaSshTailscaleAuthenticationException(SafeFailure("SshErr_TailscaleCheckUiUnavailable"));
            }
        }

        // 别的认证方式连上 check 模式的 Tailscale 节点:库开头那条 none 探测被服务端挂起等批准,
        // 链接在横幅里又被脱敏、也没有验证窗 —— 不拦的话用户干等两分钟,拿到一句笼统的认证超时。
        async ValueTask OnBannerRefusingTailscaleCheckAsync(string text, CancellationToken authToken)
        {
            await banners.OnBannerAsync(text, authToken).ConfigureAwait(false);
            if (TailscaleSshCheckUri.TryGetVerificationUri(text, out _))
            {
                throw new VelaSshTailscaleAuthenticationException(SafeFailure("SshErr_TailscaleAuthMethodRequired"));
            }
        }

        // agent 这一路的签名要回到 agent 去做,所以 agent 客户端得一直活到认证结束 ——
        // 连接建好之后就不再需要它(重协商不会重新认证),在这里释放。
        SshAgentClient? agent = null;
        // 每次尝试一个新的应答器:「口令只代答一次」与「用户点了取消」都是这一次认证的状态。
        KeyboardInteractiveResponder? keyboard =
            keyboardPrompt is null || info.AuthMethod == AuthMethod.Tailscale
                ? null : KeyboardInteractiveResponder.For(info, keyboardPrompt);
        // 私钥签名器归这一次建连所有:连接建好(或失败)之后就不再需要它(重协商不会重新认证),
        // 在 finally 里释放 —— 它释放时把私钥材料清零。交给后台「自动加钥」的那一把除外,由它用完再释放。
        IReadOnlyList<SshCredential> credentials = [];
        ISshSigner? handedToAgentLoader = null;
        try
        {
            if (info.AuthMethod == AuthMethod.Agent)
            {
                agent = await ConnectAgentAsync(info, cancellationToken).ConfigureAwait(false);
                credentials = await AgentCredentialsAsync(agent, cancellationToken, info).ConfigureAwait(false);
                if (keyboard is not null)
                {
                    credentials = [.. credentials, keyboard.ToCredential()];
                }
            }
            else
            {
                credentials = await BuildCredentialsAsync(info, cancellationToken, keyboard).ConfigureAwait(false);
            }

            SshConnectionOptions options = new(info.Username, info.Host, info.Port)
            {
                Dialer = dialer,
                HostKeyPolicy = deadline ?? policy,
                Credentials = credentials,
                ConnectTimeout = connectTimeout,
                AuthenticationTimeout = isTailscale ? Timeout.InfiniteTimeSpan : authenticationTimeout ?? DefaultAuthenticationTimeout,
                KeepAlive = KeepAlive(settings, info),
                Algorithms = Algorithms(info),
                // 「允许老算法」也要放开用户钥的 SHA-1 签名:只认 ssh-rsa 的老设备上,只放开 KEX / 主机密钥 / MAC
                // 而不放开这一项,RSA 私钥登录必然失败。库在对端不发 server-sig-algs 时会先试 SHA-2、被拒再降级一次。
                AllowSha1RsaSignatures = info.Ssh?.LegacyAlgorithms == true,
                BannerHandler = isTailscale ? OnAuthenticationBannerAsync : OnBannerRefusingTailscaleCheckAsync,
                DebugMessageHandler = banners.OnBannerAsync,
                PreAuthBannerHandler = banners.OnPreAuthBannerAsync,
                // 只有连接诊断给了轨迹才挂旁路;载荷不要(AllowPacketTapPayload 留着默认的 false)。
                PacketTap = PacketTapFor(packetTrace, target),
            };

            SshConnection connection;
            try
            {
                // 跳板这一跳经上下文去连:外层连接的计时器一起带进去,
                // 用户在跳板上看指纹、输动态码时外层停表。
                connection = jump is null
                    ? await SshConnection.ConnectAsync(options, connectToken).ConfigureAwait(false)
                    : await jump.ConnectAsync(options, connectToken).ConfigureAwait(false);
            }
            catch (SshNegotiationException ex) when (SshAlgorithmRemedy.TagHop(ex, hopDepth, target))
            {
                // 过滤器只记「谈不成的是哪一跳」、永远不接住:库会把它包进「连不上跳板」往外传,
                // 覆盖层上的一键补救据此改对那一跳的配置,而不是终点的。
                throw;
            }
            catch (Exception ex) when (isTailscale)
            {
                // 调用方取消优先，且不把带服务端原文的异常附到取消链上。
                cancellationToken.ThrowIfCancellationRequested();
                bool cancelled;
                lock (checkGate!)
                {
                    cancelled = userCancelled;
                }
                if (cancelled)
                {
                    throw new VelaSshAuthenticationCancelledException(SafeFailure("SshErr_TailscaleCheckCancelled"));
                }
                if (attempt!.IsCancellationRequested && deadline!.IsArmed)
                {
                    // 用户与调用方都没取消,是本跳的期限到了。没等到链接的超时与别的认证方式一样照常退避重试;
                    // 等过链接的要人重新验证。
                    throw new VelaSshTailscaleAuthenticationException(
                        SafeFailure(checkSeen ? "SshErr_TailscaleCheckTimeout" : "SshErr_TailscaleAuthTimeout"),
                        requiresUserAction: checkSeen);
                }
                if (ex is SshException { Phase: SshPhase.Authenticating } failure
                    && failure.Reason is not (SshFailureReason.HostKeyRejected or SshFailureReason.HostKeyChanged))
                {
                    // 服务端拒绝的真实样子是「横幅说明 + DISCONNECT」,库报的是对端断开而不是认证方法用尽 ——
                    // 断开前有说明就按拒绝报,并把说明带上。没有说明的断开、连接被重置照常退避重试,
                    // 除非已经出现过验证链接(那要人重新验证)。
                    bool rejected = failure.Reason is SshFailureReason.AuthenticationMethodExhausted or SshFailureReason.TwoFactorRequired
                                    || (failure.Reason == SshFailureReason.Disconnected && lastBanner is not null);
                    throw new VelaSshTailscaleAuthenticationException(
                        SafeFailure(rejected ? "SshErr_TailscaleRejected" : "SshErr_TailscaleCheckFailed", failure) + ServerMessage(),
                        requiresUserAction: rejected || checkSeen);
                }
                throw;
            }
            catch (SshConnectException ex) when (keyboard is not null
                                                 && ex is { Reason: SshFailureReason.Aborted, Phase: SshPhase.Authenticating }
                                                 && !cancellationToken.IsCancellationRequested)
            {
                // 认证期间会弹的框只有动态码框与「修改密码」框:库以 Aborted 结束,说明是应答回调抛了取消而调用方没取消
                // —— 用户在框上点了取消,是「不连了」而不是认证失败(规格 08 §2.1)。
                // 曾经库把这种取消报成认证超时,应答器只好自己记一笔、在这里按那一笔认回来。
                // 巡检时谢绝的是那个不问人的界面:说成「要你亲自连一次」,而不是「已取消」。
                string reasonKey = keyboardPrompt is UnattendedKeyboardPrompt
                    ? keyboard.PasswordChangeCancelled ? "SshErr_PasswordChangeUnattended" : "SshErr_KbdAuthUnattended"
                    : keyboard.PasswordChangeCancelled ? "SshErr_PasswordChangeCancelled" : "SshErr_KbdAuthCancelled";
                throw new VelaSshAuthenticationCancelledException(Strings.Get(reasonKey), ex);
            }
            catch (SshAuthenticationException ex) when (jump is not null)
            {
                // 在真实认证跳转换；Core 异常会穿透外层拨号，不能被 Tailscale 目标重新归类。
                string message = $"{SshInterop.LocalizeAuth(ex)}\n{HostAddress.FormatTarget(info.Username, info.Host, info.Port)}";
                string attempts = SshInterop.DescribeAttempts(ex);
                throw new VelaSshAuthenticationException(
                    string.IsNullOrWhiteSpace(attempts) ? message : $"{message}\n{attempts}", ex);
            }

            // 「自动加载密钥到 Agent」:认证成功之后才加(配错的钥不该进 agent),而且丢到后台 ——
            // agent 没在跑时要等满三秒才知道,那段等待不该落在连接路径上。
            if (AddKeysToAgent(settings)
                && SshAgentKeyLoader.TryGetKeyToAdd(info, credentials, out ISshSigner key, out string comment))
            {
                handedToAgentLoader = key;
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            // 加进这条连接用的那个 agent(按连接指定了端点时就是它),与 OpenSSH 的 AddKeysToAgent 一致。
                            await SshAgentKeyLoader.AddAsync(key, comment, ct => ConnectAgentAtAsync(info, ct)).ConfigureAwait(false);
                        }
                        finally
                        {
                            ((IDisposable)key).Dispose();
                        }
                    },
                    CancellationToken.None);
            }

            // 跳板链上的每一跳都走这里:哪一跳先断、为什么断,都进诊断日志
            SshDisconnectLog.Watch(connection, HostAddress.FormatTarget(info.Username, info.Host, info.Port));
            // 成功连上的跳板也记下(库的拨号器只在失败时留逐跳结果):连接信息面板据此列出经过的每一跳。
            if (jump is not null)
            {
                route.AddHop(info, connection);
            }
            return connection;
        }
        finally
        {
            if (isTailscale)
            {
                lock (checkGate!)
                {
                    completed = true;
                }
                deadline!.Finish();
                if (uiLifetime is not null)
                {
                    try
                    {
                        // 成功也只取消窗口生命周期，不能取消已建好的 SSH 传输。
                        uiLifetime.Cancel();
                    }
                    catch (Exception)
                    {
                        // 收窗清理错误不能覆盖当前跳的原始安全结果，也不能记录潜在的 URI。
                    }
                    try
                    {
                        await tailscaleCheckPrompt!(new(attemptId, target, null, null, CancellationToken.None)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 同上；End 已按顺序等待，令牌取消也会让窗口进行程序性收尾。
                    }
                    uiLifetime.Dispose();
                }
            }

            if (agent is not null)
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }

            DisposeOwnedSigners(credentials, except: handedToAgentLoader);
        }
    }

    /// <summary>
    /// 这一跳的报文旁路:给了协议轨迹(连接诊断)才有,平常的连接为 <see langword="null" /> —— 库在收发循环上只多一次判空。
    /// </summary>
    /// <param name="trace">协议轨迹;<see langword="null" /> 不挂。</param>
    /// <param name="hop">这一跳(<c>用户@主机:端口</c>),记进每一条。</param>
    internal static IPacketTap? PacketTapFor(SshPacketTrace? trace, string hop) =>
        trace is null ? null : new PacketTraceTap(trace, hop);

    /// <summary>把库的报文旁路接到 Core 的协议轨迹上:只转元信息,<b>从不碰 <see cref="PacketTapRecord.Payload" /></b>。</summary>
    private sealed class PacketTraceTap(SshPacketTrace trace, string hop) : IPacketTap
    {
        public void OnPacket(in PacketTapRecord record) =>
            trace.Record(
                hop,
                record.Direction == PacketDirection.Outbound ? SshPacketDirection.Sent : SshPacketDirection.Received,
                record.MessageNumber,
                record.Length,
                record.SequenceNumber,
                record.ChannelNumber);
    }

    /// <summary>释放这一次建连读出来的私钥签名器(释放时清零私钥)。</summary>
    /// <remarks>
    /// 只管本地读出的私钥(<see cref="InMemorySshSigner" />)与包着它(或包着系统密钥库那把)的证书签名器(它会连同里面那把一起释放);
    /// 系统密钥库的签名器(<see cref="CngSshSigner" />)也在这里释放 —— 释放的是密钥库句柄,私钥本来就不在本进程里。
    /// agent 的签名器不归这里 —— 它背后的连接由 agent 客户端管。
    /// 曾经从不释放:私钥材料在托管堆上一直留到 GC,证书读失败时已经读出的私钥也留在原处。
    /// </remarks>
    internal static void DisposeOwnedSigners(IReadOnlyList<SshCredential> credentials, ISshSigner? except)
    {
        foreach (SshCredential credential in credentials)
        {
            if (credential is not PublicKeyCredential { Signer: var signer } || ReferenceEquals(signer, except))
            {
                continue;
            }

            // 系统密钥库的签名器释放的是密钥库句柄(私钥本来就不在本进程里)。
            if (signer is InMemorySshSigner or SshCertificateSigner || IsCngSigner(signer))
            {
                ((IDisposable)signer).Dispose();
            }
        }
    }

    private static bool IsCngSigner(ISshSigner signer) => OperatingSystem.IsWindows() && signer is CngSshSigner;

    /// <summary>
    /// 这一跳的算法集:老算法开关与四个自定义清单(见 <see cref="SshAlgorithmPreferences" />),
    /// 开了压缩就把 <c>zlib@openssh.com</c> 排在 <c>none</c> 前面。跳板链上每一跳各带各的。
    /// </summary>
    /// <remarks>
    /// 压缩是**协商**出来的:服务端没开(<c>Compression no</c>)时自动落回不压缩,
    /// 不会因此连不上。用的是 <c>zlib@openssh.com</c>(认证之后才开始压缩)而不是
    /// 老式的 <c>zlib</c> —— 后者在认证之前就压缩,是历史上 CRIME 一类攻击的入口。
    /// </remarks>
    internal static SshAlgorithmSet Algorithms(VelaConnectionInfo info) => SshAlgorithmPreferences.Build(info.Ssh);

    /// <summary>连本机系统默认的 agent(按连接指定了端点的见 <see cref="ConnectAgentAtAsync" />)。</summary>
    /// <remarks>
    /// Windows 上 agent 服务没起时命名管道根本不存在 —— 等它出现的时限在库里
    /// (<c>SshAgentClient.PipeConnectTimeout</c>),到点以 <see cref="SshFailureReason.AgentNotRunning" /> 报出,
    /// agent 转发那一路也走同一个时限。宿主不再另套一层计时。
    /// <para>
    /// 端点交给库的默认值(<c>SshAgentClient.DefaultEndpoint</c>):Windows 上 <c>SSH_AUTH_SOCK</c> 是命名管道时采纳它
    /// (1Password、KeePassXC),否则用 OpenSSH agent 服务的管道。曾经库在 Windows 上一律无视 <c>SSH_AUTH_SOCK</c>,
    /// 宿主在这里另判断了一遍。
    /// </para>
    /// </remarks>
    internal static ValueTask<SshAgentClient> ConnectLocalAgentAsync(CancellationToken cancellationToken) =>
        SshAgentClient.ConnectAsync(endpoint: null, cancellationToken);

    /// <summary>连这一跳用的 agent:配置里按连接指定了端点(<see cref="SshSessionOptions.AgentEndpoint" />)就连它,否则系统默认。</summary>
    /// <remarks>开头的 <c>~</c> 在这里才展开(配置随云同步到别的机器,主目录不一样),见 <see cref="SshAgentEndpoints.Resolve" />。</remarks>
    internal static ValueTask<SshAgentClient> ConnectAgentAtAsync(VelaConnectionInfo info, CancellationToken cancellationToken) =>
        SshAgentClient.ConnectAsync(SshAgentEndpoints.Resolve(info.Ssh?.AgentEndpoint), cancellationToken);

    private static async ValueTask<SshAgentClient> ConnectAgentAsync(VelaConnectionInfo info, CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAgentAtAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (SshAgentException ex)
        {
            throw AgentUnavailable(info, ex);
        }
    }

    /// <summary>连不上 agent 的提示:按连接指定了端点时点名那个端点,「没在运行」也不再叫人去起 Windows 的 agent 服务 —— 那多半不是它。</summary>
    private static VelaSshAuthenticationException AgentUnavailable(VelaConnectionInfo info, SshAgentException ex)
    {
        if (SshAgentEndpoints.Normalize(info.Ssh?.AgentEndpoint) is not { } endpoint)
        {
            return new(Strings.Format("SshErr_AgentUnavailable", SshInterop.Localize(ex)), ex);
        }
        string reason = ex.Reason == SshFailureReason.AgentNotRunning
            ? Strings.Get("SshErr_AgentEndpointNotRunning")
            : SshInterop.Localize(ex);
        return new(Strings.Format("SshErr_AgentEndpointUnavailable", endpoint, reason), ex);
    }

    /// <summary>agent 里的每一把钥都作为一个候选凭据,由库按顺序逐把试。</summary>
    /// <remarks>
    /// agent 里一把钥都没有时直接说清楚,而不是把一个空凭据列表交给库 ——
    /// 那样用户拿到的是一句笼统的「认证方法已用尽」,看不出问题在本机。
    /// </remarks>
    /// <param name="agent">连上的 agent。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="info">这一跳的连接信息(提示里点名按连接指定的端点);<see langword="null" /> 按系统默认的说法。</param>
    internal static async ValueTask<IReadOnlyList<SshCredential>> AgentCredentialsAsync(
        SshAgentClient agent, CancellationToken cancellationToken, VelaConnectionInfo? info = null)
    {
        IReadOnlyList<SshCredential> credentials;
        try
        {
            credentials = await agent.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshAgentException ex)
        {
            // 与连 agent 失败同一条提示、同样按原因码本地化;曾经这里直接塞库的消息(写给开发者看的中文)。
            throw info is null
                ? new VelaSshAuthenticationException(Strings.Format("SshErr_AgentUnavailable", SshInterop.Localize(ex)), ex)
                : AgentUnavailable(info, ex);
        }
        if (credentials.Count > 0)
        {
            return credentials;
        }
        throw new VelaSshAuthenticationException(SshAgentEndpoints.Normalize(info?.Ssh?.AgentEndpoint) is { } endpoint
            ? Strings.Format("SshErr_AgentEndpointNoKeys", endpoint)
            : Strings.Get("SshErr_AgentNoKeys"));
    }

    /// <summary>
    /// 按用户选的认证方式给出凭据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只用用户显式选择的那一种,不做任何隐式回退。</b>库本身也不回退
    /// (不自动读 <c>~/.ssh/id_*</c>、不自动连 ssh-agent),两边是一致的。
    /// 要用 agent,得在认证方式里显式选「SSH Agent」(<see cref="AuthMethod.Agent" />,
    /// 不经过这里,见 <c>ConnectAsync</c>);选了别的方式就不会再去碰 agent ——
    /// 静默回退既会制造噪声,也可能拿一把用户没打算用的钥去认证。
    /// </para>
    /// <para>
    /// <b>密码那一路同时应答 <c>keyboard-interactive</c>。</b>很多服务端
    /// (尤其关了 <c>PasswordAuthentication</c> 却开着 PAM 的)只接受后者,
    /// 而用户填的就是同一个密码。没有界面可问时(<paramref name="keyboard" /> 为空)
    /// 由 <see cref="PasswordCredential" /> 的默认行为兼答;有界面时换成
    /// <see cref="KeyboardInteractiveResponder" />:口令提示照样用这个密码答,
    /// 验证码之类的弹框问用户,一次交互就走完。库的兼答此时关掉:它每次交互只答一次口令、
    /// 之后的轮次回空串(不会把密码填进验证码那一轮),但那样验证码一轮必然失败,要再走一遍才轮到界面。
    /// </para>
    /// <para>
    /// <b>有界面时普通认证方式后面都跟一条 keyboard-interactive</b>,好接住
    /// <c>AuthenticationMethods publickey,keyboard-interactive</c>(钥 + 动态码)的第二步。
    /// 它不会变成口令回退:非密码认证的那一条遇到单纯的口令提示只答空串。
    /// </para>
    /// </remarks>
    internal static async ValueTask<IReadOnlyList<SshCredential>> BuildCredentialsAsync(
        VelaConnectionInfo info, CancellationToken cancellationToken, KeyboardInteractiveResponder? keyboard = null)
    {
        // Tailscale 的身份由已登录的本机网络提供;只发送库的初始 none 请求。
        if (info.AuthMethod == AuthMethod.Tailscale)
        {
            return [];
        }

        IReadOnlyList<SshCredential> primary = await PrimaryCredentialsAsync(info, keyboard, cancellationToken)
            .ConfigureAwait(false);
        return keyboard is null ? primary : [.. primary, keyboard.ToCredential()];
    }

    private static async ValueTask<IReadOnlyList<SshCredential>> PrimaryCredentialsAsync(
        VelaConnectionInfo info, KeyboardInteractiveResponder? keyboard, CancellationToken cancellationToken)
    {
        switch (info.AuthMethod)
        {
            case AuthMethod.Password:
                // 有界面时,服务端要求先改密码(密码过期)就弹框问新密码(规格 04 §5.1);没界面就不改,报「要先改密码」。
                return
                [
                    new PasswordCredential(info.Password ?? "")
                    {
                        CanAnswerKeyboardInteractive = keyboard is null,
                        NewPasswordProvider = keyboard is null ? null : keyboard.AskNewPasswordAsync,
                    }
                ];

            case AuthMethod.PrivateKey:
                {
                    ISshSigner signer = await LoadSignerAsync(
                        info.PrivateKeyPath!, info.PrivateKeyPassphrase, cancellationToken).ConfigureAwait(false);
                    return [new PublicKeyCredential(signer, info.PrivateKeyPath)];
                }

            case AuthMethod.Certificate:
                {
                    InMemorySshSigner signer = await LoadSignerAsync(
                        info.PrivateKeyPath!, info.PrivateKeyPassphrase, cancellationToken).ConfigureAwait(false);
                    return [await WithCertificateAsync(signer, info.CertificatePath!, cancellationToken).ConfigureAwait(false)];
                }

            case AuthMethod.KeyStore:
                {
                    ISshSigner signer = await OpenKeyStoreSignerAsync(info, cancellationToken).ConfigureAwait(false);
                    // 证书是可选的:CA 给密钥库里这把钥签了用户证书,就照「私钥 + 证书」那一路出示证书 ——
                    // 服务端认的是 CA,不必把这把钥的公钥加进 authorized_keys。签名照旧由密钥库做。
                    return string.IsNullOrWhiteSpace(info.CertificatePath)
                        ? [new PublicKeyCredential(signer, info.KeyStoreKey!.KeyName)]
                        : [await WithCertificateAsync(signer, info.CertificatePath, cancellationToken).ConfigureAwait(false)];
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(info), info.AuthMethod, "Unsupported authentication method.");
        }
    }

    /// <summary>
    /// 打开系统密钥库里那把钥(SSH 库的 <see cref="CngSshSigner" />):私钥不出密钥库,签名交给它做。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 打开不弹框;TPM / 智能卡要 PIN 时由 Windows 在签名那一刻问(库先问服务端认不认这把钥再签,所以不认的钥不会白问一次 PIN)。
    /// 放到线程池上打开:智能卡的密钥库打开一把钥可能要跟读卡器来回几趟。
    /// </para>
    /// <para>
    /// 没人看着的探测(会话健康巡检,<see cref="VelaConnectionInfo.IsUnattended" />)以「不许弹框」打开(<see cref="CngKeyOpenOptions.Silent" />):
    /// 一次最多六台同时查,每台弹一个 PIN / 插卡 / 确认框就是一摞没人点的系统框,手册与 spec §11C 都说巡检不弹框。
    /// 密钥库非得问人才能用这把钥时,打开或签名直接失败(CNG 约定的错误码 <c>NTE_SILENT_CONTEXT</c>),巡检把它归到「需要凭据」。
    /// </para>
    /// <para>
    /// 别的平台没有 CNG:配置可能是从 Windows 同步过来的,如实说「这台电脑上用不了」,而不是一句不知所云的认证失败。
    /// 打不开(钥被删了、在别的电脑上、卡没插)由库报成 <see cref="SshPrivateKeyException" />(<see cref="SshKeyProblemKind.KeyStoreUnavailable" />),
    /// 宿主按种类本地化(<c>SshErr_KeyStoreUnavailable</c>)。
    /// </para>
    /// </remarks>
    private static async ValueTask<ISshSigner> OpenKeyStoreSignerAsync(VelaConnectionInfo info, CancellationToken cancellationToken)
    {
        if (info.KeyStoreKey is not { } key)
        {
            throw new VelaSshAuthenticationException(Strings.Get("Svc_KeyStoreKeyRequired"));
        }
        return await Task.Run<ISshSigner>(
            () => OperatingSystem.IsWindows()
                ? OpenCng(key, silent: info.IsUnattended)
                : throw new VelaSshAuthenticationException(Strings.Format("SshErr_KeyStoreNotOnThisPlatform", key.KeyName)),
            cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static CngSshSigner OpenCng(SystemKeyReference key, bool silent) =>
        CngSshSigner.Open(
            key.KeyName,
            new CngProvider(key.Provider),
            CngOpenOptions(key, silent));

    /// <summary>打开密钥库里那把钥的选项:机器级的钥带 <see cref="CngKeyOpenOptions.MachineKey" />,没人看着时带 <see cref="CngKeyOpenOptions.Silent" />。</summary>
    internal static CngKeyOpenOptions CngOpenOptions(SystemKeyReference key, bool silent) =>
        (key.MachineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None)
        | (silent ? CngKeyOpenOptions.Silent : CngKeyOpenOptions.None);

    /// <summary>
    /// 给已经打开的签名器配上证书(<c>*-cert.pub</c>):「私钥 + 证书」与「系统密钥库 + 证书」共用这一段。
    /// </summary>
    /// <remarks>
    /// <see cref="SshCertificateSigner.Create" /> 当场核对「证书与私钥是不是一对」—— 不核对的话配错了的表现是
    /// 服务端一句 Permission denied,与「CA 不被信任」「主体不匹配」没法区分。
    /// 证书签名器接管 <paramref name="signer" />:释放它就连同里面那把一起释放(私钥清零、密钥库句柄关掉)。
    /// 证书读不出来或与钥不是一对时,<paramref name="signer" /> 没有人会再用,当场释放。
    /// </remarks>
    private static async ValueTask<PublicKeyCredential> WithCertificateAsync(
        ISshSigner signer, string certificatePath, CancellationToken cancellationToken)
    {
        try
        {
            OpenSshCertificate certificate = await OpenSshCertificate
                .LoadAsync(certificatePath, cancellationToken).ConfigureAwait(false);
            return new PublicKeyCredential(SshCertificateSigner.Create(certificate, signer), certificatePath);
        }
        catch
        {
            (signer as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 读一把私钥。
    /// </summary>
    /// <remarks>
    /// 支持的格式:OpenSSH(<b>含加密</b>)、PKCS#1、PKCS#8(含加密)、SEC1、PuTTY 的 <c>.ppk</c>。
    /// <para>
    /// 上一版这里有一段 <c>OpenSshPrivateKey.TryConvertToOpenSsh</c> 的转换:
    /// 因为那个底层库**只认 OpenSSH 格式**,用户导入的传统 PEM 会被静默跳过,
    /// 认证以一句 "skipped: publickey" 失败。现在库原生认这些格式,那段转换整个不需要了。
    /// </para>
    /// <para>
    /// 整个放到线程池上跑:加密私钥的口令派生(<c>bcrypt_pbkdf</c> 按轮数、Argon2id 按内存)是同步的 CPU 计算,
    /// 库把切不切线程留给调用方,而建连是从界面线程发起的。曾经直接调用,解密离不离开调用线程全看库里读文件那一步
    /// 是不是异步完成 —— 实测各平台上都是,所以没卡过;但那是碰巧,库哪天换成同步读、或者加一层缓存,
    /// 一把轮数大的私钥就会把界面卡住好几秒。
    /// </para>
    /// </remarks>
    internal static async ValueTask<InMemorySshSigner> LoadSignerAsync(
        string path, string? passphrase, CancellationToken cancellationToken) =>
        await Task.Run(
            () => SshPrivateKeyFile.LoadAsync(
                path, string.IsNullOrWhiteSpace(passphrase) ? null : passphrase, cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);

    /// <summary>设置 → 密钥管理 →「自动加载密钥到 Agent」。读不到设置时按默认值(关)。</summary>
    private static bool AddKeysToAgent(ISettingsService? settings)
    {
        try
        {
            return settings?.GetSnapshotBlocking().Keys.AddKeysToAgent ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan ConnectTimeout(ISettingsService? settings)
    {
        try
        {
            return TimeSpan.FromSeconds(
                Math.Clamp(settings.GetSnapshotBlocking().General.ConnectTimeoutSeconds, 1, 600));
        }
        catch
        {
            return TimeSpan.FromSeconds(10);
        }
    }

    /// <summary>
    /// 保活心跳间隔:本次连接有会话级覆盖就用它,否则跟随全局设置。
    /// </summary>
    /// <remarks>
    /// 覆盖值随 <see cref="VelaConnectionInfo.KeepAliveSeconds" /> 一路带下来(F-06)。
    /// 跳板链上每一跳各带各的。
    /// </remarks>
    private static SshKeepAlivePolicy KeepAlive(ISettingsService? settings, VelaConnectionInfo info)
    {
        try
        {
            int seconds = info.KeepAliveSeconds ?? settings.GetSnapshotBlocking().General.KeepAliveSeconds;
            return seconds > 0 ? new SshKeepAlivePolicy(TimeSpan.FromSeconds(seconds)) : SshKeepAlivePolicy.Disabled;
        }
        catch
        {
            return SshKeepAlivePolicy.Disabled;
        }
    }
}
