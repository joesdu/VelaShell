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
        private readonly SshConnection _connection;
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
            _connection = connection;
            Sftp = sftp;
            SftpServer = sftpServer;
            _cts = cts;
        }

        public SftpFileSystem Sftp { get; }

        public TestSftpServer SftpServer { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Harness> StartAsync(
            Action<TestSftpServer>? arrange = null,
            TestSftpOptions? sftpOptions = null,
            SftpOptions? clientOptions = null)
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

            TestChannelServer channelServer = new(server.Transport, new TestChannelScript
            {
                SubsystemHandler = sftpServer.RunAsync,
            });
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex.SessionId);
            connection.Start();

            SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection, clientOptions, cts.Token);

            return new Harness(server, channelServer, serverChannels, connection, sftp, sftpServer, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await Sftp.DisposeAsync();
            await _cts.CancelAsync();
            await _connection.DisposeAsync();
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

    // ------------------------------------------------------------ 握手

    [TestMethod]
    public async Task 连上之后工作目录来自REALPATH()
    {
        await using Harness harness = await Harness.StartAsync();

        // 〔决策〕连上就对 "." 做一次 REALPATH —— 这是唯一可靠的
        // 「用户家目录在哪」的答案，比拼 /home/{user} 靠谱得多。
        Assert.AreEqual("/home/joe", harness.Sftp.WorkingDirectory);
        CollectionAssert.Contains(harness.SftpServer.ReceivedTypes, SftpMessageType.RealPath);
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

        StringAssert.Contains(error.Message, "v2");
    }

    [TestMethod]
    public async Task 服务端版本高于3时降级继续()
    {
        await using Harness harness = await Harness.StartAsync(
            sftpOptions: new TestSftpOptions { Version = 6 });

        // 我们按 v3 工作 —— 这是 OpenSSH 的实际口径。
        Assert.AreEqual(6U, harness.Sftp.Capabilities.ServerVersion);
        Assert.AreEqual("/home/joe", harness.Sftp.WorkingDirectory);
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

        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/big.bin", payload),
            // **服务端每次只回 100 字节** —— 协议允许，这不是错误。
            new TestSftpOptions { ShortReadLimit = 100 });

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token);

        // 不循环读的实现会在这里只拿到 100 字节，而且**不报错**。
        CollectionAssert.AreEqual(payload, content, "READ 返回的数据可以少于请求的长度，必须循环读");
    }

    [TestMethod]
    public async Task 读空文件返回零字节而不是异常()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/empty.txt", []));

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/empty.txt", harness.Token);

        // EOF 不是错误 —— 它就是「读完了」。把它抛出去的话，
        // 每次正常读完一个文件都会变成一次异常。
        Assert.AreEqual(0, content.Length);
    }

    [TestMethod]
    public async Task 写一个文件()
    {
        await using Harness harness = await Harness.StartAsync();

        await harness.Sftp.WriteAllBytesAsync(
            "/home/joe/new.txt", Text("写进去的内容"), cancellationToken: harness.Token);

        TestSftpNode node = harness.SftpServer.Nodes["/home/joe/new.txt"];
        CollectionAssert.AreEqual(Text("写进去的内容"), node.Content.ToArray());
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

        CollectionAssert.AreEqual(payload, harness.SftpServer.Nodes["/home/joe/big.bin"].Content.ToArray());
        Assert.IsTrue(harness.SftpServer.WriteCount > 20, "应当被切成多个 WRITE");
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

        CollectionAssert.AreEqual(payload, back);
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
            },
            // **服务端故意把应答倒着发。**
            new TestSftpOptions { ShuffleResponses = true });

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

        Assert.IsTrue(harness.SftpServer.ReversedBatches > 0,
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
            // 前 3 个 WRITE 正常应答，之后干脆不回 —— 模拟中途断开。
            sftpOptions: new TestSftpOptions { FailWritesAfter = 3 },
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

        Assert.AreEqual(10_000, all.Length);
        CollectionAssert.AreEqual(first, all[..5000]);
        CollectionAssert.AreEqual(second, all[5000..]);
    }

    // ------------------------------------------------------------ 目录

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
        Assert.AreEqual(7, entries.Count);
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 7).Select(i => $"f{i}.txt").ToArray(),
            entries.Select(e => e.Name).ToArray());
        Assert.AreEqual("/home/joe/f0.txt", entries.First(e => e.Name == "f0.txt").FullPath);
    }

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
        StringAssert.Contains(error.Message, "目录非空");
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
        CollectionAssert.AreEqual(Text("A"), harness.SftpServer.Nodes["/home/joe/b.txt"].Content.ToArray());
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
        StringAssert.Contains(error.Message, "不是原子的");
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

        DateTimeOffset newTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        await harness.Sftp.SetLastWriteTimeAsync("/home/joe/t.txt", newTime, harness.Token);

        TestSftpNode result = harness.SftpServer.Nodes["/home/joe/t.txt"];

        // atime 与 mtime **共用一个标志位**。只给 mtime 的话 atime 会被当成 0。
        // 所以实现要先把当前的 atime 取回来再一并写回。
        Assert.AreEqual(1_800_000_000, result.ModifyTime);
        Assert.AreEqual(1_600_000_000, result.AccessTime, "访问时间不该被抹掉");
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
        Assert.IsTrue(harness.SftpServer.PeakOpenHandles > 0, "确实开过句柄");
    }

    /// <summary>〔架构原则 1〕同步读写不提供 —— 当场说清楚该用哪个，而不是阻塞线程假装同步。</summary>
    [TestMethod]
    public async Task 同步读写明确不支持且指向异步版本()
    {
        await using Harness harness = await Harness.StartAsync(
            server => server.AddFile("/home/joe/a.txt", Text("内容")));

        await using SftpFileStream stream = await harness.Sftp.OpenReadAsync("/home/joe/a.txt", harness.Token);

        NotSupportedException read = Assert.ThrowsExactly<NotSupportedException>(() => stream.Read(new byte[4], 0, 4));
        StringAssert.Contains(read.Message, "ReadAsync");
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));

        // 同步 Flush 保留为不阻塞的空操作：包装流在自己的收尾里会同步调它。
        stream.Flush();
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
}
