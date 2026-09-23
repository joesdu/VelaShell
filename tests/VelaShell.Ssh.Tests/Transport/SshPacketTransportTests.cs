// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/01-transport-framing.md §3（收包）、§4（发包与合并）
//           velashell-docs/zh/ssh/spec/02-version-exchange.md（行式 IO 与互操作）
//
// 这一层跑在**真实的双工管道**上（InMemoryTransport），不是对着假对象断言 ——
// 所以它同时验证了「分帧 + Pipelines 的 AdvanceTo 用法」这条最容易卡死的路径。

using System.Security.Cryptography;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

[TestClass]
[TestCategory("Transport")]
public sealed class SshPacketTransportTests
{
    private static (SshPacketTransport A, SshPacketTransport B) CreatePair(
        InMemoryTransportOptions? options = null)
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair(options);
        return (new SshPacketTransport(a), new SshPacketTransport(b));
    }

    /// <summary>给两端装上同一套密钥的密码套件（模拟 NEWKEYS 之后的状态）。</summary>
    private static void InstallChaCha(SshPacketTransport a, SshPacketTransport b, bool strictKex = false)
    {
        byte[] aToB = RandomNumberGenerator.GetBytes(ChaCha20Poly1305CipherSuite.KeyMaterialBytes);
        byte[] bToA = RandomNumberGenerator.GetBytes(ChaCha20Poly1305CipherSuite.KeyMaterialBytes);

        a.SetSendCipherSuite(new ChaCha20Poly1305CipherSuite(aToB), strictKex);
        b.SetReceiveCipherSuite(new ChaCha20Poly1305CipherSuite(aToB), strictKex);
        b.SetSendCipherSuite(new ChaCha20Poly1305CipherSuite(bToA), strictKex);
        a.SetReceiveCipherSuite(new ChaCha20Poly1305CipherSuite(bToA), strictKex);
    }

    // ------------------------------------------------------------ 行式 IO

    [TestMethod]
    public async Task 版本标识串能往返()
    {
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            await a.WriteLineAsync("SSH-2.0-VelaShell.Ssh_0.1");
            Assert.AreEqual("SSH-2.0-VelaShell.Ssh_0.1", await b.ReadLineAsync());
        }
    }

    [TestMethod]
    public async Task 裸LF结尾也能接受()
    {
        // 〔互操作 spec/02 §5〕RFC 要求 \r\n，但部分嵌入式实现只发 \n。
        // 严格要求会让那些设备完全连不上，而接受它没有安全代价。
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport transport = new(peer);
        await using (raw)
        {
            await raw.WriteAsync("SSH-2.0-Dropbear_2022.83\n"u8.ToArray());
            Assert.AreEqual("SSH-2.0-Dropbear_2022.83", await transport.ReadLineAsync());
        }
    }

    [TestMethod]
    public async Task 多行前导之后仍能读到标识串()
    {
        // 服务端可以在标识串之前发任意行文本（法律声明），客户端必须跳过它们。
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport transport = new(peer);
        await using (raw)
        {
            await raw.WriteAsync("Authorized use only.\r\nAll activity is monitored.\r\nSSH-2.0-OpenSSH_9.6\r\n"u8.ToArray());

            Assert.AreEqual("Authorized use only.", await transport.ReadLineAsync());
            Assert.AreEqual("All activity is monitored.", await transport.ReadLineAsync());
            Assert.AreEqual("SSH-2.0-OpenSSH_9.6", await transport.ReadLineAsync());
        }
    }

    [TestMethod]
    public async Task 超长的行被拒绝()
    {
        // 不设上限就是一个无成本的内存耗尽面。
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport transport = new(peer);
        await using (raw)
        {
            await raw.WriteAsync(new byte[SshPacketTransport.MaxIdentificationLineBytes + 10]);
            await Assert.ThrowsExactlyAsync<SshFrameFormatException>(
                async () => await transport.ReadLineAsync());
        }
    }

    [TestMethod]
    public async Task 对端干净关闭时读行返回null()
    {
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport transport = new(peer);
        raw.CompleteWrites();
        Assert.IsNull(await transport.ReadLineAsync());
    }

    [TestMethod]
    public async Task 行式读完之后剩余字节归帧层()
    {
        // 这是用 Pipelines 而不是 Stream.ReadAsync 的直接好处：
        // 标识串之后紧跟第一个二进制报文，多读的字节自动留在缓冲里。
        // 自己搬缓冲的实现最容易在这里丢掉第一个报文。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            await a.WriteLineAsync("SSH-2.0-Test");
            byte[] payload = [(byte)SshMessageNumber.KexInit, 1, 2, 3, 4, 5];
            a.WritePacket(payload);
            await a.FlushAsync();

            Assert.AreEqual("SSH-2.0-Test", await b.ReadLineAsync());
            SshInboundPacket packet = await b.ReadPacketAsync();
            Assert.IsFalse(packet.IsEndOfStream);
            Assert.AreSequenceEqual(payload, packet.Payload.ToArray());
        }
    }

    // ------------------------------------------------------------ 帧式 IO

    [TestMethod]
    public async Task 明文报文能往返()
    {
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            byte[] payload = [(byte)SshMessageNumber.KexInit, .. RandomNumberGenerator.GetBytes(200)];
            a.WritePacket(payload);
            await a.FlushAsync();

            SshInboundPacket packet = await b.ReadPacketAsync();
            Assert.AreSequenceEqual(payload, packet.Payload.ToArray());
            Assert.AreEqual(SshMessageNumber.KexInit, packet.MessageNumber);
        }
    }

    [TestMethod]
    public async Task 加密报文能往返()
    {
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            InstallChaCha(a, b);

            for (int i = 0; i < 50; i++)
            {
                byte[] payload = [(byte)SshMessageNumber.ChannelData, .. RandomNumberGenerator.GetBytes(i * 31)];
                a.WritePacket(payload);
                await a.FlushAsync();

                SshInboundPacket packet = await b.ReadPacketAsync();
                Assert.AreSequenceEqual(payload, packet.Payload.ToArray(), $"第 {i} 个报文");
            }
        }
    }

    [TestMethod]
    public async Task 批量写入一次刷出()
    {
        // 发送合并是 §7 性能表的第 1 条：SFTP 满管线时 64 次系统调用压成 1 次。
        // 这里验证的是它的**正确性前提** —— 合并之后每一帧仍能被逐个正确取出。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            InstallChaCha(a, b);

            List<byte[]> sent = [];
            for (int i = 0; i < 64; i++)
            {
                byte[] payload = [(byte)SshMessageNumber.ChannelData, .. RandomNumberGenerator.GetBytes(100 + i)];
                sent.Add(payload);
                a.WritePacket(payload);      // 不刷出
            }
            await a.FlushAsync();            // 一次刷出

            for (int i = 0; i < 64; i++)
            {
                SshInboundPacket packet = await b.ReadPacketAsync();
                Assert.AreSequenceEqual(sent[i], packet.Payload.ToArray(), $"第 {i} 帧");
            }

            Assert.AreEqual(64, a.PacketsSent);
            Assert.AreEqual(64, b.PacketsReceived);
        }
    }

    [TestMethod]
    public async Task 序号逐帧递增()
    {
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            Assert.AreEqual(0u, a.SendSequenceNumber);
            Assert.AreEqual(0u, b.ReceiveSequenceNumber);

            for (uint i = 0; i < 10; i++)
            {
                a.WritePacket([(byte)SshMessageNumber.Ignore, (byte)i]);
                await a.FlushAsync();
                _ = await b.ReadPacketAsync();

                Assert.AreEqual(i + 1, a.SendSequenceNumber);
                Assert.AreEqual(i + 1, b.ReceiveSequenceNumber);
            }
        }
    }

    [TestMethod]
    public async Task 分片到达时能正确组帧()
    {
        // 真实网络上一个报文往往分成多段到达。PipeReader 的 AdvanceTo(consumed, examined)
        // 两个参数给错，要么丢数据、要么永远不再等新数据（表现为卡死）——
        // 这条用例把那条路径逼出来：每次只喂 1 字节。
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport receiver = new(peer);

        byte[] frame;
        {
            (InMemoryDuplexStream x, InMemoryDuplexStream y) = InMemoryTransport.CreatePair();
            await using SshPacketTransport sender = new(x);
            await using (y)
            {
                sender.WritePacket([(byte)SshMessageNumber.KexInit, .. RandomNumberGenerator.GetBytes(300)]);
                await sender.FlushAsync();
                byte[] buffer = new byte[4096];
                int n = await y.ReadAsync(buffer);
                frame = buffer[..n];
            }
        }

        Task<SshInboundPacket> pending = receiver.ReadPacketAsync().AsTask();

        await using (raw)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                Assert.IsFalse(pending.IsCompleted, $"喂了 {i} 字节就返回了 —— 不可能凑齐一帧");
                await raw.WriteAsync(frame.AsMemory(i, 1));
            }

            SshInboundPacket packet = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(SshMessageNumber.KexInit, packet.MessageNumber);
            Assert.HasCount(301, packet.Payload);
        }
    }

    [TestMethod]
    public async Task 对端干净关闭时读帧返回EndOfStream()
    {
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (b)
        {
            await a.DisposeAsync();
            SshInboundPacket packet = await b.ReadPacketAsync();
            Assert.IsTrue(packet.IsEndOfStream);
        }
    }

    [TestMethod]
    public async Task 报文中途断开是错误而不是EOF()
    {
        // 「对端在帧中途关闭」与「干净关闭」必须区分 —— 前者是异常收尾，
        // 后者是正常断开，上层的重连决策对它们的处理不同（spec/01 §5）。
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport transport = new(peer);

        await raw.WriteAsync(new byte[] { 0, 0, 1, 0, 5 });   // 声称 256 字节，只给 1 字节
        raw.CompleteWrites();

        await Assert.ThrowsExactlyAsync<SshFrameFormatException>(
            async () => await transport.ReadPacketAsync());
    }

    [TestMethod]
    public async Task 超过上限的报文被拒绝()
    {
        (InMemoryDuplexStream raw, InMemoryDuplexStream peer) = InMemoryTransport.CreatePair();
        await using SshPacketTransport transport = new(peer) { MaxPacketLength = 1024 };
        await using (raw)
        {
            // 声称 16 MiB
            await raw.WriteAsync(new byte[] { 0x01, 0x00, 0x00, 0x00, 5 });
            await Assert.ThrowsExactlyAsync<SshFrameFormatException>(
                async () => await transport.ReadPacketAsync());
        }
    }

    // ------------------------------------------------------------ 密钥切换

    [TestMethod]
    public async Task 收发方向可以各自独立切换密钥()
    {
        // RFC 4253 §7.3：两个方向的 NEWKEYS 互不等待。
        // 把它们合并成一个「切换时刻」是常见错误，症状是快速链路上偶发解密失败。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            byte[] aToB = RandomNumberGenerator.GetBytes(ChaCha20Poly1305CipherSuite.KeyMaterialBytes);

            // a→b 方向换成加密，b→a 仍是明文。
            a.SetSendCipherSuite(new ChaCha20Poly1305CipherSuite(aToB), resetSequenceNumber: false);
            b.SetReceiveCipherSuite(new ChaCha20Poly1305CipherSuite(aToB), resetSequenceNumber: false);

            a.WritePacket([(byte)SshMessageNumber.NewKeys]);
            await a.FlushAsync();
            Assert.AreEqual(SshMessageNumber.NewKeys, (await b.ReadPacketAsync()).MessageNumber);

            // 反方向此刻还是明文，照样通。
            b.WritePacket([(byte)SshMessageNumber.ServiceRequest, 1, 2, 3]);
            await b.FlushAsync();
            Assert.AreEqual(SshMessageNumber.ServiceRequest, (await a.ReadPacketAsync()).MessageNumber);
        }
    }

    [TestMethod]
    public async Task 严格KEX下切换密钥会让序号归零()
    {
        // OpenSSH kex-strict-*-v00@openssh.com，Terrapin（CVE-2023-48795）缓解的一半。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            // 先发几个明文报文把序号推上去
            for (int i = 0; i < 3; i++)
            {
                a.WritePacket([(byte)SshMessageNumber.Ignore]);
                await a.FlushAsync();
                _ = await b.ReadPacketAsync();
            }
            Assert.AreEqual(3u, a.SendSequenceNumber);
            Assert.AreEqual(3u, b.ReceiveSequenceNumber);

            InstallChaCha(a, b, strictKex: true);

            Assert.AreEqual(0u, a.SendSequenceNumber, "严格 KEX 下发送序号必须归零");
            Assert.AreEqual(0u, b.ReceiveSequenceNumber, "严格 KEX 下接收序号必须归零");

            // 归零之后仍然收发正常（两端归零到同一个值才对得上）
            a.WritePacket([(byte)SshMessageNumber.ServiceRequest, 9]);
            await a.FlushAsync();
            Assert.AreEqual(SshMessageNumber.ServiceRequest, (await b.ReadPacketAsync()).MessageNumber);
        }
    }

    [TestMethod]
    public async Task 空载荷报文的消息编号访问会抛出()
    {
        // 载荷为空的报文在协议里不存在。沉默地返回一个假的编号会让错误跑得更远。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            a.WritePacket([]);
            await a.FlushAsync();

            SshInboundPacket packet = await b.ReadPacketAsync();
            Assert.HasCount(0, packet.Payload);
            Assert.ThrowsExactly<SshFrameFormatException>(() => _ = packet.MessageNumber);
        }
    }
}
