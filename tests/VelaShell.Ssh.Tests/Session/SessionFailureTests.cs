// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 会话在「对端不按常理出牌」时的行为：沉默、违规、断开、未知报文。
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §6、§8;velashell-docs/zh/ssh/spec/08-failures.md

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
public sealed class SessionFailureTests
{
    /// <summary>
    /// 一个「手搓报文」的对端：会话跑在一头，另一头由用例逐个报文地读写。
    /// </summary>
    /// <remarks>
    /// 两头都是明文传输 —— 这些用例关心的是会话层的行为，不是密码学。
    /// 手搓的好处是对端可以<b>精确地</b>做错事：一句不回、回一个没人要的应答、
    /// 在任意位置插一个 DISCONNECT。
    /// </remarks>
    private sealed class RawPeer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));

        private RawPeer(SshPacketTransport peer, SshConnection connection)
        {
            Transport = peer;
            Connection = connection;
        }

        public SshPacketTransport Transport { get; }

        public SshConnection Connection { get; }

        public CancellationToken Token => _cts.Token;

        public static RawPeer Start(KeepAlivePolicy? keepAlive = null, SshConnectionLimits? limits = null)
        {
            (InMemoryDuplexStream client, InMemoryDuplexStream server) = InMemoryTransport.CreatePair();
            SshConnection connection = new(new SshPacketTransport(client), new byte[32], limits)
            {
                KeepAlive = keepAlive ?? KeepAlivePolicy.Disabled,
            };
            connection.Start();
            return new RawPeer(new SshPacketTransport(server), connection);
        }

        public async Task SendAsync(params byte[] payload)
        {
            Transport.WritePacket(payload);
            await Transport.FlushAsync(Token);
        }

        public Task SendAsync(SshMessageNumber number)
        {
            byte[] payload = [(byte)number];
            return SendAsync(payload);
        }

        public async Task<byte[]?> ReadAsync()
        {
            SshInboundPacket packet = await Transport.ReadPacketAsync(Token);
            return packet.IsEndOfStream ? null : packet.Payload.ToArray();
        }

        /// <summary>一直读到某个编号的报文；途中的别的报文交给 <paramref name="onOther"/>。</summary>
        public async Task<byte[]> ReadUntilAsync(SshMessageNumber number, Action<byte[]>? onOther = null)
        {
            while (true)
            {
                byte[] payload = await ReadAsync() ?? throw new AssertFailedException($"等 {number} 时连接关了。");
                if ((SshMessageNumber)payload[0] == number)
                {
                    return payload;
                }
                onOther?.Invoke(payload);
            }
        }

        /// <summary>接住客户端的 CHANNEL_OPEN 并确认它。返回客户端给的通道号。</summary>
        public async Task<uint> AcceptChannelOpenAsync(uint ourId = 7, uint window = 1024 * 1024)
        {
            byte[] open = await ReadUntilAsync(SshMessageNumber.ChannelOpen);
            SshDataReader reader = new(new ReadOnlySequence<byte>(open));
            reader.ReadMessageNumber(SshMessageNumber.ChannelOpen);
            _ = reader.ReadUtf8String(1024);
            uint clientId = reader.ReadUInt32();

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteMessageNumber(SshMessageNumber.ChannelOpenConfirmation);
            writer.WriteUInt32(clientId);
            writer.WriteUInt32(ourId);
            writer.WriteUInt32(window);
            writer.WriteUInt32(32 * 1024);
            await SendAsync(buffer.WrittenSpan.ToArray());
            return clientId;
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Transport.DisposeAsync();
            _cts.Dispose();
        }
    }

    private static async Task WaitForDisconnectAsync(SshConnection connection)
    {
        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration _ = connection.Disconnected.Register(() => signalled.TrySetResult());
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------ 保活

    /// <summary>
    /// 半开连接：报文写得进去，应答永远不来。保活<b>必须</b>在有限时间内判死。
    /// </summary>
    /// <remarks>
    /// 这正是保活存在的理由。每次探测不设期限的话，第一次探测的 await 永远不返回，
    /// 「连续 N 次无应答」这个计数永远不会增加 —— 判死逻辑形同虚设。
    /// </remarks>
    [TestMethod]
    public async Task 对端沉默时保活在有限时间内判死()
    {
        await using RawPeer peer = RawPeer.Start(new KeepAlivePolicy(TimeSpan.FromMilliseconds(100), MaxMissed: 2));

        // 对端只读、一句不回。
        int probes = 0;
        Task reading = Task.Run(async () =>
        {
            while (await peer.ReadAsync() is { } payload)
            {
                if ((SshMessageNumber)payload[0] == SshMessageNumber.GlobalRequest)
                {
                    Interlocked.Increment(ref probes);
                }
            }
        });

        await WaitForDisconnectAsync(peer.Connection);

        Assert.IsFalse(peer.Connection.IsAlive);
        Assert.IsGreaterThanOrEqualTo(2, Volatile.Read(ref probes), "判死之前至少要探测 MaxMissed 次");

        SshConnectionClosedException ex = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await peer.Connection.OpenSessionChannelAsync());
        Assert.AreEqual(SshFailureReason.KeepAliveTimeout, ex.Reason);
    }

    /// <summary>应答迟到不算丢：超时的探测留在账本里，迟到的应答落在它身上。</summary>
    [TestMethod]
    public async Task 迟到的保活应答不会让后续全局请求错位()
    {
        await using RawPeer peer = RawPeer.Start(new KeepAlivePolicy(TimeSpan.FromMilliseconds(150), MaxMissed: 10));

        // 对端先憋着第一个保活不回。
        byte[] firstProbe = await peer.ReadUntilAsync(SshMessageNumber.GlobalRequest);
        Assert.IsNotNull(firstProbe);
        await Task.Delay(400);

        // 然后发一个真请求 —— 此时账本里排在前面的是那些没回的保活。
        Task<SshGlobalRequestReply> real = peer.Connection
            .SendGlobalRequestWithReplyAsync("test@example.com", default, wantReply: true, peer.Token).AsTask();

        // 对端按顺序作答：每个保活回 FAILURE，给真请求回一个带载荷的 SUCCESS。
        int pendingProbes = 1;
        while (true)
        {
            byte[] request = await peer.ReadUntilAsync(SshMessageNumber.GlobalRequest);
            SshDataReader reader = new(new ReadOnlySequence<byte>(request));
            reader.ReadMessageNumber(SshMessageNumber.GlobalRequest);
            string type = reader.ReadUtf8String(256);
            if (type == "test@example.com")
            {
                break;
            }
            pendingProbes++;
        }

        for (int i = 0; i < pendingProbes; i++)
        {
            await peer.SendAsync(SshMessageNumber.RequestFailure);
        }
        await peer.SendAsync((byte)SshMessageNumber.RequestSuccess, 0xAB);

        SshGlobalRequestReply reply = await real.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(reply.Success, "真请求的应答被安到了某个保活头上");
        CollectionAssert.AreEqual(new byte[] { 0xAB }, reply.Payload.ToArray());
    }

    // ------------------------------------------------------------ 协议违规与断开

    /// <summary>对端违规时，我们的 DISCONNECT 必须真的发出去 —— 然后才判死。</summary>
    [TestMethod]
    public async Task 协议违规时先发出DISCONNECT再断开()
    {
        await using RawPeer peer = RawPeer.Start();

        // 一个没人要的 REQUEST_SUCCESS：全局请求的 FIFO 失步。
        await peer.SendAsync(SshMessageNumber.RequestSuccess);

        byte[] disconnect = await peer.ReadUntilAsync(SshMessageNumber.Disconnect);
        SshDataReader reader = new(new ReadOnlySequence<byte>(disconnect));
        reader.ReadMessageNumber(SshMessageNumber.Disconnect);
        Assert.AreEqual((uint)SshDisconnectReason.ProtocolError, reader.ReadUInt32());
        StringAssert.Contains(reader.ReadUtf8String(1024), "FIFO");

        await WaitForDisconnectAsync(peer.Connection);
        Assert.IsFalse(peer.Connection.IsAlive);
    }

    [TestMethod]
    public async Task 对端的DISCONNECT带着原因码与原话交出去()
    {
        await using RawPeer peer = RawPeer.Start();

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.Disconnect);
        writer.WriteUInt32((uint)SshDisconnectReason.TooManyConnections);
        writer.WriteUtf8String("Too many sessions");
        writer.WriteUtf8String("");
        await peer.SendAsync(buffer.WrittenSpan.ToArray());

        await WaitForDisconnectAsync(peer.Connection);

        SshConnectionClosedException ex = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await peer.Connection.OpenSessionChannelAsync());
        Assert.AreEqual(SshFailureReason.Disconnected, ex.Reason);
        Assert.AreEqual(SshDisconnectReason.TooManyConnections, ex.DisconnectReason);
        Assert.AreEqual("Too many sessions", ex.PeerDescription);
    }

    /// <summary>会话判死之后，通道的读者要读到结尾，而不是永远挂着。</summary>
    [TestMethod]
    public async Task 会话判死之后通道的读者读到结尾()
    {
        await using RawPeer peer = RawPeer.Start();

        Task<SshChannel> opening = peer.Connection.OpenSessionChannelAsync(cancellationToken: peer.Token).AsTask();
        await peer.AcceptChannelOpenAsync();
        await using SshChannel channel = await opening;

        // 一个明确的违规：窗口回补让计数溢出。
        byte[] adjust = new byte[9];
        adjust[0] = (byte)SshMessageNumber.ChannelWindowAdjust;
        BinaryPrimitives.WriteUInt32BigEndian(adjust.AsSpan(1), channel.LocalId);
        BinaryPrimitives.WriteUInt32BigEndian(adjust.AsSpan(5), uint.MaxValue);
        await peer.SendAsync(adjust);

        ReadResult read = await channel.StandardOutput.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(read.IsCompleted, "会话都判死了，stdout 还没结束");
        Assert.AreEqual(SshChannelState.Closed, channel.State);
    }

    // ------------------------------------------------------------ 未知报文

    /// <summary>UNIMPLEMENTED 里要带被拒那个报文的序号（RFC 4253 §11.4），不是 0。</summary>
    [TestMethod]
    public async Task 未知报文的UNIMPLEMENTED带着它的序号()
    {
        await using RawPeer peer = RawPeer.Start();

        // 对端的第 0、1 个报文是 IGNORE，第 2 个是没人认识的 200。
        await peer.SendAsync((byte)SshMessageNumber.Ignore, 0, 0, 0, 0);
        await peer.SendAsync((byte)SshMessageNumber.Ignore, 0, 0, 0, 0);
        await peer.SendAsync(200);

        byte[] unimplemented = await peer.ReadUntilAsync(SshMessageNumber.Unimplemented);
        Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(unimplemented.AsSpan(1)));
    }

    [TestMethod]
    public async Task 对端的UNIMPLEMENTED不会被回声()
    {
        await using RawPeer peer = RawPeer.Start();

        await peer.SendAsync((byte)SshMessageNumber.Unimplemented, 0, 0, 0, 9);
        await peer.SendAsync(200);

        // 如果 UNIMPLEMENTED 被回声，这里读到的第一个 UNIMPLEMENTED 就是回声（序号 0）。
        byte[] reply = await peer.ReadUntilAsync(SshMessageNumber.Unimplemented);
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(1)));
    }

    // ------------------------------------------------------------ 载荷的生命周期

    /// <summary>
    /// 对端发来的自定义通道请求：事件里的载荷在读到它的时候必须还是原样。
    /// </summary>
    /// <remarks>
    /// 载荷背后是传输的接收缓冲，下一次读包就会被覆盖。
    /// 事件里放的如果是那段内存本身，消费者读到的就是后面某个报文的字节。
    /// </remarks>
    [TestMethod]
    public async Task 通道请求事件的载荷不会被后续报文覆盖()
    {
        await using RawPeer peer = RawPeer.Start();

        Task<SshChannel> opening = peer.Connection.OpenSessionChannelAsync(cancellationToken: peer.Token).AsTask();
        await peer.AcceptChannelOpenAsync();
        await using SshChannel channel = await opening;

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelRequest);
        writer.WriteUInt32(channel.LocalId);
        writer.WriteUtf8String("custom@example.com");
        writer.WriteBoolean(false);
        writer.WriteRaw([1, 2, 3, 4, 5, 6, 7, 8]);
        await peer.SendAsync(buffer.WrittenSpan.ToArray());

        // 再来几个长度相近、内容全是 0xEE 的报文，把接收缓冲刷一遍。
        for (int i = 0; i < 4; i++)
        {
            byte[] junk = new byte[buffer.WrittenCount];
            junk.AsSpan().Fill(0xEE);
            junk[0] = (byte)SshMessageNumber.Ignore;
            await peer.SendAsync(junk);
        }

        // 等接收循环确实处理完了上面这些：发一个要应答的请求，等它的应答。
        ArrayBufferWriter<byte> probe = new();
        SshDataWriter probeWriter = new(probe);
        probeWriter.WriteMessageNumber(SshMessageNumber.GlobalRequest);
        probeWriter.WriteUtf8String("sync@example.com");
        probeWriter.WriteBoolean(true);
        await peer.SendAsync(probe.WrittenSpan.ToArray());
        _ = await peer.ReadUntilAsync(SshMessageNumber.RequestFailure);

        SshChannelEvent channelEvent = await channel.ReadEventAsync(peer.Token);
        SshChannelEvent.PeerRequest request = (SshChannelEvent.PeerRequest)channelEvent;
        Assert.AreEqual("custom@example.com", request.RequestType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, request.Payload.ToArray());
    }

    // ------------------------------------------------------------ 服务端发起的通道

    /// <summary>一个按标签认领通道的处理器：载荷的第一个字节不是自己的标签就拒。</summary>
    private sealed class TaggedHandler(byte tag) : IIncomingChannelHandler
    {
        private int _handled;
        private int _aborted;

        public int Handled => Volatile.Read(ref _handled);

        public int Aborted => Volatile.Read(ref _aborted);

        public ValueTask<SshChannelOptions> GetOptionsAsync(
            string channelType, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken) =>
            typeSpecificPayload.Span[0] == tag
                ? ValueTask.FromResult(SshChannelOptions.Default)
                : throw new InvalidOperationException($"不是 {tag} 的");

        public Task HandleAsync(
            SshChannel channel, ReadOnlyMemory<byte> typeSpecificPayload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _handled);
            return Task.CompletedTask;
        }

        public void OnOpenAborted(string channelType, ReadOnlyMemory<byte> typeSpecificPayload) =>
            Interlocked.Increment(ref _aborted);
    }

    private static byte[] PeerChannelOpen(uint senderChannel, byte tag)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteMessageNumber(SshMessageNumber.ChannelOpen);
        writer.WriteUtf8String("x-test@example.com");
        writer.WriteUInt32(senderChannel);
        writer.WriteUInt32(64 * 1024);
        writer.WriteUInt32(32 * 1024);
        writer.WriteRaw([tag]);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 同一类型挂多个处理器（两个远程转发都接 <c>forwarded-tcpip</c>）：
    /// 各自认领自己的通道，谁也不把谁挤掉；摘一个不影响另一个。
    /// </summary>
    [TestMethod]
    public async Task 同一类型的多个处理器各自认领自己的通道()
    {
        await using RawPeer peer = RawPeer.Start();
        TaggedHandler first = new(1);
        TaggedHandler second = new(2);
        peer.Connection.AddIncomingChannelHandler("x-test@example.com", first);
        peer.Connection.AddIncomingChannelHandler("x-test@example.com", second);

        await peer.SendAsync(PeerChannelOpen(100, tag: 1));
        _ = await peer.ReadUntilAsync(SshMessageNumber.ChannelOpenConfirmation);

        await peer.SendAsync(PeerChannelOpen(101, tag: 2));
        _ = await peer.ReadUntilAsync(SshMessageNumber.ChannelOpenConfirmation);

        // 摘掉第二个之后，第一个照常认领；第二个的通道被明确拒绝。
        peer.Connection.RemoveIncomingChannelHandler("x-test@example.com", second);

        await peer.SendAsync(PeerChannelOpen(102, tag: 1));
        _ = await peer.ReadUntilAsync(SshMessageNumber.ChannelOpenConfirmation);

        await peer.SendAsync(PeerChannelOpen(103, tag: 2));
        _ = await peer.ReadUntilAsync(SshMessageNumber.ChannelOpenFailure);

        for (int i = 0; i < 200 && first.Handled + second.Handled < 3; i++)
        {
            await Task.Delay(10, peer.Token);
        }

        Assert.AreEqual(2, first.Handled);
        Assert.AreEqual(1, second.Handled);
    }

    /// <summary>
    /// 处理器同意了、但本端因为限额没能打开通道：处理器必须收到通知，
    /// 好把在 <c>GetOptionsAsync</c> 里占的槽位还回去。
    /// </summary>
    [TestMethod]
    public async Task 本端限额拒绝时处理器收到OnOpenAborted()
    {
        await using RawPeer peer = RawPeer.Start(limits: new SshConnectionLimits { MaxChannels = 1 });
        TaggedHandler handler = new(1);
        peer.Connection.AddIncomingChannelHandler("x-test@example.com", handler);

        // 先占满唯一的通道位。
        Task<SshChannel> opening = peer.Connection.OpenSessionChannelAsync(cancellationToken: peer.Token).AsTask();
        await peer.AcceptChannelOpenAsync();
        await using SshChannel occupying = await opening;

        await peer.SendAsync(PeerChannelOpen(200, tag: 1));
        byte[] failure = await peer.ReadUntilAsync(SshMessageNumber.ChannelOpenFailure);

        Assert.AreEqual(
            (uint)SshChannelOpenFailureReason.ResourceShortage,
            BinaryPrimitives.ReadUInt32BigEndian(failure.AsSpan(5)));
        Assert.AreEqual(1, handler.Aborted, "处理器没有收到「这条没开成」的通知 —— 它占的槽位会漏掉");
        Assert.AreEqual(0, handler.Handled);
    }

    // ------------------------------------------------------------ 报文上限

    /// <summary>
    /// 认证之后，传输层的报文上限要放宽 —— 否则通道宣告的包上限一过 ~34 KB，
    /// 服务端照着宣告发来的报文就会被当成协议错误。
    /// </summary>
    [TestMethod]
    public async Task 认证之后能收下超过35000字节的报文()
    {
        byte[] payload = new byte[256 * 1024];
        new Random(7).NextBytes(payload);

        await using TestKit.TestSshServerHost host = await TestKit.TestSshServerHost.StartAsync(
            new TestKit.TestChannelScript { StandardOutput = payload, ExitCode = 0 });

        SshExecutionOptions options = new()
        {
            Channel = SshChannelOptions.Default with { ReceiveMaxPacketBytes = 128 * 1024 },
        };

        await using SshCommand command = await host.Connection.ExecuteAsync("cat big", options, host.Token);
        await command.CompleteStandardInputAsync(host.Token);

        using MemoryStream received = new();
        while (true)
        {
            ReadResult read = await command.StandardOutput.ReadAsync(host.Token);
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
            {
                received.Write(segment.Span);
            }
            command.StandardOutput.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        CollectionAssert.AreEqual(payload, received.ToArray());
        Assert.IsTrue(host.Connection.IsAlive);
    }

    [TestMethod]
    public async Task 通道宣告的包上限超过传输层上限时当场拒绝()
    {
        await using RawPeer peer = RawPeer.Start();

        SshChannelOptions options = SshChannelOptions.Default with { ReceiveMaxPacketBytes = 64 * 1024 };
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await peer.Connection.OpenSessionChannelAsync(options));
    }

    // ------------------------------------------------------------ 窗口

    /// <summary>
    /// 消费者读了开头就 <c>Complete</c>：管道里剩下的字节也要回补窗口，
    /// 否则对端停在一个再也不会涨的窗口上。
    /// </summary>
    [TestMethod]
    public async Task 消费者提前收尾时剩下的字节照样回补窗口()
    {
        await using RawPeer peer = RawPeer.Start();

        const int Window = SshWindowPolicy.AbsoluteMinimumBytes;
        SshChannelOptions options = SshChannelOptions.Default with { WindowPolicy = SshWindowPolicy.Fixed(Window) };

        Task<SshChannel> opening = peer.Connection
            .OpenSessionChannelAsync(options, peer.Token).AsTask();
        uint clientId = await peer.AcceptChannelOpenAsync();
        await using SshChannel channel = await opening;

        // 对端把整个窗口发满。
        byte[] data = new byte[9 + Window];
        data[0] = (byte)SshMessageNumber.ChannelData;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1), clientId);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(5), Window);
        await peer.SendAsync(data);

        // 消费者只读一点点就不要了。
        ReadResult read = await channel.StandardOutput.ReadAsync(peer.Token);
        channel.StandardOutput.AdvanceTo(read.Buffer.GetPosition(Math.Min(10, read.Buffer.Length)));
        await channel.StandardOutput.CompleteAsync();

        byte[] adjust = await peer.ReadUntilAsync(SshMessageNumber.ChannelWindowAdjust);
        Assert.IsGreaterThanOrEqualTo(
            (uint)(Window / 2), BinaryPrimitives.ReadUInt32BigEndian(adjust.AsSpan(5)),
            "没读的那部分没有回补");
    }
}
