// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §4  版本交换
//   RFC 4252     认证
//   RFC 4253 §11.1 DISCONNECT（用户取消认证时发 AUTH_CANCELLED_BY_USER）
//   行为规格:    velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/08-failures.md

using System.Buffers;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

public sealed partial class SshConnection
{
    /// <summary>拨号 → 版本交换 → 密钥交换 → 主机密钥裁决 → 认证 → 可用。</summary>
    /// <param name="options">连接参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>一条已经认证、开始收包的会话。</returns>
    /// <remarks>
    /// <para>
    /// <b>连接超时与主机密钥裁决的超时是分开的两把计时器。</b>
    /// 裁决要弹窗问用户，把那段时间算进连接超时的话，用户点完「信任」
    /// 这一轮已经被判死 —— 然后就得在外面补一次重连，而那次重连
    /// 会再问一遍同样的问题。
    /// </para>
    /// <para>
    /// 认证也是单独一把（默认两分钟）：用户可能要去掏手机看动态码。
    /// </para>
    /// </remarks>
    public static async ValueTask<SshConnection> ConnectAsync(
        SshConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 〔velashell-docs/zh/ssh/spec/08 §7〕建连耗时按结局与停在哪一步记（velashell.ssh.connect.duration）。
        string metricsHost = options.MetricsHost ?? options.Host;
        long started = options.TimeProvider.GetTimestamp();
        ConnectProgress progress = new();
        try
        {
            SshConnection connection = await ConnectCoreAsync(options, metricsHost, progress, cancellationToken).ConfigureAwait(false);
            SshMetrics.RecordConnect(metricsHost, options.TimeProvider.GetElapsedTime(started), null, SshPhase.Open);
            return connection;
        }
        catch (Exception ex)
        {
            SshMetrics.RecordConnect(metricsHost, options.TimeProvider.GetElapsedTime(started), ex, progress.Phase);
            throw;
        }
    }

    /// <summary>建连走到了哪一步：失败的异常不是 <see cref="SshException"/>（回调自己抛的、调用方取消）时，度量靠它说停在哪。</summary>
    private sealed class ConnectProgress
    {
        public SshPhase Phase { get; set; } = SshPhase.Dialing;
    }

