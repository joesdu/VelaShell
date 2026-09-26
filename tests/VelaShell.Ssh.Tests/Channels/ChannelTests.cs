// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md 全部
//
// M2 的决定性测试：握手 + 认证之后，在真实的多路复用器上跑通道。
//
// 这里最值得看的三条：
//   · **EOF 是单向半关闭**。发完 EOF 之后仍然会继续收到输出。把它当成
//     「通道结束」的库，会在 `ssh host 'cat > f' < big` 这类场景下丢掉最后的输出。
//   · **窗口挂在消费上**。数据要大于一个窗口，才测得到「不读就停下、读了才继续」。
//   · **exit-status 与 exit-signal 二选一，且都可能不来**。所以 ExitCode 是 int?。

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Channels;

[TestClass]
[TestCategory("Channels")]
public sealed class ChannelTests
{
    /// <summary>一次完整的会话：握手 → 认证 → 连接协议，两侧都跑起来。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly CancellationTokenSource _cts;
        private readonly Task _serverChannels;

        private Harness(
            TestSshServer server,
            SshPacketTransport clientTransport,
            SshConnection connection,
            TestChannelServer channelServer,
            Task serverChannels,
            CancellationTokenSource cts)
        {
            _server = server;
            ClientTransport = clientTransport;
            Connection = connection;
            ChannelServer = channelServer;
            _serverChannels = serverChannels;
            _cts = cts;
        }

        public SshPacketTransport ClientTransport { get; }

        public SshConnection Connection { get; }

        public TestChannelServer ChannelServer { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Harness> StartAsync(
            TestChannelScript? script = null,
            SshConnectionLimits? limits = null,
            Func<Stream, Stream>? wrapClient = null,
            SshKeepAlivePolicy keepAlive = default)
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();

            TestSshServer server = new(serverStream);
            SshPacketTransport clientTransport =
                new(wrapClient is null ? clientStream : wrapClient(clientStream));
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);
            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
            SshKeyExchangeRunner runner = new(
                clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex =
                await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
            TestSshServerHandshake handshake = await serverHandshake;

            TestAuthServer authServer = new(
                server.Transport, handshake.ExchangeHash,
                new TestAuthPolicy { AcceptPassword = "hunter2" });
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);

            SshAuthenticator authenticator = new(clientTransport, "joe", kex.SessionId);
            await authenticator.AuthenticateAsync([new PasswordCredential("hunter2")], cts.Token);
            Assert.IsTrue(await serverAuth, "认证应当在服务端也算成功");

