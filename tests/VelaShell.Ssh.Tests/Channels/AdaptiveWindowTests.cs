// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §3.3
//
// 这一组是**链路特征模拟**唯一存在的理由：
// 在一条零延迟的内存链路上，窗口是 32 KiB 还是 64 MiB 跑出来一样快，
// 「自适应」那段代码没有任何东西可验证。加上时延之后它才有意义。

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Channels;

[TestClass]
[TestCategory("Channels")]
public sealed class AdaptiveWindowTests
{
    /// <summary>在一条带时延的链路上跑完整会话。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly TestChannelServer _channelServer;
        private readonly Task _serverChannels;
        private readonly CancellationTokenSource _cts;

        private Harness(
            TestSshServer server,
            TestChannelServer channelServer,
            Task serverChannels,
            SshConnection connection,
            CancellationTokenSource cts)
        {
            _server = server;
            _channelServer = channelServer;
            _serverChannels = serverChannels;
            Connection = connection;
            _cts = cts;
        }

        public SshConnection Connection { get; }

        public TestChannelObservation Observed => _channelServer.Observation;

        public CancellationToken Token => _cts.Token;

        public static async Task<Harness> StartAsync(
            LinkCharacteristics link, TestChannelScript script, SshConnectionLimits? limits = null)
        {
            (InMemoryDuplexStream rawClient, InMemoryDuplexStream rawServer) =
                InMemoryTransport.CreatePair(new InMemoryTransportOptions
                {
                    // 水位要高于任何一个测试用的窗口，不然管道自己成了瓶颈，
                    // 测出来的就不是 SSH 窗口的效果了。
                    PauseWriterThreshold = 8 * 1024 * 1024,
                    ResumeWriterThreshold = 4 * 1024 * 1024,
                });

            // 只给客户端这一侧套时延 —— 两侧都套的话 RTT 会翻倍，
            // 而我们要的是「一个方向 OneWayLatency」这个语义。
            DelayedStream clientStream = new(rawClient, link);

            TestSshServer server = new(rawServer);
            SshPacketTransport clientTransport = new(clientStream);
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));

            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);
            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
            SshKeyExchangeRunner runner = new(
                clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex =
                await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
            TestSshServerHandshake handshake = await serverHandshake;

            TestAuthServer authServer = new(
                server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "p" });
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);

            SshAuthenticator authenticator = new(clientTransport, "joe", kex.SessionId);
            await authenticator.AuthenticateAsync([new PasswordCredential("p")], cts.Token);
            await serverAuth;

            TestChannelServer channelServer = new(server.Transport, script);
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex.SessionId, limits);
            connection.Start();

            return new Harness(server, channelServer, serverChannels, connection, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Connection.DisposeAsync();
            try
            {
                await _serverChannels;
            }
            catch (Exception)
            {
                // 收尾时被取消是预期的。
            }
            _channelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>一次测量的结果。</summary>
    /// <param name="Bytes">实际收到的字节数。</param>
    /// <param name="FinalWindow">结束时窗口的额定大小。</param>
    /// <param name="WindowAdjusts">
    /// 服务端收到了几次 <c>WINDOW_ADJUST</c>。
    /// <b>这就是往返次数</b> —— 每一次回补都意味着对端先停下来等了一个 RTT。
    /// </param>
    private readonly record struct Measurement(long Bytes, int FinalWindow, int WindowAdjusts);

    private static async Task<Measurement> MeasureAsync(
        LinkCharacteristics link,
        SshWindowPolicy policy,
        int payloadSize,
        SshConnectionLimits? limits = null,
        TimeSpan consumePause = default)
    {
        byte[] payload = new byte[payloadSize];

        await using Harness harness = await Harness.StartAsync(
            link,
            new TestChannelScript
            {
                StandardOutput = payload,
                ExitCode = 0,
                MaxPacket = 32 * 1024,
            },
            limits);

        SshExecutionOptions options = new()
        {
            Channel = SshChannelOptions.Default with { WindowPolicy = policy },
        };

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("大量输出", options, harness.Token);

        long total = 0;
        PipeReader reader = command.StandardOutput;

        while (true)
        {
            ReadResult read = await reader.ReadAsync(harness.Token);
            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.IsEmpty && read.IsCompleted)
            {
                break;
            }

            // ⚠️ **每轮只消费固定的一块，不是 buffer.End。**
            //
            // 窗口回补泵是按「消费了多少」触发的，所以**消费的粒度直接决定
            // WINDOW_ADJUST 的条数**。而一次 ReadAsync 能拿到多少，取决于
            // 这一刻恰好有多少数据到了 —— 机器一忙，几个包并成一次读，
            // 回补就少发几条。那样测出来的条数是调度噪声，不是窗口策略。
            //
            // 固定成 8 KiB 之后，回补条数只由「窗口多大」决定，
            // 这才是这个用例真正想比的东西。
            long take = Math.Min(buffer.Length, ConsumeChunk);
            SequencePosition consumed = buffer.GetPosition(take);
            total += take;

            // examined 必须等于 consumed：写成 buffer.End 等于说
            // 「剩下的我也看过了，没新数据别叫我」—— 剩余数据就再也读不出来。
            reader.AdvanceTo(consumed, consumed);

            if (total >= payloadSize)
            {
                break;
            }

            if (consumePause > TimeSpan.Zero)
            {
                await Task.Delay(consumePause, harness.Token);
            }
        }

        int finalWindow = command.Channel.ReceiveWindowSize;

        // 最后几条回补还在链路上飞（单向 20 ms）。此刻就去读计数器，
        // 读到的是「到目前为止飞到的条数」—— 那是一个随机数。
        int adjusts = await SettleAsync(harness, link);

        await reader.CompleteAsync();
        return new Measurement(total, finalWindow, adjusts);
    }

    /// <summary>每轮消费的固定块大小。见 <see cref="MeasureAsync"/> 里的说明。</summary>
    private const int ConsumeChunk = 8 * 1024;

    /// <summary>等到服务端不再收到新的回补为止，再读计数器。</summary>
    /// <remarks>
    /// 这不是「等够久就算过」的性能断言 —— 它等的是**一个确定会发生的终止事件**：
    /// 数据收完了，就不会再有新的回补。所以超时只是防挂死的兜底，
    /// 正常路径上它几轮就稳定了。
    /// </remarks>
    private static async Task<int> SettleAsync(Harness harness, LinkCharacteristics link)
    {
        TimeSpan step = link.RoundTrip + TimeSpan.FromMilliseconds(20);
        int last = -1;
        int stableRounds = 0;

        for (int i = 0; i < 40 && stableRounds < 2; i++)
        {
            await Task.Delay(step, harness.Token);
            int now = harness.Observed.WindowAdjustCount;
            stableRounds = now == last ? stableRounds + 1 : 0;
            last = now;
        }

        return last;
    }

    // ------------------------------------------------------------ 链路模拟本身

    [TestMethod]
    public async Task 时延确实被模拟出来了()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using DelayedStream delayed = new(a, new LinkCharacteristics(TimeSpan.FromMilliseconds(50)));

        // 时延是「另一端多久之后看见」，不是「写要等多久」—— 写入方早就返回了。
        long start = Stopwatch.GetTimestamp();
        await delayed.WriteAsync(new byte[16]);
        await ReadExactlyAsync(b, 16);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40), elapsed,
            $"单向时延 50 ms 的链路上，数据至少 ~50 ms 之后才到另一端，实际 {elapsed.TotalMilliseconds:0} ms");

        await b.DisposeAsync();
    }

    [TestMethod]
    public async Task 时延是流水线式的不按写串起来()
    {
        // 回归用例。曾经每次写都在写锁里整段睡掉单向时延：20 次写要 20 × 100 ms = 2 s 才全部到达，
        // 链路上任一时刻只有一次写在飞 —— 吞吐被压在「单次写的大小 / 时延」，
        // 窗口与管线深度的测试量到的一部分其实是这个模拟器自己造的瓶颈。
        // 流水线之下它们前后脚发出、前后脚到达：总共约一个单向时延。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        await using DelayedStream delayed = new(a, new LinkCharacteristics(TimeSpan.FromMilliseconds(100)));

        const int writes = 20;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < writes; i++)
        {
            await delayed.WriteAsync(new byte[16]);
        }
        await ReadExactlyAsync(b, writes * 16);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        // 上界留了 10 倍于期望值（~100 ms）的余量，串行模型（2 s）仍然过不了它。
        Assert.IsLessThan(TimeSpan.FromMilliseconds(1000), elapsed,
            $"{writes} 次写应当一起在途、约 100 ms 后全部到达，实际 {elapsed.TotalMilliseconds:0} ms");

        await b.DisposeAsync();
    }

    [TestMethod]
    public async Task 关流前已写出的数据照常送达()
    {
        // 写返回就意味着「已发出」。关流时把在途的丢掉，另一端就收不到最后那几条消息（比如 DISCONNECT）。
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair();
        DelayedStream delayed = new(a, new LinkCharacteristics(TimeSpan.FromMilliseconds(50)));

        await delayed.WriteAsync(new byte[] { 1, 2, 3 });
        await delayed.DisposeAsync();

        byte[] received = await ReadExactlyAsync(b, 3);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, received);

        byte[] tail = new byte[1];
        Assert.AreEqual(0, await b.ReadAsync(tail), "送达之后另一端应当看到流结束");

        await b.DisposeAsync();
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    [TestMethod]
    public async Task 带宽确实被限住了()
    {
        (InMemoryDuplexStream a, InMemoryDuplexStream b) = InMemoryTransport.CreatePair(
            new InMemoryTransportOptions
            {
                PauseWriterThreshold = 4 * 1024 * 1024,
                ResumeWriterThreshold = 2 * 1024 * 1024,
            });

        // ⚠️ **限速器等的是「上一次的额度」，所以 N 次写只会等 N-1 次。**
        //
        // `ThrottleAsync` 先等到额度允许、再把这次的开销记进桶里 ——
        // 最后一次写把开销记上了，但没有人会再为它等。
        // 于是 N 次 50 KB 的写在 1 MB/s 上测出来是 (N-1) × 50 ms，**不是 N × 50 ms**。
        //
        // 原先这里写 4 次、断言 ≥150 ms —— 期望值正好就是 150 ms，
        // 断言**压在边界上**，量到 149 ms 就红。它在二十轮里红过一次。
        // 边界上的断言不是「偶尔运气差」，是写错了：
        // 现在按真实模型取 5 次（期望 200 ms），阈值留 25% 余量。
        //
        // 只断下界，不断上界 —— 机器忙的时候上界必然翻车，
        // 而「偶尔失败的测试比没有测试更糟」。
        await using DelayedStream delayed = new(a, new LinkCharacteristics(TimeSpan.Zero, 1_000_000));

        const int chunk = 50_000;
        const int writes = 5;

        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < writes; i++)
        {
            await delayed.WriteAsync(new byte[chunk]);
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), elapsed,
            $"1 MB/s 上连发 {writes} 个 {chunk} 字节，要等掉前 {writes - 1} 份额度 " +
            $"（~200 ms），实际 {elapsed.TotalMilliseconds:0} ms");

        await b.DisposeAsync();
    }

    [TestMethod]
    public void 带宽时延积算得对()
    {
        LinkCharacteristics link = LinkCharacteristics.Intercontinental;

        Assert.AreEqual(TimeSpan.FromMilliseconds(200), link.RoundTrip);

        // 12.5 MB/s × 0.2 s = 2.5 MB —— 正好卡在「固定 2 MiB 窗口」上面，
        // 这也是那个数字被选作跨洋预设的原因。
        Assert.AreEqual(2_500_000, link.BandwidthDelayProduct);
        Assert.IsFalse(link.IsIdeal);
        Assert.IsTrue(LinkCharacteristics.Ideal.IsIdeal);
    }

    // ------------------------------------------------------------ 自适应窗口

    [TestMethod]
    public async Task 高时延链路上窗口会自己长大()
    {
        // 单向 20 ms（RTT 40 ms）。固定的小窗口会把吞吐压在 窗口/RTT。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(20));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;   // 32 KiB
        const int payload = window * 12;

        Measurement m = await MeasureAsync(
            link,
            SshWindowPolicy.Adaptive(minimumBytes: window, maximumBytes: window * 16),
            payload);

        Assert.AreEqual(payload, m.Bytes, "数据要一字节不差地收完");
        int finalWindow = m.FinalWindow;

        // 每一轮回补之间只隔一个 RTT（40 ms < 250 ms 阈值），
        // 说明窗口就是瓶颈 —— 它应当翻倍上去。
        Assert.IsGreaterThan(
            window, finalWindow,
            $"自适应窗口应当从 {window} 长上去，实际停在 {finalWindow}");
    }

    [TestMethod]
    public async Task 读得慢时窗口不跟着长()
    {
        // 数据堆在管道里没人读，回补不发，窗口一样见底 —— 但那时瓶颈是读的一方，不是窗口。
        // 扩窗换不来吞吐，只会让这条通道多缓着几十 MiB 没读的数据。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(1));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;   // 32 KiB
        const int payload = window * 8;

        // 读的一方要**明显**比链路慢：每 8 KiB 停 20 ms，读空一个窗口要 ~80 ms。
        // 曾经只停 5 ms（~20 ms 读空一窗）—— 满跑时机器一忙，发送方被调度得比这还慢，
        // 读的一方真的读空了、真的在等数据，那一刻窗口确实是瓶颈，扩窗是对的；用例的前提被负载打破，偶发失败。
        Measurement m = await MeasureAsync(
            link,
            SshWindowPolicy.Adaptive(minimumBytes: window, maximumBytes: window * 16),
            payload,
            consumePause: TimeSpan.FromMilliseconds(20));

        Assert.AreEqual(payload, m.Bytes, "数据要一字节不差地收完");
        Assert.AreEqual(window, m.FinalWindow, "读的一方跟不上时，窗口不该往上翻");
    }

    [TestMethod]
    public async Task 固定窗口不会变()
    {
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(20));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        const int payload = window * 12;

        Measurement m = await MeasureAsync(link, SshWindowPolicy.Fixed(window), payload);

        Assert.AreEqual(payload, m.Bytes);
        int finalWindow = m.FinalWindow;

        // 需要确定性内存占用的场景（嵌入式、成百上千条并发通道）靠的就是这一条。
        Assert.AreEqual(window, finalWindow, "固定策略下窗口一个字节都不该变");
    }

    [TestMethod]
    public async Task 窗口长满之后数据依然一字节不差()
    {
        // 回归用例。曾经的 bug：通道接收管道的暂停水位是按**起步窗口**算的
        // （`InitialBytes * 2`），而自适应窗口会一路长到 `MaximumBytes`。
        // 窗口一长过起步值的两倍，「未消费数据不可能顶到水位」这个前提就没了；
        // 顶到水位之后 flush 不再同步完成，而接收循环**不等 flush 就接着写**
        // 同一个 PipeWriter —— 那是对 Pipe 的误用。
        //
        // 它的表现极具迷惑性：不是当场报错，而是过一会儿在**消费者**那边抛出
        // 「Reading is not allowed after reader was completed」，
        // 或者干脆只收到一半数据就 EOF。满跑约八轮复现一次。
        //
        // 这里把窗口上限设成起步值的 16 倍，**逼着窗口长过老水位**，
        // 然后只断言一件事：数据一字节不差。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(20));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        const int payload = window * 48;

        Measurement m = await MeasureAsync(
            link,
            SshWindowPolicy.Adaptive(minimumBytes: window, maximumBytes: window * 16),
            payload);

        Assert.AreEqual(payload, m.Bytes, "窗口长大之后数据不能丢");
        Assert.IsGreaterThan(
            window * 2, m.FinalWindow,
            $"前提：窗口要真的长过老水位（{window * 2}），实际 {m.FinalWindow}");
    }

    [TestMethod]
    public async Task 自适应窗口把往返次数降下来()
    {
        // 这是「窗口 / RTT 封死吞吐」那句话的**可验证形式**。
        //
        // 不比墙钟时间 —— 那在忙碌的 CI 机器上是不可靠的，
        // 而一个偶尔失败的测试比没有测试更糟。
        // 比的是**往返次数**：每一次 WINDOW_ADJUST 都意味着对端
        // 先把窗口用光、停下来等了一个 RTT。往返少，吞吐自然高，
        // 而这个数是确定性的。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(20));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        const int payload = window * 16;

        Measurement fixedWindow = await MeasureAsync(
            link, SshWindowPolicy.Fixed(window), payload);

        Measurement adaptive = await MeasureAsync(
            link,
            SshWindowPolicy.Adaptive(minimumBytes: window, maximumBytes: window * 32),
            payload);

        Assert.AreEqual(payload, fixedWindow.Bytes);
        Assert.AreEqual(payload, adaptive.Bytes);
        Assert.IsGreaterThan(window, adaptive.FinalWindow, "前提：窗口确实长大了");

        Assert.IsLessThan(
            fixedWindow.WindowAdjusts, adaptive.WindowAdjusts,
            $"自适应应当用更少的往返：实际 {adaptive.WindowAdjusts} 次 vs " +
            $"固定窗口 {fixedWindow.WindowAdjusts} 次");
    }

    [TestMethod]
    public async Task 扩窗计入会话窗口总预算_预算不够就不扩()
    {
        // 与「高时延链路上窗口会自己长大」同一条链路、同一个策略 —— 那里窗口会一路翻倍上去。
        // 这里会话总预算只留出一次翻倍的余量：扩窗必须先向预算申请，申请不到就停在那里。
        // 曾经扩窗从不计预算，256 MiB 的会话总上限只管得住「开通道那一刻」。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(20));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;   // 32 KiB
        const int budget = window * 2;

        Measurement m = await MeasureAsync(
            link,
            SshWindowPolicy.Adaptive(minimumBytes: window, maximumBytes: window * 16),
            window * 24,
            new SshConnectionLimits { SessionWindowBudgetBytes = budget });

        Assert.AreEqual(window * 24, m.Bytes, "预算封住的是窗口，不是数据");
        Assert.IsLessThanOrEqualTo(budget, m.FinalWindow, $"窗口 {m.FinalWindow} 超过了会话预算 {budget}");
    }

    [TestMethod]
    public async Task 窗口不会长过上限()
    {
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(20));

        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        const int cap = window * 2;

        Measurement m = await MeasureAsync(
            link,
            SshWindowPolicy.Adaptive(minimumBytes: window, maximumBytes: cap),
            window * 16);

        int finalWindow = m.FinalWindow;

        // 上限就是**单条通道最坏的接收缓冲占用** —— 它必须是硬的。
        Assert.IsLessThanOrEqualTo(cap, finalWindow, $"窗口 {finalWindow} 超过了上限 {cap}");
    }
}
