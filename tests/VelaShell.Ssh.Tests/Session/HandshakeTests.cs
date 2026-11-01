// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/02-version-exchange.md、velashell-docs/zh/ssh/spec/03-key-exchange.md 全部
//
// 这是 M1 的**决定性测试**：客户端与测试服务端在内存里跑完整条握手，
// 两侧独立算出交换哈希与会话密钥，然后用新密钥真的收发一个报文。
//
// 只测「不抛异常」没有意义 —— 交换哈希算错了照样不抛，
// 只会在签名验证那一步失败，而那时你看不出是哪个字段错了。
// 所以这里断言的是**两侧算出的 H 逐字节相同**，以及**换密钥之后数据仍然通**。

using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class HandshakeTests
{
    /// <summary>跑一次完整握手，返回两侧的结果。</summary>
    private static async Task<(SshKeyExchangeResult Client, TestSshServerHandshake Server)> HandshakeAsync(
        SshAlgorithmSet? clientAlgorithms = null,
        TestSshServerOptions? serverOptions = null,
        IHostKeyPolicy? policy = null,
        Func<SshPacketTransport, SshPacketTransport, Task>? afterHandshake = null)
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();

        await using TestSshServer server = new(serverStream, serverOptions);
        await using SshPacketTransport clientTransport = new(clientStream);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // 服务端与客户端**并发**跑 —— 握手里有双向同时发的步骤，
        // 串行跑会在第一个 KEXINIT 上就死锁。
        Task<TestSshServerHandshake> serverTask = server.HandshakeAsync(cts.Token);

        SshKeyExchangeResult clientResult;
        try
        {
            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);

            SshKeyExchangeRunner runner = new(
                clientTransport,
                clientAlgorithms ?? SshAlgorithmSet.Default,
                policy ?? new DangerousAcceptAnyHostKeyPolicy());

            clientResult = await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
        }
        catch
        {
            // 客户端这边失败了（拒绝主机密钥、协商不成……）：先关掉客户端，服务端那一侧随之读到连接断开、收工，
            // 等它收完再把客户端的异常交出去。曾经直接交出去，await using 拆掉的是一个还停在读里的服务端
            // （velashell-docs/zh/ssh/spec/01 §4「释放」）。
            await clientTransport.DisposeAsync();
            await WaitForServerToStopAsync(serverTask);
            throw;
        }

        TestSshServerHandshake serverResult = await serverTask;

        if (afterHandshake is not null)
        {
            await afterHandshake(clientTransport, server.Transport);
        }

        return (clientResult, serverResult);
    }

    /// <summary>等服务端那一侧收工。客户端先走了，它失败是意料之中的。</summary>
    private static async Task WaitForServerToStopAsync(Task serverTask)
    {
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    // ------------------------------------------------------------ 主路径

    [TestMethod]
    public async Task 默认算法下握手成功且两侧交换哈希一致()
    {
        (SshKeyExchangeResult client, TestSshServerHandshake server) = await HandshakeAsync();

        Assert.AreSequenceEqual(server.ExchangeHash, client.ExchangeHash, "两侧独立算出的交换哈希必须逐字节相同 —— 不同就说明某个字段的顺序或编码错了");
        Assert.AreSequenceEqual(client.ExchangeHash, client.SessionId, "首次密钥交换时 session_id 就是 H");
        Assert.AreEqual(server.Negotiated.KeyExchange, client.Algorithms.KeyExchange);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, client.HostKey.KeyType);
    }

    [TestMethod]
    public async Task 换完密钥之后数据仍然通()
    {
        // 交换哈希一致只说明双方算出了同一个 H；派生密钥的**字母与方向**
        // 还可能弄反（A/C/E 是发送、B/D/F 是接收）。弄反的症状正是这一步失败。
        await HandshakeAsync(afterHandshake: static async (client, server) =>
        {
            byte[] toServer = [(byte)SshMessageNumber.ServiceRequest, .. "ssh-userauth"u8];
            client.WritePacket(toServer);
            await client.FlushAsync();
            SshInboundPacket received = await server.ReadPacketAsync();
            Assert.AreSequenceEqual(toServer, received.Payload.ToArray(), "客户端→服务端方向");

            byte[] toClient = [(byte)SshMessageNumber.ServiceAccept, .. "ssh-userauth"u8];
            server.WritePacket(toClient);
            await server.FlushAsync();
            SshInboundPacket back = await client.ReadPacketAsync();
            Assert.AreSequenceEqual(toClient, back.Payload.ToArray(), "服务端→客户端方向");
        });
    }

    [TestMethod]
    public async Task 每一种密钥交换算法都能完成握手()
    {
        foreach (string kex in SshAlgorithmSet.Default.KeyExchange)
        {
            if (!TestKexResponder.IsSupported(kex))
            {
                continue;
            }

            SshAlgorithmSet algorithms = SshAlgorithmSet.Default with { KeyExchange = [kex] };
            (SshKeyExchangeResult client, TestSshServerHandshake server) = await HandshakeAsync(algorithms);

            Assert.AreEqual(kex, client.Algorithms.KeyExchange, kex);
            Assert.AreSequenceEqual(server.ExchangeHash, client.ExchangeHash, $"{kex}：交换哈希不一致");
        }
    }

    /// <summary>〔spec/00 §6.6〕FIPS 清单里有 group18（与 RHEL 的 FIPS 策略一致）：只给它的服务端谈得成。</summary>
    [TestMethod]
    public async Task FIPS清单与只给group18的服务端谈得成()
    {
        (SshKeyExchangeResult client, TestSshServerHandshake server) = await HandshakeAsync(
            SshAlgorithmSet.FipsApprovedOnly,
            new TestSshServerOptions
            {
                HostKeyType = SshAlgorithmNames.EcdsaSha2Nistp256,
                Algorithms = SshAlgorithmSet.FipsApprovedOnly with { KeyExchange = [SshAlgorithmNames.DiffieHellmanGroup18Sha512] },
            });

        Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroup18Sha512, client.Algorithms.KeyExchange);
        Assert.AreSequenceEqual(server.ExchangeHash, client.ExchangeHash, "group18：交换哈希不一致");
    }

    /// <summary>
    /// 〔spec/00 §6.6〕FIPS 清单不含群交换（服务端现给的群不是命名的群，维护者 2026-10-31 定）：只给群交换的服务端
    /// 当场协商失败，异常说清是密钥交换这一类、我们没有报群交换。默认清单照旧谈成它。
    /// </summary>
    [TestMethod]
    public async Task FIPS清单与只给群交换的服务端协商失败_默认清单照旧谈成()
    {
        SshAlgorithmSet gexOnly = SshAlgorithmSet.Default with { KeyExchange = [SshAlgorithmNames.DiffieHellmanGroupExchangeSha256] };
        TestSshServerOptions server = new() { HostKeyType = SshAlgorithmNames.EcdsaSha2Nistp256, Algorithms = gexOnly };

        SshNegotiationException ex = await Assert.ThrowsExactlyAsync<SshNegotiationException>(
            async () => await HandshakeAsync(SshAlgorithmSet.FipsApprovedOnly, server));
        Assert.AreEqual(SshNegotiationCategory.KeyExchange, ex.Category);
        Assert.DoesNotContain(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256, ex.OfferedByUs);

        (SshKeyExchangeResult viaDefault, _) = await HandshakeAsync(SshAlgorithmSet.Default, server);
        Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256, viaDefault.Algorithms.KeyExchange);
    }

    [TestMethod]
    public async Task 每一种主机密钥类型都能完成握手()
    {
        foreach (string hostKeyType in new[]
                 {
                     SshAlgorithmNames.SshEd25519,
                     SshAlgorithmNames.EcdsaSha2Nistp256,
                     SshAlgorithmNames.EcdsaSha2Nistp384,
                     SshAlgorithmNames.EcdsaSha2Nistp521,
                     SshAlgorithmNames.RsaSha512,
                 })
        {
            (SshKeyExchangeResult client, _) = await HandshakeAsync(
                serverOptions: new TestSshServerOptions { HostKeyType = hostKeyType });

            // RSA 的密钥类型名与签名算法名不同 —— 这正是 RFC 8332 的那处不对称。
            string expectedKeyType = hostKeyType == SshAlgorithmNames.RsaSha512
                ? SshAlgorithmNames.SshRsa
                : hostKeyType;
            Assert.AreEqual(expectedKeyType, client.HostKey.KeyType, hostKeyType);
        }
    }

    [TestMethod]
    public async Task 每一种加密算法都能完成握手并通数据()
    {
        foreach (string cipher in SshAlgorithmSet.Default.EncryptionClientToServer)
        {
            SshAlgorithmSet algorithms = SshAlgorithmSet.Default with
            {
                EncryptionClientToServer = [cipher],
                EncryptionServerToClient = [cipher],
            };

            await HandshakeAsync(algorithms, afterHandshake: async (client, server) =>
            {
                byte[] payload = [(byte)SshMessageNumber.Ignore, .. System.Text.Encoding.ASCII.GetBytes(cipher)];
                client.WritePacket(payload);
                await client.FlushAsync();
                SshInboundPacket received = await server.ReadPacketAsync();
                Assert.AreSequenceEqual(payload, received.Payload.ToArray(), cipher);
            });
        }
    }

    [TestMethod]
    public async Task 非AEAD加密时MAC也参与并能通数据()
    {
        // AES-CTR + HMAC 走的是与 AEAD 完全不同的分帧路径（EtM / MtE、长度字段是否加密）。
        foreach (string mac in new[]
                 {
                     SshAlgorithmNames.HmacSha256Etm,
                     SshAlgorithmNames.HmacSha512Etm,
                     SshAlgorithmNames.HmacSha256,
                     SshAlgorithmNames.HmacSha512,
                 })
        {
            SshAlgorithmSet algorithms = SshAlgorithmSet.Default with
            {
                EncryptionClientToServer = [SshAlgorithmNames.Aes256Ctr],
                EncryptionServerToClient = [SshAlgorithmNames.Aes256Ctr],
                MacClientToServer = [mac],
                MacServerToClient = [mac],
            };

            (SshKeyExchangeResult client, _) = await HandshakeAsync(algorithms,
                afterHandshake: async (c, s) =>
                {
                    byte[] payload = [(byte)SshMessageNumber.Ignore, .. System.Text.Encoding.ASCII.GetBytes(mac)];
                    c.WritePacket(payload);
                    await c.FlushAsync();
                    Assert.AreSequenceEqual(payload, (await s.ReadPacketAsync()).Payload.ToArray(), mac);
                });

            Assert.AreEqual(mac, client.Algorithms.MacClientToServer, mac);
        }
    }

    // ------------------------------------------------------------ 版本交换

    /// <summary>
    /// 〔spec/02 §六〕服务端标识串的注释里有不是合法 UTF-8 的字节（Latin-1 的 ©）：进交换哈希的必须是原始字节。
    /// 曾经解成 string 再按 UTF-8 编回去，那个字节变成 EF BF BD，验签必然失败，还报「可能有中间人」。
    /// </summary>
    [TestMethod]
    public async Task 标识串里的非UTF8字节原样进交换哈希()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        byte[] identification = [.. "SSH-2.0-Vendor_1.0 "u8, 0xA9, .. " Corp"u8];
        await using TestSshServer server = new(serverStream, new TestSshServerOptions { IdentificationBytes = identification });
        await using SshPacketTransport clientTransport = new(clientStream);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task<TestSshServerHandshake> serverTask = server.HandshakeAsync(cts.Token);

        SshVersionExchangeResult versions =
            await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
        Assert.AreSequenceEqual(identification, versions.ServerVersionBytes);

        SshKeyExchangeRunner runner = new(clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
        SshKeyExchangeResult kex = await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
        TestSshServerHandshake handshake = await serverTask;

        Assert.AreSequenceEqual(handshake.ExchangeHash, kex.ExchangeHash, "两边算出的交换哈希必须一致");
    }

    /// <summary>〔RFC 4253 §4.2〕标识串含 CRLF 最多 255 字节：255 照收，256 拒绝（曾经差一，256 也能过）。</summary>
    [TestMethod]
    [DataRow(253, true)]
    [DataRow(254, false)]
    public async Task 标识串含行尾最多255字节(int lengthWithoutCrLf, bool accepted)
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        byte[] identification = [.. "SSH-2.0-"u8, .. Enumerable.Repeat((byte)'x', lengthWithoutCrLf - 8)];
        await using TestSshServer server = new(serverStream, new TestSshServerOptions { IdentificationBytes = identification });
        await using SshPacketTransport clientTransport = new(clientStream);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        _ = server.HandshakeAsync(cts.Token);

        if (accepted)
        {
            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
            Assert.HasCount(lengthWithoutCrLf, versions.ServerVersionBytes);
        }
        else
        {
            SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
                async () => await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token));
            Assert.AreEqual(SshFailureReason.NotAnSshServer, error.Reason);
        }

        await cts.CancelAsync();
    }

    [TestMethod]
    public async Task 服务端的前导行被收集起来()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using TestSshServer server = new(serverStream, new TestSshServerOptions
        {
            PreAuthBanner = ["Authorized use only.", "All activity is monitored."],
        });
        await using SshPacketTransport clientTransport = new(clientStream);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task<TestSshServerHandshake> serverTask = server.HandshakeAsync(cts.Token);

        SshVersionExchangeResult versions =
            await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);

        Assert.HasCount(2, versions.PreAuthBanner);
        Assert.AreEqual("Authorized use only.", versions.PreAuthBanner[0]);
        Assert.StartsWith("SSH-2.0-", versions.ServerVersion);

        SshKeyExchangeRunner runner = new(clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
        _ = await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
        _ = await serverTask;
    }

    [TestMethod]
    public async Task 我们的标识串不泄漏运行时信息()
    {
        // 〔决策 velashell-docs/zh/ssh/spec/02 §2.1〕注释部分是纯粹的指纹面 ——
        // 把「.NET 11 / Windows」广播给每一台连过的机器（包括蜜罐）没有任何收益。
        (SshKeyExchangeResult _, TestSshServerHandshake _) = await HandshakeAsync();

        string id = SshVersionExchange.ClientIdentification;
        Assert.DoesNotContain(" ", id, "不发注释");
        Assert.DoesNotContain("NET", id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows", id, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("SSH-2.0-", id);
    }

    // ------------------------------------------------------------ 失败路径

    [TestMethod]
    public async Task 签名被改动时拒绝连接()
    {
        // 这一条验证的是「我们真的在验签」。
        // 不验签的客户端在所有正常测试里都会通过 —— 只有这条能抓到它。
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(
                serverOptions: new TestSshServerOptions { CorruptSignature = true }));

        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.AreEqual(SshPhase.KeyExchange, ex.Phase);
        Assert.AreEqual(
            "服务端对交换哈希的签名验证失败（ssh-ed25519，对端 SSH-2.0-VelaShellTestServer_1.0）。这意味着对端没有它所声称的那把主机私钥 —— 可能有中间人。",
            ex.Message);

        // 库自己验不过（还没轮到策略）：种类与算法交给界面，宿主不再把这句中文当成策略写的理由照用。
        Assert.AreEqual(SshConnectFailureKind.HostKeySignatureInvalid, ex.FailureKind);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, ex.HostKeyAlgorithm);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, ex.HostKeyType);
    }

    /// <summary>服务端出示的主机密钥 blob 解析不了：库自己拒（种类 <see cref="SshConnectFailureKind.HostKeyMalformed"/>），内层是公钥解析异常。</summary>
    [TestMethod]
    public async Task 主机密钥解析不了时按种类拒绝()
    {
        using var real = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(serverOptions: new TestSshServerOptions { HostKey = new TruncatedBlobHostKey(real) }));

        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.StartsWith("服务端主机密钥无法解析：", ex.Message);
        Assert.IsInstanceOfType<SshPublicKeyException>(ex.InnerException);
        Assert.AreEqual(SshConnectFailureKind.HostKeyMalformed, ex.FailureKind);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, ex.HostKeyAlgorithm);
    }

    /// <summary>出示的 blob 里 Ed25519 公钥只有 3 个字节（签名照常由真钥做 —— 走不到验签那一步）。</summary>
    private sealed class TruncatedBlobHostKey(TestHostKey real) : TestHostKey
    {
        public override string KeyType => real.KeyType;

        public override IReadOnlyList<string> SignatureAlgorithms => real.SignatureAlgorithms;

        public override byte[] PublicKeyBlob => [0, 0, 0, 11, .. "ssh-ed25519"u8, 0, 0, 0, 3, 1, 2, 3];

        public override byte[] Sign(ReadOnlySpan<byte> data, string algorithm) => real.Sign(data, algorithm);
    }

    [TestMethod]
    public async Task 严格KEX下密钥交换期间的IGNORE会断开()
    {
        // Terrapin（CVE-2023-48795）利用的正是握手期允许插入这类报文。
        // 严格 KEX 的一半缓解就是「见到就断」。
        SshProtocolException ex = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await HandshakeAsync(
                serverOptions: new TestSshServerOptions { InjectIgnoreDuringKex = true }));

        Assert.AreEqual(SshFailureReason.ProtocolError, ex.Reason);
        Assert.Contains("Terrapin", ex.Message);
    }

    [TestMethod]
    public async Task 严格KEX下对端的第一个报文不是KEXINIT时断开()
    {
        // 读 KEXINIT 时还不知道会协商出严格 KEX，前面的 IGNORE 只能先照 RFC 跳过 —— 协商完必须回头追究。
        SshProtocolException ex = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await HandshakeAsync(
                serverOptions: new TestSshServerOptions { InjectIgnoreBeforeKexInit = true }));

        Assert.AreEqual(SshFailureReason.ProtocolError, ex.Reason);
        Assert.Contains("第一个报文", ex.Message);
    }

    [TestMethod]
    public async Task 不启用严格KEX时KEXINIT之前的IGNORE照常跳过()
    {
        (SshKeyExchangeResult client, _) = await HandshakeAsync(
            serverOptions: new TestSshServerOptions { AdvertiseStrictKex = false, InjectIgnoreBeforeKexInit = true });

        Assert.IsFalse(client.StrictKeyExchange);
    }

    [TestMethod]
    public async Task 不启用严格KEX时IGNORE被忽略()
    {
        // 老服务端不宣告严格 KEX。此时按 RFC 4253 §11.2 忽略这些报文 ——
        // 拒绝连接会把大量合法场景打死。
        (SshKeyExchangeResult client, _) = await HandshakeAsync(
            serverOptions: new TestSshServerOptions
            {
                AdvertiseStrictKex = false,
                InjectIgnoreDuringKex = true,
            });

        Assert.IsFalse(client.StrictKeyExchange,
            "对端不支持时如实反映 —— 使用者要能看到这条连接没有 Terrapin 缓解");
    }

    [TestMethod]
    public async Task 主机密钥策略拒绝时带出它给的原因()
    {
        // 回调只能返回一个裁决，原因得由它自己带上 ——
        // 否则用户拿到的只有一句「不受信任的对端」，不知道该去哪操作。
        const string reason = "这台机器的指纹不在公司白名单里，请联系管理员。";

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(policy: new RejectingPolicy(reason)));

        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.AreEqual(reason, ex.Message);
        Assert.AreEqual(SshConnectFailureKind.HostKeyPolicyRejected, ex.FailureKind, "这句话是策略写的 —— 界面原样用");
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, ex.HostKeyType);
    }

    [TestMethod]
    public async Task 策略返回未填的裁决时按拒绝处理()
    {
        // default(SshHostKeyVerdict) 必须是拒绝：一个忘了填裁决的策略只能让连接失败，不能悄悄放行。
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(policy: new FixedVerdictPolicy(default)));

        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.IsFalse(default(SshHostKeyVerdict).IsAccepted);
        Assert.AreEqual("test.invalid:22 的主机密钥被策略拒绝。", ex.Message);
        Assert.AreEqual(SshConnectFailureKind.HostKeyPolicyRejectedWithoutReason, ex.FailureKind, "策略没给原因：这句是库补的，界面另说");
    }

    [TestMethod]
    public async Task 策略判定密钥变了时以HostKeyChanged拒绝()
    {
        const string message = "指纹变了。";

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(policy: new FixedVerdictPolicy(SshHostKeyVerdict.RejectChanged(message))));

        Assert.AreEqual(SshFailureReason.HostKeyChanged, ex.Reason);
        Assert.AreEqual(message, ex.Message);
        Assert.AreEqual(SshConnectFailureKind.HostKeyPolicyRejected, ex.FailureKind);
    }

    [TestMethod]
    public async Task 指纹固定策略能放行与拦截()
    {
        // 先拿到真实指纹
        string fingerprint = "";
        (SshKeyExchangeResult probe, _) = await HandshakeAsync(
            policy: new CapturingPolicy(k => fingerprint = k.Sha256Fingerprint));
        Assert.IsNotEmpty(fingerprint);
        Assert.AreEqual(fingerprint, probe.HostKey.Sha256Fingerprint);

        // 每次握手服务端都新生成一把密钥，所以这把指纹对下一次连接必然不匹配 ——
        // 这正好是「换了台机器」的场景。
        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(policy: new PinnedFingerprintHostKeyPolicy([fingerprint])));
        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.Contains("不在允许列表里", ex.Message);
    }

    [TestMethod]
    public async Task 算法无交集时异常带出双方名单()
    {
        SshAlgorithmSet clientOnly = SshAlgorithmSet.Default with
        {
            EncryptionClientToServer = ["cipher-that-does-not-exist"],
            EncryptionServerToClient = ["cipher-that-does-not-exist"],
        };

        SshNegotiationException ex = await Assert.ThrowsExactlyAsync<SshNegotiationException>(
            async () => await HandshakeAsync(clientOnly));

        Assert.AreEqual(SshNegotiationCategory.EncryptionClientToServer, ex.Category);
        Assert.IsNotEmpty(ex.OfferedByPeer);
        Assert.AreSequenceEqual(["cipher-that-does-not-exist"], [.. ex.OfferedByUs]);
        Assert.StartsWith("SSH-2.0-", ex.PeerVersion);
    }

    [TestMethod]
    public async Task 对端在握手中途消失时报连接已断()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using SshPacketTransport clientTransport = new(clientStream);
        await using SshPacketTransport serverTransport = new(serverStream);

        // 服务端发完标识串就**半关闭写端**：它不再说话，但还能收 ——
        // 这正是「对端进程还在、但 SSH 服务已经放弃这条连接」的形状。
        await serverTransport.WriteLineAsync("SSH-2.0-RudeServer");
        serverStream.CompleteWrites();

        SshVersionExchangeResult versions = await SshVersionExchange.ExchangeAsync(clientTransport);
        SshKeyExchangeRunner runner = new(clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());

        SshConnectionClosedException ex = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await runner.RunAsync(versions, "test.invalid", 22));
        Assert.AreEqual(SshFailureReason.ClosedByPeer, ex.Reason);
    }

    [TestMethod]
    public async Task 对端在密钥交换的报文中途断开时也报连接已断()
    {
        // 与上一条的区别：断开时一个报文只收到一半。那是连接断了，不是报文写错了 ——
        // 会话期间早就这么归类（SshConnection.NormalizeFault），握手期间曾经报成协议错误，调用方就不会重连。
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using SshPacketTransport clientTransport = new(clientStream);
        await using SshPacketTransport serverTransport = new(serverStream);

        await serverTransport.WriteLineAsync("SSH-2.0-HalfPacketServer");
        SshVersionExchangeResult versions = await SshVersionExchange.ExchangeAsync(clientTransport);

        // 声称 252 字节（加上长度字段正好是块大小 8 的倍数，过得了对齐检查），只给 1 字节。
        await serverStream.WriteAsync(new byte[] { 0, 0, 0, 252, 5 });
        serverStream.CompleteWrites();

        SshKeyExchangeRunner runner = new(clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());

        SshConnectionClosedException ex = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await runner.RunAsync(versions, "test.invalid", 22));
        Assert.AreEqual(SshFailureReason.ClosedByPeer, ex.Reason);
    }

    [TestMethod]
    public async Task 端口上没有SSH服务时给出能照着办的错误()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using SshPacketTransport clientTransport = new(clientStream);

        await using (serverStream)
        {
            // 一个 HTTP 服务器会这么回。
            await serverStream.WriteAsync("HTTP/1.1 400 Bad Request\r\n"u8.ToArray());
            serverStream.CompleteWrites();

            SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
                async () => await SshVersionExchange.ExchangeAsync(clientTransport));

            Assert.AreEqual(SshFailureReason.ClosedByPeer, ex.Reason);
            Assert.AreEqual(SshPhase.VersionExchange, ex.Phase);
        }
    }

    private sealed class RejectingPolicy(string reason) : IHostKeyPolicy
    {
        public ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SshHostKeyVerdict.Reject(reason));
    }

    private sealed class FixedVerdictPolicy(SshHostKeyVerdict verdict) : IHostKeyPolicy
    {
        public ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(verdict);
    }

    private sealed class CapturingPolicy(Action<SshPublicKey> capture) : IHostKeyPolicy
    {
        public ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            capture(context.Key);
            return ValueTask.FromResult(SshHostKeyVerdict.Accept);
        }
    }
}
