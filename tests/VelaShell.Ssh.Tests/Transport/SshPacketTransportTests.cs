// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/01-transport-framing.md §3（收包）、§4（发包与合并、释放）
//           velashell-docs/zh/ssh/spec/02-version-exchange.md（行式 IO 与互操作）
//
// 这一层跑在**真实的双工管道**上（InMemoryTransport），不是对着假对象断言 ——
// 所以它同时验证了「分帧 + Pipelines 的 AdvanceTo 用法」这条最容易卡死的路径。

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
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

    // ------------------------------------------------------------ 序号回绕的最后一道保险

    /// <summary>
    /// 同一套密钥下报文数用完（重协商没能完成）：宁可断开，也不让序号回绕 ——
    /// chacha20-poly1305 的 nonce 就是序号，回绕就是 nonce 重用。换了密钥就重新计数。
    /// </summary>
    [TestMethod]
    public async Task 同一套密钥下报文数用完时拒绝再收发_换钥之后重新计数()
    {
        (InMemoryDuplexStream rawA, InMemoryDuplexStream rawB) = InMemoryTransport.CreatePair();
        await using SshPacketTransport a = new(rawA) { MaxPacketsPerKey = 2 };
        await using SshPacketTransport b = new(rawB) { MaxPacketsPerKey = 2 };
        InstallChaCha(a, b);

        byte[] payload = [(byte)SshMessageNumber.Ignore, 1, 2, 3];
        a.WritePacket(payload);
        a.WritePacket(payload);
        Assert.ThrowsExactly<SshFrameFormatException>(() => a.WritePacket(payload), "发送方向没拦住第三个");
        await a.FlushAsync();

        await b.ReadPacketAsync();
        await b.ReadPacketAsync();

        // 换钥（NEWKEYS 之后）：计数归零，又能发了。
        InstallChaCha(a, b);
        a.WritePacket(payload);
        await a.FlushAsync();
        Assert.IsFalse((await b.ReadPacketAsync()).IsEndOfStream);
        Assert.AreEqual(1, b.ReceivePacketsUnderKey);
    }

    [TestMethod]
    public async Task 接收方向同一套密钥下报文数用完时拒绝再收()
    {
        (InMemoryDuplexStream rawA, InMemoryDuplexStream rawB) = InMemoryTransport.CreatePair();
        await using SshPacketTransport a = new(rawA);
        await using SshPacketTransport b = new(rawB) { MaxPacketsPerKey = 1 };
        InstallChaCha(a, b);

        byte[] payload = [(byte)SshMessageNumber.Ignore, 9];
        a.WritePacket(payload);
        a.WritePacket(payload);
        await a.FlushAsync();

        await b.ReadPacketAsync();
        await Assert.ThrowsExactlyAsync<SshFrameFormatException>(async () => await b.ReadPacketAsync());
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
    public async Task 解压失败时报出真正的原因()
    {
        // 能在一帧里发出的 128 KiB 零：解压要撞上报文上限（压缩炸弹）。
        // 曾经解压失败之后 reader 被 AdvanceTo 两次，真正的原因被一个 InvalidOperationException 盖掉。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            a.SetSendCompressor(new ZlibCompressor());
            b.SetReceiveCompressor(new ZlibCompressor());

            a.WritePacket(new byte[128 * 1024]);
            await a.FlushAsync();

            SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
                async () => await b.ReadPacketAsync());
            Assert.Contains("压缩炸弹", error.Message);
        }
    }

    [TestMethod]
    public async Task 一次flush被取消之后释放不会再去写()
    {
        // 对端不读了（TCP 零窗口、半开的链路）：flush 卡住、被调用方的期限取消。
        // 释放时要是把剩下的字节再刷一遍，就是在释放路径上等一个永远不来的对端。
        await using SshPacketTransport transport = new(new NeverWritableStream());

        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));
        transport.WritePacket([2, 1, 2, 3]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await transport.FlushAsync(cancel.Token));

        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>写永远完不成（只有令牌取消时才返回）的流 —— 对端不读的样子。</summary>
    private sealed class NeverWritableStream : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------ 释放时还有读写在途（spec/01 §4「释放」）

    [TestMethod]
    public async Task 释放时还停在读里_读缓冲等那次读退出了才还回内存池()
    {
        // 宿主 CI 上一条握手用例读到被截短的 KEXINIT：上一条用例的服务端还停在读里，传输就被释放、读缓冲还回了池，
        // 那次读随后把数据写进了下一条用例刚租到的同一个数组。
        GatedStream stream = new();
        SshPacketTransport transport = new(stream, ownsStream: false);
        Task<SshInboundPacket> pending = transport.ReadPacketAsync().AsTask();
        byte[] handedOut = await stream.ReadBuffer.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ValueTask dispose = transport.DisposeAsync();
        Assert.IsTrue(dispose.IsCompleted, "释放不该等一个不理会取消的读");
        await dispose;

        // 紧接着在同一个线程上租同样大小的数组：曾经租到的正是那次读手里的那一个。
        byte[] rented = ArrayPool<byte>.Shared.Rent(handedOut.Length);
        try
        {
            Assert.AreNotSame(handedOut, rented, "在途的读还攥着的缓冲不能先还回池里");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        // 那次读这时才拿到数据：它照常结束（这几个字节不是合法的报文），结束时由它收尾。
        stream.ReleaseRead([0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0]);
        await Assert.ThrowsExactlyAsync<SshFrameFormatException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await transport.ReadPacketAsync());
    }

    [TestMethod]
    public async Task 释放时还在刷出_写缓冲等那次刷出退出了才还回内存池()
    {
        // 写端同理：刷到一半时还回去，底层流照样从里面读 —— 发出去的就是别人的数据。
        GatedStream stream = new();
        SshPacketTransport transport = new(stream, ownsStream: false);
        transport.WritePacket([2, 1, 2, 3]);
        Task<System.IO.Pipelines.FlushResult> flush = transport.FlushAsync().AsTask();
        byte[] handedOut = await stream.WriteBuffer.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ValueTask dispose = transport.DisposeAsync();
        Assert.IsTrue(dispose.IsCompleted, "释放不该等一次不理会取消的刷出");
        await dispose;

        byte[] rented = ArrayPool<byte>.Shared.Rent(handedOut.Length);
        try
        {
            Assert.AreNotSame(handedOut, rented, "在途的刷出还攥着的缓冲不能先还回池里");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        stream.ReleaseWrite();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => transport.WritePacket([2]));
    }

    [TestMethod]
    public async Task 释放时还停在读里_接收套件等那次读退出了才释放()
    {
        // 当场释放接收套件（清零密钥），在途的那次解密就会算出一个假的 MAC 错误。
        GatedStream stream = new();
        SshPacketTransport transport = new(stream, ownsStream: false);
        DisposalTrackingSuite suite = new();
        transport.SetReceiveCipherSuite(suite, resetSequenceNumber: false);
        Task<SshInboundPacket> pending = transport.ReadPacketAsync().AsTask();
        await stream.ReadBuffer.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await transport.DisposeAsync();
        Assert.IsFalse(suite.IsDisposed, "读还在途，接收套件不能先释放");

        ArrayBufferWriter<byte> frame = new();
        using (PlaintextCipherSuite sealer = new())
        {
            sealer.Seal([2, 1, 2, 3], sequenceNumber: 0, frame);
        }
        stream.ReleaseRead(frame.WrittenSpan.ToArray());
        SshInboundPacket packet = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSequenceEqual(new byte[] { 2, 1, 2, 3 }, packet.Payload.ToArray(), "已经到达的那一帧照常交出");
        Assert.IsTrue(suite.IsDisposed, "那次读退出时收尾");
    }

    [TestMethod]
    public async Task 释放时还停在读里_理会取消的流上那次读以已释放退出()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using (a)
        {
            SshPacketTransport transport = new(b);
            ValueTask<SshInboundPacket> pending = transport.ReadPacketAsync();
            Assert.IsFalse(pending.IsCompleted, "还没有数据，读应当挂着");

            await transport.DisposeAsync();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    /// <summary>
    /// 读写都先把手里的缓冲交出来、等放行再动它 —— 把「底层流已经拿到缓冲、还没用完」这段时间拉长。
    /// 不理会取消（Windows 上 ProxyCommand 的匿名管道就是这样），释放只能等它自己结束。
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource<byte[]> _readData = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>读的时候交出来的缓冲所在的数组。</summary>
        public TaskCompletionSource<byte[]> ReadBuffer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>写的时候交出来的缓冲所在的数组。</summary>
        public TaskCompletionSource<byte[]> WriteBuffer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseRead(byte[] data) => _readData.TrySetResult(data);

        public void ReleaseWrite() => _writeRelease.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadBuffer.TrySetResult(ArrayOf(buffer));
            byte[] data = await _readData.Task;
            data.CopyTo(buffer.Span);
            return data.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteBuffer.TrySetResult(ArrayOf(buffer));
            await _writeRelease.Task;
        }

        private static byte[] ArrayOf(ReadOnlyMemory<byte> memory) =>
            MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment)
                ? segment.Array!
                : throw new InvalidOperationException("缓冲不在数组里，这条用例比较不了。");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>明文套件，外加记下自己有没有被释放。</summary>
    private sealed class DisposalTrackingSuite : ISshCipherSuite
    {
        private readonly PlaintextCipherSuite _inner = new();

        public bool IsDisposed { get; private set; }

        public CipherSuiteShape Shape => _inner.Shape;

        public void Seal(ReadOnlySpan<byte> payload, uint sequenceNumber, IBufferWriter<byte> output) =>
            _inner.Seal(payload, sequenceNumber, output);

        public SshOpenStatus TryOpen(
            ReadOnlySequence<byte> input, uint sequenceNumber, int maxPacketLength, IBufferWriter<byte> payload, out long consumed) =>
            _inner.TryOpen(input, sequenceNumber, maxPacketLength, payload, out consumed);

        public void Dispose() => IsDisposed = true;
    }

    [TestMethod]
    public async Task 空载荷报文在帧层就被拒收()
    {
        // 载荷为空的报文在协议里不存在（spec/01 §5）。沉默地返回一个假的编号会让错误跑得更远；
        // 曾经是交出去、等上层取消息编号时才抛 —— 握手期那一下没人接，漏出了 ConnectAsync。
        (SshPacketTransport a, SshPacketTransport b) = CreatePair();
        await using (a)
        await using (b)
        {
            a.WritePacket([]);
            await a.FlushAsync();

            await Assert.ThrowsExactlyAsync<SshFrameFormatException>(async () => await b.ReadPacketAsync());
        }
    }
}
