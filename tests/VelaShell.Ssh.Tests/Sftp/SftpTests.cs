// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/06-sftp.md 全部
//
// 这里最值得看的四条：
//   · **SYMLINK 的参数顺序**。draft-02 与 OpenSSH 的实现是反的，而 OpenSSH 是事实标准。
//     弄反了不会报错,只是把链接建在你本想指向的位置上。
//   · **DurableLength**。流水线写入时应答顺序不保证,文件长度不等于「前面都写进去了」。
//   · **应答靠 request-id 对齐,不靠顺序**。服务端乱序回应时靠顺序的实现会给出静默的错误答案。
//   · **READ 可以短读,EOF 不是错误**。

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpTests
{
    /// <summary>握手 → 认证 → 通道 → SFTP，两侧都跑起来。</summary>
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
            SftpFileSystem sftp,
            TestSftpServer sftpServer,
            CancellationTokenSource cts)
        {
            _server = server;
            _channelServer = channelServer;
            _serverChannels = serverChannels;
            Connection = connection;
            Sftp = sftp;
            SftpServer = sftpServer;
            _cts = cts;
        }

        public SftpFileSystem Sftp { get; }

        public TestSftpServer SftpServer { get; }

        public SshConnection Connection { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Harness> StartAsync(
            Action<TestSftpServer>? arrange = null,
            TestSftpOptions? sftpOptions = null,
            SftpOptions? clientOptions = null,
            Func<TestChannelScript, TestChannelScript>? channelScript = null)
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();

            TestSshServer server = new(serverStream);
            SshPacketTransport clientTransport = new(clientStream);
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
                server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);

            SshAuthenticator authenticator = new(clientTransport, "joe", kex.SessionId);
            await authenticator.AuthenticateAsync([new PasswordCredential("hunter2")], cts.Token);
            await serverAuth;

            TestSftpServer sftpServer = new(sftpOptions);
            arrange?.Invoke(sftpServer);

            TestChannelScript script = new() { SubsystemHandler = sftpServer.RunAsync };
            TestChannelServer channelServer = new(server.Transport, channelScript?.Invoke(script) ?? script);
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex);
            connection.Start();

            SftpFileSystem sftp;
            try
            {
                sftp = await SftpFileSystem.ConnectAsync(connection, clientOptions, cts.Token);
            }
            catch
            {
                // 握手失败的用例：把已经建好的收干净，别让连接的后台循环一直引用着失败的那条 SFTP 会话 ——
                // 「失败之后有没有留下未观察的任务异常」要靠它被 GC 回收才查得出来。
                await cts.CancelAsync();
                await connection.DisposeAsync();
                try
                {
                    await serverChannels;
                }
                catch (Exception)
                {
                }
                channelServer.Dispose();
                await server.DisposeAsync();
                cts.Dispose();
                throw;
            }

            return new Harness(server, channelServer, serverChannels, connection, sftp, sftpServer, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await Sftp.DisposeAsync();
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

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>等到条件成立（只读流的 CLOSE 在后台发，应答回来之前句柄还开着）；等不到由夹具的时限收尾。</summary>
    private static async Task EventuallyAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    // ------------------------------------------------------------ 握手

    [TestMethod]
    public async Task 连上之后工作目录来自REALPATH()
    {
        await using Harness harness = await Harness.StartAsync();

        // 〔决策〕连上就对 "." 做一次 REALPATH —— 这是唯一可靠的
        // 「用户家目录在哪」的答案，比拼 /home/{user} 靠谱得多。
        Assert.AreEqual("/home/joe", harness.Sftp.WorkingDirectory);
        Assert.Contains(SftpMessageType.RealPath, harness.SftpServer.ReceivedTypes);
    }

    [TestMethod]
    public async Task 能力可查而不只是内部降级()
    {
        await using Harness harness = await Harness.StartAsync();

        // posix-rename 与普通 rename 的语义不一样。静默降级的话上层无从知道
        // 自己拿到的是哪一种，也没法在界面上提示「这台服务器不支持原子覆盖」。
        Assert.IsTrue(harness.Sftp.Capabilities.HasPosixRename);
        Assert.IsTrue(harness.Sftp.Capabilities.HasHardLink);
        Assert.IsTrue(harness.Sftp.Capabilities.HasLimits);
        Assert.IsFalse(harness.Sftp.Capabilities.HasStatVfs);
        Assert.IsFalse(harness.Sftp.Capabilities.HasCopyData);
    }

    [TestMethod]
    public async Task 块大小按服务端宣告的limits定()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Limits = new SftpLimits(70_000, 65_536, 65_536, 0) });

        // 写死 32 KiB 的实现在这里会白白浪费一半的可用带宽。
        Assert.AreEqual(65_536, harness.Sftp.BlockSize);
        Assert.AreEqual(65_536UL, harness.Sftp.Capabilities.Limits.MaxWriteLength);
    }

    /// <summary>
    /// 厂商私有扩展（spec/06 §7.3）：扩展名与载荷原样发出，EXTENDED_REPLY 的载荷原样交回；STATUS OK 交回空；
    /// 错误状态抛 SftpException（不认识是 OperationUnsupported），消息里有扩展名。请求照样走同一条流水线。
    /// </summary>
    [TestMethod]
    public async Task 厂商私有扩展原样收发()
    {
        List<(string Name, byte[] Request)> seen = [];
        await using Harness harness = await Harness.StartAsync(sftpOptions: new TestSftpOptions
        {
            Extensions = ["echo@vendor.example", "ack@vendor.example", "fail@vendor.example"],
            VendorExtension = (name, request) =>
            {
                lock (seen)
                {
                    seen.Add((name, request));
                }
                return name switch
                {
                    "echo@vendor.example" => [.. request.Reverse()],
                    "ack@vendor.example" => null,
                    _ => throw new InvalidOperationException("厂商那边出错了"),
                };
            },
        });

        Assert.IsTrue(harness.Sftp.Capabilities.RawExtensions.ContainsKey("echo@vendor.example"));
        Assert.AreSequenceEqual(new byte[] { 3, 2, 1 }, await harness.Sftp.SendExtendedAsync("echo@vendor.example", new byte[] { 1, 2, 3 }, harness.Token));
        Assert.IsEmpty(await harness.Sftp.SendExtendedAsync("ack@vendor.example", ReadOnlyMemory<byte>.Empty, harness.Token));

        SftpException failed = await Assert.ThrowsAsync<SftpException>(
            async () => await harness.Sftp.SendExtendedAsync("fail@vendor.example", "x"u8.ToArray(), harness.Token));
        Assert.AreEqual(SftpStatusCode.Failure, failed.StatusCode);
        Assert.AreEqual(SftpOperation.Extension, failed.Operation);
        Assert.Contains("fail@vendor.example", failed.Message);

        SftpException unknown = await Assert.ThrowsAsync<SftpException>(
            async () => await harness.Sftp.SendExtendedAsync("nope@vendor.example", ReadOnlyMemory<byte>.Empty, harness.Token));
        Assert.AreEqual(SftpStatusCode.OperationUnsupported, unknown.StatusCode);

        lock (seen)
        {
            Assert.AreEqual("echo@vendor.example", seen[0].Name);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, seen[0].Request);
        }

        // 用过之后流水线照常：别的请求不受影响。
        Assert.IsNotNull(await harness.Sftp.GetAttributesAsync("/", cancellationToken: harness.Token));
    }
    [TestMethod]
    public async Task 没有limits扩展时用保守默认()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [] });

        Assert.IsFalse(harness.Sftp.Capabilities.HasLimits);
        Assert.AreEqual(SftpProtocol.DefaultBlockSize, harness.Sftp.BlockSize);
    }

    [TestMethod]
    public async Task 服务端版本低于3时拒绝连接()
    {
        SftpUnavailableException error = await Assert.ThrowsExactlyAsync<SftpUnavailableException>(
            async () => await Harness.StartAsync(sftpOptions: new TestSftpOptions { Version = 2 }));

        Assert.AreEqual("服务端只支持 SFTP v2，本库要求至少 v3。", error.Message);
        Assert.AreEqual(SftpUnavailableKind.VersionTooOld, error.FailureKind, "界面据此不再说成「服务端没开 SFTP」");
        Assert.AreEqual(2u, error.ServerVersion);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "MaxInFlight 为 0")]
    [DataRow(-1, DisplayName = "MaxInFlight 为负")]
    public void 非法的SFTP参数在构造时就抛(int maxInFlight) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SftpOptions { MaxInFlight = maxInFlight });

    [TestMethod]
    public async Task 参数不自洽时先抛_不留下开了没人关的通道()
    {
        // MaxInFlight 300 大于默认的上限 256：曾经是 sftp 通道开了才在建流水线时抛，那条通道一直挂在连接上。
        await using Harness harness = await Harness.StartAsync();
        int before = harness.Connection.ChannelCount;

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await SftpFileSystem.ConnectAsync(harness.Connection, new SftpOptions { MaxInFlight = 300 }, harness.Token));

        Assert.AreEqual(before, harness.Connection.ChannelCount, "不该多出一条通道");
    }

    [TestMethod]
    public async Task 接收窗口装不下一个SFTP报文时先抛_不让双方死等()
    {
        // 通道默认的 256 KiB 窗口恰好装不下一块 256 KiB 的 DATA 应答：报文收齐之前一个字节都不消费，窗口永远回补不了。
        await using Harness harness = await Harness.StartAsync();
        int before = harness.Connection.ChannelCount;

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await SftpFileSystem.ConnectAsync(
                harness.Connection, new SftpOptions { Channel = SshChannelOptions.Default }, harness.Token));

        Assert.AreEqual("Channel", error.ParamName);
        Assert.AreEqual(before, harness.Connection.ChannelCount);
    }

    [TestMethod]
    public async Task Sftp_server一直不回VERSION时握手超时()
    {
        // 登录 shell 的启动文件卡住时 sftp-server 永远不会回 VERSION；曾经只靠调用方的令牌，没给就一直挂着。
        // 只把原因码带出来：异常对象经 InnerException（WaitAsync 抛的 TaskCanceledException）一路引用着等 VERSION 的那个任务，
        // 抓着它的话检查时那个任务还活着，GC 收不到，查不出来。
        SshFailureReason reason = SshFailureReason.Unknown;
        List<Exception> unobserved = await CollectUnobservedAsync(async () =>
            reason = (await Assert.ThrowsExactlyAsync<SftpUnavailableException>(
                async () => await Harness.StartAsync(
                    sftpOptions: new TestSftpOptions { NeverAnswerInit = true },
                    clientOptions: new SftpOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(200) }))).Reason);

        Assert.AreEqual(SshFailureReason.Timeout, reason);

        // 超时之后流水线才收工：那时等 VERSION 的人已经走了，故障曾经落在一个没人看的任务上。
        Assert.IsEmpty(unobserved, string.Join(Environment.NewLine, unobserved));
    }

    /// <summary>跑 <paramref name="action"/>，收集期间（以及之后几轮 GC）冒出来的未观察的任务异常。</summary>
    /// <remarks>
    /// 这是进程级的事件：之前留下的垃圾先收干净；别的用例留下的测试桩任务（服务端那一半）堆栈在 TestKit 里，不算。
    /// </remarks>
    private static async Task<List<Exception>> CollectUnobservedAsync(Func<Task> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        List<Exception> unobserved = [];
        void Record(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains(".TestKit.", StringComparison.Ordinal))
            {
                return;
            }
            lock (unobserved)
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Record;
        try
        {
            await action();
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }
        return unobserved;
    }

    [TestMethod]
    public async Task 启动文件往stdout输出文字时报出真实原因()
    {
        // 「Welcome…」的前 4 个字节被当成报文长度：曾经只报「长度超上限」，看不出是启动文件在说话。
        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await Harness.StartAsync(sftpOptions: new TestSftpOptions { StdoutBanner = "Welcome to the jump host\n" }));

        Assert.Contains("启动文件", error.Message);
        Assert.Contains("Welc", error.Message);
    }

    /// <summary>
    /// 〔SF-M3〕sftp-server 起不来（Subsystem 指向的程序不存在）：通道在 VERSION 之前就关了。报 CommandFailed，
    /// 带上退出码与它在 stderr 上说的话（清洗过）；曾经报「通道在还有在途请求时就关闭了」，两样都丢了。
    /// </summary>
    [TestMethod]
    public async Task Sftp_server起不来时报出退出码与它的stderr()
    {
        SftpUnavailableException error = await Assert.ThrowsExactlyAsync<SftpUnavailableException>(
            async () => await Harness.StartAsync(channelScript: script => script with
            {
                SubsystemHandler = null,
                StandardError = "sh: 1: /usr/lib/sftp-server: not found\u001b[31m\n"u8.ToArray(),
                ExitCode = 127,
            }));

        Assert.AreEqual(SshFailureReason.CommandFailed, error.Reason);
        Assert.AreEqual(127, error.ServerExitStatus);
        Assert.IsNotNull(error.ServerErrorOutput);
        Assert.Contains("/usr/lib/sftp-server: not found", error.ServerErrorOutput);
        Assert.DoesNotContain("\u001b", error.ServerErrorOutput, "对端文本要清洗");
        Assert.Contains("退出码 127", error.Message);

        // stderr 只留末尾：一大段输出之后，出错的那一句在最后。
        byte[] noisy = [.. Enumerable.Repeat((byte)'.', 5000), .. "\nfinal: Permission denied\n"u8];
        SftpUnavailableException noisyError = await Assert.ThrowsExactlyAsync<SftpUnavailableException>(
            async () => await Harness.StartAsync(channelScript: script => script with
            {
                SubsystemHandler = null,
                StandardError = noisy,
                ExitSignal = "SEGV",
            }));
        Assert.Contains("final: Permission denied", noisyError.ServerErrorOutput);
        Assert.IsNull(noisyError.ServerExitStatus, "被信号杀掉时没有退出码");
        Assert.Contains("信号 SEGV", noisyError.Message);
    }

    [TestMethod]
    public async Task 服务端拒绝sftp子系统时报SftpUnavailable()
    {
        SftpUnavailableException error = await Assert.ThrowsExactlyAsync<SftpUnavailableException>(
            async () => await Harness.StartAsync(channelScript: script => script with { RejectCommand = true }));

        Assert.Contains("Subsystem", error.Message);
        Assert.AreEqual(SftpUnavailableKind.SubsystemRejected, error.FailureKind);
    }

    [TestMethod]
    public async Task Session通道没开成时原样报ChannelOpenFailed_不改写成没有sftp子系统()
    {
        // 服务端 MaxSessions 满了、管理上禁止：用户不该被引去改一个本来没问题的 sshd_config，
        // 也不该丢了「稍后可以重试」这个信息。
        SshChannelException error = await Assert.ThrowsExactlyAsync<SshChannelException>(
            async () => await Harness.StartAsync(
                channelScript: script => script with { RejectOpenWith = SshChannelOpenFailureReason.ResourceShortage }));

        Assert.AreEqual(SshFailureReason.ChannelOpenFailed, error.Reason);
        Assert.AreEqual(SshChannelOpenFailureReason.ResourceShortage, error.OpenFailureReason);
    }

    [TestMethod]
    public async Task 服务端回的版本高于3时不连()
    {
        // 〔Q9〕draft-02 §4：服务端回双方版本里较小的那个。回 6 的服务端会按 v6 说话（ATTRS 结构不同），按 v3 解析就是静默错位。
        // 曾经「降到 3 继续」。
        SftpUnavailableException error = await Assert.ThrowsExactlyAsync<SftpUnavailableException>(
            async () => await Harness.StartAsync(sftpOptions: new TestSftpOptions { Version = 6 }));

        Assert.AreEqual(SshFailureReason.ProtocolError, error.Reason);
        Assert.Contains("v6", error.Message);
        Assert.AreEqual(SftpUnavailableKind.VersionTooNew, error.FailureKind);
        Assert.AreEqual(6u, error.ServerVersion);
    }

    // ------------------------------------------------------------ 读写

    [TestMethod]
    public async Task 读一个文件()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("你好，SFTP。")));

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/a.txt", harness.Token);

        Assert.AreEqual("你好，SFTP。", Encoding.UTF8.GetString(content));
    }

    [TestMethod]
    public async Task 短读时会循环直到读完()
    {
        byte[] payload = new byte[20_000];
        Random.Shared.NextBytes(payload);
        // 服务端每次只回 100 字节 —— 协议允许，这不是错误
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            new TestSftpOptions { ShortReadLimit = 100 });

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token);

        // 不循环读的实现会在这里只拿到 100 字节，而且**不报错**。
        Assert.AreSequenceEqual(payload, content, "READ 返回的数据可以少于请求的长度，必须循环读");
    }

    // ------------------------------------------------------------ 顺序读的预读（spec/06 §5.5）

    [TestMethod]
    public async Task 顺序读时同时有多个READ在途()
    {
        // 第三块的应答被服务端扣住，直到它收到更靠后的 READ。一次只发一个 READ 的实现
        // 永远等不到那个「更靠后」的请求 —— 吞吐被钉死在「块大小 ÷ RTT」，下载在高延迟链路上只有几百 KB/s。
        byte[] payload = new byte[8 * 32 * 1024];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            new TestSftpOptions { HoldReadReplyAtOffset = 2 * 32 * 1024 },
            new SftpOptions { BlockSize = 32 * 1024 });

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), harness.Token);

        Assert.AreSequenceEqual(payload, content);
        Assert.IsTrue(harness.SftpServer.PipelinedReadObserved, "扣住的应答应当被预读的后续请求放出来");
    }

    [TestMethod]
    [DataRow(32 * 1024, 32, 8)]
    [DataRow(256 * 1024, 32, 4)]
    [DataRow(32 * 1024, 2, 2)]
    public async Task UnknownLengthRegularFilePipelinesWithinBudget(int blockSize, int depth, int maxRequests)
    {
        byte[] payload = new byte[16 * blockSize + 123];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/data", payload),
            new TestSftpOptions
            {
                Limits = new((ulong)(blockSize + SftpProtocol.RequestHeaderAllowance), (ulong)blockSize, (ulong)blockSize, 0),
                FStatAttributes = SftpFileAttributes.WithPermissions(SftpProtocol.FileTypeRegular),
                HoldReadReplyAtOffset = blockSize,
                ShuffleResponses = true,
                ShortReadAtOffset = 6 * blockSize,
            },
            new SftpOptions { BlockSize = blockSize, MaxInFlight = depth, IsPipelineDepthAdaptive = false });
        Assert.AreEqual(blockSize, harness.Sftp.BlockSize);

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/data", harness.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), harness.Token);
        await EventuallyAsync(() => harness.SftpServer.OpenHandleCount == 0, harness.Token);

        Console.WriteLine($"Negotiated block {harness.Sftp.BlockSize} bytes, depth limit {depth}, EOF replies {harness.SftpServer.EofReadRequests}");
        Assert.IsTrue(harness.SftpServer.PipelinedReadObserved);
        Assert.IsGreaterThan(0, harness.SftpServer.ReversedBatches);
        Assert.AreSequenceEqual(payload, content);
        Assert.AreEqual(0L, harness.SftpServer.RereadBytes);
        Assert.IsGreaterThan(0, harness.SftpServer.EofReadRequests);
        Assert.IsLessThanOrEqualTo(maxRequests, harness.SftpServer.EofReadRequests);
    }

    [TestMethod]
    public async Task UnknownLengthGapSurvivesCancellationAndSeekDropsPartial()
    {
        const int block = 8192;
        byte[] payload = new byte[8 * block];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/data", payload),
            new TestSftpOptions
            {
                FStatAttributes = SftpFileAttributes.WithPermissions(SftpProtocol.FileTypeRegular),
                ShortReadAtOffset = block,
                HoldReadReplyAtOffset = block + block / 2,
            },
            new SftpOptions { BlockSize = block });
        await using SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/data", harness.Token);
        byte[] prefix = await ReadExactlyAsync(stream, block + block / 2, block, harness.Token);
        Assert.AreSequenceEqual(payload[..prefix.Length], prefix);

        using CancellationTokenSource wait = new();
        byte[] peek = new byte[17];
        Task<int> gap = stream.ReadAsync(peek, wait.Token).AsTask();
        _ = await harness.Sftp.GetAttributesAsync("/home/joe/data", harness.Token);
        await wait.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await gap);
        Assert.AreEqual(prefix.LongLength, stream.Position);

        // 随机读放行扣住的缺口。下一次顺序读复用迟到的应答，不重读，也不丢掉尚未交出的块尾。
        Assert.AreEqual(0, await stream.ReadAtAsync(payload.Length, peek, harness.Token));
        Assert.AreEqual(peek.Length, await stream.ReadAsync(peek, harness.Token));
        Assert.AreSequenceEqual(payload[prefix.Length..(prefix.Length + peek.Length)], peek);
        Assert.AreEqual(0L, harness.SftpServer.RereadBytes);
        stream.Seek(5 * block, SeekOrigin.Begin);
        Assert.AreEqual(peek.Length, await stream.ReadAsync(peek, harness.Token));
        Assert.AreSequenceEqual(payload[(5 * block)..(5 * block + peek.Length)], peek);
    }

    [TestMethod]
    [DataRow(false, 0x2000u)] // 字符设备
    [DataRow(false, 0u)]     // 没给类型
    [DataRow(true, 0u)]      // FSTAT 被拒
    public async Task UnknownLengthUnidentifiedOrSpecialHandleReadsOnDemand(bool reject, uint mode)
    {
        const int blockSize = 8192;
        byte[] payload = new byte[10 * blockSize];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/source", payload),
            new TestSftpOptions { FStatAttributes = SftpFileAttributes.WithPermissions(mode), RejectFStat = reject },
            new SftpOptions { BlockSize = blockSize });

        await using SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/source", harness.Token);
        byte[] prefix = await ReadExactlyAsync(stream, 4 * blockSize, blockSize, harness.Token);
        // 后续 STAT 的应答是屏障，此前发出的投机 READ 已全部到达服务端。
        _ = await harness.Sftp.GetAttributesAsync("/home/joe/source", harness.Token);

        Assert.AreSequenceEqual(payload[..prefix.Length], prefix);
        Assert.AreEqual(4, harness.SftpServer.ReceivedTypes.Count(type => type == SftpMessageType.Read));
    }

    [TestMethod]
    public async Task 预读下小缓冲逐段读_中途Seek_读到的都与文件一致()
    {
        byte[] payload = new byte[1024 * 1024 + 123];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            clientOptions: new SftpOptions { BlockSize = 32 * 1024 });

        await using SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/big.bin", harness.Token);

        // 调用方的缓冲比块小、而且不整除：一块要分几次交出去。
        byte[] head = await ReadExactlyAsync(stream, 100_000, bufferSize: 7_000, harness.Token);
        Assert.AreSequenceEqual(payload[..100_000], head);

        // 跳到别处：队伍里的偏移都对不上了，必须从新位置重来。
        stream.Seek(700_000, SeekOrigin.Begin);
        byte[] tail = await ReadExactlyAsync(stream, payload.Length - 700_000, bufferSize: 50_000, harness.Token);
        Assert.AreSequenceEqual(payload[700_000..], tail);

        Assert.AreEqual(0, await stream.ReadAsync(new byte[10], harness.Token), "读完之后返回 0");

        // 往回跳也一样。
        stream.Position = 10;
        byte[] again = await ReadExactlyAsync(stream, 1_000, bufferSize: 1_000, harness.Token);
        Assert.AreSequenceEqual(payload[10..1_010], again);
    }

    [TestMethod]
    public async Task 预读下短读与乱序应答都不丢数据()
    {
        byte[] payload = new byte[300_000];
        Random.Shared.NextBytes(payload);

        foreach (TestSftpOptions serverOptions in new[]
                 {
                     new TestSftpOptions { ShortReadLimit = 10_000 },
                     new TestSftpOptions { ShuffleResponses = true },
                 })
        {
            await using Harness harness = await Harness.StartAsync(
                server => server.AddFile("/home/joe/big.bin", payload),
                serverOptions,
                new SftpOptions { BlockSize = 32 * 1024 });

            byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token);
            Assert.AreSequenceEqual(payload, content);
        }
    }

    /// <summary>
    /// 〔Q11〕每一块都短读的服务端（实际读上限比块小、又没宣告 limits）：学到它实际给的长度之后照样预读，一个字节都不重读。
    /// 曾经每次短读都整队作废、窗口回到 1，吞吐塌到一块 / RTT —— 扣住的那条应答永远等不到更靠后的请求。
    /// </summary>
    [TestMethod]
    public async Task 每块都短读的服务端照样预读()
    {
        byte[] payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            new TestSftpOptions { ShortReadLimit = 10_000, HoldReadReplyAtOffset = 50_000 },
            new SftpOptions { BlockSize = 32 * 1024 });

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), harness.Token);

        Assert.AreSequenceEqual(payload, content);
        Assert.IsTrue(harness.SftpServer.PipelinedReadObserved, "学到读上限之后应当又有好几个 READ 在途");
        Assert.AreEqual(0L, harness.SftpServer.RereadBytes);
    }

    /// <summary>
    /// 〔Q11〕预读队伍建起来之后中间一块短读：只为缺口补发请求插到队首，后面已发的请求照用 —— 一个字节都不重读。
    /// 曾经整队作废、从读位置重来，已经读回来的后面几块都白读了。
    /// </summary>
    [TestMethod]
    public async Task 中途短读只补缺口不作废后面的预读()
    {
        byte[] payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            new TestSftpOptions { ShortReadAtOffset = 8 * 32 * 1024 },
            new SftpOptions { BlockSize = 32 * 1024 });

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token);

        Assert.AreSequenceEqual(payload, content);
        Assert.AreEqual(0L, harness.SftpServer.RereadBytes, "缺口之后已发的请求照用，没有哪一段被读了两遍");
    }

    [TestMethod]
    public async Task 预读的流读一半就关_句柄与在途应答都收拾干净()
    {
        byte[] payload = new byte[1024 * 1024];
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            clientOptions: new SftpOptions { BlockSize = 32 * 1024 });

        await using (SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/big.bin", harness.Token))
        {
            _ = await ReadExactlyAsync(stream, 200_000, bufferSize: 32 * 1024, harness.Token);
        }

        // 后面的操作照常可用 —— 作废的预读没有占着在途额度不还。
        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token);
        Assert.HasCount(payload.Length, content);

        // 只读流的 CLOSE 在后台发（不等应答），句柄在服务端处理完它之后才关上。
        await EventuallyAsync(() => harness.SftpServer.OpenHandleCount == 0, harness.Token);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        SftpFileStream stream, int count, int bufferSize, CancellationToken cancellationToken)
    {
        byte[] result = new byte[count];
        byte[] buffer = new byte[bufferSize];
        int filled = 0;
        while (filled < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(bufferSize, count - filled)), cancellationToken);
            Assert.IsGreaterThan(0, read, "文件还没读完就返回了 0");
            buffer.AsSpan(0, read).CopyTo(result.AsSpan(filled));
            filled += read;
        }
        return result;
    }

    [TestMethod]
    public async Task 读空文件返回零字节而不是异常()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/empty.txt", []));

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/empty.txt", harness.Token);

        // EOF 不是错误 —— 它就是「读完了」。把它抛出去的话，
        // 每次正常读完一个文件都会变成一次异常。
        Assert.IsEmpty(content);
    }

    [TestMethod]
    public async Task 写一个文件()
    {
        await using Harness harness = await Harness.StartAsync();

        await harness.Sftp.WriteAllBytesAsync(
            "/home/joe/new.txt", Text("写进去的内容"), cancellationToken: harness.Token);

        TestSftpNode node = harness.SftpServer.Nodes["/home/joe/new.txt"];
        Assert.AreSequenceEqual(Text("写进去的内容"), [.. node.Content]);
        Assert.AreEqual(SftpProtocol.DefaultFilePermissions, node.Permissions,
            "创建时要传明确的权限 —— 不传会让服务端用受 umask 影响的默认值，结果不可预测");
    }

    [TestMethod]
    public async Task 写一个跨多块的大文件()
    {
        byte[] payload = new byte[200_000];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            clientOptions: SftpOptions.Default with { BlockSize = 8192 });

        await harness.Sftp.WriteAllBytesAsync("/home/joe/big.bin", payload, cancellationToken: harness.Token);

        Assert.AreSequenceEqual(payload, [.. harness.SftpServer.Nodes["/home/joe/big.bin"].Content]);
        Assert.IsGreaterThan(20, harness.SftpServer.WriteCount, "应当被切成多个 WRITE");
    }

    /// <summary>
    /// 调用方每次写 256 KiB，而服务端的块是 255 KiB（OpenSSH 的 limits）：要按整块发。
    /// 曾经每次调用各自切成「一大一小」两个 WRITE —— 在途名额按请求个数算，在途字节少了一半。
    /// </summary>
    [TestMethod]
    public async Task 每次写的长度不是块的整数倍时凑满整块再发()
    {
        const int chunk = 256 * 1024;
        byte[] payload = new byte[10 * chunk];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync();
        int block = harness.Sftp.BlockSize;
        Assert.AreEqual(261_120, block, "前提：测试服务端宣告的块是 255 KiB");

        await using (SftpFileStream stream = await harness.Sftp.OpenWriteAsync(
            "/home/joe/up.bin", cancellationToken: harness.Token))
        {
            for (int i = 0; i < 10; i++)
            {
                await stream.WriteAsync(payload.AsMemory(i * chunk, chunk), harness.Token);
            }
        }

        Assert.AreSequenceEqual(payload, [.. harness.SftpServer.Nodes["/home/joe/up.bin"].Content]);
        Assert.AreEqual((payload.Length + block - 1) / block, harness.SftpServer.WriteCount,
            "除了最后的尾巴，每个 WRITE 都该是整块");
    }

    [TestMethod]
    public async Task 攒着的尾巴在读改长度和按偏移写之前发出()
    {
        await using Harness harness = await Harness.StartAsync();

        await using SftpFileStream stream = await harness.Sftp.OpenAsync(
            "/home/joe/rw.bin",
            SftpOpenModes.Read | SftpOpenModes.Write | SftpOpenModes.Create | SftpOpenModes.Truncate,
            cancellationToken: harness.Token);

        // 不足一块：留在本端。读之前必须先发出去，读到的才是写过的内容。
        await stream.WriteAsync("hello world"u8.ToArray(), harness.Token);
        byte[] read = new byte[11];
        int got = await stream.ReadAtAsync(0, read, harness.Token);
        Assert.AreEqual(11, got);
        Assert.AreEqual("hello world", Encoding.UTF8.GetString(read));

        // 尾巴之后跳着写（Seek 过）：接不上的尾巴先发，两段都要落到对的位置。
        stream.Position = 11;
        await stream.WriteAsync("!!"u8.ToArray(), harness.Token);
        stream.Position = 100;
        await stream.WriteAsync("end"u8.ToArray(), harness.Token);

        // 按偏移写与尾巴重叠：先写的先到。
        await stream.WriteAtAsync(0, "HELLO"u8.ToArray(), harness.Token);

        // 截断之前尾巴先发出去，截断之后不会被它又撑长。
        await stream.SetLengthAsync(50, harness.Token);
        await stream.FlushAsync(harness.Token);

        byte[] content = [.. harness.SftpServer.Nodes["/home/joe/rw.bin"].Content];
        Assert.HasCount(50, content);
        Assert.AreEqual("HELLO world!!", Encoding.UTF8.GetString(content, 0, 13));
    }

    [TestMethod]
    public async Task 读写往返一致()
    {
        byte[] payload = new byte[100_000];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            clientOptions: SftpOptions.Default with { BlockSize = 4096 });

        await harness.Sftp.WriteAllBytesAsync("/home/joe/round.bin", payload, cancellationToken: harness.Token);
        byte[] back = await harness.Sftp.ReadAllBytesAsync("/home/joe/round.bin", harness.Token);

        Assert.AreSequenceEqual(payload, back);
    }

    // ------------------------------------------------------------ 流水线与 DurableLength

    [TestMethod]
    public async Task 应答乱序时靠request_id对齐而不是靠顺序()
    {
        // 每个文件的长度都不一样 —— 应答被安到错误的请求上时，长度立刻对不上。
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                for (int i = 1; i <= 6; i++)
                {
                    server.AddFile($"/home/joe/f{i}.txt", new byte[i * 10]);
                }
            }, // 服务端故意把应答倒着发
            new TestSftpOptions
            {
                ShuffleResponses = true,
                DelayStatReplies = TimeSpan.FromMilliseconds(50),
            });

        // 一口气发出去，不逐个等 —— 这才有多个在途请求可供打乱。
        Task<SftpFileAttributes>[] stats =
        [
            .. Enumerable.Range(1, 6).Select(i =>
                harness.Sftp.GetAttributesAsync($"/home/joe/f{i}.txt", harness.Token).AsTask()),
        ];

        SftpFileAttributes[] results = await Task.WhenAll(stats);

        // 靠顺序对齐的实现会把应答安到错误的请求上 ——
        // 而且是**静默的错误答案**，不抛任何异常。
        for (int i = 0; i < results.Length; i++)
        {
            Assert.AreEqual((ulong)((i + 1) * 10), results[i].Size,
                $"f{i + 1}.txt 的长度应当是 {(i + 1) * 10}");
        }

        Assert.IsGreaterThan(0, harness.SftpServer.ReversedBatches,
            "服务端必须真的倒着发过至少一批，否则这条用例是空跑的");
    }

    [TestMethod]
    public async Task 顺序写完之后连续确认长度等于文件长度()
    {
        byte[] payload = new byte[40_000];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            clientOptions: SftpOptions.Default with { BlockSize = 4096 });

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync(
            "/home/joe/w.bin", cancellationToken: harness.Token);

        await stream.WriteAsync(payload, harness.Token);
        await stream.FlushAsync(harness.Token);

        Assert.AreEqual(payload.Length, stream.DurableLength);
        Assert.AreEqual(1, stream.AckedRangeCount,
            "顺序写入时区间应当合并成一段 —— 有序数组加二分插入的设计就是为此");
    }

    [TestMethod]
    public async Task 中途断开时异常里带着精确的续传点()
    {
        byte[] payload = new byte[40_000];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { FailWritesAfter = 3 }, // 前 3 个 WRITE 正常应答，之后干脆不回 —— 模拟中途断开。
            clientOptions: SftpOptions.Default with { BlockSize = 4096, MaxInFlight = 4 });

        SftpFileStream stream = await harness.Sftp.OpenWriteAsync(
            "/home/joe/w.bin", cancellationToken: harness.Token);

        using CancellationTokenSource writeTimeout = new(TimeSpan.FromSeconds(2));

        long durable;
        try
        {
            await stream.WriteAsync(payload, writeTimeout.Token);
            await stream.FlushAsync(writeTimeout.Token);
            durable = stream.DurableLength;
        }
        catch (SftpTransferInterruptedException ex)
        {
            // 这才是重点：不必再 stat 一次、更不必盲退一个在途窗口。
            durable = ex.DurableLength;
        }
        catch (OperationCanceledException)
        {
            durable = stream.DurableLength;
        }

        Assert.AreEqual(3 * 4096, durable,
            "前 3 块确认了，第 4 块起没有应答 —— 续传点就该是 3 块");
    }

    [TestMethod]
    public async Task 乱序确认时连续长度停在第一个空洞()
    {
        await using Harness harness = await Harness.StartAsync();

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync(
            "/home/joe/holes.bin", cancellationToken: harness.Token);

        byte[] block = new byte[1000];

        // 故意**跳过** [1000, 2000) 那一段。
        await stream.WriteAtAsync(0, block, harness.Token);
        await stream.WriteAtAsync(2000, block, harness.Token);
        await stream.WriteAtAsync(3000, block, harness.Token);
        await stream.FlushAsync(harness.Token);

        // 服务端报告的文件长度是 4000（已确认的**最高**偏移），
        // 但 1000–2000 那段其实没写。从 4000 续传会留下一段读作 0 的空洞。
        Assert.AreEqual(1000, stream.DurableLength,
            "连续确认长度必须停在第一个空洞处，而不是跟着文件长度走");
        Assert.AreEqual(2, stream.AckedRangeCount);
    }

    [TestMethod]
    public async Task 顺序写模式下任何时刻文件都是完整前缀()
    {
        byte[] payload = new byte[20_000];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            clientOptions: SftpOptions.Default with { BlockSize = 4096 });

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync(
            "/home/joe/seq.bin", writeMode: SftpWriteMode.Sequential, cancellationToken: harness.Token);

        Assert.AreEqual(SftpWriteMode.Sequential, stream.WriteMode);

        await stream.WriteAsync(payload, harness.Token);

        // 顺序模式的承诺：WriteAsync 返回时那一块已经确认了。
        Assert.AreEqual(payload.Length, stream.DurableLength);
        Assert.AreEqual(1, stream.AckedRangeCount);
    }

    [TestMethod]
    public async Task 断点续传从DurableLength接上()
    {
        byte[] first = new byte[5000];
        byte[] second = new byte[5000];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);

        await using Harness harness = await Harness.StartAsync(
            clientOptions: SftpOptions.Default with { BlockSize = 4096 });

        long durable;
        await using (SftpFileStream stream = await harness.Sftp.OpenWriteAsync(
            "/home/joe/resume.bin", cancellationToken: harness.Token))
        {
            await stream.WriteAsync(first, harness.Token);
            await stream.FlushAsync(harness.Token);
            durable = stream.DurableLength;
        }

        await using (SftpFileStream stream = await harness.Sftp.OpenAppendAsync(
            "/home/joe/resume.bin", durable, cancellationToken: harness.Token))
        {
            await stream.WriteAsync(second, harness.Token);
            await stream.FlushAsync(harness.Token);
        }

        byte[] all = await harness.Sftp.ReadAllBytesAsync("/home/joe/resume.bin", harness.Token);

        Assert.HasCount(10_000, all);
        Assert.AreSequenceEqual(first, all[..5000]);
        Assert.AreSequenceEqual(second, all[5000..]);
    }

    // ------------------------------------------------------------ 目录

    [TestMethod]
    public async Task Sftp_server退出之后IsConnected变假_Closed带出原因()
    {
        // sftp-server 崩溃、服务端按 ChannelTimeout 关掉闲置通道：这个对象不会自己恢复，
        // 使用者得知道它死了、丢掉重建 —— 曾经没有这个信号，宿主的文件面板一直坏到整条连接重连。
        await using Harness harness = await Harness.StartAsync(server => server.AddFile("/home/joe/a.txt", Text("x")));
        Assert.IsTrue(harness.Sftp.IsConnected);
        Assert.IsFalse(harness.Sftp.Closed.IsCompleted);

        harness.SftpServer.Exit();

        Exception reason = await harness.Sftp.Closed.WaitAsync(harness.Token);
        Assert.IsFalse(harness.Sftp.IsConnected);
        Assert.IsInstanceOfType<SshException>(reason);
        await Assert.ThrowsAsync<SshException>(
            async () => await harness.Sftp.GetAttributesAsync("/home/joe/a.txt", harness.Token));
    }

    [TestMethod]
    public async Task 释放之后Closed以SftpUnavailable完成()
    {
        Harness harness = await Harness.StartAsync();
        Task<Exception> closed = harness.Sftp.Closed;

        await harness.DisposeAsync();

        Exception ended = await closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<SftpUnavailableException>(ended);
        Assert.AreEqual(SftpUnavailableKind.SessionClosed, ((SftpUnavailableException)ended).FailureKind, "本端收工，不是服务端没开 SFTP");
        Assert.AreEqual(SshFailureReason.Aborted, ((SftpUnavailableException)ended).Reason, "本端收工是中止，不是不支持");
        Assert.IsFalse(harness.Sftp.IsConnected);
    }

    [TestMethod]
    public async Task 名字不是合法UTF8的文件也能打开与删除()
    {
        // 「café.txt」按 Latin-1 写在磁盘上：E9 不是合法的 UTF-8。曾经它被解成 U+FFFD，
        // 再按 UTF-8 编回去是 EF BF BD —— 服务端找不到这个文件，打不开、删不掉。
        const string onDisk = "/home/joe/caf\uDCE9.txt";
        await using Harness harness = await Harness.StartAsync(server => server.AddFile(onDisk, Text("内容")));

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        SftpDirectoryEntry listed = Assert.ContainsSingle(entries);
        Assert.AreEqual(onDisk, listed.FullPath, "解不开的字节要无损地带回来");
        Assert.AreEqual("内容", Encoding.UTF8.GetString(await harness.Sftp.ReadAllBytesAsync(listed.FullPath, harness.Token)));

        await harness.Sftp.DeleteFileAsync(listed.FullPath, harness.Token);
        Assert.IsFalse(await harness.Sftp.ExistsAsync(listed.FullPath, harness.Token));
    }

    [TestMethod]
    public async Task WriteAsync返回之后取消它的令牌_在途的写照样落盘并记账()
    {
        // WriteAsync 返回时 WRITE 还在路上。曾经它带着这一次调用的令牌：令牌之后被取消，已经发出的 WRITE 照样落盘，
        // 本端却不再记账（DurableLength 偏小），流还被标成写入故障 —— Flush / 关闭抛「传输中断」而不是成功。
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { DelayWriteReplies = TimeSpan.FromMilliseconds(200) });

        byte[] payload = new byte[(harness.Sftp.BlockSize * 2) + 100];
        Random.Shared.NextBytes(payload);

        await using (SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/up.bin", cancellationToken: harness.Token))
        {
            using (CancellationTokenSource perCall = new())
            {
                await stream.WriteAsync(payload, perCall.Token);
                await perCall.CancelAsync();   // WriteAsync 已经返回，整块的 WRITE 还在等应答
            }

            await stream.FlushAsync(harness.Token);
            Assert.AreEqual(payload.Length, stream.DurableLength, "在途的写照样被确认、记账");
        }

        Assert.AreSequenceEqual(payload, [.. harness.SftpServer.Nodes["/home/joe/up.bin"].Content]);
    }

    /// <summary>
    /// 写槽占满时被取消的那一次写：这一段没发出去、位置不前进；调用方重试同一段之后，文件里恰好一份。
    /// </summary>
    [TestMethod]
    public async Task 等写槽时被取消_这一段没发出去_重试之后文件恰好一份()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { DelayWriteReplies = TimeSpan.FromMilliseconds(300) },
            clientOptions: new SftpOptions { MaxInFlight = 1, IsPipelineDepthAdaptive = false });

        int block = harness.Sftp.BlockSize;
        byte[] first = new byte[block];
        byte[] second = new byte[block];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);

        await using (SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/slot.bin", cancellationToken: harness.Token))
        {
            await stream.WriteAsync(first, harness.Token);   // 占住唯一的写槽：应答要 300 ms 才回

            using (CancellationTokenSource impatient = new(TimeSpan.FromMilliseconds(50)))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.WriteAsync(second, impatient.Token));
            }
            Assert.AreEqual(block, stream.Position, "没发出去的那一段不算写过");

            await stream.WriteAsync(second, harness.Token);
            await stream.FlushAsync(harness.Token);
            Assert.AreEqual(2L * block, stream.DurableLength);
        }

        Assert.AreSequenceEqual([.. first, .. second], [.. harness.SftpServer.Nodes["/home/joe/slot.bin"].Content]);
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount, "句柄照常关掉");
    }

    /// <summary>
    /// 读与写都还在途时连接断了：调用方拿到的异常是它自己 await 的那些，库内部的任务（在途的写、预读、流水线）
    /// 不留下没人观察的异常 —— 断线时几十个请求一起失败，宿主的崩溃日志里不该全是它们。
    /// </summary>
    [TestMethod]
    public async Task 读写在途时断线不留下没人观察的异常()
    {
        List<Exception> unobserved = await CollectUnobservedAsync(DisconnectWithIoInFlightAsync);

        Assert.IsEmpty(unobserved, string.Join(Environment.NewLine, unobserved.Select(e => e.InnerException?.ToString() ?? e.ToString())));

        static async Task DisconnectWithIoInFlightAsync()
        {
            byte[] content = new byte[4 * 1024 * 1024];
            await using Harness harness = await Harness.StartAsync(
                server => server.AddFile("/home/joe/big.bin", content),
                sftpOptions: new TestSftpOptions
                {
                    DelayWriteReplies = TimeSpan.FromSeconds(5),
                    HoldReadReplyAtOffset = 1024 * 1024,
                });

            SftpFileStream writer = await harness.Sftp.OpenWriteAsync("/home/joe/up.bin", cancellationToken: harness.Token);
            SftpFileStream reader = await harness.Sftp.OpenReadAsync("/home/joe/big.bin", harness.Token);

            byte[] chunk = new byte[harness.Sftp.BlockSize];
            for (int i = 0; i < 16; i++)
            {
                await writer.WriteAsync(chunk, harness.Token);   // 应答 5 秒后才回：这些写都在途
            }
            Task<int> reading = Task.Run(async () =>
            {
                byte[] buffer = new byte[64 * 1024];
                int total = 0;
                while (true)
                {
                    int read = await reader.ReadAsync(buffer, harness.Token);
                    if (read == 0)
                    {
                        return total;
                    }
                    total += read;
                }
            });
            await Task.Delay(200);   // 读到 1 MiB 处卡住，预读的请求在途

            await harness.Connection.DisposeAsync();   // 断线

            // 调用方自己 await 的那些照常拿到异常（这是被观察了的）。
            try
            {
                await reading;
            }
            catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
            try
            {
                await writer.FlushAsync(harness.Token);
            }
            catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or OperationCanceledException)
            {
            }

            // 关流也一样：写入流关闭时报「传输中断、从 N 续传」，那是给调用方的。
            foreach (SftpFileStream stream in new[] { writer, reader })
            {
                try
                {
                    await stream.DisposeAsync();
                }
                catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
        }
    }

    [TestMethod]
    public async Task FlushAsync被取消只是不再等_之后照常冲完()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { DelayWriteReplies = TimeSpan.FromMilliseconds(300) });

        byte[] payload = new byte[harness.Sftp.BlockSize * 2];
        Random.Shared.NextBytes(payload);

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/up.bin", cancellationToken: harness.Token);
        await stream.WriteAsync(payload, harness.Token);

        using (CancellationTokenSource impatient = new(TimeSpan.FromMilliseconds(20)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.FlushAsync(impatient.Token));
        }

        await stream.FlushAsync(harness.Token);
        Assert.AreEqual(payload.Length, stream.DurableLength, "取消 Flush 不是写入失败");
    }

    [TestMethod]
    public async Task OPEN收到DATA应答时是协议错误_不把数据当句柄()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("x")),
            new TestSftpOptions { WrongOpenReply = true });

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await harness.Sftp.OpenReadAsync("/home/joe/a.txt", harness.Token));

        Assert.AreEqual(SshFailureReason.ProtocolError, error.Reason);
    }

    [TestMethod]
    public async Task WRITE收到非STATUS应答时不算确认()
    {
        // 曾经 WRITE 收到任何非 STATUS 的应答都被记成已确认：DurableLength 失真，续传点跨过了没确认的数据。
        await using Harness harness = await Harness.StartAsync(sftpOptions: new TestSftpOptions { WrongWriteReply = true });

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/up.bin", cancellationToken: harness.Token);
        await stream.WriteAsync(new byte[harness.Sftp.BlockSize], harness.Token);

        SftpTransferInterruptedException error = await Assert.ThrowsExactlyAsync<SftpTransferInterruptedException>(
            async () => await stream.FlushAsync(harness.Token));

        Assert.AreEqual(0, stream.DurableLength, "类型对不上的应答不是确认");
        Assert.IsInstanceOfType<SshProtocolException>(error.InnerException);
        await Assert.ThrowsAsync<SshException>(async () => await stream.DisposeAsync());
    }

    [TestMethod]
    public async Task 服务端宣告了句柄上限时按它排队_关一个才开得了下一个()
    {
        // 曾经读了不用：并发传输撞上服务端的句柄上限时只会得到一个随机的「操作失败」。
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddFile("/home/joe/a", Text("a"));
                server.AddFile("/home/joe/b", Text("b"));
                server.AddFile("/home/joe/c", Text("c"));
            },
            new TestSftpOptions { Limits = new SftpLimits(262_144, 261_120, 261_120, MaxOpenHandles: 2) });

        SftpFileStream a = await harness.Sftp.OpenReadAsync("/home/joe/a", harness.Token);
        SftpFileStream b = await harness.Sftp.OpenReadAsync("/home/joe/b", harness.Token);
        Assert.AreEqual(0, harness.Sftp.FreeHandleSlots);

        Task<SftpFileStream> third = harness.Sftp.OpenReadAsync("/home/joe/c", harness.Token).AsTask();
        await Task.Delay(100, harness.Token);
        Assert.IsFalse(third.IsCompleted, "额度用完了就排队，而不是去撞服务端的上限");

        await a.DisposeAsync();
        await using SftpFileStream c = await third.WaitAsync(TimeSpan.FromSeconds(10));
        await b.DisposeAsync();

        // 只读流的 CLOSE 在后台发，额度等应答回来才还。
        await EventuallyAsync(() => harness.Sftp.FreeHandleSlots == 1, harness.Token);
    }

    [TestMethod]
    public async Task 列目录预取下一批_调用方还在处理这一批时下一个READDIR已经发出()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            for (int i = 0; i < 7; i++)
            {
                server.AddFile($"/home/joe/f{i}.txt", Text("x"));
            }
        });

        int seen = 0;
        await foreach (SftpDirectoryEntry _ in harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            if (seen++ == 0)
            {
                // 还停在第一批的第一项上：曾经要等这一批处理完才发下一个 READDIR，这里会一直等不到。
                using CancellationTokenSource patience = new(TimeSpan.FromSeconds(5));
                await EventuallyAsync(() => harness.SftpServer.ReadDirRequests >= 2, patience.Token);
            }
        }

        Assert.AreEqual(7, seen);
    }

    [TestMethod]
    public async Task 列目录跨多批()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            for (int i = 0; i < 7; i++)
            {
                server.AddFile($"/home/joe/f{i}.txt", Text($"内容 {i}"));
            }
        });

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in
            harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        // 每批只给 2 项 —— READDIR 返回的是**一批**，不是全部。
        Assert.HasCount(7, entries);
        Assert.AreSequenceEqual(
            [.. Enumerable.Range(0, 7).Select(i => $"f{i}.txt")], [.. entries.Select(e => e.Name)], SequenceOrder.InAnyOrder);
        Assert.AreEqual("/home/joe/f0.txt", entries.First(e => e.Name == "f0.txt").FullPath);
    }

    [TestMethod]
    public async Task 名字不合法的目录项被丢掉_不拼出目录以外的路径()
    {
        // 服务端回 `../x`、`a/b` 或空名字：拼出来的 FullPath 指向这个目录以外（或就是它自己），
        // 照着它递归复制、删除，动的就是别处的东西。
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/ok.txt", Text("x")),
            new TestSftpOptions { ExtraDirectoryEntryNames = ["../escape", "a/b", "/etc/passwd", "", "nul\0x", "..", "."] });

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in
            harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        Assert.AreSequenceEqual(["ok.txt"], [.. entries.Select(e => e.Name)]);
        Assert.AreEqual(5, harness.Sftp.MalformedEntriesSkipped, "「.」「..」是按选项过滤的，不算不合法");
    }

    [TestMethod]
    public async Task 服务端不再确认写入时关闭有时限_报带续传点的中断而不是一直等()
    {
        // 第二块之后服务端不再应答：曾经关闭（释放）一直等下去 —— 关标签页、取消上传都会挂住。
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { FailWritesAfter = 1 },
            clientOptions: new SftpOptions { CloseTimeout = TimeSpan.FromMilliseconds(300) });
        int block = harness.Sftp.BlockSize;

        SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/stall.bin", cancellationToken: harness.Token);
        await stream.WriteAsync(new byte[block * 3], harness.Token);

        SftpTransferInterruptedException error = await Assert.ThrowsExactlyAsync<SftpTransferInterruptedException>(
            async () => await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(block, error.DurableLength, "只有第一块确认了");
        Assert.AreEqual(SshFailureReason.Timeout, error.Reason);
    }

    /// <summary>
    /// 服务端拒写（磁盘满）不是断线：原因码随内层的 SFTP 错误，不可重试。
    /// 曾经一律是 ClosedByPeer —— 按原因码判断的调用方会把「磁盘满」当成断线、照样去续传。
    /// </summary>
    [TestMethod]
    public async Task 服务端拒写时中断的原因码不是断线()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { RejectWritesWith = SftpStatusCode.Failure });

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/full.bin", cancellationToken: harness.Token);
        await stream.WriteAsync(new byte[harness.Sftp.BlockSize], harness.Token);

        SftpTransferInterruptedException error = await Assert.ThrowsExactlyAsync<SftpTransferInterruptedException>(
            async () => await stream.FlushAsync(harness.Token));

        Assert.AreNotEqual(SshFailureReason.ClosedByPeer, error.Reason);
        Assert.IsFalse(error.IsRetryable);
        Assert.IsInstanceOfType<SftpException>(error.InnerException);
        Assert.AreEqual(0, error.DurableLength);
        await Assert.ThrowsAsync<SshException>(async () => await stream.DisposeAsync());
    }

    [TestMethod]
    [DataRow(false, DisplayName = "取消")]
    [DataRow(true, DisplayName = "本端释放")]
    public void 取消与本端释放引起的中断是Aborted(bool disposed)
    {
        Exception inner = disposed ? new ObjectDisposedException("stream") : new OperationCanceledException();

        Assert.AreEqual(SshFailureReason.Aborted, new SftpTransferInterruptedException(10, "中断", inner).Reason);
    }

    [TestMethod]
    public void 断线引起的中断仍是ClosedByPeer()
    {
        SshConnectionClosedException dropped = new(SshFailureReason.ClosedByPeer, SshPhase.Open, "断了");

        Assert.AreEqual(SshFailureReason.ClosedByPeer, new SftpTransferInterruptedException(10, "中断", dropped).Reason);
        Assert.AreEqual(SshFailureReason.ClosedByPeer, new SftpTransferInterruptedException(10, "中断").Reason);
    }

    [TestMethod]
    public async Task 只读流关闭不等CLOSE的应答_可写的流照等()
    {
        // 只读的流关不上无关紧要（不报），那就不必等这一轮往返 —— 小文件下载省掉一整轮。
        // 可写的流要看 CLOSE 的状态（有的服务端到关闭时才报出写入失败），照等。
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("x")),
            new TestSftpOptions { DelayCloseReplies = TimeSpan.FromMilliseconds(800) });

        SftpFileStream reading = await harness.Sftp.OpenReadAsync("/home/joe/a.txt", harness.Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await reading.DisposeAsync();
        Assert.IsLessThan(500, watch.ElapsedMilliseconds, "只读流的关闭不该等 CLOSE 的应答");

        SftpFileStream writing = await harness.Sftp.OpenWriteAsync("/home/joe/w.bin", cancellationToken: harness.Token);
        watch.Restart();
        await writing.DisposeAsync();
        Assert.IsGreaterThanOrEqualTo(700, watch.ElapsedMilliseconds, "可写的流要等 CLOSE 的状态");

        Assert.AreEqual(2, harness.SftpServer.ReceivedTypes.Count(t => t == SftpMessageType.Close), "只读流的 CLOSE 照样发了");
    }

    [TestMethod]
    public async Task 带CREAT打开时没给权限就补上0644()
    {
        // 不传的话服务端用它自己的默认值（受 umask 影响），结果不可预测 —— 宿主的 Create / CreateNew / OpenOrCreate 都走这条。
        await using Harness harness = await Harness.StartAsync();

        await using (await harness.Sftp.OpenAsync(
            "/home/joe/new.txt", SftpOpenModes.Write | SftpOpenModes.Create | SftpOpenModes.Truncate, cancellationToken: harness.Token))
        {
        }

        SftpFileAttributes sent = harness.SftpServer.LastOpenAttributes!.Value;
        Assert.IsTrue(sent.HasPermissions);
        Assert.AreEqual(SftpProtocol.DefaultFilePermissions, sent.PermissionBits);
    }

    [TestMethod]
    [DataRow(SftpOpenModes.Write | SftpOpenModes.Truncate, DisplayName = "TRUNC 没配 CREAT")]
    [DataRow(SftpOpenModes.Write | SftpOpenModes.Exclusive, DisplayName = "EXCL 没配 CREAT")]
    [DataRow(SftpOpenModes.Create, DisplayName = "既不读也不写")]
    public async Task 自相矛盾的打开方式在本地就拒(SftpOpenModes flags)
    {
        await using Harness harness = await Harness.StartAsync();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await harness.Sftp.OpenAsync("/home/joe/x", flags, cancellationToken: harness.Token));
        Assert.IsNull(harness.SftpServer.LastOpenAttributes, "一个 OPEN 都不该发出去");
    }

    [TestMethod]
    public async Task 按偏移读写也看打开方式_只读流上写不会弄坏关闭()
    {
        await using Harness harness = await Harness.StartAsync(server => server.AddFile("/home/joe/r.txt", Text("内容")));

        SftpFileStream reading = await harness.Sftp.OpenReadAsync("/home/joe/r.txt", harness.Token);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => await reading.WriteAtAsync(0, new byte[4], harness.Token));

        // 曾经那个 WRITE 发出去被拒，记成写入故障 —— 关闭这个只读流时抛「传输中断」。
        await reading.DisposeAsync();

        await using SftpFileStream writing = await harness.Sftp.OpenWriteAsync("/home/joe/w.bin", cancellationToken: harness.Token);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => await writing.ReadAtAsync(0, new byte[4], harness.Token));
    }

    [TestMethod]
    public async Task 截短之后DurableLength跟着回退()
    {
        // 曾经不回退：之后再断开，报出的续传点会跨过已经被截掉的数据。
        await using Harness harness = await Harness.StartAsync();
        int block = harness.Sftp.BlockSize;

        await using SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/t.bin", cancellationToken: harness.Token);
        await stream.WriteAsync(new byte[block * 3], harness.Token);
        await stream.FlushAsync(harness.Token);
        Assert.AreEqual(block * 3, stream.DurableLength);

        await stream.SetLengthAsync(block, harness.Token);

        Assert.AreEqual(block, stream.DurableLength);
        Assert.HasCount(block, harness.SftpServer.Nodes["/home/joe/t.bin"].Content);
    }

    [TestMethod]
    public async Task READDIR一直回空批时判协议错误_不无限循环()
    {
        await using Harness harness = await Harness.StartAsync(sftpOptions: new TestSftpOptions { EmptyReadDirBatches = true });

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(async () =>
        {
            await foreach (SftpDirectoryEntry _ in harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
            {
            }
        });

        Assert.Contains("空的一批", error.Message);
    }

    [TestMethod]
    public async Task 名字含NUL的链接项不让整个目录列不出来()
    {
        // 曾经补这个链接的 READLINK / STAT 时在本地抛 ArgumentException（路径里有 NUL），
        // 而「悄悄」版本只吞 SFTP 异常 —— 一个怪链接让整个目录列不出来。现在这种名字在列表里就被丢掉。
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddFile("/home/joe/ok.txt", Text("x"));
            server.AddSymbolicLink("/home/joe/li\0nk", "/home/joe/ok.txt");
        });

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        Assert.AreSequenceEqual(["ok.txt"], [.. entries.Select(e => e.Name)]);
    }

    [TestMethod]
    public async Task READDIR不带权限位时补一次LSTAT_目录照样认得出()
    {
        // 没有权限位就分不出是不是目录 —— 曾经一律当成文件，宿主进不了这样的目录。
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddDirectory("/home/joe/sub");
                server.AddFile("/home/joe/a.txt", Text("x"));
            },
            new TestSftpOptions { OmitPermissionsInReadDir = true });

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        Assert.IsTrue(entries.Single(e => e.Name == "sub").IsDirectory);
        Assert.IsFalse(entries.Single(e => e.Name == "a.txt").IsDirectory);
    }

    [TestMethod]
    public async Task 取单个路径的完整条目_与列目录给出的一样()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddDirectory("/home/joe/real");
            server.AddSymbolicLink("/home/joe/link", "/home/joe/real");
            server.AddSymbolicLink("/home/joe/dangling", "/nowhere");
            server.AddFile(@"/home/joe/a\b.txt", Text("x"));
        });

        SftpDirectoryEntry? link = await harness.Sftp.GetEntryAsync("/home/joe/link", harness.Token);
        Assert.IsNotNull(link);
        Assert.IsTrue(link.Value.IsSymbolicLink);
        Assert.IsTrue(link.Value.IsDirectory, "其余字段描述的是链接指向的对象");
        Assert.AreEqual("/home/joe/real", link.Value.LinkTarget);
        Assert.AreEqual("link", link.Value.Name);

        SftpDirectoryEntry? dangling = await harness.Sftp.GetEntryAsync("/home/joe/dangling", harness.Token);
        Assert.IsTrue(dangling!.Value.IsBrokenLink, "断链照样有条目，不是「找不到」");

        SftpDirectoryEntry? file = await harness.Sftp.GetEntryAsync(@"/home/joe/a\b.txt", harness.Token);
        Assert.AreEqual(@"a\b.txt", file!.Value.Name, "名字按 SFTP 的「/」取，反斜杠是名字的一部分");

        Assert.IsNull(await harness.Sftp.GetEntryAsync("/home/joe/none", harness.Token));
    }

    [TestMethod]
    [DataRow("/a/b", "b")]
    [DataRow("/a/b/", "b")]
    [DataRow("/", "/")]
    [DataRow("rel", "rel")]
    [DataRow(@"dir/a\b", @"a\b")]
    public void 路径的最后一段按斜杠取(string path, string name) =>
        Assert.AreEqual(name, SftpFileSystem.NameOf(path));

    [TestMethod]
    public async Task 符号链接保留是链接这个事实()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddDirectory("/home/joe/real");
            server.AddSymbolicLink("/home/joe/link", "/home/joe/real");
        });

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in
            harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        SftpDirectoryEntry link = entries.First(e => e.Name == "link");

        // 直接用跟随的 STAT 会让「这是个链接」彻底消失 —— 于是删除一个指向目录的链接
        // 会变成递归删除目标目录里的东西。那是数据事故。
        Assert.IsTrue(link.IsSymbolicLink, "必须知道这一项本身是链接");
        Assert.AreEqual("/home/joe/real", link.LinkTarget);
        Assert.IsTrue(link.IsDirectory, "其余字段描述的是链接指向的对象");
        Assert.IsFalse(link.IsBrokenLink);
    }

    [TestMethod]
    public async Task 断链保留链接自身的属性而不是消失()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddSymbolicLink("/home/joe/dangling", "/nowhere"));

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in
            harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        SftpDirectoryEntry broken = entries.Single(e => e.Name == "dangling");

        // 返回 null 是不对的：链接本身是存在的，删除它不能先报「找不到」。
        Assert.IsTrue(broken.IsSymbolicLink);
        Assert.IsTrue(broken.IsBrokenLink);
        Assert.AreEqual("/nowhere", broken.LinkTarget);
        Assert.IsFalse(broken.IsDirectory);
    }

    [TestMethod]
    public async Task 建目录与删空目录()
    {
        await using Harness harness = await Harness.StartAsync();

        await harness.Sftp.CreateDirectoryAsync("/home/joe/sub", cancellationToken: harness.Token);
        Assert.IsTrue(await harness.Sftp.ExistsAsync("/home/joe/sub", harness.Token));

        await harness.Sftp.DeleteDirectoryAsync("/home/joe/sub", harness.Token);
        Assert.IsFalse(await harness.Sftp.ExistsAsync("/home/joe/sub", harness.Token));
    }

    [TestMethod]
    public async Task 删非空目录时服务端原话进异常()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddDirectory("/home/joe/full");
            server.AddFile("/home/joe/full/x.txt", Text("x"));
        });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.DeleteDirectoryAsync("/home/joe/full", harness.Token));

        // 「目录非空」在 v3 里只有码 4（万能错误码）。那段文本是**唯一**能区分
        // 它与「磁盘满」「权限不足」的信息，所以必须原样留着。
        Assert.AreEqual(SftpStatusCode.Failure, error.StatusCode);
        Assert.AreEqual("目录非空", error.ServerMessage);
        Assert.Contains("目录非空", error.Message);
    }

    [TestMethod]
    public async Task 文件不存在是一个可判定的状态()
    {
        await using Harness harness = await Harness.StartAsync();

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.GetAttributesAsync("/home/joe/nope.txt", harness.Token));

        Assert.IsTrue(error.IsNotFound);
        Assert.IsFalse(await harness.Sftp.ExistsAsync("/home/joe/nope.txt", harness.Token));
    }

    // ------------------------------------------------------------ 符号链接

    [TestMethod]
    public async Task 建符号链接时参数顺序按OpenSSH而不是按draft()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/target.txt", Text("我是目标")));

        await harness.Sftp.CreateSymbolicLinkAsync(
            "/home/joe/mylink", "/home/joe/target.txt", harness.Token);

        // ⚠️ **这是 SFTP 里最有名的一个坑。**
        //
        // draft-02 规定先 linkpath 后 targetpath，但 OpenSSH 的实现把两者写反了
        // （bugzilla #861），而 OpenSSH 是事实标准 —— 所有服务端都按它的顺序解析。
        //
        // 弄反了**不会报错**，只是把链接建在你本想指向的位置上：
        // 下面这两条断言会变成 /home/joe/target.txt 成了一个指向 /home/joe/mylink 的链接。
        Assert.IsTrue(harness.SftpServer.Nodes.ContainsKey("/home/joe/mylink"),
            "链接必须建在 linkPath 上");
        Assert.AreEqual("/home/joe/target.txt", harness.SftpServer.Nodes["/home/joe/mylink"].LinkTarget,
            "链接必须指向 targetPath");

        Assert.IsNull(harness.SftpServer.Nodes["/home/joe/target.txt"].LinkTarget,
            "目标文件不该反过来变成一个链接 —— 那正是参数顺序弄反的症状");
    }

    /// <summary>
    /// 〔Q12〕建完回读自检：服务端按 draft 的顺序解析时，链接建在了本想指向的位置上 —— 删掉建错的那一条、报错。
    /// 曾经不查，链接建错地方而且不报错。
    /// </summary>
    [TestMethod]
    public async Task 服务端按相反顺序建了链接时删掉建错的那条并报错()
    {
        await using Harness harness = await Harness.StartAsync(sftpOptions: new TestSftpOptions { SymLinkInDraftOrder = true });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(async () =>
            await harness.Sftp.CreateSymbolicLinkAsync("/home/joe/mylink", "/home/joe/not-yet", harness.Token));

        Assert.AreEqual(SftpOperation.CreateSymbolicLink, error.Operation);
        Assert.Contains("相反的参数顺序", error.Message);
        Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey("/home/joe/not-yet"), "建错的那条要删掉");
        Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey("/home/joe/mylink"));
    }

    [TestMethod]
    public async Task 读符号链接拿到原文()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddSymbolicLink("/home/joe/rel", "../elsewhere"));

        string target = await harness.Sftp.ReadSymbolicLinkAsync("/home/joe/rel", harness.Token);

        Assert.AreEqual("../elsewhere", target, "相对路径要原样给出，不替使用者解析");
    }

    [TestMethod]
    public async Task 建硬链接需要扩展支持()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/orig.txt", Text("内容")));

        await harness.Sftp.CreateHardLinkAsync("/home/joe/hard.txt", "/home/joe/orig.txt", harness.Token);

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/hard.txt", harness.Token);
        Assert.AreEqual("内容", Encoding.UTF8.GetString(content));
    }

    [TestMethod]
    public async Task 服务端没有hardlink扩展时如实报不支持()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/orig.txt", Text("内容")),
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.CreateHardLinkAsync(
                "/home/joe/hard.txt", "/home/joe/orig.txt", harness.Token));

        Assert.IsTrue(error.IsUnsupported);
        Assert.AreSequenceEqual([SftpExtensionNames.HardLink], [.. error.MissingExtensions]);
    }

    // ------------------------------------------------------------ uid / gid 翻成名字

    /// <summary>users-groups-by-id：与问的 id 一一对应，不认识的为 null。</summary>
    [TestMethod]
    public async Task 把uid与gid翻成名字_不认识的为null()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.UsersGroupsById] });

        SftpIdNames names = await harness.Sftp.LookupUserAndGroupNamesAsync([1000, 7, 0], [100], harness.Token);

        Assert.AreSequenceEqual(["joe", null, "root"], [.. names.UserNames]);
        Assert.AreSequenceEqual(["users"], [.. names.GroupNames]);
    }

    /// <summary>回的名字比问的 id 少：对端的错，报格式不对，而不是把名字错位地安到别的 id 上。</summary>
    [TestMethod]
    public async Task 名字条数对不上时报格式不对()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.UsersGroupsById], DropOneIdName = true });

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(async () =>
            await harness.Sftp.LookupUserAndGroupNamesAsync([1000, 0], [], harness.Token));
        Assert.Contains("users-groups-by-id", error.Message);
    }

    /// <summary>没有扩展时报不支持；一次问太多个 id 时在发请求之前就拒。</summary>
    [TestMethod]
    public async Task 没有扩展时报不支持_问太多时当场拒()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(async () =>
            await harness.Sftp.LookupUserAndGroupNamesAsync([0], [0], harness.Token));
        Assert.IsTrue(error.IsUnsupported);
        Assert.AreSequenceEqual([SftpExtensionNames.UsersGroupsById], [.. error.MissingExtensions]);

        uint[] tooMany = [.. Enumerable.Range(0, SftpFileSystem.MaxIdsPerLookup + 1).Select(i => (uint)i)];
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Sftp.LookupUserAndGroupNamesAsync(tooMany, [], harness.Token));
    }

    // ------------------------------------------------------------ 服务端内复制

    /// <summary>copy-data：按段复制（每段一个请求），每段报一次进度，内容与源逐字节一致，目标按源的权限位创建。</summary>
    [TestMethod]
    public async Task 服务端内复制按段进行且每段报进度()
    {
        byte[] content = [.. Enumerable.Range(0, 10_000).Select(i => (byte)(i * 7))];
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/src.bin", content, permissions: 0b111_101_000),
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.CopyData] });
        harness.Sftp.CopySegmentBytes = 4096;
        List<long> reported = [];

        await harness.Sftp.CopyFileAsync("/home/joe/src.bin", "/home/joe/dst.bin", progress: new SyncProgress<long>(reported.Add), cancellationToken: harness.Token);

        Assert.AreSequenceEqual(content, await harness.Sftp.ReadAllBytesAsync("/home/joe/dst.bin", harness.Token));
        Assert.AreEqual(3, harness.SftpServer.CopyDataRequests, "10000 字节按 4096 一段是三段");
        Assert.AreSequenceEqual([4096, 8192, 10_000], [.. reported]);
        Assert.AreEqual(0b111_101_000u, (await harness.Sftp.GetAttributesAsync("/home/joe/dst.bin", harness.Token)).PermissionBits);
    }

    /// <summary>不覆盖时目标已存在就失败、目标原样不动；覆盖时截短重写。</summary>
    [TestMethod]
    public async Task 服务端内复制不覆盖时目标已存在就失败_覆盖时重写()
    {
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddFile("/home/joe/src.txt", Text("新"));
                server.AddFile("/home/joe/dst.txt", Text("原来的长内容"));
            },
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.CopyData] });

        await Assert.ThrowsExactlyAsync<SftpException>(async () =>
            await harness.Sftp.CopyFileAsync("/home/joe/src.txt", "/home/joe/dst.txt", cancellationToken: harness.Token));
        Assert.AreEqual("原来的长内容", Encoding.UTF8.GetString(await harness.Sftp.ReadAllBytesAsync("/home/joe/dst.txt", harness.Token)));

        await harness.Sftp.CopyFileAsync("/home/joe/src.txt", "/home/joe/dst.txt", overwrite: true, cancellationToken: harness.Token);
        Assert.AreEqual("新", Encoding.UTF8.GetString(await harness.Sftp.ReadAllBytesAsync("/home/joe/dst.txt", harness.Token)));
    }

    /// <summary>没有 copy-data：如实报不支持，而且什么都没打开、没建。</summary>
    [TestMethod]
    public async Task 没有copy_data时报不支持且不建目标()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/src.txt", Text("x")),
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(async () =>
            await harness.Sftp.CopyFileAsync("/home/joe/src.txt", "/home/joe/dst.txt", cancellationToken: harness.Token));

        Assert.IsTrue(error.IsUnsupported);
        Assert.AreSequenceEqual([SftpExtensionNames.CopyData], [.. error.MissingExtensions]);
        Assert.IsFalse(await harness.Sftp.ExistsAsync("/home/joe/dst.txt", harness.Token));
    }

    // ------------------------------------------------------------ 按句柄设时间 / 不跟随链接设属性

    /// <summary>
    /// 关闭之前按句柄设修改时间（一次往返，省掉 STAT + SETSTAT）：先等攒着的写落地 ——
    /// 之后才到的写会把修改时间又改成「现在」。
    /// </summary>
    [TestMethod]
    public async Task 按句柄设时间时先让攒着的写落地()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { WriteTouchesModifyTime = true });
        DateTimeOffset mtime = new(2024, 2, 3, 4, 5, 6, TimeSpan.Zero);

        await using (SftpFileStream file = await harness.Sftp.OpenWriteAsync("/home/joe/t.txt", cancellationToken: harness.Token))
        {
            await file.WriteAsync(Text("还在缓冲里的一小段"), harness.Token);
            await file.SetTimesAsync(mtime, mtime, harness.Token);
        }

        SftpFileAttributes attributes = await harness.Sftp.GetAttributesAsync("/home/joe/t.txt", harness.Token);
        Assert.AreEqual(mtime, attributes.LastWriteTime);
        Assert.AreEqual("还在缓冲里的一小段", Encoding.UTF8.GetString(await harness.Sftp.ReadAllBytesAsync("/home/joe/t.txt", harness.Token)));
    }

    /// <summary>lsetstat：改的是链接自身，目标不动；SETSTAT 跟随链接改到目标。</summary>
    [TestMethod]
    public async Task 不跟随链接设属性改的是链接自身()
    {
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddFile("/home/joe/target.txt", Text("目标"));
                server.AddSymbolicLink("/home/joe/link", "/home/joe/target.txt");
            },
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.LSetStat] });
        DateTimeOffset linkTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await harness.Sftp.SetLinkAttributesAsync("/home/joe/link", SftpFileAttributes.WithTimes(linkTime, linkTime), harness.Token);

        Assert.AreEqual(linkTime, (await harness.Sftp.GetLinkAttributesAsync("/home/joe/link", harness.Token)).LastWriteTime);
        Assert.AreNotEqual(linkTime, (await harness.Sftp.GetAttributesAsync("/home/joe/target.txt", harness.Token)).LastWriteTime, "目标不该被改到");
    }

    /// <summary>没有 lsetstat：如实报不支持，不退化成跟随链接的 SETSTAT（那会改到目标）。</summary>
    [TestMethod]
    public async Task 没有lsetstat时报不支持而不是改到目标()
    {
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddFile("/home/joe/target.txt", Text("目标"));
                server.AddSymbolicLink("/home/joe/link", "/home/joe/target.txt");
            },
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(async () =>
            await harness.Sftp.SetLinkAttributesAsync("/home/joe/link", SftpFileAttributes.WithPermissions(0x1FF), harness.Token));

        Assert.IsTrue(error.IsUnsupported);
        Assert.AreSequenceEqual([SftpExtensionNames.LSetStat], [.. error.MissingExtensions]);
    }

    // ------------------------------------------------------------ 展开 ~

    /// <summary>有 expand-path@openssh.com：整条交给服务端，~ 与 ~用户名都认。</summary>
    [TestMethod]
    [DataRow("~", "/home/joe")]
    [DataRow("~/projects", "/home/joe/projects")]
    [DataRow("~alice", "/srv/alice")]
    [DataRow("~alice/shared", "/srv/alice/shared")]
    [DataRow("/etc", "/etc")]
    public async Task 有expand_path时整条交给服务端(string input, string expected)
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.ExpandPath] });

        Assert.AreEqual(expected, await harness.Sftp.ExpandPathAsync(input, harness.Token));
    }

    /// <summary>没有 expand-path：~ 与 ~/… 用登录时的工作目录拼，~用户名用 home-directory。</summary>
    [TestMethod]
    [DataRow("~", "/home/joe")]
    [DataRow("~/projects", "/home/joe/projects")]
    [DataRow("~alice", "/srv/alice")]
    [DataRow("~alice/shared", "/srv/alice/shared")]
    public async Task 没有expand_path时用工作目录与home_directory拼(string input, string expected)
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.HomeDirectory] });

        Assert.AreEqual(expected, await harness.Sftp.ExpandPathAsync(input, harness.Token));
    }

    /// <summary>两个扩展都没有：~ 照样能展开（工作目录），~用户名如实报不支持。</summary>
    [TestMethod]
    public async Task 两个扩展都没有时用户名的家目录报不支持()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        Assert.AreEqual("/home/joe/x", await harness.Sftp.ExpandPathAsync("~/x", harness.Token));
        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.ExpandPathAsync("~alice", harness.Token));
        Assert.IsTrue(error.IsUnsupported);
        Assert.AreEqual(SftpOperation.ExpandPath, error.Operation);
        Assert.AreSequenceEqual([SftpExtensionNames.ExpandPath, SftpExtensionNames.HomeDirectory], [.. error.MissingExtensions]);
    }

    // ------------------------------------------------------------ 文件系统用量

    /// <summary>statvfs@openssh.com：11 个字段按顺序解出；字节数按 f_frsize 算，「还能写多少」看 f_bavail。</summary>
    [TestMethod]
    public async Task 文件系统用量按字段解出且字节数按基本块算()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddDirectory("/data"),
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits, SftpExtensionNames.StatVfs] });

        SftpFileSystemInfo info = await harness.Sftp.GetFileSystemInfoAsync("/data", harness.Token);

        Assert.AreEqual(new SftpFileSystemInfo(4096, 1024, 1_000_000, 400_000, 300_000, 65_536, 60_000, 59_000, 0xABCD, 0x1, 255), info);
        Assert.AreEqual(1_000_000UL * 1024, info.TotalBytes);
        Assert.AreEqual(400_000UL * 1024, info.FreeBytes);
        Assert.AreEqual(300_000UL * 1024, info.AvailableBytes, "上传前预检看的是普通用户还能写多少");
        Assert.IsTrue(info.IsReadOnly);
    }

    /// <summary>没有这个扩展：如实报不支持，而且不发请求（能力位已经说了）。</summary>
    [TestMethod]
    public async Task 服务端没有statvfs扩展时如实报不支持()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddDirectory("/data"),
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        Assert.IsFalse(harness.Sftp.Capabilities.HasStatVfs);
        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.GetFileSystemInfoAsync("/data", harness.Token));

        Assert.IsTrue(error.IsUnsupported);
        Assert.AreEqual(SftpOperation.GetFileSystemInfo, error.Operation);
        Assert.AreSequenceEqual([SftpExtensionNames.StatVfs], [.. error.MissingExtensions]);
        Assert.AreEqual("", error.ServerMessage, "服务端没说话：说明不进 ServerMessage");
    }

    [TestMethod]
    public void 字节数溢出时饱和_基本块为零时按块大小算()
    {
        Assert.AreEqual(ulong.MaxValue, new SftpFileSystemInfo(4096, 4096, ulong.MaxValue / 2, 0, 0, 0, 0, 0, 0, 0, 255).TotalBytes);
        Assert.AreEqual(10UL * 512, new SftpFileSystemInfo(512, 0, 10, 0, 0, 0, 0, 0, 0, 0, 255).TotalBytes);
    }

    // ------------------------------------------------------------ 重命名

    [TestMethod]
    public async Task 普通重命名在目标存在时失败()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddFile("/home/joe/a.txt", Text("A"));
            server.AddFile("/home/joe/b.txt", Text("B"));
        });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.RenameAsync(
                "/home/joe/a.txt", "/home/joe/b.txt", overwrite: false, harness.Token));

        Assert.AreEqual("目标已存在", error.ServerMessage);
    }

    [TestMethod]
    public async Task 原子覆盖式重命名走posix_rename()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddFile("/home/joe/a.txt", Text("A"));
            server.AddFile("/home/joe/b.txt", Text("B"));
        });

        await harness.Sftp.RenameAsync("/home/joe/a.txt", "/home/joe/b.txt", overwrite: true, harness.Token);

        Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey("/home/joe/a.txt"));
        Assert.AreSequenceEqual(Text("A"), [.. harness.SftpServer.Nodes["/home/joe/b.txt"].Content]);
    }

    [TestMethod]
    public async Task 服务端不支持原子重命名时不静默降级()
    {
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddFile("/home/joe/a.txt", Text("A"));
                server.AddFile("/home/joe/b.txt", Text("B"));
            },
            new TestSftpOptions { Extensions = [SftpExtensionNames.Limits] });

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(
            async () => await harness.Sftp.RenameAsync(
                "/home/joe/a.txt", "/home/joe/b.txt", overwrite: true, harness.Token));

        // 悄悄换成普通 rename 会让上层以为自己拿到了原子语义。
        // 「先删再改名」不是原子的 —— 中途失败会两个都没有。
        Assert.IsTrue(error.IsUnsupported);
        Assert.Contains("不是原子的", error.Message);
        Assert.AreSequenceEqual([SftpExtensionNames.PosixRename], [.. error.MissingExtensions]);
        Assert.IsFalse(harness.Sftp.Capabilities.HasPosixRename);
    }

    // ------------------------------------------------------------ 属性

    [TestMethod]
    public async Task 改权限()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/s.sh", Text("#!/bin/sh")));

        await harness.Sftp.SetPermissionsAsync("/home/joe/s.sh", 0b111_101_101, harness.Token);

        Assert.AreEqual(0b111_101_101u, harness.SftpServer.Nodes["/home/joe/s.sh"].Permissions);
    }

    [TestMethod]
    public async Task 只改修改时间时不会把访问时间抹成1970()
    {
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                TestSftpNode node = server.AddFile("/home/joe/t.txt", Text("x"));
                node.AccessTime = 1_600_000_000;
                node.ModifyTime = 1_600_000_000;
            });

        var newTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await harness.Sftp.SetLastWriteTimeAsync("/home/joe/t.txt", newTime, harness.Token);

        TestSftpNode result = harness.SftpServer.Nodes["/home/joe/t.txt"];

        // atime 与 mtime **共用一个标志位**。只给 mtime 的话 atime 会被当成 0。
        // 所以实现要先把当前的 atime 取回来再一并写回。
        Assert.AreEqual(1_800_000_000u, result.ModifyTime);
        Assert.AreEqual(1_600_000_000u, result.AccessTime, "访问时间不该被抹掉");
    }

    [TestMethod]
    public async Task 属性里的文件类型来自权限高位()
    {
        await using Harness harness = await Harness.StartAsync(server =>
        {
            server.AddDirectory("/home/joe/d");
            server.AddFile("/home/joe/f.txt", Text("x"));
        });

        SftpFileAttributes dir = await harness.Sftp.GetAttributesAsync("/home/joe/d", harness.Token);
        SftpFileAttributes file = await harness.Sftp.GetAttributesAsync("/home/joe/f.txt", harness.Token);

        // v3 没有单独的类型字段 —— 类型只能从 permissions 的高位（S_IFMT）取。
        Assert.IsTrue(dir.IsDirectory);
        Assert.IsFalse(dir.IsRegularFile);
        Assert.IsTrue(file.IsRegularFile);
        Assert.IsFalse(file.IsDirectory);
        Assert.AreEqual(0b110_100_100u, file.PermissionBits, "PermissionBits 要去掉类型位");
    }

    // ------------------------------------------------------------ 资源

    [TestMethod]
    public async Task 读写之后句柄都关掉了()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("内容")));

        _ = await harness.Sftp.ReadAllBytesAsync("/home/joe/a.txt", harness.Token);
        await harness.Sftp.WriteAllBytesAsync("/home/joe/b.txt", Text("x"), cancellationToken: harness.Token);

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in
            harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        // 句柄泄漏在服务端是看不见的 —— 直到撞上 MaxSessions 或者 max-open-handles。
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount, "所有句柄都该被关掉");
        Assert.IsGreaterThan(0, harness.SftpServer.PeakOpenHandles, "确实开过句柄");
    }

    /// <summary>〔架构原则 1〕同步读写不提供 —— 当场说清楚该用哪个，而不是阻塞线程假装同步。</summary>
    [TestMethod]
    public async Task 同步读写明确不支持且指向异步版本()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("内容")));

        await using SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/a.txt", harness.Token);

        NotSupportedException read = Assert.ThrowsExactly<NotSupportedException>(() => stream.Read(new byte[4], 0, 4));
        Assert.Contains("ReadAsync", read.Message);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));

        // 同步 Flush 保留为不阻塞的空操作：包装流在自己的收尾里会同步调它。
        stream.Flush();
    }

    /// <summary>
    /// 等句柄时取消了 OPEN：服务端照样打开了文件，句柄晚到 —— 它必须被关掉，不能泄漏在服务端
    /// （sftp-server 的句柄数有上限，积多了新的 OPEN 会失败）。
    /// </summary>
    [TestMethod]
    public async Task 等句柄时取消打开_迟到的句柄被关掉()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/慢.txt", Text("内容")),
            sftpOptions: new TestSftpOptions { DelayOpenReplies = TimeSpan.FromMilliseconds(300) });

        using (CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await harness.Sftp.OpenReadAsync("/home/joe/慢.txt", cancel.Token));
        }

        for (int i = 0; i < 200 && harness.SftpServer.OpenHandleCount != 0; i++)
        {
            await Task.Delay(10, harness.Token);
        }

        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount, "被取消的 OPEN 迟到的句柄没有关掉");
    }

    /// <summary>同步释放不阻塞调用线程，句柄照样在后台关掉。</summary>
    [TestMethod]
    public async Task 同步释放不阻塞且句柄最终关闭()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("内容")));

        SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/a.txt", harness.Token);
        Assert.AreEqual(1, harness.SftpServer.OpenHandleCount);

        stream.Dispose();

        for (int i = 0; i < 200 && harness.SftpServer.OpenHandleCount != 0; i++)
        {
            await Task.Delay(10, harness.Token);
        }

        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount, "同步释放之后句柄没有关掉");
    }

    [TestMethod]
    public async Task 路径里含NUL在本地就被拒绝()
    {
        await using Harness harness = await Harness.StartAsync();

        // 不发给服务端：它在不同服务端上的行为从「截断」到「拒绝」都有，全是意外。
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await harness.Sftp.GetAttributesAsync("/home/joe/a\0b.txt", harness.Token));

        Assert.IsFalse(harness.SftpServer.Nodes.ContainsKey("/home/joe/a\0b.txt"));
    }

    // ------------------------------------------------------------ 句柄与边界

    [TestMethod]
    public async Task 可写的流关闭失败要报出来()
    {
        // 有的服务端（NFS、配额）直到关闭时才报出写入失败 —— 吞掉它，调用方就以为文件写好了。
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/in.txt", Text("x")),
            sftpOptions: new TestSftpOptions { FailCloseWith = SftpStatusCode.Failure });

        SftpFileStream stream = await harness.Sftp.OpenWriteAsync("/home/joe/out.txt", cancellationToken: harness.Token);
        await stream.WriteAsync(Text("data"), harness.Token);

        SftpException error = await Assert.ThrowsExactlyAsync<SftpException>(async () => await stream.DisposeAsync());
        Assert.Contains("配额", error.Message);

        // 只读的流关不上无关紧要，不报。
        SftpFileStream reader = await harness.Sftp.OpenReadAsync("/home/joe/in.txt", harness.Token);
        await reader.DisposeAsync();
    }

    [TestMethod]
    public async Task 句柄关掉之后流上的操作一律报已释放()
    {
        // OpenSSH 的句柄是表里的下标，关掉之后会分给下一个打开的文件 ——
        // 拿旧句柄再写一次，写进去的是别人的文件。
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("hello")));

        SftpFileStream stream = await harness.Sftp.OpenAsync(
            "/home/joe/a.txt", SftpOpenModes.Read | SftpOpenModes.Write,
            cancellationToken: harness.Token);
        await stream.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await stream.WriteAtAsync(0, Text("X"), harness.Token));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await stream.ReadAtAsync(0, new byte[4], harness.Token));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await stream.SetLengthAsync(0, harness.Token));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await stream.GetAttributesAsync(harness.Token));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await stream.FsyncAsync(harness.Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));

        // Stream 的约定：关闭之后 CanRead / CanWrite 为假 —— 包装流靠它们判断还能不能用。
        Assert.IsFalse(stream.CanRead);
        Assert.IsFalse(stream.CanWrite);

        Assert.AreEqual("hello", Encoding.UTF8.GetString([.. harness.SftpServer.Nodes["/home/joe/a.txt"].Content]));
    }

    [TestMethod]
    public async Task 数组版本的读写也走异步()
    {
        // Stream 的数组版本默认绕到同步读写上，而本类的同步读写直接抛 ——
        // 调用方写的明明是 await ReadAsync(buffer, 0, n)。
        await using Harness harness = await Harness.StartAsync();

        byte[] content = Text("array overloads");

        // 这条用例测的就是数组版本本身，分析器建议的 Memory 版本恰恰绕开了它。
#pragma warning disable CA1835
        await using (SftpFileStream writer = await harness.Sftp.OpenWriteAsync(
            "/home/joe/arr.txt", cancellationToken: harness.Token))
        {
            await writer.WriteAsync(content, 0, content.Length, harness.Token);
        }

        await using SftpFileStream reader = await harness.Sftp.OpenReadAsync("/home/joe/arr.txt", harness.Token);
        byte[] buffer = new byte[64];
        int read = await reader.ReadAsync(buffer, 0, buffer.Length, harness.Token);
#pragma warning restore CA1835

        Assert.AreEqual("array overloads", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [TestMethod]
    public async Task 老式的Begin和End也走异步()
    {
        // 基类的 BeginRead / BeginWrite 绕到同步读写上，而本类的同步读写直接抛。
        await using Harness harness = await Harness.StartAsync();

        byte[] content = Text("begin end");
        await using (SftpFileStream writer = await harness.Sftp.OpenWriteAsync(
            "/home/joe/apm.txt", cancellationToken: harness.Token))
        {
            await Task.Factory.FromAsync(writer.BeginWrite, writer.EndWrite, content, 0, content.Length, null);
        }

        await using SftpFileStream reader = await harness.Sftp.OpenReadAsync("/home/joe/apm.txt", harness.Token);
        byte[] buffer = new byte[64];
        int read = await Task.Factory.FromAsync(reader.BeginRead, reader.EndRead, buffer, 0, buffer.Length, null);

        Assert.AreEqual("begin end", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [TestMethod]
    public async Task 按偏移读写一次不超过一块()
    {
        // 比服务端 max-write-length 还长的 WRITE 会被拒 —— OpenSSH 直接断开 SFTP 会话。
        await using Harness harness = await Harness.StartAsync(
            clientOptions: new SftpOptions { BlockSize = 32 * 1024 });

        byte[] big = new byte[100_000];
        Random.Shared.NextBytes(big);

        await using (SftpFileStream writer = await harness.Sftp.OpenWriteAsync(
            "/home/joe/big.bin", cancellationToken: harness.Token))
        {
            await writer.WriteAtAsync(0, big, harness.Token);
            await writer.FlushAsync(harness.Token);
        }

        Assert.IsLessThanOrEqualTo(32 * 1024, harness.SftpServer.LargestWrite);
        Assert.AreSequenceEqual(big, [.. harness.SftpServer.Nodes["/home/joe/big.bin"].Content]);

        await using SftpFileStream reader = await harness.Sftp.OpenReadAsync("/home/joe/big.bin", harness.Token);
        int read = await reader.ReadAtAsync(0, new byte[100_000], harness.Token);

        Assert.AreEqual(32 * 1024, read, "一次至多一块；调用方本来就要循环读");
        Assert.IsLessThanOrEqualTo(32 * 1024, harness.SftpServer.LargestReadRequest);
    }

    [TestMethod]
    public async Task 服务端limits报0时块大小取保守默认而不是1()
    {
        // 0 是「没说」。按字面取，块大小就成了 1 —— 一个 1 MB 的文件要一百万个请求。
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Limits = new SftpLimits(0, 0, 0, 0) });

        Assert.AreEqual(SftpProtocol.DefaultBlockSize, harness.Sftp.BlockSize);
    }

    [TestMethod]
    public void 块大小服从服务端与本端的上限()
    {
        // OpenSSH 的典型宣告。
        Assert.AreEqual(261_120, SftpFileSystem.ChooseBlockSize(0, new SftpLimits(262_144, 261_120, 261_120, 0)));

        // 使用者要的更大：服从服务端，超长的 WRITE 会被拒。
        Assert.AreEqual(65_536, SftpFileSystem.ChooseBlockSize(1024 * 1024, new SftpLimits(70_000, 65_536, 65_536, 0)));

        // 服务端没说上限：仍不超过本端肯收的报文里装得下的一块。
        Assert.AreEqual(SftpProtocol.MaxBlockSize, SftpFileSystem.ChooseBlockSize(1024 * 1024, new SftpLimits(0, 0, 0, 0)));

        // 读上限也算数：比它长的 READ 会被截短，顺序读就把每一块都当成「中间有洞」。
        Assert.AreEqual(16_384, SftpFileSystem.ChooseBlockSize(0, new SftpLimits(0, 16_384, 0, 0)));

        // 报文上限要扣掉请求头。
        Assert.AreEqual(8_192, SftpFileSystem.ChooseBlockSize(0, new SftpLimits(8_192 + SftpProtocol.RequestHeaderAllowance, 0, 0, 0)));
    }

    [TestMethod]
    public async Task 续写的流把偏移之前算作已确认()
    {
        // 续传途中再断一次，DurableLength 要报「到哪了」，而不是 0 —— 否则下一次续传从头来过。
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/part.bin", new byte[1000]));

        await using (SftpFileStream stream = await harness.Sftp.OpenAppendAsync(
            "/home/joe/part.bin", 600, cancellationToken: harness.Token))
        {
            Assert.AreEqual(600, stream.DurableLength);
            Assert.AreEqual(1000, stream.Length, "续写打开的流也要知道文件有多长");
            Assert.AreEqual(600, stream.Position);

            await stream.WriteAsync(new byte[100], harness.Token);
            await stream.FlushAsync(harness.Token);
            Assert.AreEqual(700, stream.DurableLength);
        }

        // 偏移比文件还长：只算到文件末尾 —— 中间那段是洞，不是确认过的数据。
        await using (SftpFileStream beyond = await harness.Sftp.OpenAppendAsync(
            "/home/joe/part.bin", 5000, cancellationToken: harness.Token))
        {
            Assert.AreEqual(1000, beyond.DurableLength);
        }

        // 偏移不合法：开之前就拒，不留一个没人关的句柄。
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await harness.Sftp.OpenAppendAsync("/home/joe/part.bin", -1, cancellationToken: harness.Token));
        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount);
    }

    [TestMethod]
    public async Task 打开时属性应答不合格式_报协议错误且句柄被关掉()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("hello")),
            sftpOptions: new TestSftpOptions { MalformedFStat = true });

        // 公开的协议错误，而不是一个使用者按类型 catch 不到的内部解析异常。
        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await harness.Sftp.OpenReadAsync("/home/joe/a.txt", harness.Token));
        Assert.Contains("ATTRS", error.Message);

        Assert.AreEqual(0, harness.SftpServer.OpenHandleCount, "句柄已经开在服务端了，不关就漏");
    }

    [TestMethod]
    public async Task 一个链接的应答不合格式不影响整个目录()
    {
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.AddFile("/home/joe/f.txt", Text("f"));
                server.AddSymbolicLink("/home/joe/l", "/home/joe/f.txt");
            },
            sftpOptions: new TestSftpOptions { MalformedReadLink = true });

        List<SftpDirectoryEntry> entries = [];
        await foreach (SftpDirectoryEntry entry in
            harness.Sftp.EnumerateDirectoryAsync("/home/joe", harness.Token))
        {
            entries.Add(entry);
        }

        SftpDirectoryEntry link = entries.Single(e => e.Name == "l");
        Assert.IsTrue(link.IsSymbolicLink);
        Assert.IsNull(link.LinkTarget, "目标读不出来就是没有，不是整个列表失败");
        Assert.Contains(e => e.Name == "f.txt", entries);
    }

    /// <summary>
    /// 服务端回了一个不认识的报文类型：这一次调用报协议错误，不把它当成应答去解；流水线照常可用。
    /// </summary>
    [TestMethod]
    public async Task 应答是不认识的报文类型时这一次报协议错误()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { StatReplyOverride = ((SftpMessageType)200, [1, 2, 3, 4]) });

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await harness.Sftp.GetAttributesAsync("/home/joe", harness.Token));
        Assert.Contains("200", error.Message);

        Assert.AreEqual("/home/joe", await harness.Sftp.GetRealPathAsync(".", harness.Token), "别的请求照常");
    }

    /// <summary>
    /// ATTRS 带着 v3 没定义的标志位：那些位的字段没法对齐，报协议错误而不是从错位的地方接着解。
    /// 曾经默默忽略那些位 —— 在 NAME 应答里，后面每一项的名字与属性都会是错的。
    /// </summary>
    [TestMethod]
    public async Task ATTRS带着不认识的标志位时报协议错误()
    {
        // flags = SIZE | 0x10（v4 起才有的位），后面是 size 与 4 个说不清属于谁的字节。
        byte[] attrs = [0, 0, 0, 0x11, 0, 0, 0, 0, 0, 0, 0, 7, 9, 9, 9, 9];
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { StatReplyOverride = (SftpMessageType.Attrs, attrs) });

        SshProtocolException error = await Assert.ThrowsExactlyAsync<SshProtocolException>(
            async () => await harness.Sftp.GetAttributesAsync("/home/joe", harness.Token));
        Assert.Contains("0x00000010", error.Message);

        Assert.AreEqual("/home/joe", await harness.Sftp.GetRealPathAsync(".", harness.Token), "只影响这一次调用");
    }

    /// <summary>
    /// 一堆操作还在途就把文件系统释放掉：在途的与之后的调用只以「已释放 / 通道或连接的错误 / 取消」结束，
    /// 不出 <see cref="NullReferenceException"/>。宿主的 SFTP 包装曾经专门把 NRE 归一成「已释放」—— 那是换库前留下的。
    /// </summary>
    [TestMethod]
    public async Task 操作在途时被释放_只以已释放或通道错误结束()
    {
        for (int round = 0; round < 10; round++)
        {
            await using Harness harness = await Harness.StartAsync(server => server.AddFile("/home/joe/a.txt", Text("hello")));

            Task[] operations = [.. Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                for (int j = 0; j < 40; j++)
                {
                    _ = i % 2 == 0
                        ? await harness.Sftp.GetAttributesAsync("/home/joe/a.txt", cancellationToken: harness.Token)
                        : (object)await harness.Sftp.ReadAllBytesAsync("/home/joe/a.txt", harness.Token);
                }
            }, harness.Token))];

            await Task.Delay(round, harness.Token);
            await harness.Sftp.DisposeAsync();

            foreach (Task operation in operations)
            {
                try
                {
                    await operation;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SshException or OperationCanceledException)
                {
                    // 预期之内的结束方式；别的（尤其是 NRE）让用例失败。
                }
            }
        }
    }
}
