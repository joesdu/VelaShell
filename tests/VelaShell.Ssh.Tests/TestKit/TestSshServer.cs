// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 会说 SSH 的测试服务端桩。它跑在内存传输上，让整条握手不需要网络、
// 不需要容器就能在毫秒级跑完（velashell-docs/zh/ssh/design/architecture.md §10.2）。
//
// ⚠️ **只为测试存在，绝不发布。**
//    它没有任何访问控制、没有速率限制、认证一律放行，
//    而且刻意保留了一些「可配置地做错事」的开关来构造攻击场景。
//    它放在 tests/ 而不是 src/ 正是为了这一点。

using System.Buffers;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>测试服务端的行为开关。</summary>
public sealed record TestSshServerOptions
{
    /// <summary>服务端的版本标识串。</summary>
    public string Identification { get; init; } = "SSH-2.0-VelaShellTestServer_1.0";

    /// <summary>标识串之前发的前导行。</summary>
    public IReadOnlyList<string> PreAuthBanner { get; init; } = [];

    /// <summary>服务端宣告的算法清单；<see langword="null"/> 时用与客户端相同的默认集。</summary>
    public SshAlgorithmSet? Algorithms { get; init; }

    /// <summary>主机密钥类型。</summary>
    public string HostKeyType { get; init; } = SshAlgorithmNames.SshEd25519;

    /// <summary>是否宣告支持严格 KEX。</summary>
    public bool AdvertiseStrictKex { get; init; } = true;

    /// <summary>
    /// 在密钥交换期间插入一个 <c>SSH_MSG_IGNORE</c>。
    /// </summary>
    /// <remarks>
    /// 用来构造 Terrapin 那类攻击的前置条件：启用严格 KEX 时客户端**必须**因此断开。
    /// </remarks>
    public bool InjectIgnoreDuringKex { get; init; }

    /// <summary>把签名故意弄坏，用来验证客户端确实在验签。</summary>
    public bool CorruptSignature { get; init; }
}

/// <summary>一次握手之后服务端这一侧的结果。</summary>
/// <param name="Negotiated">服务端算出的协商结果。</param>
/// <param name="ExchangeHash">交换哈希。</param>
/// <param name="HostKeyBlob">服务端出示的主机公钥 blob。</param>
public sealed record TestSshServerHandshake(
    SshNegotiatedAlgorithms Negotiated,
    byte[] ExchangeHash,
    byte[] HostKeyBlob);

/// <summary>会说 SSH 的测试服务端。</summary>
public sealed class TestSshServer : IAsyncDisposable
{
    private const int MaxField = 256 * 1024;

    private readonly TestSshServerOptions _options;
    private readonly TestHostKey _hostKey;

    /// <summary>客户端的版本串 —— 重协商时算交换哈希还要用它。</summary>
    private string? _clientVersion;

    /// <summary>首次交换定下的 session_id。重协商时 <c>H</c> 变而它不变。</summary>
    private byte[]? _sessionId;

    /// <summary>在给定的流上建立一个测试服务端。</summary>
    public TestSshServer(Stream stream, TestSshServerOptions? options = null)
    {
        _options = options ?? new TestSshServerOptions();
        Transport = new SshPacketTransport(stream);
        _hostKey = TestHostKey.Create(_options.HostKeyType);
    }

    /// <summary>底层传输 —— 握手之后用它继续收发（认证、通道…）。</summary>
    public SshPacketTransport Transport { get; }

    /// <summary>执行版本交换与密钥交换的服务端一侧。</summary>
    public async Task<TestSshServerHandshake> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        // ① 版本交换。前导行（若有）先发。
        foreach (string line in _options.PreAuthBanner)
        {
            await Transport.WriteLineAsync(line, cancellationToken);
        }
        await Transport.WriteLineAsync(_options.Identification, cancellationToken);