            TestChannelServer channelServer = new(server.Transport, script);
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex, limits) { KeepAlive = keepAlive };
            connection.Start();

            return new Harness(server, clientTransport, connection, channelServer, serverChannels, cts);
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
                // 收尾时服务端循环被取消是预期的。
            }
            ChannelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();

            // 服务端如果是**在用例跑到一半时**挂的，那条用例看到的多半是
            // 「等一个永远不来的应答」。取消导致的收尾不会走到这里
            // （那一类在 RunAsync 里就被识别掉了），所以到这儿还有值，
            // 就是一个真问题 —— 把它抬出来，别让它继续伪装成超时。
            TestChannelObservation observed = ChannelServer.Observation;
            if (observed.ServerFault is { } serverFault)
            {
                throw new InvalidOperationException(
                    $"测试服务端的收包循环挂了：{serverFault.Message}", serverFault);
            }
            if (observed.ScriptFault is { } scriptFault)
            {
                throw new InvalidOperationException(
                    $"测试服务端的剧本回放挂了：{scriptFault.Message}", scriptFault);
            }
        }
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    // ------------------------------------------------------------ 一次性命令

    [TestMethod]
    public async Task 执行命令并收到标准输出与退出码()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("hello\n"),
            ExitCode = 0,
        });

        (SshExitStatus result, string stdout, string stderr) =
            await harness.Connection.RunAsync("echo hello", cancellationToken: harness.Token);

        Assert.AreEqual("hello\n", stdout);
        Assert.AreEqual("", stderr);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreSequenceEqual(new[] { "echo hello" }, harness.ChannelServer.Observation.Commands);
    }

    /// <summary>
    /// 请求账本的登记必须先于报文上线（velashell-docs/zh/ssh/spec/05-connection.md §5.1）。
    /// </summary>
    /// <remarks>
    /// 回归：原先是「入队，再登记」—— 发送泵在另一个线程上，入队那一刻就可能把帧发出去，
    /// 应答赶在登记之前到达，接收循环按 FIFO 失步把整条连接判死，挂着的请求一律结算成
    /// 「服务端拒绝」。窗口很窄，只在泵线程能立刻抢到核的 Linux / macOS CI 上随机冒出来。
    /// 这里在登记回调里睡一会儿把窗口撑大：回调结束之前，服务端不许已经看到这个请求。
    /// </remarks>
    [TestMethod]
    public async Task 请求账本的登记先于报文上线()
    {
        await using Harness harness = await Harness.StartAsync();
        const string probe = "order-probe@velashell.test";

        ArrayBufferWriter<byte> packet = new();
        SshDataWriter writer = new(packet);
        writer.WriteMessageNumber(SshMessageNumber.GlobalRequest);
        writer.WriteUtf8String(probe);
        writer.WriteBoolean(false);   // 不要应答：这里只看上线时机

        bool seenBeforeRegistered = true;
        await ((ISshChannelHost)harness.Connection).SendAsync(
            packet.WrittenMemory,
            () =>
            {
                Thread.Sleep(300);
                seenBeforeRegistered = harness.ChannelServer.Observation.GlobalRequests.Contains(probe);
            },
            harness.Token);

        Assert.IsFalse(seenBeforeRegistered, "登记还没做完，报文已经到了对端 —— 快的应答会撞上空账本。");

        // 登记完之后这一帧照常上线，连接也还活着。
        while (!harness.ChannelServer.Observation.GlobalRequests.Contains(probe))
        {
            await Task.Delay(10, harness.Token);
        }
        Assert.IsTrue(harness.Connection.IsAlive);
    }

    [TestMethod]
    public async Task 标准输出与标准错误是两条独立的流()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("到 stdout"),
            StandardError = Text("到 stderr"),
            ExitCode = 1,
        });

        (SshExitStatus result, string stdout, string stderr) =
            await harness.Connection.RunAsync("两边都写", cancellationToken: harness.Token);

        // 只有一个读接口的库在这里会死锁：调用方轮流读两边，
        // 一边读空时另一边可能正在被对端写满。
        Assert.AreEqual("到 stdout", stdout);
        Assert.AreEqual("到 stderr", stderr);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task 非零退出码被如实报告()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { ExitCode = 42 });

        (SshExitStatus result, _, _) =
            await harness.Connection.RunAsync("exit 42", cancellationToken: harness.Token);

        Assert.AreEqual(42, result.ExitCode);
        Assert.IsNull(result.ExitSignalName);
    }

    [TestMethod]
    public async Task 被信号杀死时给出信号名而不是伪退出码()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            ExitSignal = "KILL",
            ExitCode = null,
        });

        (SshExitStatus result, _, _) =
            await harness.Connection.RunAsync("sleep 100", cancellationToken: harness.Token);

        // 〔决策〕**不编 128+9 = 137 这样的伪退出码。**那是 shell 的约定，
        // 不是 SSH 的；伪造它会让「进程返回 137」与「进程被 KILL」无法区分。
        Assert.IsNull(result.ExitCode, "被信号杀死时没有退出码，不能凭空造一个");
        Assert.AreEqual("KILL", result.ExitSignalName);
        Assert.IsTrue(result.CoreDumped);
        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task 一个退出状态都没来时退出码是空而不是零()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("有输出但没有退出状态"),
            ExitCode = null,
        });

        (SshExitStatus result, string stdout, _) =
            await harness.Connection.RunAsync("怪服务端", cancellationToken: harness.Token);

        Assert.AreEqual("有输出但没有退出状态", stdout);

        // 这就是 ExitCode 必须是 int? 的理由：对端实现不规范、连接中断，
        // 都会走到这里。报成 0 等于谎称命令成功了。
        Assert.IsNull(result.ExitCode);
        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task 服务端拒绝执行时抛出可读的异常()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { RejectCommand = true });

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.ExecuteAsync("任何命令", cancellationToken: harness.Token));

        // 不等应答就发数据的库，在这里会表现成「命令没输出也没报错」。
        Assert.Contains("ForceCommand", error.Message);
    }

    // ------------------------------------------------------------ 标准输入与 EOF

    [TestMethod]
    public async Task 标准输入能送到远端()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            WaitForClientEof = true,
            StandardOutput = Text("收到了"),
            ExitCode = 0,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("cat", cancellationToken: harness.Token);

        await command.StandardInput.WriteAsync(Text("喂给远端的内容"), harness.Token);
        await command.CompleteStandardInputAsync(harness.Token);

        (SshExitStatus result, string stdout, _) = await command.ReadToEndAsync(harness.Token);

        Assert.AreEqual("收到了", stdout);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreSequenceEqual(
            Text("喂给远端的内容"), [.. harness.ChannelServer.Observation.StandardInput]);
    }

    [TestMethod]
    public async Task 直接完成StandardInput也会发EOF_远端不会一直等输入()
    {
        // PipeWriter 表达「写完了」的惯用法是 Complete —— 它不发 EOF 的话，
        // 远端的 cat 会一直等，这里的 ReadToEndAsync 就永远不返回。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            WaitForClientEof = true,
            StandardOutput = Text("收到了"),
            ExitCode = 0,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("cat", cancellationToken: harness.Token);

        await command.StandardInput.WriteAsync(Text("喂给远端的内容"), harness.Token);
        await command.StandardInput.CompleteAsync();

        (SshExitStatus result, string stdout, _) = await command.ReadToEndAsync(harness.Token);

        Assert.AreEqual("收到了", stdout);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(harness.ChannelServer.Observation.ReceivedEof);
        Assert.AreSequenceEqual(
            Text("喂给远端的内容"), [.. harness.ChannelServer.Observation.StandardInput],
            "EOF 必须排在全部数据之后");
    }

    [TestMethod]
    public async Task stdin积压时远端退出_挂起的写入会返回而不是永远等下去()
    {
        // 远端不读 stdin（窗口不回补），收到第一个包就退出。此时本端 stdin 管道里还压着
        // 一大截，写入方的 FlushAsync 正卡在背压上 —— 通道关了，它必须被放出来。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            InitialWindow = 32 * 1024,
            WithholdWindowAdjust = true,
            CloseAfterStandardInputBytes = 1,
            CloseAfterScript = false,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("head -c 1", cancellationToken: harness.Token);

        // **不带令牌**：使用者常这么写，此时唯一能放出写入方的就是通道自己的收尾。
        ValueTask<FlushResult> write = command.StandardInput.WriteAsync(new byte[512 * 1024]);

        FlushResult flushed = await write.AsTask().WaitAsync(TimeSpan.FromSeconds(10), harness.Token);
        Assert.IsTrue(flushed.IsCompleted, "通道关了，写入方应当看到「对端不再收」");
    }

    [TestMethod]
    public async Task 通道流写完就释放_最后一段照样送到并带上EOF()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
        });

        // 远大于对端窗口（64 KiB），而且**逐块写**：小块写入在管道里低于恢复水位就返回，
        // 最后一次写返回时，末尾那一截还压在本地 stdin 管道里等窗口。
        byte[] payload = new byte[300 * 1024];
        Random.Shared.NextBytes(payload);

        SshChannel channel = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);
        SshChannelStream stream = new(channel);
        for (int offset = 0; offset < payload.Length; offset += 1024)
        {
            await stream.WriteAsync(payload.AsMemory(offset, 1024), harness.Token);
        }
        await stream.DisposeAsync();

        await WaitUntilAsync(() => harness.ChannelServer.Observation.ReceivedClose, harness.Token);
        Assert.IsTrue(harness.ChannelServer.Observation.ReceivedEof, "释放流应当先发 EOF 再关通道");
        Assert.AreSequenceEqual(payload, [.. harness.ChannelServer.Observation.StandardInput],
            "「写完就关」是跳板与隧道上最常见的用法，最后一段不能丢");
    }

    [TestMethod]
    public async Task 通道流的FlushAsync等到数据交给会话才返回_通道先关则报错()
    {
        // 窗口 32 KiB、不回补：写 48 KiB，后 16 KiB 只能压在本地 stdin 管道里。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            InitialWindow = 32 * 1024,
            WithholdWindowAdjust = true,
            CloseAfterScript = false,
            ExitCode = null,
        });

        SshChannel channel = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);
        await using SshChannelStream stream = new(channel, ownsChannel: false);
        for (int i = 0; i < 48; i++)
        {
            await stream.WriteAsync(new byte[1024], harness.Token);
        }

        Task flush = stream.FlushAsync(harness.Token);
        await Task.Delay(200, harness.Token);
        Assert.IsFalse(flush.IsCompleted, "还有 16 KiB 在本地管道里等窗口 —— 冲刷不能先返回");

        // 通道关了，那 16 KiB 再也发不出去：冲刷要如实报错，而不是假装成功。
        await channel.CloseAsync(harness.Token);
        await Assert.ThrowsExactlyAsync<IOException>(() => flush.WaitAsync(TimeSpan.FromSeconds(10), harness.Token));

        await channel.DisposeAsync();
    }

    [TestMethod]
    public async Task 发出EOF之后仍然能收到输出()
    {
        // 这是 EOF 语义的决定性用例：EOF 是**单向**半关闭。
        // 把它当成「通道结束」的库会在这里丢掉服务端的全部输出。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            WaitForClientEof = true,   // 服务端**等我们发完 EOF 才开始输出**
            StandardOutput = Text("EOF 之后才发出来的内容"),
            ExitCode = 0,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("cat", cancellationToken: harness.Token);

        await command.StandardInput.WriteAsync(Text("x"), harness.Token);
        await command.CompleteStandardInputAsync(harness.Token);

        (SshExitStatus result, string stdout, _) = await command.ReadToEndAsync(harness.Token);

        Assert.AreEqual("EOF 之后才发出来的内容", stdout,
            "发了 CHANNEL_EOF 之后仍然必须能收数据 —— 半关闭是单向的");
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(harness.ChannelServer.Observation.ReceivedEof);
    }

    [TestMethod]
    public async Task 回显能双向跑通()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            EchoStandardInput = true,
            WaitForClientEof = true,
            ExitCode = 0,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("cat", cancellationToken: harness.Token);

        await command.StandardInput.WriteAsync(Text("回来吧"), harness.Token);
        await command.CompleteStandardInputAsync(harness.Token);

        (_, string stdout, _) = await command.ReadToEndAsync(harness.Token);
        Assert.AreEqual("回来吧", stdout);
    }

    // ------------------------------------------------------------ 流控

    [TestMethod]
    public async Task 数据量远超一个窗口时靠回补跑完()
    {
        // 窗口收到最小值，数据给 8 个窗口那么多 —— 不回补窗口的实现会卡在这里，
        // 而那正是 30 秒超时要抓的东西。
        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        byte[] payload = new byte[window * 8];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = payload,
            ExitCode = 0,
            MaxPacket = 8 * 1024,
        });

        SshCommandOptions options = new()
        {
            Channel = SshChannelOptions.Default with
            {
                WindowPolicy = SshWindowPolicy.Fixed(window),
                ReceiveMaxPacketBytes = 8 * 1024,
            },
        };

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("大量输出", options, harness.Token);

        byte[] received = await ReadAllBytesAsync(command.StandardOutput, harness.Token);
        SshExitStatus result = await command.WaitAsync(harness.Token);

        Assert.AreSequenceEqual(payload, received, "数据必须一字节不差地过来");
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsGreaterThan(0, harness.ChannelServer.Observation.WindowAdjustCount,
            "跨窗口的数据必须触发 WINDOW_ADJUST，否则对端第一个窗口用完就停了");
    }

    [TestMethod]
    public async Task 逐小块消费时窗口不会一点点漏光()
    {
        // 这条专门抓「攒不够阈值就把已消费字节丢掉」那类 bug：
        // 对端视角的窗口会一点一点缩小，最后归零，症状是传了一阵子之后
        // 通道永久停住 —— 而本地账面上看窗口明明是满的。
        //
        // 每次只读 64 字节（模拟逐行读），总量是窗口的 6 倍。
        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        byte[] payload = new byte[window * 6];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = payload,
            ExitCode = 0,
            MaxPacket = 4 * 1024,
        });

        SshCommandOptions options = new()
        {
            Channel = SshChannelOptions.Default with
            {
                WindowPolicy = SshWindowPolicy.Fixed(window),
                ReceiveMaxPacketBytes = 4 * 1024,
            },
        };

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("大量输出", options, harness.Token);

        ArrayBufferWriter<byte> received = new();
        PipeReader reader = command.StandardOutput;

        while (received.WrittenCount < payload.Length)
        {
            ReadResult read = await reader.ReadAsync(harness.Token);
            if (read.Buffer.IsEmpty && read.IsCompleted)
            {
                break;
            }

            // **一次只吃一小口。**
            long take = Math.Min(64, read.Buffer.Length);
            ReadOnlySequence<byte> chunk = read.Buffer.Slice(0, take);
            foreach (ReadOnlyMemory<byte> segment in chunk)
            {
                received.Write(segment.Span);
            }
            // examined 也停在 chunk.End：传 read.Buffer.End 等于说「整段我都看过了，
            // 有新数据再叫我」—— 而对端正因为窗口被吃空而不再发，
            // 于是缓冲里明明还有数据却谁也不动。
            reader.AdvanceTo(chunk.End);
        }

        await reader.CompleteAsync();
        SshExitStatus result = await command.WaitAsync(harness.Token);

        Assert.AreSequenceEqual(payload, received.WrittenSpan.ToArray(), "逐小块消费也必须一字节不差地收完 —— 收不完就说明窗口漏掉了");
        Assert.AreEqual(0, result.ExitCode);

        // 回补的总量应当覆盖「超出第一个窗口的那部分」，否则对端根本发不完。
        Assert.IsGreaterThanOrEqualTo(
            payload.Length - window, harness.ChannelServer.Observation.WindowAdjustBytes,
            $"回补总量 {harness.ChannelServer.Observation.WindowAdjustBytes} 不足以让对端发完 {payload.Length} 字节");
    }

    [TestMethod]
    public async Task 不读取时对端会被窗口挡住()
    {
        const int window = SshWindowPolicy.AbsoluteMinimumBytes;
        byte[] payload = new byte[window * 4];

        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = payload,
            ExitCode = 0,
            MaxPacket = 8 * 1024,
        });

        SshCommandOptions options = new()
        {
            Channel = SshChannelOptions.Default with
            {
                WindowPolicy = SshWindowPolicy.Fixed(window),
                ReceiveMaxPacketBytes = 8 * 1024,
            },
        };

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("大量输出", options, harness.Token);

        // **一个字节都不读**，等一会儿。
        await Task.Delay(200, harness.Token);

        // 对端最多只能发一个窗口那么多 —— 背压是结构性的，
        // 不需要额外的限流器，也不会出现「内部队列无限涨」。
        Assert.IsLessThanOrEqualTo(
            window, harness.ChannelServer.Observation.WindowAdjustBytes,
            $"没人消费时不该回补窗口，实际补了 {harness.ChannelServer.Observation.WindowAdjustBytes} 字节");

        // 开始读之后应当能全部收完 —— 证明刚才只是停住，不是坏了。
        byte[] received = await ReadAllBytesAsync(command.StandardOutput, harness.Token);
        Assert.HasCount(payload.Length, received);
    }

    [TestMethod]
    public async Task 不认识的扩展数据被丢弃且不报错()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("正常输出"),
            UnknownExtendedData = Text("类型码 7，我们不认识"),
            ExitCode = 0,
        });

        (SshExitStatus result, string stdout, string stderr) =
            await harness.Connection.RunAsync("混着发", cancellationToken: harness.Token);

        // 剧本挂了的话，下面那些断言会报出一堆看不出所以然的「少了几个字」。
        // 先把真正的原因抬出来。
        Assert.IsNull(
            harness.ChannelServer.Observation.ScriptFault,
            $"服务端剧本挂了：{harness.ChannelServer.Observation.ScriptFault}");

        // 〔决策〕保留值的语义未来可能被定义，为它断开会让我们无法与新实现共处。
        Assert.AreEqual("正常输出", stdout);
        Assert.AreEqual("", stderr, "不认识的类型码不该混进 stderr");
        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task 丢弃stderr时它是一条立刻结束的空流()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("out"),
            StandardError = Text("这些会被丢掉"),
            ExitCode = 0,
        });

        SshCommandOptions options = new()
        {
            Channel = SshChannelOptions.Default with { StderrMode = SshStderrMode.Discard },
        };

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("两边都写", options, harness.Token);

        // 关键在于它**立刻结束**而不是「永远没有数据」——
        // 后者会让读它的调用方挂死。
        (SshExitStatus result, string stdout, string stderr) = await command.ReadToEndAsync(harness.Token);

        Assert.AreEqual("out", stdout);
        Assert.AreEqual("", stderr);
        Assert.AreEqual(0, result.ExitCode);
    }

    // ------------------------------------------------------------ 交互式 shell

    [TestMethod]
    public async Task 开交互式shell并发出pty请求()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("$ "),
            ExitCode = 0,
        });

        SshShellOptions options = new()
        {
            TerminalType = "xterm-256color",
            Size = new SshTerminalSize(120, 40, 960, 800),
            Modes = SshTerminalModes.Empty
                .With(SshTerminalModeOpcode.Echo, 1)
                .With(SshTerminalModeOpcode.Utf8Input, 1),
        };

        await using SshShell shell = await harness.Connection.OpenShellAsync(options, harness.Token);

        TestChannelObservation observed = harness.ChannelServer.Observation;
        Assert.HasCount(1, observed.PtyRequests);

        (string term, SshTerminalSize size, byte[] modes) = observed.PtyRequests[0];
        Assert.AreEqual("xterm-256color", term);
        Assert.AreEqual(new SshTerminalSize(120, 40, 960, 800), size);

        // 〔决策〕**像素尺寸是一等公民，不恒为 0。**
        // sixel、kitty 图形协议这类东西要靠它排版；写死成 0 会让它们退化或不工作。
        Assert.AreEqual(960, size.PixelWidth);
        Assert.AreEqual(800, size.PixelHeight);

        // 模式表**必须**以 TTY_OP_END(0) 结尾 —— 漏掉它 OpenSSH 会拒绝整个 pty-req。
        Assert.AreEqual(0, modes[^1], "终端模式表必须以 TTY_OP_END 结尾");
        Assert.HasCount(1 + 4 + 1 + 4 + 1, modes, "两个模式各 5 字节，外加一个结束字节");

        Assert.AreSequenceEqual(
            new[] { SshProtocolNames.RequestPty, SshProtocolNames.RequestShell }, [.. observed.Requests.Where(r => r is SshProtocolNames.RequestPty or SshProtocolNames.RequestShell)]);
    }

    [TestMethod]
    public void 负的终端尺寸在构造时就被拒绝()
    {
        // 线上四个字段都是 uint32。`-1` 会无声无息地变成 4294967295，
        // 远端照单全收，然后按四十亿列排版 —— 那不是「尺寸不对」，是乱码，
        // 而且报错只会出现在远端程序里，指不回这里。所以在构造时就拦住。
        //
        // 四个字段都要拦，不只是像素那两个：列数/行数同样是 uint32。
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshTerminalSize(-1, 24));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshTerminalSize(80, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshTerminalSize(80, 24, -1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshTerminalSize(80, 24, 0, -1));
    }

    [TestMethod]
    public void 用with改成负数一样会被拒绝()
    {
        // 校验写在 init 访问器里，所以 `with` 绕不过去 ——
        // 写成自动属性的话这条会漏。
        SshTerminalSize size = new(80, 24, 640, 480);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = size with { Columns = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = size with { PixelHeight = -1 });
    }

    [TestMethod]
    public void 零是合法的像素尺寸_它的意思是不知道()
    {
        // 0 不是「非法」，是一个有意义的回答：使用者不知道字形尺寸。
        // 把 0 也拒了会逼着调用方瞎编一个数，那比说「不知道」更糟。
        SshTerminalSize size = new(80, 24);

        Assert.AreEqual(0, size.PixelWidth);
        Assert.AreEqual(0, size.PixelHeight);
        Assert.AreEqual(SshTerminalSize.Default, size);

        // 位置式记录换成手写构造之后，Deconstruct 是补回来的 —— 这里钉住它还在。
        (int columns, int rows, int pixelWidth, int pixelHeight) = size;
        Assert.AreEqual((80, 24, 0, 0), (columns, rows, pixelWidth, pixelHeight));
    }

    [TestMethod]
    public async Task 终端尺寸变化发出window_change()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
        });

        await using SshShell shell = await harness.Connection.OpenShellAsync(
            SshShellOptions.Default, harness.Token);

        await shell.ResizeAsync(new SshTerminalSize(200, 60, 1600, 1200), harness.Token);

        // want_reply 必为假，所以服务端不会回；等它被处理到。
        await WaitUntilAsync(
            () => harness.ChannelServer.Observation.WindowChanges.Count > 0, harness.Token);

        Assert.AreEqual(
            new SshTerminalSize(200, 60, 1600, 1200),
            harness.ChannelServer.Observation.WindowChanges[0]);
        Assert.AreEqual(new SshTerminalSize(200, 60, 1600, 1200), shell.Size);
    }

    [TestMethod]
    public async Task 服务端拒绝分配伪终端时说得出原因()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { RejectPty = true });

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenShellAsync(SshShellOptions.Default, harness.Token));

        Assert.Contains("PermitTTY", error.Message);
    }

    [TestMethod]
    public async Task 信号名不带SIG前缀()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("sleep 100", cancellationToken: harness.Token);

        await command.SendSignalAsync("TERM", harness.Token);
        await WaitUntilAsync(() => harness.ChannelServer.Observation.Signals.Count > 0, harness.Token);

        Assert.AreSequenceEqual(new[] { "TERM" }, harness.ChannelServer.Observation.Signals);

        // 传 "SIGTERM" 要在本地就被挡住 —— 发过去服务端只会静默忽略，
        // 而 signal 请求的 want_reply 必为假，调用方永远收不到任何反馈。
        ArgumentException wrong = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await command.SendSignalAsync("SIGTERM", harness.Token));
        Assert.Contains("\"TERM\"", wrong.Message);
    }

    // ------------------------------------------------------------ 环境变量与子系统

    [TestMethod]
    public async Task 环境变量以不要求回复的方式发出()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("ok"),
            ExitCode = 0,
        });

        SshCommandOptions options = new()
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LANG"] = "zh_CN.UTF-8",
                ["VELASHELL"] = "1",
            },
        };

        (SshExitStatus result, _, _) =
            await harness.Connection.RunAsync("env", options, harness.Token);

        Assert.AreEqual(0, result.ExitCode);

        // 要求回复会让每设一个变量多一个 RTT，还会把「AcceptEnv 没放行」
        // 这个常态报成失败。
        Dictionary<string, string> received = harness.ChannelServer.Observation.Environment;
        Assert.AreEqual("zh_CN.UTF-8", received["LANG"]);
        Assert.AreEqual("1", received["VELASHELL"]);
    }

    [TestMethod]
    public async Task 发出通道打开请求之前通道就已经登记好()
    {
        // 回归用例。曾经的 bug：`_channels[localId] = channel` 写在
        // `await SendAsync(CHANNEL_OPEN)` **之后**，而收包循环是另一个线程。
        //
        // 内存传输上服务端可以在那个 await 恢复之前就把 OPEN_CONFIRMATION 送到：
        // `OnChannelOpenConfirmation` 于是把 `_pendingOpens` 里的项取走、
        // 去 `_channels` 里找通道找不着，`return` —— 应答被丢在地上，
        // TCS 永远不完成，`OpenChannelAsync` 一直等到用例超时。
        // 满跑约十轮复现一次，而且每次挂的用例都不一样。
        //
        // 这里不去赌那个时序，而是直接断言**不变式**：
        // CHANNEL_OPEN 的字节落到流上的那一刻，通道必须已经在 `_channels` 里。
        // 把登记挪回 await 之后，这条**每次**都红。
        SshConnection? connection = null;
        bool watching = false;
        int countWhenOpenWentOut = -1;

        await using Harness harness = await Harness.StartAsync(
            new TestChannelScript { CloseAfterScript = false, ExitCode = null },
            wrapClient: inner => new WriteWatcherStream(inner, () =>
            {
                if (watching && countWhenOpenWentOut < 0)
                {
                    countWhenOpenWentOut = connection!.ChannelCount;
                }
            }));

        connection = harness.Connection;
        watching = true;

        await using SshChannel channel =
            await harness.Connection.OpenSubsystemAsync("sftp", cancellationToken: harness.Token);

        Assert.AreEqual(
            1, countWhenOpenWentOut,
            "CHANNEL_OPEN 上线时通道就该在 _channels 里 —— 否则应答会被丢掉");
    }

    /// <summary>每次写之前招呼一声的流 —— 只为把「字节上线」那一刻钉住。</summary>
    private sealed class WriteWatcherStream(Stream inner, Action onWrite) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanWrite => inner.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onWrite();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    public async Task 打开子系统通道()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
        });

        await using SshChannel channel =
            await harness.Connection.OpenSubsystemAsync("sftp", cancellationToken: harness.Token);

        Assert.AreEqual(SshChannelState.Open, channel.State);
        Assert.AreSequenceEqual(new[] { "sftp" }, harness.ChannelServer.Observation.Subsystems);
    }

    [TestMethod]
    public async Task 服务端没有sftp子系统时提示去看配置()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { RejectCommand = true });

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSubsystemAsync("sftp", cancellationToken: harness.Token));

        Assert.Contains("Subsystem sftp", error.Message);
    }

    // ------------------------------------------------------------ 通道打开失败

    [TestMethod]
    public async Task 服务端禁了转发时给出可操作的提示()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            RejectOpenWith = SshChannelOpenFailureReason.AdministrativelyProhibited,
        });

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenChannelAsync(
                SshProtocolNames.ChannelDirectTcpIp, default, null, harness.Token));

        // 「服务端禁止了端口转发（AllowTcpForwarding no）」比「通道打开失败」有用得多：
        // 前者告诉用户去改哪个配置，后者只告诉他事情没成。
        Assert.Contains("AllowTcpForwarding", error.Message);
        Assert.AreEqual(SshChannelOpenFailureReason.AdministrativelyProhibited, error.OpenFailureReason);
    }

    [TestMethod]
    public async Task 并发会话数满时提示MaxSessions()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            RejectOpenWith = SshChannelOpenFailureReason.ResourceShortage,
        });

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSessionChannelAsync(null, harness.Token));

        Assert.Contains("MaxSessions", error.Message);
    }

    [TestMethod]
    public async Task 取消开通道之后迟到的确认会被关掉而不是泄漏在服务端()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            HoldOpenConfirmationUntil = release.Task,
            CloseAfterScript = false,
        });

        using CancellationTokenSource cancel = new();
        Task<SshChannel> opening = harness.Connection.OpenSessionChannelAsync(null, cancel.Token).AsTask();

        // 等 CHANNEL_OPEN 真的到了服务端，再取消 —— 这才是「请求已上线、调用方不等了」。
        await WaitUntilAsync(() => harness.ChannelServer.Observation.ClientAnnounced != default, harness.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => opening);

        release.SetResult();   // 服务端这时才回确认

        // 迟到的那条通道必须被关掉，否则它在服务端占着一个 MaxSessions 名额，直到连接断开。
        await WaitUntilAsync(() => harness.ChannelServer.Observation.ReceivedClose, harness.Token);
        await WaitUntilAsync(() => harness.Connection.ChannelCount == 0, harness.Token);
        Assert.IsTrue(harness.Connection.IsAlive);
    }

    [TestMethod]
    public async Task 开通道途中释放连接_等待方拿到异常而不是永远挂着()
    {
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            HoldOpenConfirmationUntil = never.Task,
        });

        // 不带令牌：关标签页时正在开的 SFTP / 隧道就是这样等着的。
        Task<SshChannel> opening = harness.Connection.OpenSessionChannelAsync(null, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => harness.ChannelServer.Observation.ClientAnnounced != default, harness.Token);

        await harness.Connection.DisposeAsync();

        SshConnectionClosedException error = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            () => opening.WaitAsync(TimeSpan.FromSeconds(10), harness.Token));
        Assert.AreEqual(SshFailureReason.Aborted, error.Reason);
    }

    [TestMethod]
    public async Task 释放通道之后号要等对端的CLOSE到了才回收()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
            HoldCloseReplyUntil = release.Task,
        });

        SshChannel channel = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);
        await channel.DisposeAsync();
        await WaitUntilAsync(() => harness.ChannelServer.Observation.ReceivedClose, harness.Token);

        // 本端已经收尾，但对端那条通道还开着 —— 它迟到的 DATA / exit-status / CLOSE 还会发往这个号。
        // 此时把号还回去，过了回收延迟就会落到复用它的新通道上。
        Assert.AreEqual(SshChannelState.Closed, channel.State);
        Assert.AreEqual(1, harness.Connection.ChannelCount, "对端的 CLOSE 还没到，号必须扣着");

        release.SetResult();
        await WaitUntilAsync(() => harness.Connection.ChannelCount == 0, harness.Token);
    }

    [TestMethod]
    public async Task 发出CLOSE之后通道上不再有任何报文()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
            HoldCloseReplyUntil = release.Task,
        });

        SshChannel channel = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);
        await channel.CloseAsync(harness.Token);

        // 远端 shell 退出后界面才发来的一次改窗：不许上线（RFC 4254 §5.3）。
        bool sent = await channel.SendRequestAsync(
            "window-change", new byte[16], wantReply: false, harness.Token);
        bool accepted = await channel.SendRequestAsync("x@velashell.test", default, wantReply: true, harness.Token);
        await channel.SendEofAsync(harness.Token);

        Assert.IsFalse(sent);
        Assert.IsFalse(accepted);

        release.SetResult();

        // 服务端按顺序处理：保活的应答回来时，排在它前面的报文都已经处理过了。
        Assert.IsTrue(await harness.Connection.SendKeepAliveAsync(harness.Token));
        CollectionAssert.DoesNotContain(harness.ChannelServer.Observation.Requests, "window-change");
        CollectionAssert.DoesNotContain(harness.ChannelServer.Observation.Requests, "x@velashell.test");
        Assert.IsFalse(harness.ChannelServer.Observation.ReceivedEof);

        await channel.DisposeAsync();
    }

    [TestMethod]
    public async Task 对端灌未知通道请求时事件积压有上限()
    {
        // 大多数使用者只读 stdout、不读事件流。没有上限的话，对端每发一条不认识的请求
        // 就白占一份内存（载荷最长 256 KiB）—— 它绕过了窗口流控。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            UnknownRequestsBeforeExit = 500,
            ExitCode = 7,
        });

        await using SshCommand command = await harness.Connection.ExecuteAsync("灌", cancellationToken: harness.Token);
        await WaitUntilAsync(() => command.Channel.State == SshChannelState.Closed, harness.Token);

        int peerRequests = 0;
        bool sawExitStatus = false;
        bool sawClosed = false;
        while (!sawClosed)
        {
            SshChannelEvent channelEvent = await command.Channel.ReadEventAsync(harness.Token);
            peerRequests += channelEvent is SshChannelEvent.PeerRequest ? 1 : 0;
            sawExitStatus |= channelEvent is SshChannelEvent.ExitStatus { Code: 7 };
            sawClosed |= channelEvent is SshChannelEvent.Closed;
        }

        Assert.IsInstanceOfType<SshChannelEvent.Closed>(
            await command.Channel.ReadEventAsync(harness.Token), "关了之后再读，交回的还是那条 Closed，而不是抛异常");

        Assert.IsLessThanOrEqualTo(SshChannel.MaxQueuedEvents, peerRequests, "未知请求的积压必须有上限");
        Assert.IsTrue(sawExitStatus, "退出状态不受上限影响");
        Assert.IsTrue(sawClosed, "Closed 不受上限影响");
    }

    [TestMethod]
    public async Task 对端只发不收时应答积压超限就断开()
    {
        // 客户端写出去的东西对端一概不读（这里用「写被卡住」模拟），同时对端不停地发要应答的全局请求。
        // 接收循环不能在背压上等，应答只能排队 —— 没有上限就是一条无本的内存放大。
        TaskCompletionSource writesBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedWriteStream? gate = null;

        await using Harness harness = await Harness.StartAsync(
            new TestChannelScript { CloseAfterScript = false, ExitCode = null },
            new SshConnectionLimits { MaxQueuedReplyBytes = 2048 },
            wrapClient: inner => gate = new GatedWriteStream(inner));

        gate!.Block();

        // 先让客户端往外写一次：发送泵卡在这次写上，之后的应答只能排在队列里。
        _ = harness.Connection.SendKeepAliveAsync(harness.Token).AsTask();
        await WaitUntilAsync(() => gate.WritesWaiting > 0, harness.Token);

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteMessageNumber(SshMessageNumber.GlobalRequest);
        writer.WriteUtf8String("flood@velashell.test");
        writer.WriteBoolean(true);   // 要应答

        // ⚠️ 灌请求放到后台，不等它：客户端判死之后就不再读了，而服务端这时可能正卡在一次
        //    「管道满了等对端读」的写上 —— 在这里 await 它就永远等不到（满跑并行、客户端读得慢时约 1/35 撞上）。
        //    收尾时用例令牌取消，那次写随之放出来。
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 4096 && harness.Connection.IsAlive; i++)
                {
                    await harness.ChannelServer.SendRawAsync(request.WrittenMemory, harness.Token);
                }
            }
            catch (Exception)
            {
                // 同上：收尾时被取消是预期的。
            }
        });

        await WaitUntilAsync(() => !harness.Connection.IsAlive, harness.Token);
        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await harness.Connection.OpenSessionChannelAsync(null, harness.Token));
        Assert.Contains("积压", error.Message);

        gate.Unblock();   // 放开，免得收尾时卡在那次写上
    }

    [TestMethod]
    public async Task 发送泵卡在写上时保活照样能判死()
    {
        // 死链的典型样子：对端不再读，我们的写卡在发送缓冲上，入站一片安静。
        // 探测若要等自己刷上线才开始计时，它就跟着卡住，永远判不了死。
        GatedWriteStream? gate = null;

        await using Harness harness = await Harness.StartAsync(
            new TestChannelScript { CloseAfterScript = false, ExitCode = null },
            wrapClient: inner => gate = new GatedWriteStream(inner),
            keepAlive: new SshKeepAlivePolicy(TimeSpan.FromMilliseconds(200), maxMissed: 2));

        gate!.Block();

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !harness.Connection.IsAlive, deadline.Token);

        SshConnectionClosedException error = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await harness.Connection.OpenSessionChannelAsync(null, harness.Token));
        Assert.AreEqual(SshFailureReason.KeepAliveTimeout, error.Reason);
        Assert.IsGreaterThan(0, gate.WritesWaiting, "探测确实卡在了写上");

        gate.Unblock();
    }

    /// <summary>写可以被卡住的流：用来模拟「对端不读我们发的东西」。</summary>
    private sealed class GatedWriteStream(Stream inner) : Stream
    {
        private volatile TaskCompletionSource? _gate;
        private int _waiting;

        public int WritesWaiting => Volatile.Read(ref _waiting);

        public void Block() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Unblock() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_gate is { } gate)
            {
                Interlocked.Increment(ref _waiting);
                await gate.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Unblock();
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Unblock();
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task 通道被拒之后窗口预算原数退回而不是多退()
    {
        // 预算只够同时开一条通道。被拒一次若退两次，预算就「越拒越多」，上限形同虚设 ——
        // 端口转发里目标连不上是家常便饭，每一次都在给预算「充值」。
        await using Harness harness = await Harness.StartAsync(
            new TestChannelScript { CloseAfterScript = false, ExitCode = null },
            new SshConnectionLimits { SessionWindowBudgetBytes = 300 * 1024 });

        SshChannelOptions options = SshChannelOptions.Default with
        {
            WindowPolicy = SshWindowPolicy.Fixed(256 * 1024),
        };

        // 测试服务端没配隧道处理器：direct-tcpip 一律被拒。
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsExactlyAsync<SshChannelException>(
                async () => await harness.Connection.OpenChannelAsync(
                    SshProtocolNames.ChannelDirectTcpIp, default, options, harness.Token));
        }

        SshChannel first = await harness.Connection.OpenSessionChannelAsync(options, harness.Token);

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSessionChannelAsync(options, harness.Token));
        Assert.Contains("总预算", error.Message);

        await first.DisposeAsync();
    }

    [TestMethod]
    public async Task 通道打开失败不影响会话()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            RejectOpenWith = SshChannelOpenFailureReason.ResourceShortage,
        });

        await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSessionChannelAsync(null, harness.Token));

        // **通道是独立的失败域。**一条打不开，会话必须还能用。
        Assert.IsTrue(harness.Connection.IsAlive);

        await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSessionChannelAsync(null, harness.Token));
        Assert.IsTrue(harness.Connection.IsAlive, "第二次失败之后会话仍然应当活着");
    }

    [TestMethod]
    public async Task 本端通道数上限撞满时不断开会话()
    {
        await using Harness harness = await Harness.StartAsync(
            new TestChannelScript { CloseAfterScript = false, ExitCode = null },
            new SshConnectionLimits { MaxChannels = 2 });

        SshChannel first = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);
        SshChannel second = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSessionChannelAsync(null, harness.Token));

        Assert.Contains("上限 2", error.Message);
        Assert.IsTrue(harness.Connection.IsAlive, "限额是本端的事，不该连累会话");

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [TestMethod]
    public async Task 会话窗口总预算用尽时拒绝开新通道()
    {
        // 没有这道闸，开 100 条自适应窗口的通道就能把进程撑爆。
        await using Harness harness = await Harness.StartAsync(
            new TestChannelScript { CloseAfterScript = false, ExitCode = null },
            new SshConnectionLimits { SessionWindowBudgetBytes = 300 * 1024 });

        SshChannelOptions options = SshChannelOptions.Default with
        {
            WindowPolicy = SshWindowPolicy.Fixed(256 * 1024),
        };

        SshChannel first = await harness.Connection.OpenSessionChannelAsync(options, harness.Token);

        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await harness.Connection.OpenSessionChannelAsync(options, harness.Token));

        Assert.Contains("总预算", error.Message);
        Assert.IsTrue(harness.Connection.IsAlive);

        await first.DisposeAsync();
    }

    // ------------------------------------------------------------ 多通道

    [TestMethod]
    public async Task 多条通道互不干扰()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("同一份输出"),
            ExitCode = 0,
        });

        // 四条通道并发跑：一条卡住会把其余三条一起拖死，
        // 那正是「接收循环绝不因为某一条通道而停下」要防的。
        Task<SshCommandResult>[] tasks =
        [
            .. Enumerable.Range(0, 4).Select(i =>
                harness.Connection.RunAsync($"命令 {i}", cancellationToken: harness.Token).AsTask()),
        ];

        SshCommandResult[] results = await Task.WhenAll(tasks);

        foreach ((SshExitStatus result, string stdout, _) in results)
        {
            Assert.AreEqual("同一份输出", stdout);
            Assert.AreEqual(0, result.ExitCode);
        }

        Assert.HasCount(4, harness.ChannelServer.Observation.Commands);
    }

    // ------------------------------------------------------------ 全局请求

    [TestMethod]
    public async Task 保活探测拿到应答就算链路活着()
    {
        await using Harness harness = await Harness.StartAsync();

        // 服务端不认识 keepalive@openssh.com，会回 REQUEST_FAILURE ——
        // **那也算数**：我们只关心有没有应答。
        bool alive = await harness.Connection.SendKeepAliveAsync(harness.Token);

        Assert.IsTrue(alive);
        Assert.Contains(
SshProtocolNames.KeepAliveOpenSsh, harness.ChannelServer.Observation.GlobalRequests);
    }

    [TestMethod]
    public async Task 未知全局请求也会拿到应答而不是沉默()
    {
        await using Harness harness = await Harness.StartAsync();

        // 沉默会让对端的 FIFO 队列永远错位 ——
        // 它下一个请求的应答会被认成这一个的。
        bool first = (await harness.Connection.SendGlobalRequestAsync(
            "没人认识的请求", default, wantReply: true, harness.Token)).Success;
        bool second = (await harness.Connection.SendGlobalRequestAsync(
            SshProtocolNames.KeepAliveOpenSsh, default, wantReply: true, harness.Token)).Success;

        Assert.IsFalse(first, "服务端不认识它，回 FAILURE");
        Assert.IsFalse(second);
        Assert.HasCount(2, harness.ChannelServer.Observation.GlobalRequests,
            "两个请求都得有各自的应答，顺序不能错位");
    }

    // ------------------------------------------------------------ 关闭

    [TestMethod]
    public async Task 通道关闭后能读到Closed事件()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("bye"),
            ExitCode = 0,
        });

        await using SshCommand command =
            await harness.Connection.ExecuteAsync("echo bye", cancellationToken: harness.Token);

        _ = await ReadAllBytesAsync(command.StandardOutput, harness.Token);

        List<SshChannelEvent> events = [];
        while (true)
        {
            SshChannelEvent channelEvent = await command.Channel.ReadEventAsync(harness.Token);
            events.Add(channelEvent);
            if (channelEvent is SshChannelEvent.Closed)
            {
                break;
            }
        }

        Assert.IsNotEmpty(events.OfType<SshChannelEvent.Eof>(), "应当读到 Eof");
        Assert.IsNotEmpty(events.OfType<SshChannelEvent.ExitStatus>(), "应当读到 ExitStatus");
        Assert.IsInstanceOfType<SshChannelEvent.Closed>(events[^1], "Closed 必须是最后一件事");
        Assert.AreEqual(SshChannelState.Closed, command.Channel.State);
    }

    [TestMethod]
    public async Task 通道关闭后会话上的通道计数归零()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            StandardOutput = Text("x"),
            ExitCode = 0,
        });

        await harness.Connection.RunAsync("echo x", cancellationToken: harness.Token);
        await WaitUntilAsync(() => harness.Connection.ChannelCount == 0, harness.Token);

        // 号回收了，但**不会立刻复用** —— 对端可能还在路上发这个号的数据，
        // 复用得太早会让那些数据投递到新通道上（串话）。
        Assert.AreEqual(0, harness.Connection.ChannelCount);
    }

    // ------------------------------------------------------------ 工具

    private static async Task<byte[]> ReadAllBytesAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();

        while (true)
        {
            ReadResult read = await reader.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
            {
                buffer.Write(segment.Span);
            }
            reader.AdvanceTo(read.Buffer.End);

            if (read.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }
}