    /// <summary><see cref="ConnectAsync"/> 的本体。</summary>
    private static async ValueTask<SshConnection> ConnectCoreAsync(
        SshConnectionOptions options, string metricsHost, ConnectProgress progress, CancellationToken cancellationToken)
    {
        Stream? stream = null;
        SshPacketTransport? transport = null;

        // 算法清单由多个 init 属性共同决定合不合法，没法在构造时拦 —— 在这里当场报，
        // 而不是等连上之后才在协商里出问题。
        options.Algorithms.Validate();

        // 超时的时候要说清是哪一步超时了 —— 「连接超时」这四个字
        // 对「DNS 慢」「端口被防火墙丢包」「服务端卡在密钥交换」「用户没输动态码」
        // 是同一句话，而这四件事的下一步完全不同。
        SshPhase phase = SshPhase.Dialing;

        // 失败路径要据此分清「本库的计时器到点了」与「回调自己抛了取消」（见下面接取消的那一段）。
        SshConnectDeadline? connectTimer = null;
        CancellationTokenSource? authTimer = null;

        try
        {
            // ① 拨号 + 版本交换 + 密钥交换，共用一把连接计时器。
            //    主机密钥裁决期间它停表（见 SshConnectDeadline）；经跳板时挂在外层计时器上，
            //    这里停表时外层也跟着停。
            using SshConnectDeadline connect = new(
                options.ConnectTimeout, cancellationToken, options.OuterDeadline, options.TimeProvider);
            connectTimer = connect;

            // 各阶段的耗时（ConnectTimings）：排障时回答「连得慢，慢在哪一步」。
            TimeProvider clock = options.TimeProvider;
            long dialStartedAt = clock.GetTimestamp();

            stream = await options.Dialer
                .DialAsync(
                    SshDialTarget.Direct(options.Host, options.Port) with { Deadline = connect, MetricsHost = metricsHost },
                    connect.Token)
                .ConfigureAwait(false);

            transport = new SshPacketTransport(stream)
            {
                PacketTap = options.PacketTap,
                AllowPacketTapPayload = options.AllowPacketTapPayload,
                MetricsHost = metricsHost,
            };
            progress.Phase = phase = SshPhase.VersionExchange;
            long dialedAt = clock.GetTimestamp();

            SshVersionExchangeResult versions = await SshVersionExchange
                .ExchangeAsync(transport, cancellationToken: connect.Token)
                .ConfigureAwait(false);
            long versionsExchangedAt = clock.GetTimestamp();

            if (options.PreAuthBannerHandler is { } preAuthBanner && versions.PreAuthBanner.Count > 0)
            {
                await SshCallbackFaultException.InvokeAsync(
                    () => preAuthBanner(versions.PreAuthBanner, connect.Token)).ConfigureAwait(false);
            }

            progress.Phase = phase = SshPhase.KeyExchange;

            // 已经记着这台主机哪些类型的主机密钥，就把那些类型排到前面 —— 正常的服务端因此谈成
            // 已知的那一种（见 IHostKeyTypePreference）。重协商用的是同一份清单。
            SshAlgorithmSet algorithms = options.Algorithms;
            if (options.HostKeyPolicy is IHostKeyTypePreference preference)
            {
                IReadOnlyList<string> knownTypes = await SshCallbackFaultException.InvokeAsync(
                    () => preference.GetKnownKeyTypesAsync(options.Host, options.Port, connect.Token))
                    .ConfigureAwait(false);
                algorithms = algorithms.PreferHostKeyTypes([.. knownTypes]);
            }

            SshKeyExchangeRunner runner = new(transport, algorithms, options.HostKeyPolicy)
            {
                // ② 裁决用自己的计时器（见方法说明）：连接计时器在裁决期间停表，
                //    裁决与持久化只认调用方的取消。
                HostKeyDecisionTimeout = options.HostKeyDecisionTimeout,
                ConnectDeadline = connect,
                DecisionCancellationToken = cancellationToken,
            };

            SshKeyExchangeResult kex = await runner
                .RunAsync(versions, options.Host, options.Port, cancellationToken: connect.Token)
                .ConfigureAwait(false);
            long keysExchangedAt = clock.GetTimestamp();

            // ③ 认证又是一把（默认两分钟）。
            progress.Phase = phase = SshPhase.Authenticating;
            using var auth = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            authTimer = auth;

            if (options.AuthenticationTimeout != Timeout.InfiniteTimeSpan)
            {
                auth.CancelAfter(options.AuthenticationTimeout);
            }

            // 认证期间对端发起的重协商（RFC 4253 §9 允许任何时刻）：钉住首次的主机密钥，就地跑完。
            // 压缩不在这里装 —— 唯一支持的 zlib@openssh.com 要等认证成功之后才启用（下面的 ④ 用最新一次的协商结果）。
            SshKeyExchangeResult latest = kex;
            SshAuthenticator authenticator = new(transport, options.UserName, kex.SessionId)
            {
                BannerHandler = options.BannerHandler,
                DebugMessageHandler = options.DebugMessageHandler,
                AllowSha1RsaSignatures = options.AllowSha1RsaSignatures,
                SessionProof = kex.CreateSessionProof(),
                PeerKexInitHandler = async (peerKexInit, ct) =>
                {
                    SshKeyExchangeRunner rekey = new(transport, RestrictToPinnedHostKey(algorithms, kex.HostKey), options.HostKeyPolicy)
                    {
                        PinnedHostKey = kex.HostKey,
                        InitialStrictKeyExchange = kex.Algorithms.StrictKeyExchange,
                    };
                    SshKeyExchangeResult result = await rekey.RunAsync(
                        versions, options.Host, options.Port,
                        sessionId: kex.SessionId,
                        peerKexInit: peerKexInit.ToArray(),
                        cancellationToken: ct).ConfigureAwait(false);
                    latest = latest with { Algorithms = result.Algorithms };
                },
            };

            List<SshCredential> credentials = [.. options.Credentials];
            SshAuthenticationResult? authenticated = null;

            // 经跳板时，这条连接的认证跑在外层连接的拨号计时之内 —— 而认证是在等人
            // （输口令、看手机上的动态码）。那段时间停外层的表，认证用它自己的这把计时器；
            // 不停的话，用户在跳板上输动态码花了二十秒，外层十五秒的连接超时早就到了。
            options.OuterDeadline?.Pause();
            try
            {
                authenticated = await authenticator.AuthenticateAsync(credentials, auth.Token).ConfigureAwait(false);
            }
            finally
            {
                options.OuterDeadline?.Resume();
            }
            long authenticatedAt = clock.GetTimestamp();

            // ④ 压缩要在**认证成功之后**才挂上去（zlib@openssh.com 的语义）。
            //
            // 推迟不是为了省事：认证之前的报文里有密码与公钥，而压缩会让
            // 密文长度泄漏明文的可压缩性 —— 对着一个长度可观测的口令做
            // 压缩旁路攻击（CRIME 那一类）是现实的。
            ActivateDelayedCompression(transport, latest.Algorithms);

            // ⑤ 认证过了，报文上限放宽到认证后的默认值。
            //
            // 握手期锁在 35000 是对的（RFC 4253 §6.1 的下限，也是未认证对端能让我们
            // 分配的最大单块）；认证之后还锁着，通道宣告的包上限一旦超过约 34 KB，
            // 服务端按我们宣告的大小发来的报文就会被当成协议错误。
            transport.MaxPacketLength = SshPacketFormat.DefaultMaxPacketLength;

            SshConnection connection = new(transport, latest, options.Limits)
            {
                // 重协商要把密钥交换整个再跑一遍，所以把它需要的东西留下来。
                // 没有这一份，对端发起重协商时我们只能报错断连 ——
                // 而 OpenSSH 默认每 1 GiB 或每小时就会发起一次。
                RekeyContext = new SshRekeyContext(
                    algorithms,
                    options.HostKeyPolicy,
                    versions,
                    options.Host,
                    options.Port,
                    MinimumRsaKeyBits: SshKeyExchangeRunner.DefaultMinimumRsaKeyBits,
                    options.HostKeyDecisionTimeout),
                KeepAlive = options.KeepAlive,
                Time = options.TimeProvider,
                RekeyPolicy = options.Rekey,
                RekeyCheckInterval = options.RekeyCheckInterval,
                RekeyHardPacketLimit = options.RekeyHardPacketLimit,
                RekeyTimeout = options.RekeyTimeout,
                Description = $"{options.UserName}@{options.EndPoint}",
                PeerSupportsPing = authenticated?.ServerExtensions.Contains(SshProtocolNames.ExtPing) == true,
                MetricsHost = metricsHost,
                HostKeyPersistFailure = runner.HostKeyPersistFailure,
                DebugMessageHandler = options.DebugMessageHandler,
                PeerVersion = PeerText.Sanitize(versions.ServerVersion, 255),
                AuthenticationMethod = authenticated?.Method ?? "",
                ServerSignatureAlgorithms = authenticated?.ServerSignatureAlgorithms ?? [],
                ConnectTimings = new SshConnectTimings(
                    clock.GetElapsedTime(dialStartedAt, dialedAt),
                    clock.GetElapsedTime(dialedAt, versionsExchangedAt),
                    clock.GetElapsedTime(versionsExchangedAt, keysExchangedAt),
                    clock.GetElapsedTime(keysExchangedAt, authenticatedAt)),
            };

            connection.Start();
            return connection;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 〔velashell-docs/zh/ssh/spec/08 §2.1〕调用方没取消，也不一定是我们的计时器到了：判超时只看本库自己的那把。
            // 都没到点却冒出了取消，是回调自己抛的 —— 用户在口令框、动态码框上点了「取消」，回调最自然的写法就是抛它。
            // 曾经一律报成「限 120 秒」的超时，宿主只好在回调里另记一笔、失败之后再认回来。
            bool timedOut = phase == SshPhase.Authenticating
                ? authTimer?.IsCancellationRequested == true
                : connectTimer?.IsExpired == true;

            if (!timedOut)
            {
                if (phase == SshPhase.Authenticating && transport is not null)
                {
                    // 认证期间密钥已经装好：告诉服务端是用户不连了，而不是让它等到自己的 LoginGraceTime。
                    await TrySendDisconnectAsync(transport, SshDisconnectReason.AuthCancelledByUser).ConfigureAwait(false);
                }
                await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

                throw new SshConnectException(
                    SshFailureReason.Aborted, phase,
                    $"连 {options.EndPoint} 时在 {phase} 这一步被使用者取消了（回调抛出了取消，而调用方的令牌与本库的计时器都没有触发）。",
                    ex);
            }

            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

            // 是我们自己的计时器到了。
            (SshFailureReason reason, string what, TimeSpan budget) = phase switch
            {
                SshPhase.Dialing =>
                    (SshFailureReason.TcpTimeout, "建立 TCP 连接", options.ConnectTimeout),
                SshPhase.VersionExchange =>
                    (SshFailureReason.Timeout, "交换版本标识串（对端可能不是 SSH 服务）", options.ConnectTimeout),
                SshPhase.KeyExchange =>
                    (SshFailureReason.Timeout, "密钥交换", options.ConnectTimeout),
                _ =>
                    (SshFailureReason.Timeout, "认证", options.AuthenticationTimeout),
            };

            throw new SshConnectException(
                reason, phase,
                $"连 {options.EndPoint} 时在「{what}」这一步超时" +
                $"（限 {budget.TotalSeconds:0.#} 秒）。");
        }
        catch (SshCallbackFaultException fault)
        {
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

            // 调用方回调自己抛的：原样交还（见 SshCallbackFaultException）。
            fault.ThrowOriginal();
            throw;   // 到不了；编译器要它
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException && phase != SshPhase.Dialing)
        {
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

            // 拨通之后流上的读写出错，就是连接断了（对端重置、链路掉了）。按会话期间的同一套口径报
            // （见 ConnectionFaults.NormalizeFault），不让原始的 IOException / SocketException 漏给调用方。
            // 拨号阶段不在这里管：各个拨号器自己把失败翻成了带原因的 SshConnectException。
            throw new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, phase,
                $"连 {options.EndPoint} 时连接断了（{phase}）：{PeerText.Sanitize(ex.Message, 256)}", ex);
        }
        catch (SshFrameFormatException ex) when (ex.PeerClosedMidPacket)
        {
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);
            throw new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, phase, $"连 {options.EndPoint} 时对端在一个报文中途断开（{phase}）。", ex);
        }
        catch (Exception ex) when (ex is SshFrameFormatException or SshWireFormatException)
        {
            await TrySendHandshakeDisconnectAsync(transport, phase, ex).ConfigureAwait(false);
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

            // 〔velashell-docs/zh/ssh/spec/08 §二〕握手与认证期间对端发来的东西解不开（KEXINIT 的名单被截断、
            // 收到空载荷的帧……）：与会话期间同一个口径，报协议错误（见 ConnectionFaults.NormalizeFault）。
            // 曾经让这两个 internal 异常原样漏出 ConnectAsync —— 调用方 catch (SshException) 接不住。
            throw new SshProtocolException(phase, $"连 {options.EndPoint} 时对端违反了协议（{phase}）：{ex.Message}", ex);
        }
        catch (Exception ex)
        {
            await TrySendHandshakeDisconnectAsync(transport, phase, ex).ConfigureAwait(false);
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/08 §六〕握手与认证失败时先告诉对端原因（协议错误、协商不上、主机密钥验不过、认证方法用尽）。
    /// 曾经一声不吭就断，服务端日志里只有「Connection closed」。版本交换完成之前不发：那时对端还不一定说 SSH。
    /// </summary>
    private static async ValueTask TrySendHandshakeDisconnectAsync(SshPacketTransport? transport, SshPhase phase, Exception failure)
    {
        if (transport is not null
            && phase is SshPhase.KeyExchange or SshPhase.Authenticating
            && ConnectionFaults.DisconnectReasonFor(failure) is { } reason)
        {
            await TrySendDisconnectAsync(transport, reason, failure).ConfigureAwait(false);
        }
    }

    /// <summary>认证成功之后，挂上「延迟启用」的那种压缩器（<c>zlib@openssh.com</c>）。</summary>
    /// <remarks>
    /// <para>
    /// 两个方向<b>各自独立协商</b> —— 一边压一边不压是合法的，
    /// 而且在「上传大量数据、下载很少」这类场景里是合理的。
    /// </para>
    /// </remarks>
    private static void ActivateDelayedCompression(
        SshPacketTransport transport, SshNegotiatedAlgorithms algorithms)
    {
        if (SshCompressorFactory.IsDelayed(algorithms.CompressionClientToServer))
        {
            transport.SetSendCompressor(
                SshCompressorFactory.Create(algorithms.CompressionClientToServer));
        }

        if (SshCompressorFactory.IsDelayed(algorithms.CompressionServerToClient))
        {
            transport.SetReceiveCompressor(
                SshCompressorFactory.Create(algorithms.CompressionServerToClient));
        }
    }

    /// <summary>建连期间（还没有发送泵）直接在传输上尽力发一个 <c>DISCONNECT</c>，最多等两秒；发不出去不报。</summary>
    private static async ValueTask TrySendDisconnectAsync(SshPacketTransport transport, SshDisconnectReason reason, Exception? failure = null)
    {
        try
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteMessageNumber(SshMessageNumber.Disconnect);
            writer.WriteUInt32((uint)reason);
            writer.WriteUtf8String(ConnectionFaults.DisconnectDescription(reason, failure));
            writer.WriteUtf8String("");

            transport.WritePacket(buffer.WrittenSpan);
            using CancellationTokenSource flush = new(TimeSpan.FromSeconds(2));
            await transport.FlushAsync(flush.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 已经在断开的路上了。
        }
    }

    private static async ValueTask DisposeQuietlyAsync(SshPacketTransport? transport, Stream? stream)
    {
        // 失败路径上的清理**不抛** —— 否则真正的失败原因会被一个次要异常盖住。
        if (transport is not null)
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
            return;
        }

        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }
}
