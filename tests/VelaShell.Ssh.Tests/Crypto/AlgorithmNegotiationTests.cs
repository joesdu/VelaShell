// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §2（KEXINIT 与协商规则）
//           velashell-docs/zh/ssh/spec/08-failures.md §5.1（协商失败必须带双方名单）

using System.Buffers;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class AlgorithmNegotiationTests
{
    private static SshKexInitMessage PeerOffering(
        IEnumerable<string>? kex = null,
        IEnumerable<string>? hostKey = null,
        IEnumerable<string>? encryption = null,
        IEnumerable<string>? mac = null,
        IEnumerable<string>? compression = null)
    {
        string[] enc = [.. encryption ?? [SshAlgorithmNames.Aes256Gcm]];
        string[] m = [.. mac ?? [SshAlgorithmNames.HmacSha256Etm]];
        string[] c = [.. compression ?? [SshAlgorithmNames.None]];

        return new SshKexInitMessage
        {
            KeyExchangeAlgorithms = [.. kex ?? [SshAlgorithmNames.Curve25519Sha256]],
            ServerHostKeyAlgorithms = [.. hostKey ?? [SshAlgorithmNames.SshEd25519]],
            EncryptionClientToServer = enc,
            EncryptionServerToClient = enc,
            MacClientToServer = m,
            MacServerToClient = m,
            CompressionClientToServer = c,
            CompressionServerToClient = c,
            FirstKexPacketFollows = false,
            Payload = ReadOnlyMemory<byte>.Empty,
        };
    }

    // ------------------------------------------------------------ 编解码往返

    [TestMethod]
    public void KexInit编码之后能解析回来()
    {
        ArrayBufferWriter<byte> writer = new();
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, includeIndicators: true, writer);

        var decoded = SshKexInitMessage.Decode(writer.WrittenMemory);

        Assert.AreSequenceEqual(
            [.. SshAlgorithmSet.Default.HostKey], [.. decoded.ServerHostKeyAlgorithms]);
        Assert.IsFalse(decoded.FirstKexPacketFollows, "我们自己发送时恒为 false");
    }

    [TestMethod]
    public void 保留的载荷与写出的字节逐字节相同()
    {
        // 这条是**签名验证能不能过**的前提：载荷就是交换哈希里的 I_C / I_S，
        // 任何重新编码都可能产生不同的字节，而那会让签名以一种完全看不出原因的方式失败
        //（velashell-docs/zh/ssh/spec/03 §4.1）。
        ArrayBufferWriter<byte> writer = new();
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, includeIndicators: true, writer);
        byte[] original = writer.WrittenSpan.ToArray();

        var decoded = SshKexInitMessage.Decode(original);
        Assert.AreSequenceEqual(original, decoded.Payload.ToArray());
    }

    [TestMethod]
    public void 每次编码的cookie都不同()
    {
        // cookie 必须是密码学随机数：它让任何一方都无法单独决定交换哈希的取值。
        ArrayBufferWriter<byte> a = new();
        ArrayBufferWriter<byte> b = new();
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, true, a);
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, true, b);

        ReadOnlySpan<byte> cookieA = a.WrittenSpan.Slice(1, SshKexInitMessage.CookieBytes);
        ReadOnlySpan<byte> cookieB = b.WrittenSpan.Slice(1, SshKexInitMessage.CookieBytes);
        Assert.IsFalse(cookieA.SequenceEqual(cookieB));
    }

    [TestMethod]
    public void 指示符只在首次KexInit里出现()
    {
        // RFC 8308 §2.2：ext-info-c 只能出现在**第一次** KEXINIT 里。
        // 重协商时再发是协议违规，某些服务端会直接断连。
        ArrayBufferWriter<byte> first = new();
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, includeIndicators: true, first);
        var firstMsg = SshKexInitMessage.Decode(first.WrittenMemory);
        Assert.Contains(SshAlgorithmNames.ExtInfoClient, [.. firstMsg.KeyExchangeAlgorithms]);
        Assert.Contains(SshAlgorithmNames.StrictKexClient, [.. firstMsg.KeyExchangeAlgorithms]);

        ArrayBufferWriter<byte> rekey = new();
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, includeIndicators: false, rekey);
        var rekeyMsg = SshKexInitMessage.Decode(rekey.WrittenMemory);
        Assert.DoesNotContain(SshAlgorithmNames.ExtInfoClient, [.. rekeyMsg.KeyExchangeAlgorithms]);
        Assert.DoesNotContain(SshAlgorithmNames.StrictKexClient, [.. rekeyMsg.KeyExchangeAlgorithms]);
    }

    // ------------------------------------------------------------ 协商规则

    [TestMethod]
    public void 取我方列表中第一个双方都支持的()
    {
        // RFC 4253 §7.1：以**客户端**列表的顺序为准，服务端的顺序不起作用。
        SshAlgorithmSet ours = SshAlgorithmSet.Default with
        {
            EncryptionClientToServer = [SshAlgorithmNames.Aes128Gcm, SshAlgorithmNames.Aes256Gcm],
            EncryptionServerToClient = [SshAlgorithmNames.Aes128Gcm, SshAlgorithmNames.Aes256Gcm],
        };

        // 服务端把 aes256 放前面 —— 不起作用，仍按我方顺序取 aes128。
        SshKexInitMessage peer = PeerOffering(
            encryption: [SshAlgorithmNames.Aes256Gcm, SshAlgorithmNames.Aes128Gcm]);

        SshNegotiatedAlgorithms result = SshAlgorithmNegotiator.Negotiate(ours, peer, "SSH-2.0-Test");
        Assert.AreEqual(SshAlgorithmNames.Aes128Gcm, result.EncryptionClientToServer);
    }

    [TestMethod]
    public void AEAD加密下MAC协商结果为空()
    {
        // RFC 5647：AEAD 自带完整性，该方向的 MAC 协商结果被忽略。
        SshNegotiatedAlgorithms result = SshAlgorithmNegotiator.Negotiate(
            SshAlgorithmSet.Default,
            PeerOffering(encryption: [SshAlgorithmNames.Aes256Gcm]),
            "SSH-2.0-Test");

        Assert.IsNull(result.MacClientToServer);
        Assert.IsNull(result.MacServerToClient);
    }

    [TestMethod]
    public void 非AEAD加密下会协商出MAC()
    {
        SshNegotiatedAlgorithms result = SshAlgorithmNegotiator.Negotiate(
            SshAlgorithmSet.Default,
            PeerOffering(encryption: [SshAlgorithmNames.Aes256Ctr], mac: [SshAlgorithmNames.HmacSha256Etm]),
            "SSH-2.0-Test");

        Assert.AreEqual(SshAlgorithmNames.HmacSha256Etm, result.MacClientToServer);
    }

    [TestMethod]
    public void 即使只支持AEAD也要发送非空MAC列表()
    {
        // 不发会让只支持非 AEAD 的对端无法与我们协商（velashell-docs/zh/ssh/spec/03 §2.2）。
        ArrayBufferWriter<byte> writer = new();
        SshKexInitMessage.Encode(SshAlgorithmSet.Default, true, writer);
        var decoded = SshKexInitMessage.Decode(writer.WrittenMemory);

        Assert.IsNotEmpty(decoded.MacClientToServer);
        Assert.IsNotEmpty(decoded.MacServerToClient);
    }

    [TestMethod]
    public void 指示符不会被选成协商结果()
    {
        SshNegotiatedAlgorithms result = SshAlgorithmNegotiator.Negotiate(
            SshAlgorithmSet.Default,
            PeerOffering(kex: [SshAlgorithmNames.ExtInfoServer, SshAlgorithmNames.StrictKexServer,
                               SshAlgorithmNames.Curve25519Sha256]),
            "SSH-2.0-Test");

        Assert.AreEqual(SshAlgorithmNames.Curve25519Sha256, result.KeyExchange);
        Assert.IsTrue(result.StrictKeyExchange);
        Assert.IsTrue(result.PeerSupportsExtensionInfo);
    }

    [TestMethod]
    public void 对端不宣告严格KEX时如实反映()
    {
        // 我们不因此拒绝连接（老服务端很多），但使用者要能看到这条连接没有 Terrapin 缓解。
        SshNegotiatedAlgorithms result = SshAlgorithmNegotiator.Negotiate(
            SshAlgorithmSet.Default, PeerOffering(), "SSH-2.0-Old");

        Assert.IsFalse(result.StrictKeyExchange);
    }

    // ------------------------------------------------------------ 协商失败

    [TestMethod]
    public void 无交集时异常带上双方的完整名单()
    {
        // 这是本库相对现有实现最直接的一处改进：
        // 用户拿到的不是「No common encryption algorithm.」，
        // 而是「对端只给了 aes128-cbc，而我们的清单里没有它」。
        SshKexInitMessage peer = PeerOffering(encryption: [SshAlgorithmNames.Aes128Cbc]);

        SshNegotiationException ex = Assert.ThrowsExactly<SshNegotiationException>(
            () => SshAlgorithmNegotiator.Negotiate(SshAlgorithmSet.Default, peer, "SSH-2.0-Ancient_1.0"));

        Assert.AreEqual(SshNegotiationCategory.EncryptionClientToServer, ex.Category);
        Assert.AreSequenceEqual(new[] { SshAlgorithmNames.Aes128Cbc }, [.. ex.OfferedByPeer]);
        Assert.IsNotEmpty(ex.OfferedByUs);
        Assert.AreEqual("SSH-2.0-Ancient_1.0", ex.PeerVersion);
        Assert.AreEqual(SshFailureReason.NegotiationFailed, ex.Reason);
        Assert.AreEqual(SshPhase.KeyExchange, ex.Phase);
        Assert.IsFalse(ex.IsRetryable, "改配置之前重试没有意义");
    }

    [TestMethod]
    public void 失败的名单里不含指示符()
    {
        // 把 ext-info-c 摆进「本端支持」只会让用户困惑 —— 那不是一个他能去服务端打开的算法。
        SshNegotiationException ex = Assert.ThrowsExactly<SshNegotiationException>(
            () => SshAlgorithmNegotiator.Negotiate(
                SshAlgorithmSet.Default, PeerOffering(kex: ["nonexistent-kex"]), "SSH-2.0-Test"));

        Assert.DoesNotContain(SshAlgorithmNames.ExtInfoClient, [.. ex.OfferedByUs]);
        Assert.DoesNotContain(SshAlgorithmNames.StrictKexClient, [.. ex.OfferedByUs]);
    }

    [TestMethod]
    public void 主机密钥无交集也能报出来()
    {
        SshNegotiationException ex = Assert.ThrowsExactly<SshNegotiationException>(
            () => SshAlgorithmNegotiator.Negotiate(
                SshAlgorithmSet.Default, PeerOffering(hostKey: ["ssh-dss"]), "SSH-2.0-Test"));

        Assert.AreEqual(SshNegotiationCategory.HostKey, ex.Category);
    }

    // ------------------------------------------------------------ 老设备互操作

    [TestMethod]
    public void 默认清单不含任何已弃用算法()
    {
        SshAlgorithmSet d = SshAlgorithmSet.Default;
        Assert.DoesNotContain(SshAlgorithmNames.DiffieHellmanGroup14Sha1, [.. d.KeyExchange]);
        Assert.DoesNotContain(SshAlgorithmNames.SshRsa, [.. d.HostKey]);
        Assert.DoesNotContain(SshAlgorithmNames.HmacSha1, [.. d.MacClientToServer]);
        Assert.DoesNotContain(SshAlgorithmNames.Aes256Cbc, [.. d.EncryptionClientToServer]);
    }

    [TestMethod]
    public void 显式放开之后能与老设备协商()
    {
        // 「连不上那台交换机」对运维是一个每天都在发生的真实问题。
        // 做成显式开关，好过让人去别处找一个更差的工具。
        SshAlgorithmSet legacy = SshAlgorithmSet.Default.WithLegacyInterop();

        SshNegotiatedAlgorithms result = SshAlgorithmNegotiator.Negotiate(
            legacy,
            PeerOffering(
                kex: [SshAlgorithmNames.DiffieHellmanGroup14Sha1],
                hostKey: [SshAlgorithmNames.SshRsa],
                encryption: [SshAlgorithmNames.Aes128Ctr],
                mac: [SshAlgorithmNames.HmacSha1]),
            "SSH-2.0-Cisco-1.25");

        Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroup14Sha1, result.KeyExchange);
        Assert.AreEqual(SshAlgorithmNames.SshRsa, result.HostKey);
        Assert.AreEqual(SshAlgorithmNames.Aes128Ctr, result.EncryptionClientToServer);
        Assert.AreEqual(SshAlgorithmNames.HmacSha1, result.MacClientToServer);
    }

    [TestMethod]
    public void 放开老算法也不报本库没实现的CBC()
    {
        // 报了就会在只剩 CBC 的设备上「谈成」它，然后在派生密钥时才失败。
        SshAlgorithmSet legacy = SshAlgorithmSet.Default.WithLegacyInterop();

        Assert.DoesNotContain(SshAlgorithmNames.Aes128Cbc, [.. legacy.EncryptionClientToServer]);
        Assert.DoesNotContain(SshAlgorithmNames.Aes256Cbc, [.. legacy.EncryptionServerToClient]);
        legacy.Validate();

        SshNegotiationException ex = Assert.ThrowsExactly<SshNegotiationException>(() =>
            SshAlgorithmNegotiator.Negotiate(
                legacy, PeerOffering(encryption: [SshAlgorithmNames.Aes128Cbc]), "SSH-2.0-Cisco-1.25"));
        Assert.AreEqual(SshNegotiationCategory.EncryptionClientToServer, ex.Category);
    }

    [TestMethod]
    [DataRow("enc")]
    [DataRow("mac")]
    [DataRow("kex")]
    [DataRow("comp")]
    public void 清单里有没实现的算法在连接前就报(string category)
    {
        SshAlgorithmSet d = SshAlgorithmSet.Default;
        SshAlgorithmSet bad = category switch
        {
            "enc" => d with { EncryptionServerToClient = [.. d.EncryptionServerToClient, SshAlgorithmNames.Aes128Cbc] },
            "mac" => d with { MacClientToServer = [.. d.MacClientToServer, "hmac-md5"] },
            "kex" => d with { KeyExchange = [.. d.KeyExchange, "diffie-hellman-group1-sha1"] },
            _ => d with { CompressionServerToClient = ["zlib"] },
        };

        ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(bad.Validate);
        StringAssert.Contains(ex.Message, "未实现");
        d.WithLegacyInterop().WithCompression().Validate();
    }

    [TestMethod]
    public async Task 连接用了坏清单不会先去拨号()
    {
        bool dialed = false;
        SshConnectionOptions options = new("u", "example.invalid")
        {
            Algorithms = SshAlgorithmSet.Default with { EncryptionClientToServer = [SshAlgorithmNames.Aes256Cbc] },
            Dialer = new RecordingDialer(() => dialed = true),
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await SshConnection.ConnectAsync(options));
        Assert.IsFalse(dialed);
    }

    private sealed class RecordingDialer(Action onDial) : ISshTransportDialer
    {
        public SshDialKind Kind => SshDialKind.Tcp;

        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
        {
            onDial();
            throw new IOException("不该走到这里。");
        }
    }

    [TestMethod]
    public void 放开老算法不会改变现代对端的协商结果()
    {
        // 追加而不是前置：只有在对端一个现代算法都不支持时才会落到老算法上。
        SshNegotiatedAlgorithms modern = SshAlgorithmNegotiator.Negotiate(
            SshAlgorithmSet.Default.WithLegacyInterop(),
            PeerOffering(
                kex: [SshAlgorithmNames.Curve25519Sha256, SshAlgorithmNames.DiffieHellmanGroup14Sha1],
                encryption: [SshAlgorithmNames.Aes256Gcm, SshAlgorithmNames.Aes128Cbc]),
            "SSH-2.0-OpenSSH_9.6");

        Assert.AreEqual(SshAlgorithmNames.Curve25519Sha256, modern.KeyExchange);
        Assert.AreEqual(SshAlgorithmNames.Aes256Gcm, modern.EncryptionClientToServer);
    }
}
