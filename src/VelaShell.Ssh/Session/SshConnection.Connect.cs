// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §4  版本交换
//   RFC 4252     认证
//   行为规格:    velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
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

        Stream? stream = null;
        SshPacketTransport? transport = null;

        // 算法清单由多个 init 属性共同决定合不合法，没法在构造时拦 —— 在这里当场报，
        // 而不是等连上之后才在协商里出问题。
        options.Algorithms.Validate();

        // 超时的时候要说清是哪一步超时了 —— 「连接超时」这四个字
        // 对「DNS 慢」「端口被防火墙丢包」「服务端卡在密钥交换」「用户没输动态码」
        // 是同一句话，而这四件事的下一步完全不同。
        SshPhase phase = SshPhase.Dialing;

        try
        {
            // ① 拨号 + 版本交换 + 密钥交换，共用一把连接计时器。
            //    主机密钥裁决期间它停表（见 SshConnectDeadline）；经跳板时挂在外层计时器上，
            //    这里停表时外层也跟着停。
            using SshConnectDeadline connect = new(options.ConnectTimeout, cancellationToken, options.OuterDeadline);

            stream = await options.Dialer
                .DialAsync(SshDialTarget.Direct(options.Host, options.Port) with { Deadline = connect }, connect.Token)
                .ConfigureAwait(false);

            transport = new SshPacketTransport(stream);
            phase = SshPhase.VersionExchange;

            SshVersionExchangeResult versions = await SshVersionExchange
                .ExchangeAsync(transport, cancellationToken: connect.Token)
                .ConfigureAwait(false);

            phase = SshPhase.KeyExchange;

            // 已经记着这台主机哪些类型的主机密钥，就把那些类型排到前面 —— 正常的服务端因此谈成
            // 已知的那一种（见 IHostKeyTypePreference）。重协商用的是同一份清单。
            SshAlgorithmSet algorithms = options.Algorithms;
            if (options.HostKeyPolicy is IHostKeyTypePreference preference)
            {
                IReadOnlyList<string> knownTypes = await preference
                    .GetKnownKeyTypesAsync(options.Host, options.Port, connect.Token)
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

            // ③ 认证又是一把（默认两分钟）。
            phase = SshPhase.Authenticating;
            using var auth = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);

            if (options.AuthenticationTimeout != Timeout.InfiniteTimeSpan)
            {
                auth.CancelAfter(options.AuthenticationTimeout);
            }

            SshAuthenticator authenticator = new(transport, options.UserName, kex.SessionId)
            {
                BannerHandler = options.BannerHandler,
                AllowSha1RsaSignatures = options.AllowSha1RsaSignatures,
            };

            List<SshCredential> credentials = [.. options.Credentials];

            // 经跳板时，这条连接的认证跑在外层连接的拨号计时之内 —— 而认证是在等人
            // （输口令、看手机上的动态码）。那段时间停外层的表，认证用它自己的这把计时器；
            // 不停的话，用户在跳板上输动态码花了二十秒，外层十五秒的连接超时早就到了。
            options.OuterDeadline?.Pause();
            try
            {
                await authenticator.AuthenticateAsync(credentials, auth.Token).ConfigureAwait(false);
            }
            finally
            {
                options.OuterDeadline?.Resume();
            }

            // ④ 压缩要在**认证成功之后**才挂上去（zlib@openssh.com 的语义）。
            //
            // 推迟不是为了省事：认证之前的报文里有密码与公钥，而压缩会让
            // 密文长度泄漏明文的可压缩性 —— 对着一个长度可观测的口令做
            // 压缩旁路攻击（CRIME 那一类）是现实的。
            ActivateDelayedCompression(transport, kex.Algorithms);

            // ⑤ 认证过了，报文上限放宽到认证后的默认值。
            //
            // 握手期锁在 35000 是对的（RFC 4253 §6.1 的下限，也是未认证对端能让我们
            // 分配的最大单块）；认证之后还锁着，通道宣告的包上限一旦超过约 34 KB，
            // 服务端按我们宣告的大小发来的报文就会被当成协议错误。
            transport.MaxPacketLength = SshPacketFormat.DefaultMaxPacketLength;

            SshConnection connection = new(transport, kex, options.Limits)
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
                RekeyPolicy = options.Rekey,
                RekeyCheckInterval = options.RekeyCheckInterval,
                RekeyTimeout = options.RekeyTimeout,
                Description = $"{options.UserName}@{options.EndPoint}",
            };

            connection.Start();
            return connection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

            // 取消不是调用方要求的 → 是我们自己的计时器到了。
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
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException && phase != SshPhase.Dialing)
        {
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);

            // 拨通之后流上的读写出错，就是连接断了（对端重置、链路掉了）。按会话期间的同一套口径报
            // （见 SshConnection.NormalizeFault），不让原始的 IOException / SocketException 漏给调用方。
            // 拨号阶段不在这里管：各个拨号器自己把失败翻成了带原因的 SshConnectException。
            throw new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, phase,
                $"连 {options.EndPoint} 时连接断了（{phase}）：{PeerText.Sanitize(ex.Message, 256)}", ex);
        }
        catch (Exception)
        {
            await DisposeQuietlyAsync(transport, stream).ConfigureAwait(false);
            throw;
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
