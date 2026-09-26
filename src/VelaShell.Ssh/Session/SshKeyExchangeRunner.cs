// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7    密钥交换的报文时序
//   RFC 4253 §7.3  SSH_MSG_NEWKEYS;两个方向各自独立切换
//   RFC 4253 §8    交换哈希与签名验证
//   RFC 5656 §4    ECDH 的 30/31 报文
//   RFC 8308 §2.2  ext-info-c 只出现在首次 KEXINIT
//   OpenSSH PROTOCOL 的 kex-strict-*-v00@openssh.com —— Terrapin(CVE-2023-48795)缓解
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §1、§5、§6

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>一次密钥交换的结果。</summary>
/// <param name="Algorithms">协商出的算法。</param>
/// <param name="ExchangeHash">本次交换的 <c>H</c>。</param>
/// <param name="SessionId">会话标识（首次交换的 <c>H</c>，此后不变）。</param>
/// <param name="HostKey">服务端出示并已通过验证的主机公钥。</param>
/// <param name="StrictKeyExchange">本次连接是否启用了严格 KEX。</param>
internal sealed record SshKeyExchangeResult(
    SshNegotiatedAlgorithms Algorithms,
    byte[] ExchangeHash,
    byte[] SessionId,
    SshPublicKey HostKey,
    bool StrictKeyExchange);

/// <summary>执行一次完整的密钥交换（客户端侧）。</summary>
/// <remarks>
/// <para>
/// 时序（velashell-docs/zh/ssh/spec/03 §1）：KEXINIT 双向同时发 → 协商 → 30/31 → 验签 →
/// 主机密钥策略裁决 → NEWKEYS 双向。
/// </para>
/// <para>
/// <b>两个并发事实必须记住</b>：KEXINIT 不是请求-应答，谁先到都合法；
/// NEWKEYS 的两个方向互不等待，发出之后我们发的下一个报文就用新密钥，
/// 而收的方向要等对端的 NEWKEYS 到达。
/// </para>
/// </remarks>
internal sealed class SshKeyExchangeRunner
{
    private const int MaxFieldBytes = 256 * 1024;

    /// <summary>默认接受的最小 RSA 模数位数（〔决策，velashell-docs/zh/ssh/spec/03 §5.3〕）。</summary>
    internal const int DefaultMinimumRsaKeyBits = 2048;

    // 30–49 是各密钥交换方法自己的报文编号（RFC 4250 §4.1.2），所以不进 SshMessageNumber。
    // 本库实现的方法（ECDH、Curve25519、DH 固定群、混合 KEM）都只用前两个：客户端的公开值与服务端的应答。
    private const byte KexMethodInit = 30;
    private const byte KexMethodReply = 31;

    private readonly ISshKexTransport _transport;
    private readonly SshAlgorithmSet _algorithms;
    private readonly IHostKeyPolicy _hostKeyPolicy;
    private readonly int _minimumRsaKeyBits;

