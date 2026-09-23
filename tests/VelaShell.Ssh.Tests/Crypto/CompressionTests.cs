// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 4253 §6.2;OpenSSH PROTOCOL 的 zlib@openssh.com

using System.Buffers;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Crypto;

[TestClass]
[TestCategory("Crypto")]
public sealed class CompressionTests
{
    private static byte[] RoundTrip(ISshCompressor sender, ISshCompressor receiver, byte[] payload)
    {
        ArrayBufferWriter<byte> compressed = new();
        sender.Compress(payload, compressed);

        ArrayBufferWriter<byte> decompressed = new();
        receiver.Decompress(
            new ReadOnlySequence<byte>(compressed.WrittenMemory), decompressed, 16 * 1024 * 1024);

        return decompressed.WrittenSpan.ToArray();
    }

    [TestMethod]
    public void 压了能解回来()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("重复的内容 ", 200)));
        byte[] back = RoundTrip(sender, receiver, payload);

        CollectionAssert.AreEqual(payload, back);
    }

    [TestMethod]
    public void 字典跨报文保留所以后面的报文越压越小()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] payload = Encoding.UTF8.GetBytes("GET /api/status HTTP/1.1\r\nHost: example.com\r\n\r\n");

        int firstSize = 0;
        int lastSize = 0;

        for (int i = 0; i < 10; i++)
        {
            ArrayBufferWriter<byte> compressed = new();
            sender.Compress(payload, compressed);

            // ⚠️ 收方**每一个报文都要跟着解**，两边的字典才对得上。
            //    漏解一个，后面所有报文在收方看来都是从流中间开始的 ——
            //    zlib 会直接报「unknown compression method」。
            ArrayBufferWriter<byte> sink = new();
            receiver.Decompress(new ReadOnlySequence<byte>(compressed.WrittenMemory), sink, 1 << 20);
            CollectionAssert.AreEqual(payload, sink.WrittenSpan.ToArray(), $"第 {i} 个");

            if (i == 0)
            {
                firstSize = compressed.WrittenCount;
            }
            lastSize = compressed.WrittenCount;
        }

        // **这就是压缩率的全部来源。**每个报文各压各的话，
        // SSH 那种几十字节的小报文几乎压不动 —— 共用一本字典才有意义。
        Assert.IsTrue(
            lastSize < firstSize / 2,
            $"第 10 个相同报文应当明显更小：首个 {firstSize} 字节，第 10 个 {lastSize} 字节");
    }

    [TestMethod]
    public void 连续多个不同报文都能对上()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        for (int i = 0; i < 50; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"第 {i} 个报文，内容长度不一 {new string('x', i * 7)}");
            byte[] back = RoundTrip(sender, receiver, payload);

            // 一个报文对不上，后面全乱 —— 字典是跨报文的。
            CollectionAssert.AreEqual(payload, back, $"第 {i} 个");
        }
    }

    [TestMethod]
    public void 空载荷也能往返()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] back = RoundTrip(sender, receiver, []);
        Assert.AreEqual(0, back.Length);
    }

    [TestMethod]
    public void 大载荷能往返()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);   // 随机数据压不动，走的是「压完更长」那条路

        byte[] back = RoundTrip(sender, receiver, payload);
        CollectionAssert.AreEqual(payload, back);
    }

    [TestMethod]
    public void 压缩炸弹被上限挡住()
    {
        using ZlibCompressor sender = new();
        using ZlibCompressor receiver = new();

        // 4 MiB 的零 —— 压完只有几 KiB。
        byte[] bomb = new byte[4 * 1024 * 1024];

        ArrayBufferWriter<byte> compressed = new();
        sender.Compress(bomb, compressed);

        Assert.IsTrue(compressed.WrittenCount < 64 * 1024, "前提：它确实压得很小");

        ArrayBufferWriter<byte> output = new();

        // 上限要在**写出之前**检查 —— 等写完再看就已经把内存吃掉了。
        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => receiver.Decompress(
                new ReadOnlySequence<byte>(compressed.WrittenMemory), output, maxOutputLength: 64 * 1024));

        StringAssert.Contains(error.Message, "压缩炸弹");
    }

    [TestMethod]
    public void 直通压缩器什么都不做()
    {
        NoCompression none = NoCompression.Instance;
        Assert.IsFalse(none.IsActive);

        byte[] payload = Encoding.UTF8.GetBytes("原样");
        byte[] back = RoundTrip(none, none, payload);

        CollectionAssert.AreEqual(payload, back);
    }

    [TestMethod]
    public void 算法名的分类()
    {
        Assert.IsTrue(SshCompressorFactory.IsCompression(SshAlgorithmNames.Zlib));
        Assert.IsTrue(SshCompressorFactory.IsCompression(SshAlgorithmNames.ZlibOpenSsh));
        Assert.IsFalse(SshCompressorFactory.IsCompression(SshAlgorithmNames.None));

        // zlib@openssh.com 推迟到认证之后；裸 zlib 立即生效。
        Assert.IsTrue(SshCompressorFactory.IsDelayed(SshAlgorithmNames.ZlibOpenSsh));
        Assert.IsFalse(SshCompressorFactory.IsDelayed(SshAlgorithmNames.Zlib));

        Assert.IsInstanceOfType<ZlibCompressor>(
            SshCompressorFactory.Create(SshAlgorithmNames.ZlibOpenSsh));
        Assert.IsInstanceOfType<NoCompression>(
            SshCompressorFactory.Create(SshAlgorithmNames.None));
    }

    [TestMethod]
    public void 算法清单里能打开压缩()
    {
        SshAlgorithmSet withCompression = SshAlgorithmSet.Default.WithCompression();

        CollectionAssert.Contains(
            withCompression.CompressionClientToServer.ToArray(), SshAlgorithmNames.ZlibOpenSsh);
        CollectionAssert.Contains(
            withCompression.CompressionClientToServer.ToArray(), SshAlgorithmNames.None,
            "none 要留在清单里 —— 对端不支持压缩时还得能连上");

        // 默认**不开**压缩：交互式会话上它几乎没有收益（终端输出本来就小），
        // 而 OpenSSH 的默认也是 none。
        CollectionAssert.AreEqual(
            new[] { SshAlgorithmNames.None },
            SshAlgorithmSet.Default.CompressionClientToServer.ToArray());
    }

    // ------------------------------------------------------------ 接进传输层

    [TestMethod]
    public async Task 传输层挂上压缩之后报文照样对得上()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                PauseWriterThreshold = 4 * 1024 * 1024,
                ResumeWriterThreshold = 2 * 1024 * 1024,
            });

        await using SshPacketTransport client = new(clientStream);
        await using SshPacketTransport server = new(serverStream);

        // 两个方向各挂一对 —— 客户端的发送压缩器要对上服务端的接收解压器。
        client.SetSendCompressor(new ZlibCompressor());
        server.SetReceiveCompressor(new ZlibCompressor());
        server.SetSendCompressor(new ZlibCompressor());
        client.SetReceiveCompressor(new ZlibCompressor());

        for (int i = 0; i < 20; i++)
        {
            byte[] up = Encoding.UTF8.GetBytes($"客户端第 {i} 条：{new string('a', 500)}");
            client.WritePacket(up);
            await client.FlushAsync();

            SshInboundPacket received = await server.ReadPacketAsync();
            CollectionAssert.AreEqual(up, received.Payload.ToArray(), $"上行第 {i} 条");

            byte[] down = Encoding.UTF8.GetBytes($"服务端第 {i} 条：{new string('b', 700)}");
            server.WritePacket(down);
            await server.FlushAsync();

            SshInboundPacket back = await client.ReadPacketAsync();
            CollectionAssert.AreEqual(down, back.Payload.ToArray(), $"下行第 {i} 条");
        }
    }

    [TestMethod]
    public async Task 只压一个方向也能跑()
    {
        // 两个方向是**各自独立协商**的 —— 一边压一边不压是合法的。
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair();

        await using SshPacketTransport client = new(clientStream);
        await using SshPacketTransport server = new(serverStream);

        client.SetSendCompressor(new ZlibCompressor());
        server.SetReceiveCompressor(new ZlibCompressor());
        // 下行方向不压。

        byte[] up = Encoding.UTF8.GetBytes("压过的上行");
        client.WritePacket(up);
        await client.FlushAsync();
        CollectionAssert.AreEqual(up, (await server.ReadPacketAsync()).Payload.ToArray());

        byte[] down = Encoding.UTF8.GetBytes("没压的下行");
        server.WritePacket(down);
        await server.FlushAsync();
        CollectionAssert.AreEqual(down, (await client.ReadPacketAsync()).Payload.ToArray());
    }

    [TestMethod]
    public async Task 压缩确实让线上字节变少()
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("可压缩的日志行\n", 500)));

        long withoutCompression = await MeasureWireBytesAsync(payload, compress: false);
        long withCompression = await MeasureWireBytesAsync(payload, compress: true);

        Assert.IsTrue(
            withCompression < withoutCompression / 2,
            $"压过之后线上字节应当明显更少：{withCompression} vs {withoutCompression}");
    }

    private static async Task<long> MeasureWireBytesAsync(byte[] payload, bool compress)
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                PauseWriterThreshold = 8 * 1024 * 1024,
                ResumeWriterThreshold = 4 * 1024 * 1024,
            });

        await using SshPacketTransport client = new(clientStream);
        await using SshPacketTransport server = new(serverStream);

        if (compress)
        {
            client.SetSendCompressor(new ZlibCompressor());
            server.SetReceiveCompressor(new ZlibCompressor());
        }

        client.WritePacket(payload);
        await client.FlushAsync();

        SshInboundPacket received = await server.ReadPacketAsync();
        CollectionAssert.AreEqual(payload, received.Payload.ToArray(), "压缩不能改变内容");

        return server.BytesReceived;
    }

    [TestMethod]
    public async Task 整条连接开着压缩也能跑通()
    {
        // 前面那些用例都是手搭传输。这一条走**完整的连接建立流程**，
        // 因为「认证成功之后才挂压缩」这个切换点只有在真流程里才存在，
        // 而切换点错一个报文，症状就是连上之后第一个报文解不开。
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("可压缩的一行\n", 100)));

        await using TestKit.TestSshServerHost host = await TestKit.TestSshServerHost.StartAsync(
            new TestKit.TestChannelScript { StandardOutput = payload, ExitCode = 0 },
            SshAlgorithmSet.Default.WithCompression());

        Assert.AreEqual(
            SshAlgorithmNames.ZlibOpenSsh,
            host.Connection.Algorithms!.Value.CompressionServerToClient);

        VelaShell.Ssh.Session.SshCommandOutput output =
            await VelaShell.Ssh.Session.SshConnectionExtensions.RunAsync(host.Connection, "压", cancellationToken: host.Token);

        CollectionAssert.AreEqual(
            payload, System.Text.Encoding.UTF8.GetBytes(output.StandardOutput),
            "整条连接上压缩要能原样往返");
    }

    /// <summary>
    /// 普通 <c>zlib</c>（不延迟）从首次 <c>NEWKEYS</c> 起就压 —— 认证报文也在压缩流里。
    /// </summary>
    /// <remarks>
    /// 以前两种一律在认证之后才装：谈成普通 zlib 时，服务端从 NEWKEYS 起就在压，
    /// 我们却按明文发认证请求，两端在第一个认证报文上就错位了。
    /// </remarks>
    [TestMethod]
    public async Task 普通zlib从首次NEWKEYS起就压()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("普通 zlib\n", 100)));

        SshAlgorithmSet plainZlib = SshAlgorithmSet.Default with
        {
            CompressionClientToServer = [SshAlgorithmNames.Zlib],
            CompressionServerToClient = [SshAlgorithmNames.Zlib],
        };

        await using TestKit.TestSshServerHost host = await TestKit.TestSshServerHost.StartAsync(
            new TestKit.TestChannelScript { StandardOutput = payload, ExitCode = 0 },
            plainZlib);

        Assert.AreEqual(SshAlgorithmNames.Zlib, host.Connection.Algorithms!.Value.CompressionClientToServer);

        VelaShell.Ssh.Session.SshCommandOutput output =
            await VelaShell.Ssh.Session.SshConnectionExtensions.RunAsync(host.Connection, "压", cancellationToken: host.Token);

        CollectionAssert.AreEqual(payload, System.Text.Encoding.UTF8.GetBytes(output.StandardOutput));
    }
}
