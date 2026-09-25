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

        SshVersionExchangeResult versions =
            await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);

        SshKeyExchangeRunner runner = new(
            clientTransport,
            clientAlgorithms ?? SshAlgorithmSet.Default,
            policy ?? new DangerousAcceptAnyHostKeyPolicy());

        SshKeyExchangeResult clientResult =
            await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);

        TestSshServerHandshake serverResult = await serverTask;

        if (afterHandshake is not null)
        {
            await afterHandshake(clientTransport, server.Transport);
        }

        return (clientResult, serverResult);
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
        Assert.Contains("签名验证失败", ex.Message);
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
        const string Reason = "这台机器的指纹不在公司白名单里，请联系管理员。";

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await HandshakeAsync(policy: new RejectingPolicy(Reason)));

        Assert.AreEqual(SshFailureReason.HostKeyRejected, ex.Reason);
        Assert.AreEqual(Reason, ex.Message);
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
        Assert.AreSequenceEqual(new[] { "cipher-that-does-not-exist" }, [.. ex.OfferedByUs]);
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