        string? clientVersion = null;
        while (clientVersion is null)
        {
            string? line = await Transport.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException("客户端未发出版本标识串。");
            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                clientVersion = line;
            }
        }

        _clientVersion = clientVersion;
        TestSshServerHandshake handshake = await RunKeyExchangeAsync(
            SendTransportAsync, ReadTransportAsync,
            sessionId: null, ourKexInitAlreadySent: null, peerKexInitAlreadyRead: null,
            cancellationToken);
        _sessionId = handshake.ExchangeHash;
        return handshake;
    }

    /// <summary>服务端主动发起一次密钥重协商。</summary>
    /// <param name="send">
    /// 发送。<b>由拥有发送锁的那一方提供</b> —— 会话跑起来之后这条传输上
    /// 还有剧本泵在写，这里不能自己动传输，否则两个写者会把报文交错写坏。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 这是「OpenSSH 到了 RekeyLimit 自己发 KEXINIT」的那一幕 ——
    /// 客户端要能接住，接不住的表现就是会话在某个时刻忽然断掉。
    /// </remarks>
    /// <summary>发起重协商的第一步：把服务端的 KEXINIT 发出去。</summary>
    /// <remarks>
    /// ⚠️ <b>发起与收尾必须拆成两步。</b>
    /// 收包循环平时都停在「等下一个报文」上；如果只设一个标志等循环回到顶上再发，
    /// 那循环就永远等不到东西 —— 客户端在等我们的 KEXINIT，我们在等客户端说话，
    /// <b>两边一起停住</b>。（这个死锁我是先写出来才发现的。）
    ///
    /// 所以：这一步在调用方的线程上立刻发出 KEXINIT，客户端收到后会回它自己的 KEXINIT，
    /// 那个报文把收包循环唤醒，循环再调
    /// <see cref="CompleteRekeyAsync"/> 把剩下的做完。
    /// </remarks>
    public async Task<byte[]> BeginRekeyAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
        {
            throw new InvalidOperationException("还没做过首次密钥交换，谈不上重协商。");
        }

        SshAlgorithmSet algorithms = _options.Algorithms ?? SshAlgorithmSet.Default;
        byte[] serverKexInit = BuildServerKexInit(algorithms, includeIndicators: false);
        await send(serverKexInit, cancellationToken);
        return serverKexInit;
    }

    /// <summary>收到客户端的 KEXINIT 之后，把重协商做完。</summary>
    /// <param name="ourKexInit"><see cref="BeginRekeyAsync"/> 发出去的那份。</param>
    /// <param name="clientKexInit">刚收到的客户端 KEXINIT 载荷。</param>
    /// <param name="send">发送（由拥有发送锁的一方提供）。</param>
    /// <param name="read">读下一个传输层报文（由拥有收包循环的一方提供）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TestSshServerHandshake> CompleteRekeyAsync(
        byte[] ourKexInit,
        byte[] clientKexInit,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<CancellationToken, ValueTask<SshInboundPacket>> read,
        CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
        {
            throw new InvalidOperationException("还没做过首次密钥交换，谈不上重协商。");
        }

        return await RunKeyExchangeAsync(
            send, read, _sessionId, ourKexInit, clientKexInit, cancellationToken);
    }

    /// <summary>客户端发起了重协商 —— 我们应答。</summary>
    /// <param name="clientKexInit">刚收到的客户端 KEXINIT 载荷。</param>
    /// <param name="send">发送（由拥有发送锁的一方提供）。</param>
    /// <param name="read">读下一个传输层报文（由拥有收包循环的一方提供）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 与 <see cref="CompleteRekeyAsync"/> 的差别只有一处：那边我们先发过
    /// KEXINIT，这边还没发 —— 所以这里要发。
    /// </remarks>
    public async Task<TestSshServerHandshake> RespondToRekeyAsync(
        byte[] clientKexInit,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<CancellationToken, ValueTask<SshInboundPacket>> read,
        CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
        {
            throw new InvalidOperationException("还没做过首次密钥交换，谈不上重协商。");
        }

        return await RunKeyExchangeAsync(
            send, read, _sessionId,
            ourKexInitAlreadySent: null, peerKexInitAlreadyRead: clientKexInit,
            cancellationToken);
    }

    /// <summary>跑一次密钥交换（服务端侧）。首次与重协商共用这一段。</summary>
    /// <param name="send">发送一个报文。</param>
    /// <param name="read">读下一个传输层报文。</param>
    /// <param name="sessionId">
    /// 已有的 session_id；首次交换传 <see langword="null"/>（此时 <c>H</c> 即 session_id）。
    /// </param>
    /// <param name="ourKexInitAlreadySent">
    /// 我们的 KEXINIT 已经发出去了（重协商）；<see langword="null"/> 表示这里去发。
    /// </param>
    /// <param name="peerKexInitAlreadyRead">
    /// 对端的 KEXINIT 已经被读掉了（重协商）；<see langword="null"/> 表示这里去读。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<TestSshServerHandshake> RunKeyExchangeAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<CancellationToken, ValueTask<SshInboundPacket>> read,
        byte[]? sessionId,
        byte[]? ourKexInitAlreadySent,
        byte[]? peerKexInitAlreadyRead,
        CancellationToken cancellationToken)
    {
        string clientVersion = _clientVersion
            ?? throw new InvalidOperationException("还没拿到客户端版本串。");
        bool isInitial = sessionId is null;

        // ② 双向 KEXINIT。重协商时这两份都已经在手上了（见 BeginRekeyAsync）。
        SshAlgorithmSet algorithms = _options.Algorithms ?? SshAlgorithmSet.Default;
        byte[] serverKexInit = ourKexInitAlreadySent ?? BuildServerKexInit(algorithms, isInitial);
        if (ourKexInitAlreadySent is null)
        {
            await send(serverKexInit, cancellationToken);
        }
        // 重协商时客户端的 KEXINIT 已经被收包循环读掉了 —— 不能再等一个。
        byte[] clientKexInitPayload = peerKexInitAlreadyRead
            ?? (await ExpectAsync(read, SshMessageNumber.KexInit, cancellationToken)).Payload.ToArray();
        var clientKexInit = SshKexInitMessage.Decode(clientKexInitPayload);

        // 服务端视角的协商：规则相同，但**以客户端的顺序为准**（RFC 4253 §7.1），
        // 所以这里要拿客户端的列表当「我们的偏好」。
        //
        // ⚠️ 主机密钥那一类必须用**我们实际宣告的那一份**（_hostKey.SignatureAlgorithms），
        //    不能用 algorithms.HostKey —— 后者是完整的默认清单，而我们手上只有一把密钥。
        //    用错的后果是「宣告 RSA，却按 Ed25519 签名」，客户端只会报一句签名验证失败。
        SshNegotiatedAlgorithms negotiated = NegotiateAsServer(clientKexInit, algorithms, _hostKey.SignatureAlgorithms);

        if (_options.InjectIgnoreDuringKex)
        {
            Transport.WritePacket([(byte)SshMessageNumber.Ignore, 1, 2, 3]);
            await Transport.FlushAsync(cancellationToken);
        }

        // ③ 收客户端公开值，算共享密钥。
        SshInboundPacket initPacket = await ExpectAsync(read, (SshMessageNumber)30, cancellationToken);
        byte[] clientPublic = ReadKexValue(initPacket.Payload, negotiated.KeyExchange);

        TestKexResponse response = TestKexResponder.Respond(negotiated.KeyExchange, clientPublic);

        // ④ 算交换哈希并签名。
        using VelaShell.Ssh.Crypto.Kex.ISshKeyExchange shape =
            Ssh.Crypto.Kex.SshKeyExchangeFactory.Create(negotiated.KeyExchange);

        byte[] hostKeyBlob = _hostKey.PublicKeyBlob;
        byte[] exchangeHash = SshExchangeHash.Compute(shape.HashAlgorithm, new SshExchangeHashInput
        {
            ClientVersion = System.Text.Encoding.ASCII.GetBytes(clientVersion),
            ServerVersion = System.Text.Encoding.ASCII.GetBytes(_options.Identification),
            ClientKexInit = clientKexInitPayload,
            ServerKexInit = serverKexInit,
            HostKeyBlob = hostKeyBlob,
            ClientPublicValue = clientPublic,
            ServerPublicValue = response.ServerPublicValue,
            SharedSecret = response.SharedSecret,
            PublicValueEncoding = shape.PublicValueEncoding,
            SharedSecretEncoding = shape.SharedSecretEncoding,
        });

        byte[] signature = _hostKey.Sign(exchangeHash, negotiated.HostKey);
        if (_options.CorruptSignature)
        {
            signature[^1] ^= 0xFF;
        }

        ArrayBufferWriter<byte> reply = new();
        SshDataWriter replyWriter = new(reply);
        replyWriter.WriteByte(31);
        replyWriter.WriteString(hostKeyBlob);
        if (shape.PublicValueEncoding == Ssh.Crypto.Kex.SshKexValueEncoding.Mpint)
        {
            replyWriter.WriteMpint(response.ServerPublicValue);
        }
        else
        {
            replyWriter.WriteString(response.ServerPublicValue);
        }
        replyWriter.WriteString(signature);

        await send(reply.WrittenMemory, cancellationToken);

        // ⑤ 双向 NEWKEYS。服务端的收发方向与客户端相反。
        // 〔RFC 4253 §7.2〕重协商时 H 变而 session_id 不变 —— 密钥派生用的是 session_id。
        byte[] effectiveSessionId = sessionId ?? exchangeHash;
        (ISshCipherSuite clientToServer, ISshCipherSuite serverToClient) = SshSessionKeys.Derive(
            negotiated, shape.HashAlgorithm, response.SharedSecret, shape.SharedSecretEncoding,
            exchangeHash, effectiveSessionId);

        await send(new[] { (byte)SshMessageNumber.NewKeys }, cancellationToken);
        // 客户端派生出的「发送套件」是我们的「接收套件」，反之亦然。
        Transport.SetSendCipherSuite(serverToClient, negotiated.StrictKeyExchange);

        // 重协商时压缩上下文也要跟着换 —— 与客户端对称。
        // 少换一边的症状是对端解压失败（velashell-docs/zh/ssh/spec/01 §六）。
        // 首次 NEWKEYS 不装：zlib@openssh.com 在认证成功之后才装（TestSshServerHost）。
        if (!isInitial)
        {
            Transport.SetSendCompressor(
                SshCompressorFactory.Create(negotiated.CompressionServerToClient));
        }

        _ = await ExpectAsync(read, SshMessageNumber.NewKeys, cancellationToken);
        Transport.SetReceiveCipherSuite(clientToServer, negotiated.StrictKeyExchange);

        if (!isInitial)
        {
            Transport.SetReceiveCompressor(
                SshCompressorFactory.Create(negotiated.CompressionClientToServer));
        }

        return new TestSshServerHandshake(negotiated, exchangeHash, hostKeyBlob);
    }

    private byte[] BuildServerKexInit(SshAlgorithmSet algorithms, bool includeIndicators = true)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteMessageNumber(SshMessageNumber.KexInit);
        w.WriteRaw(System.Security.Cryptography.RandomNumberGenerator.GetBytes(SshKexInitMessage.CookieBytes));

        // 服务端发的是 *-s 版本的指示符。
        // 〔RFC 8308 §2.2〕指示符**只出现在第一次 KEXINIT 里**，
        // 重协商时再发是协议违规，某些服务端会直接断连。
        string[] kex = _options.AdvertiseStrictKex && includeIndicators
            ? [.. algorithms.KeyExchange.Where(TestKexResponder.IsSupported),
               SshAlgorithmNames.ExtInfoServer, SshAlgorithmNames.StrictKexServer]
            : [.. algorithms.KeyExchange.Where(TestKexResponder.IsSupported)];

        w.WriteNameList(kex);
        w.WriteNameList([.. _hostKey.SignatureAlgorithms]);
        w.WriteNameList([.. algorithms.EncryptionClientToServer]);
        w.WriteNameList([.. algorithms.EncryptionServerToClient]);
        w.WriteNameList([.. algorithms.MacClientToServer]);
        w.WriteNameList([.. algorithms.MacServerToClient]);
        w.WriteNameList([.. algorithms.CompressionClientToServer]);
        w.WriteNameList([.. algorithms.CompressionServerToClient]);
        w.WriteNameList([]);
        w.WriteNameList([]);
        w.WriteBoolean(false);
        w.WriteUInt32(0);
        return buffer.WrittenSpan.ToArray();
    }

    private static SshNegotiatedAlgorithms NegotiateAsServer(
        SshKexInitMessage clientKexInit, SshAlgorithmSet serverAlgorithms, IReadOnlyList<string> hostKeyAlgorithms)
    {
        // 协商以**客户端**的顺序为准，所以把客户端的列表当偏好、服务端的当候选。
        static string Pick(IReadOnlyList<string> client, IEnumerable<string> server)
        {
            HashSet<string> available = new(server, StringComparer.Ordinal);
            foreach (string candidate in client)
            {
                if (!SshAlgorithmNegotiator.IsIndicator(candidate) && available.Contains(candidate))
                {
                    return candidate;
                }
            }
            throw new InvalidOperationException("测试服务端与客户端没有共同算法。");
        }

        string encC2S = Pick(clientKexInit.EncryptionClientToServer, serverAlgorithms.EncryptionClientToServer);
        string encS2C = Pick(clientKexInit.EncryptionServerToClient, serverAlgorithms.EncryptionServerToClient);

        return new SshNegotiatedAlgorithms(
            Pick(clientKexInit.KeyExchangeAlgorithms, serverAlgorithms.KeyExchange.Where(TestKexResponder.IsSupported)),
            Pick(clientKexInit.ServerHostKeyAlgorithms, hostKeyAlgorithms),
            encC2S,
            encS2C,
            SshAlgorithmNegotiator.IsAead(encC2S) ? null : Pick(clientKexInit.MacClientToServer, serverAlgorithms.MacClientToServer),
            SshAlgorithmNegotiator.IsAead(encS2C) ? null : Pick(clientKexInit.MacServerToClient, serverAlgorithms.MacServerToClient),
            Pick(clientKexInit.CompressionClientToServer, serverAlgorithms.CompressionClientToServer),
            Pick(clientKexInit.CompressionServerToClient, serverAlgorithms.CompressionServerToClient),
            StrictKeyExchange: clientKexInit.KeyExchangeAlgorithms.Contains(SshAlgorithmNames.StrictKexClient, StringComparer.Ordinal),
            PeerSupportsExtensionInfo: clientKexInit.KeyExchangeAlgorithms.Contains(SshAlgorithmNames.ExtInfoClient, StringComparer.Ordinal));
    }

    private static byte[] ReadKexValue(ReadOnlyMemory<byte> payload, string kexAlgorithm)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();

        bool isMpint = kexAlgorithm is SshAlgorithmNames.DiffieHellmanGroup14Sha256
            or SshAlgorithmNames.DiffieHellmanGroup16Sha512
            or SshAlgorithmNames.DiffieHellmanGroup14Sha1;

        return isMpint
            ? reader.ReadMpint(MaxField).ToArray()
            : reader.ReadStringAsArray(MaxField);
    }

    /// <summary>直接往传输写一个报文 —— 只有首次交换能这么做（那时还没有别的写者）。</summary>
    private async ValueTask SendTransportAsync(
        ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        Transport.WritePacket(packet.Span);
        await Transport.FlushAsync(cancellationToken);
    }

    /// <summary>直接从传输读下一个报文 —— 只有首次交换能这么做（那时还没有别的读者）。</summary>
    private async ValueTask<SshInboundPacket> ReadTransportAsync(CancellationToken cancellationToken) =>
        await Transport.ReadPacketAsync(cancellationToken);

    /// <summary>从注入的读取器取下一个报文，并断言它的消息编号。</summary>
    private static async Task<SshInboundPacket> ExpectAsync(
        Func<CancellationToken, ValueTask<SshInboundPacket>> read,
        SshMessageNumber expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            SshInboundPacket packet = await read(cancellationToken);
            if (packet.IsEndOfStream)
            {
                throw new InvalidOperationException($"客户端在期望 {expected} 时关闭了连接。");
            }
            if (packet.MessageNumber is SshMessageNumber.Ignore or SshMessageNumber.Debug)
            {
                continue;
            }
            if (packet.MessageNumber != expected)
            {
                throw new InvalidOperationException($"期望 {expected}，收到 {packet.MessageNumber}。");
            }
            return packet;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _hostKey.Dispose();
        await Transport.DisposeAsync();
    }
}