    /// <summary>创建一个密钥交换执行器。</summary>
    /// <param name="transport">传输。</param>
    /// <param name="algorithms">本端的算法清单。</param>
    /// <param name="hostKeyPolicy">主机密钥策略。</param>
    /// <param name="minimumRsaKeyBits">
    /// 接受的最小 RSA 模数位数。〔决策，velashell-docs/zh/ssh/spec/03 §5.3〕默认 2048。
    /// </param>
    public SshKeyExchangeRunner(
        SshPacketTransport transport,
        SshAlgorithmSet algorithms,
        IHostKeyPolicy hostKeyPolicy,
        int minimumRsaKeyBits = DefaultMinimumRsaKeyBits)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = new DirectKexTransport(transport);
        _algorithms = algorithms ?? throw new ArgumentNullException(nameof(algorithms));
        _hostKeyPolicy = hostKeyPolicy ?? throw new ArgumentNullException(nameof(hostKeyPolicy));
        _minimumRsaKeyBits = minimumRsaKeyBits;
    }

    /// <summary>重协商用的构造：借道会话的收发路径，而不是直接动传输。</summary>
    /// <remarks>
    /// 会话跑起来之后，读归接收循环、写归发送锁。见 <see cref="ISshKexTransport"/>。
    /// </remarks>
    internal SshKeyExchangeRunner(
        ISshKexTransport transport,
        SshAlgorithmSet algorithms,
        IHostKeyPolicy hostKeyPolicy,
        int minimumRsaKeyBits = DefaultMinimumRsaKeyBits)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _algorithms = algorithms ?? throw new ArgumentNullException(nameof(algorithms));
        _hostKeyPolicy = hostKeyPolicy ?? throw new ArgumentNullException(nameof(hostKeyPolicy));
        _minimumRsaKeyBits = minimumRsaKeyBits;
    }

    /// <summary>
    /// 主机密钥裁决的超时。
    /// </summary>
    /// <remarks>
    /// <b>独立于连接超时</b>（velashell-docs/zh/ssh/spec/03 §5.3）。默认 <see cref="Timeout.InfiniteTimeSpan"/>：
    /// 交互式客户端在这里要弹窗问用户，而弹窗摆着的时间不该被一个为网络往返设计的超时打断。
    /// </remarks>
    public TimeSpan HostKeyDecisionTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>建连的计时器；裁决期间停表（见 <see cref="SshConnectDeadline"/>）。</summary>
    internal SshConnectDeadline? ConnectDeadline { get; init; }

    /// <summary>
    /// 裁决与持久化用的取消令牌 —— <b>只含调用方的取消，不含连接计时器</b>。
    /// <see langword="null"/> 时用 <see cref="RunAsync"/> 收到的令牌（重协商走这条）。
    /// </summary>
    /// <remarks>
    /// 持久化尤其不能用连接计时器的令牌：用户点了「永久信任」，
    /// 那一次写 known_hosts 不该因为弹窗摆得久而拿到一个已取消的令牌。
    /// </remarks>
    internal CancellationToken? DecisionCancellationToken { get; init; }

    /// <summary>
    /// 重协商时：首次交换验明、并经策略裁决过的主机密钥。设了就<b>钉住它</b>，不再走主机密钥策略。
    /// </summary>
    /// <remarks>
    /// velashell-docs/zh/ssh/spec/03 §8.4：重协商照样验签，但 <c>K_S</c> 必须与首次相同，
    /// 不同就以 <c>HostKeyChanged</c> 断开 —— 连接中途换主机密钥没有任何正当场景。
    /// 曾经每次重协商都把整套策略再跑一遍：交互式策略会在会话中途弹窗（而那时接收循环正停着等它，
    /// 所有通道一起卡住），宽松的策略则会让一把换过的密钥悄悄通过。
    /// </remarks>
    internal SshPublicKey? PinnedHostKey { get; init; }

    /// <summary>重协商时：这条连接的<b>首次</b>交换有没有启用严格 KEX。首次交换不看它。</summary>
    /// <remarks>严格 KEX 是整条连接的属性，不由重协商时的 KEXINIT 决定 —— 见 <see cref="RunAsync"/> 里的说明。</remarks>
    internal bool InitialStrictKeyExchange { get; init; }

    /// <summary>执行一次密钥交换。</summary>
    /// <param name="versions">版本交换的结果（交换哈希的前两个输入）。</param>
    /// <param name="host">被连的逻辑主机名（交给主机密钥策略）。</param>
    /// <param name="port">端口。</param>
    /// <param name="sessionId">
    /// 已有的会话标识；首次交换传 <see langword="null"/>（此时 <c>H</c> 即 <c>session_id</c>）。
    /// </param>
    /// <param name="peerKexInit">
    /// 对端的 <c>KEXINIT</c> 载荷（重协商时对端先发，接收循环已经读掉了）。
    /// 传 <see langword="null"/> 表示还没收到，由这里去读。
    /// </param>
    /// <param name="ourKexInitAlreadySent">
    /// 我们的 <c>KEXINIT</c> 已经发出去了（我们主动发起重协商时）。
    /// <b>非 <see langword="null"/> 时这里绝不能再发一个</b> ——
    /// 重复发 <c>KEXINIT</c> 是协议违规。
    /// </param>
    /// <param name="resetCompression">
    /// 是否在 <c>NEWKEYS</c> 处重建两个方向的压缩上下文。
    /// <b>重协商传 <see langword="true"/>，首次交换传 <see langword="false"/></b> ——
    /// 首次交换时压缩要等认证成功之后才启用。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<SshKeyExchangeResult> RunAsync(
        SshVersionExchangeResult versions,
        string host,
        int port,
        byte[]? sessionId = null,
        byte[]? peerKexInit = null,
        byte[]? ourKexInitAlreadySent = null,
        bool resetCompression = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        bool isInitial = sessionId is null;

        // ① 双向 KEXINIT。我们先发，不等对端 —— 省一个 RTT。
        //
        // 〔RFC 4253 §7.1〕KEXINIT 不是请求-应答，两边同时发都合法。
        // 所以**对端先发起时它的 KEXINIT 已经被接收循环读掉了** ——
        // 那份载荷会由 peerKexInit 带进来，这里就不能再去读一个。
        // 再读一个的症状是挂死：对端不会为同一次重协商发第二个 KEXINIT。
        byte[] clientKexInitPayload;
        if (ourKexInitAlreadySent is not null)
        {
            clientKexInitPayload = ourKexInitAlreadySent;
        }
        else
        {
            ArrayBufferWriter<byte> ourKexInit = new();
            SshKexInitMessage.Encode(_algorithms, includeIndicators: isInitial, ourKexInit);
            clientKexInitPayload = ourKexInit.WrittenSpan.ToArray();

            await _transport.SendAsync(clientKexInitPayload, cancellationToken).ConfigureAwait(false);
        }

        byte[] serverKexInitPayload = peerKexInit ?? (await ReadKexPacketAsync(
                SshMessageNumber.KexInit, strictKex: false, cancellationToken).ConfigureAwait(false))
            .Payload.ToArray();
        var serverKexInit = SshKexInitMessage.Decode(serverKexInitPayload);

        // ② 协商。任一类没有交集就抛 SshNegotiationException（带双方名单）。
        SshNegotiatedAlgorithms negotiated =
            SshAlgorithmNegotiator.Negotiate(_algorithms, serverKexInit, versions.ServerVersion);
        SshCompressorFactory.EnsureSupported(negotiated.CompressionClientToServer);
        SshCompressorFactory.EnsureSupported(negotiated.CompressionServerToClient);

        // 严格 KEX 是**整条连接**的属性（OpenSSH PROTOCOL 的 kex-strict 一节）：标记只在首个 KEXINIT 里有效，
        // 之后的 KEXINIT 里出现与否一律不看；而「每次 NEWKEYS 之后序号归零」持续整条连接。
        //
        // ⚠️ 曾经每次都按对端这一次的 KEXINIT 重算。对端重协商时不再带标记（本端也不带）的话，
        //    这边就不再归零、对端照旧归零 —— 序号对不上，chacha20-poly1305（nonce 就是序号）
        //    与 HMAC 套件（MAC 覆盖序号）在重协商后的第一个报文上校验失败，长连接当场断掉。
        //    AES-GCM 的 nonce 不看序号，把这件事掩盖了 —— 有 AES-NI 的机器默认正是 GCM。
        if (!isInitial)
        {
            negotiated = negotiated with { StrictKeyExchange = InitialStrictKeyExchange };
        }

        // 规则 (a)「密钥交换期间出现 IGNORE / DEBUG / UNIMPLEMENTED 即断开」只管首次交换；
        // 重协商期间它们是合法的普通报文。
        bool strictReads = isInitial && negotiated.StrictKeyExchange;

        // ③ 交换公开值。
        using ISshKeyExchange kex = SshKeyExchangeFactory.Create(negotiated.KeyExchange);
        byte[] clientPublic = kex.CreateClientPublicValue();

        ArrayBufferWriter<byte> initMessage = new();
        SshDataWriter initWriter = new(initMessage);
        initWriter.WriteByte(KexMethodInit);
        WriteKexValue(ref initWriter, clientPublic, kex.PublicValueEncoding);
        await _transport.SendAsync(initMessage.WrittenMemory, cancellationToken).ConfigureAwait(false);

        // 对端若在它的 KEXINIT 里设了 first_kex_packet_follows，且猜错了，
        // 它会先发一个要被丢弃的报文（RFC 4253 §7.1）。
        if (serverKexInit.FirstKexPacketFollows && !GuessedCorrectly(serverKexInit, negotiated))
        {
            _ = await ReadAnyKexPacketAsync(strictReads, cancellationToken).ConfigureAwait(false);
        }

        SshInboundPacket reply = await ReadKexPacketAsync(
            (SshMessageNumber)KexMethodReply, strictReads, cancellationToken).ConfigureAwait(false);

        (byte[] hostKeyBlob, byte[] serverPublic, byte[] signature) = ParseReply(reply.Payload, kex);

        // ④ 算共享密钥与交换哈希。
        byte[] sharedSecret = kex.ComputeSharedSecret(serverPublic);
        byte[] exchangeHash;
        try
        {
            exchangeHash = SshExchangeHash.Compute(kex.HashAlgorithm, new SshExchangeHashInput
            {
                ClientVersion = versions.ClientVersionBytes,
                ServerVersion = versions.ServerVersionBytes,
                ClientKexInit = clientKexInitPayload,
                ServerKexInit = serverKexInitPayload,
                HostKeyBlob = hostKeyBlob,
                ClientPublicValue = clientPublic,
                ServerPublicValue = serverPublic,
                SharedSecret = sharedSecret,
                PublicValueEncoding = kex.PublicValueEncoding,
                SharedSecretEncoding = kex.SharedSecretEncoding,
            });

            // ⑤ 验主机密钥。**顺序本身是安全属性**（velashell-docs/zh/ssh/spec/03 §5.3）：
            //    先验签名，再问策略 —— 签名没过就问用户「要不要信任」是荒唐的，
            //    那把密钥根本没证明自己持有对应私钥。
            SshPublicKey hostKey = VerifyHostKey(
                hostKeyBlob, signature, exchangeHash, negotiated.HostKey, versions.ServerVersion);

            if (PinnedHostKey is { } pinned)
            {
                if (!pinned.Blob.Span.SequenceEqual(hostKey.Blob.Span))
                {
                    throw new SshConnectionClosedException(
                        SshFailureReason.HostKeyChanged, SshPhase.Rekeying,
                        $"{host}:{port} 在密钥重协商时换了主机密钥" +
                        $"（首次 {pinned.KeyType} {pinned.Sha256Fingerprint}，" +
                        $"这次 {hostKey.KeyType} {hostKey.Sha256Fingerprint}）。连接中途换主机密钥没有正当场景，已断开。");
                }
            }
            else
            {
                await ApplyHostKeyPolicyAsync(hostKey, negotiated, host, port, versions, cancellationToken)
                    .ConfigureAwait(false);
            }

            // ⑥ 双向 NEWKEYS。
            byte[] effectiveSessionId = sessionId ?? exchangeHash;
            await ExchangeNewKeysAsync(
                negotiated, kex, sharedSecret, exchangeHash, effectiveSessionId,
                resetCompression, strictReads, cancellationToken)
                .ConfigureAwait(false);

            return new SshKeyExchangeResult(
                negotiated, exchangeHash, effectiveSessionId, hostKey, negotiated.StrictKeyExchange);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <summary>对端的猜测是否与协商结果一致（RFC 4253 §7.1）。</summary>
    /// <remarks>两个列表的**第一项**都要等于协商结果才算猜对。</remarks>
    private static bool GuessedCorrectly(SshKexInitMessage peer, in SshNegotiatedAlgorithms negotiated) =>
        peer.KeyExchangeAlgorithms.Count > 0
        && peer.ServerHostKeyAlgorithms.Count > 0
        && string.Equals(peer.KeyExchangeAlgorithms[0], negotiated.KeyExchange, StringComparison.Ordinal)
        && string.Equals(peer.ServerHostKeyAlgorithms[0], negotiated.HostKey, StringComparison.Ordinal);

    private static (byte[] HostKey, byte[] ServerPublic, byte[] Signature) ParseReply(
        ReadOnlyMemory<byte> payload, ISshKeyExchange kex)
    {
        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadByte();   // 31
            byte[] hostKey = reader.ReadStringAsArray(MaxFieldBytes);
            byte[] serverPublic = kex.PublicValueEncoding == SshKexValueEncoding.Mpint
                ? reader.ReadMpint(MaxFieldBytes).ToArray()
                : reader.ReadStringAsArray(MaxFieldBytes);
            byte[] signature = reader.ReadStringAsArray(MaxFieldBytes);
            return (hostKey, serverPublic, signature);
        }
        catch (SshWireFormatException ex)
        {
            throw new SshProtocolException(SshPhase.KeyExchange, $"密钥交换应答格式非法：{ex.Message}", ex);
        }
    }

    private SshPublicKey VerifyHostKey(
        byte[] hostKeyBlob, byte[] signature, byte[] exchangeHash, string negotiatedAlgorithm, string peerVersion)
    {
        SshPublicKey hostKey;
        try
        {
            hostKey = SshPublicKey.Decode(hostKeyBlob);
        }
        catch (SshPublicKeyException ex)
        {
            throw new SshConnectException(
                SshFailureReason.HostKeyRejected, SshPhase.KeyExchange,
                $"服务端主机密钥无法解析：{ex.Message}", ex);
        }

        // 协商出的算法必须是这把密钥能用的。RSA 的三个签名算法名对应同一个密钥类型 ——
        // 直接拿算法名比对 blob 里的类型串会失败（RFC 8332 的那处不对称）。
        // 证书与否也要一致：SupportsSignatureAlgorithm 比的是去掉证书后缀之后的名字，
        // 不另外查的话，谈成证书算法却出示一把普通钥（或反过来）也能过（velashell-docs/zh/ssh/spec/03 §5.5）。
        bool certificateNegotiated = negotiatedAlgorithm.EndsWith(SshAlgorithmNames.CertificateSuffix, StringComparison.Ordinal);
        if (!hostKey.SupportsSignatureAlgorithm(negotiatedAlgorithm) || hostKey.IsCertificate != certificateNegotiated)
        {
            throw new SshConnectException(
                SshFailureReason.HostKeyRejected, SshPhase.KeyExchange,
                $"服务端出示的是 {hostKey.KeyType} 密钥，与协商出的 {negotiatedAlgorithm} 不匹配。");
        }

        // RSA 长度检查放在**验签之前**：不给弱密钥任何计算资源。
        // 看的是 PlainKeyType：RSA 证书的类型串是 ssh-rsa-cert-v01@openssh.com，按 KeyType 比会让检查落空。
        if (hostKey.PlainKeyType == SshAlgorithmNames.SshRsa && hostKey.KeyBits < _minimumRsaKeyBits)
        {
            throw new SshConnectException(
                SshFailureReason.HostKeyRejected, SshPhase.KeyExchange,
                $"服务端的 RSA 主机密钥只有 {hostKey.KeyBits} 位，低于要求的 {_minimumRsaKeyBits} 位。");
        }

        if (!hostKey.VerifySignature(signature, exchangeHash, negotiatedAlgorithm))
        {
            throw new SshConnectException(
                SshFailureReason.HostKeyRejected, SshPhase.KeyExchange,
                $"服务端对交换哈希的签名验证失败（{negotiatedAlgorithm}，对端 {PeerText.Sanitize(peerVersion, 128)}）。" +
                "这意味着对端没有它所声称的那把主机私钥 —— 可能有中间人。");
        }

        return hostKey;
    }

    private async ValueTask ApplyHostKeyPolicyAsync(
        SshPublicKey hostKey,
        SshNegotiatedAlgorithms negotiated,
        string host,
        int port,
        SshVersionExchangeResult versions,
        CancellationToken cancellationToken)
    {
        SshHostKeyContext context = new()
        {
            Host = host,
            Port = port,
            Key = hostKey,
            NegotiatedAlgorithm = negotiated.HostKey,
            PeerVersion = versions.ServerVersion,
        };

        // 裁决**独立计时**：它可能要弹窗问用户，而那不该被连接超时打断。
        // 所以连接计时器在这段时间里停表，裁决只受调用方的取消与它自己的超时约束。
        CancellationToken outer = DecisionCancellationToken ?? cancellationToken;
        ConnectDeadline?.Pause();
        try
        {
            using var decisionCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            if (HostKeyDecisionTimeout != Timeout.InfiniteTimeSpan)
            {
                decisionCts.CancelAfter(HostKeyDecisionTimeout);
            }

            SshHostKeyVerdict verdict;
            try
            {
                verdict = await _hostKeyPolicy.EvaluateAsync(context, decisionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!outer.IsCancellationRequested)
            {
                throw new SshConnectException(
                    SshFailureReason.Timeout, SshPhase.KeyExchange,
                    $"主机密钥裁决超时（{HostKeyDecisionTimeout}）。");
            }

            switch (verdict.Decision)
            {
                case SshHostKeyDecision.Accept:
                    break;

                case SshHostKeyDecision.AcceptAndPersist:
                    // 用调用方的令牌，不用连接计时器的 —— 见 DecisionCancellationToken 的说明。
                    await _hostKeyPolicy.PersistAsync(context, outer).ConfigureAwait(false);
                    break;

                default:
                    // 只认两种放行，其余（含 default 裁决）一律拒绝。
                    // 把策略给出的人话原样抛出去 —— 回调只能返回一个裁决，原因得由它自己带上，
                    // 否则用户拿到的只有一句「不受信任的对端」。
                    throw new SshConnectException(
                        verdict.Reason == SshFailureReason.HostKeyChanged
                            ? SshFailureReason.HostKeyChanged
                            : SshFailureReason.HostKeyRejected,
                        SshPhase.KeyExchange,
                        verdict.Message ?? $"{context.Target} 的主机密钥被策略拒绝。");
            }
        }
        finally
        {
            ConnectDeadline?.Resume();
        }
    }

    private async ValueTask ExchangeNewKeysAsync(
        SshNegotiatedAlgorithms negotiated,
        ISshKeyExchange kex,
        byte[] sharedSecret,
        byte[] exchangeHash,
        byte[] sessionId,
        bool resetCompression,
        bool strictReads,
        CancellationToken cancellationToken)
    {
        (ISshCipherSuite send, ISshCipherSuite receive) = SshSessionKeys.Derive(
            negotiated, kex.HashAlgorithm, sharedSecret, kex.SharedSecretEncoding, exchangeHash, sessionId);

        // 〔velashell-docs/zh/ssh/spec/01 §六〕**每次密钥重协商后压缩上下文必须重置。**
        // 不重置的症状是「重协商之后对端解压失败」—— 而那时早已看不出是压缩的问题。
        //
        // 首次交换时不装：唯一支持的 zlib@openssh.com 要等认证成功之后才启用，由连接工厂在那时装上。
        ISshCompressor? sendCompressor = resetCompression
            ? SshCompressorFactory.Create(negotiated.CompressionClientToServer)
            : null;
        ISshCompressor? receiveCompressor = resetCompression
            ? SshCompressorFactory.Create(negotiated.CompressionServerToClient)
            : null;

        bool installed = false;
        try
        {
            // 发出 NEWKEYS 之后，**我们发的下一个报文**就用新的发送密钥 ——
            // 所以这两步必须在同一个临界区里，见 ISshKexTransport 上的说明。
            await _transport.SendNewKeysAndSwitchSendAsync(
                send, sendCompressor, negotiated.StrictKeyExchange, cancellationToken)
                .ConfigureAwait(false);

            // 收的方向要等对端的 NEWKEYS 到达 —— 两个方向互不等待（RFC 4253 §7.3）。
            _ = await ReadKexPacketAsync(SshMessageNumber.NewKeys, strictReads, cancellationToken)
                .ConfigureAwait(false);
            _transport.SwitchReceive(receive, receiveCompressor, negotiated.StrictKeyExchange);
            installed = true;
        }
        finally
        {
            if (!installed)
            {
                send.Dispose();
                receive.Dispose();
                sendCompressor?.Dispose();
                receiveCompressor?.Dispose();
            }
        }
    }


    /// <summary>读下一个报文并断言它的消息编号。</summary>
    private async ValueTask<SshInboundPacket> ReadKexPacketAsync(
        SshMessageNumber expected, bool strictKex, CancellationToken cancellationToken)
    {
        SshInboundPacket packet = await ReadAnyKexPacketAsync(strictKex, cancellationToken).ConfigureAwait(false);
        if (packet.MessageNumber != expected)
        {
            throw new SshProtocolException(
                SshPhase.KeyExchange,
                $"密钥交换期间期望 {expected}({(byte)expected})，收到 {packet.MessageNumber}({(byte)packet.MessageNumber})。");
        }
        return packet;
    }

    /// <summary>读下一个报文，处理任何阶段都可能到达的那几种。</summary>
    private async ValueTask<SshInboundPacket> ReadAnyKexPacketAsync(
        bool strictKex, CancellationToken cancellationToken)
    {
        while (true)
        {
            SshInboundPacket packet;
            try
            {
                packet = await _transport.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshFrameFormatException ex) when (ex.PeerClosedMidPacket)
            {
                // 对端在一个报文中途断开：那是连接断了，不是报文写错了（与会话期间的归类一致，
                // 见 SshConnection.NormalizeFault）—— 报成协议错误会让调用方以为不值得重连。
                throw new SshConnectionClosedException(
                    SshFailureReason.ClosedByPeer, SshPhase.KeyExchange, "对端在密钥交换期间关闭了连接（一个报文只收到一半）。", ex);
            }
            catch (SshFrameFormatException ex)
            {
                throw new SshProtocolException(SshPhase.KeyExchange, ex.Message, ex);
            }

            if (packet.IsEndOfStream)
            {
                throw new SshConnectionClosedException(
                    SshFailureReason.ClosedByPeer, SshPhase.KeyExchange,
                    "对端在密钥交换期间关闭了连接。");
            }

            switch (packet.MessageNumber)
            {
                case SshMessageNumber.Disconnect:
                    throw BuildDisconnectException(packet.Payload);

                case SshMessageNumber.Ignore:
                case SshMessageNumber.Debug:
                case SshMessageNumber.Unimplemented:
                    // 严格 KEX 下这三种在密钥交换期间一律是攻击信号(Terrapin 的入口)。
                    // 不启用严格 KEX 时按 RFC 忽略它们。
                    if (strictKex)
                    {
                        throw new SshProtocolException(
                            SshPhase.KeyExchange,
                            $"启用严格 KEX 时，密钥交换期间不允许出现 {packet.MessageNumber} —— " +
                            "这正是 Terrapin 攻击（CVE-2023-48795）利用的报文。");
                    }
                    continue;

                default:
                    return packet;
            }
        }
    }

    private static SshConnectionClosedException BuildDisconnectException(ReadOnlyMemory<byte> payload)
    {
        SshDisconnectReason? reason = null;
        string? description = null;

        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.Disconnect);
            reason = (SshDisconnectReason)reader.ReadUInt32();
            // 对端给的描述常常是唯一有用的信息，但它是**不可信文本** —— 原样保留，
            // 展示时由使用者按不可信内容处理。
            description = reader.ReadUtf8String(64 * 1024);
        }
        catch (SshWireFormatException)
        {
            // DISCONNECT 本身格式错也不影响结论：连接要断了。
        }

        return new SshConnectionClosedException(
            SshFailureReason.Disconnected, SshPhase.KeyExchange,
            $"服务端主动断开：{reason?.ToString() ?? "未知原因"}" +
            (string.IsNullOrEmpty(description) ? "" : $" —— {PeerText.Sanitize(description)}"))
        {
            DisconnectReason = reason,
            PeerDescription = description,
        };
    }

    private static void WriteKexValue(ref SshDataWriter writer, ReadOnlySpan<byte> value, SshKexValueEncoding encoding)
    {
        if (encoding == SshKexValueEncoding.Mpint)
        {
            writer.WriteMpint(value);
        }
        else
        {
            writer.WriteString(value);
        }
    }
}
