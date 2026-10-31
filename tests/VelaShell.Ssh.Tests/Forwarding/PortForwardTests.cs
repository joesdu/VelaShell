// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §二、§三、§四、§五、§八
//
// 这里跑的是真东西：本机真的开一个 TCP 监听，真的连上去，
// 数据真的穿过完整的 SSH 会话（握手 → 认证 → 通道 → 隧道）再回来。

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class PortForwardTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
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
            ChannelServer = channelServer;
            _serverChannels = serverChannels;
            Connection = connection;
            _cts = cts;
        }

        public SshConnection Connection { get; }

        public TestChannelObservation Observed => ChannelServer.Observation;

        /// <summary>服务端的通道层 —— 从这里发起回连。</summary>
        public TestChannelServer ChannelServer { get; }

        public CancellationToken Token => _cts.Token;

        /// <summary>服务端那头的传输一下子没了 —— 链路中途断掉。</summary>
        public ValueTask DropServerAsync() => _server.DisposeAsync();

        public static async Task<Harness> StartAsync(TestChannelScript script)
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

            TestChannelServer channelServer = new(server.Transport, script);
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex);
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
            ChannelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>一个把收到的内容大写之后回送的隧道处理器。</summary>
    private static async Task UppercaseEchoAsync(
        string target, PipeReader input, PipeWriter output, CancellationToken cancellationToken)
    {
        _ = target;

        while (true)
        {
            ReadResult read = await input.ReadAsync(cancellationToken);
            if (!read.Buffer.IsEmpty)
            {
                byte[] data = read.Buffer.ToArray();
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)char.ToUpperInvariant((char)data[i]);
                }
                await output.WriteAsync(data, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            input.AdvanceTo(read.Buffer.End);

            if (read.IsCompleted)
            {
                return;
            }
        }
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    // ------------------------------------------------------------ 本地转发

    [TestMethod]
    public async Task 本地转发把数据送到远端再送回来()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using var forwarder = LocalPortForwarder.Start(
            harness.Connection, "10.0.0.9", 80,
            new LocalPortForwardOptions { BindPort = 0 });

        // 端口给 0 → 由系统分配，结果在 BoundEndPoint 里。
        var bound = (IPEndPoint)forwarder.BoundEndPoint!;
        Assert.IsGreaterThan(0, bound.Port);
        Assert.AreEqual(IPAddress.Loopback, bound.Address, "默认绑环回，不是 0.0.0.0");

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(bound, harness.Token);
        await client.SendAsync(Text("hello tunnel"), harness.Token);
        client.Shutdown(SocketShutdown.Send);

        byte[] buffer = new byte[128];
        int read = await ReadAllAsync(client, buffer, harness.Token);

        Assert.AreEqual("HELLO TUNNEL", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.AreSequenceEqual(["10.0.0.9:80"], harness.Observed.TunnelTargets, "目标要如实传给服务端");
    }

    [TestMethod]
    public async Task 本地转发计量按方向分开且不含协议开销()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using var forwarder = LocalPortForwarder.Start(
            harness.Connection, "target", 1234);

        List<ForwardConnectionEventArgs> closed = [];
        forwarder.ConnectionClosed += (_, e) => closed.Add(e);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
        await client.SendAsync(new byte[500], harness.Token);
        client.Shutdown(SocketShutdown.Send);

        byte[] buffer = new byte[1024];
        int read = await ReadAllAsync(client, buffer, harness.Token);
        Assert.AreEqual(500, read);

        await WaitUntilAsync(() => closed.Count > 0, harness.Token);

        Assert.AreEqual(500, forwarder.BytesSent, "本机 → 远端");
        Assert.AreEqual(500, forwarder.BytesReceived, "远端 → 本机");
        Assert.AreEqual(1, forwarder.TotalConnections);
        Assert.AreEqual(500, closed[0].BytesSent);
        Assert.AreEqual(500, closed[0].BytesReceived);
    }

    [TestMethod]
    public async Task 服务端拒绝隧道时单条连接失败而转发器继续跑()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = null,   // 服务端一律拒绝
            RejectTunnelWith = SshChannelOpenFailureReason.AdministrativelyProhibited,
        });

        await using var forwarder = LocalPortForwarder.Start(
            harness.Connection, "blocked", 80);

        List<ForwardErrorEventArgs> errors = [];
        forwarder.Error += (_, e) => errors.Add(e);

        using (Socket client = new(SocketType.Stream, ProtocolType.Tcp))
        {
            // 〔spec 07 §二〕拒绝也是出错：本机那条连接被重置（RST），不是一个像正常结束的 FIN。
            SocketException reset = await ConnectAndExpectResetAsync(client, forwarder.BoundEndPoint!, harness.Token);
            Assert.AreEqual(SocketError.ConnectionReset, reset.SocketErrorCode);
        }

        await WaitUntilAsync(() => errors.Count > 0, harness.Token);

        // **单条连接的失败绝不影响转发器本身。**一条隧道要能跑几天，
        // 期间必然有连不上的目标。把这些当成致命错误，隧道就没法用了。
        Assert.AreEqual(ForwardErrorReason.ChannelOpen, errors[0].Reason);
        Assert.Contains("AllowTcpForwarding", errors[0].Message);
        Assert.IsTrue(forwarder.IsActive, "转发器必须还活着");
        Assert.AreEqual("blocked:80", errors[0].Target);
        Assert.IsNull(errors[0].Timeout, "被拒不是超时");
        Assert.IsInstanceOfType<SshChannelException>(errors[0].Exception);

        // 再来一条，仍然能被接受（并仍然失败）。
        using Socket second = new(SocketType.Stream, ProtocolType.Tcp);
        await ConnectAndExpectResetAsync(second, forwarder.BoundEndPoint!, harness.Token);
        await WaitUntilAsync(() => errors.Count > 1, harness.Token);
    }

    /// <summary>连上本机的监听、一直读到连接被重置为止，交回那个异常。</summary>
    /// <remarks>
    /// 内存里的服务端拒得快，转发器重置本机连接的 RST 可能在 <c>ConnectAsync</c> 交回之前就到 ——
    /// Linux 上那时 connect 本身就报 <see cref="SocketError.ConnectionReset"/>（CI 上见过）。两种都是「本机那条连接被重置」。
    /// 转发器要是给了一个 FIN，读会正常结束、这里抛断言失败。
    /// </remarks>
    private static Task<SocketException> ConnectAndExpectResetAsync(Socket client, EndPoint endPoint, CancellationToken cancellationToken) =>
        Assert.ThrowsExactlyAsync<SocketException>(() => ConnectAndReadAllAsync(client, endPoint, cancellationToken));

    private static async Task ConnectAndReadAllAsync(Socket client, EndPoint endPoint, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(endPoint, cancellationToken);
        await ReadAllAsync(client, new byte[16], cancellationToken);
    }

    /// <summary>
    /// 〔Q5〕默认同时听 127.0.0.1 与 ::1（同一个端口）：先试 ::1 的客户端也连得上；同端口的 IPv6 环回被别的进程占着时不起这个转发，
    /// IPv4 那个也不留。
    /// </summary>
    [TestMethod]
    public async Task 默认同时监听IPv4与IPv6环回()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Inconclusive("这台机器没有 IPv6：默认只听 127.0.0.1。");
        }

        await using Harness harness = await Harness.StartAsync(new TestChannelScript { TunnelHandler = UppercaseEchoAsync });
        await using (var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 80))
        {
            int port = ((IPEndPoint)forwarder.BoundEndPoint!).Port;
            Assert.AreSequenceEqual(
                [new IPEndPoint(IPAddress.Loopback, port), new IPEndPoint(IPAddress.IPv6Loopback, port)],
                [.. forwarder.BoundEndPoints]);

            using Socket client = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port), harness.Token);
            await client.SendAsync(Text("via v6"), harness.Token);
            client.Shutdown(SocketShutdown.Send);
            byte[] buffer = new byte[64];
            int read = await ReadAllAsync(client, buffer, harness.Token);
            Assert.AreEqual("VIA V6", Encoding.UTF8.GetString(buffer, 0, read));
        }

        using Socket squatter = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        squatter.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        squatter.Listen();
        int taken = ((IPEndPoint)squatter.LocalEndPoint!).Port;

        SshForwardException error = Assert.ThrowsExactly<SshForwardException>(
            () => LocalPortForwarder.Start(harness.Connection, "t", 80, new LocalPortForwardOptions { BindPort = taken }));
        Assert.Contains("IPv6 环回被别的进程占着", error.Message);
        Assert.AreEqual(SshForwardFailureKind.Ipv6LoopbackBindFailed, error.FailureKind);
        Assert.AreEqual($"[::1]:{taken}", error.Endpoint);
        Assert.AreEqual(SocketError.AddressAlreadyInUse, ((SocketException)error.InnerException!).SocketErrorCode, "界面据内层的错误码说「被占」");

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, taken));   // IPv4 那个没留下
    }

    [TestMethod]
    public async Task 端口被占用时不留半挂的监听()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());

        await using var first = LocalPortForwarder.Start(
            harness.Connection, "t", 1, new LocalPortForwardOptions { BindPort = 0 });

        int taken = ((IPEndPoint)first.BoundEndPoint!).Port;

        SshForwardException error = Assert.ThrowsExactly<SshForwardException>(
            () => LocalPortForwarder.Start(
                harness.Connection, "t", 1, new LocalPortForwardOptions { BindPort = taken }));

        Assert.Contains("端口可能已被占用", error.Message);
        Assert.AreEqual(SshForwardFailureKind.ListenFailed, error.FailureKind);
        Assert.EndsWith($":{taken}", error.Endpoint);
    }

    // ------------------------------------------------------------ 动态转发

    [TestMethod]
    public async Task 动态转发的目标由SOCKS握手给出()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using var forwarder = LocalPortForwarder.StartDynamic(harness.Connection);
        Assert.AreEqual(ForwardKind.Dynamic, forwarder.Kind);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);

        // SOCKS5 方法协商。
        await client.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, harness.Token);
        byte[] methodReply = new byte[2];
        await client.ReceiveAsync(methodReply, harness.Token);
        Assert.AreSequenceEqual(new byte[] { 0x05, 0x00 }, methodReply);

        // CONNECT 到一个**域名** —— 不该在本地解析。
        byte[] host = Encoding.ASCII.GetBytes("db.internal");
        byte[] request = [0x05, 0x01, 0x00, 0x03, (byte)host.Length, .. host, 0x14, 0x51];
        await client.SendAsync(request, harness.Token);

        byte[] connectReply = new byte[10];
        await client.ReceiveAsync(connectReply, harness.Token);
        Assert.AreEqual((byte)SocksReply.Succeeded, connectReply[1]);

        await client.SendAsync(Text("via socks"), harness.Token);
        client.Shutdown(SocketShutdown.Send);

        byte[] buffer = new byte[128];
        int read = await ReadAllAsync(client, buffer, harness.Token);

        Assert.AreEqual("VIA SOCKS", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.AreSequenceEqual(["db.internal:5201"], harness.Observed.TunnelTargets, "域名要原样送到服务端 —— 本地解析会让内网域名直接失效，而且泄漏访问目标");
    }

    [TestMethod]
    public async Task 动态转发把通道失败翻成对应的SOCKS码()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = null,
            RejectTunnelWith = SshChannelOpenFailureReason.ConnectFailed,
        });

        await using var forwarder = LocalPortForwarder.StartDynamic(harness.Connection);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);

        await client.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, harness.Token);
        byte[] methodReply = new byte[2];
        await client.ReceiveAsync(methodReply, harness.Token);

        await client.SendAsync(
            new byte[] { 0x05, 0x01, 0x00, 0x01, 10, 0, 0, 1, 0x00, 0x50 }, harness.Token);

        byte[] connectReply = new byte[10];
        await client.ReceiveAsync(connectReply, harness.Token);

        // curl 与浏览器会根据这个码决定要不要重试、报给用户哪句话。
        // 一律回 0x01 等于把信息丢了。
        Assert.AreEqual((byte)SocksReply.ConnectionRefused, connectReply[1]);
    }

    /// <summary>
    /// 〔T6 / CH-E2〕对端开过来的通道一确认就被关（端口扫描、健康检查）：决定开通道的后台任务恰好停在「确认入队之后、起泵之前」，
    /// 接收循环先处理了对端的 CLOSE —— 回给对端的 CLOSE 要带着它的真实通道号。曾经先发确认、后设对端的号：
    /// 回的 CLOSE 带着 0，关掉的是对端的 0 号通道（往往是用户的第一条 shell），真正那条通道永远收不到 CLOSE。
    /// </summary>
    [TestMethod]
    public async Task 对端开过来的通道一确认就被关时_回的CLOSE带着对端的真实通道号()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34572,
            CloseServerOpenedChannelOnConfirm = true,
            CloseAfterScript = false,
            ExitCode = null,
        });

        // 先开一条 session 通道，占住服务端的 0 号 —— 用户的第一条 shell 就在那里。
        SshChannel firstShell = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);

        TaskCompletionSource<bool> closedBetweenSteps = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.AfterIncomingOpenConfirmationQueued = channel =>
            closedBetweenSteps.TrySetResult(SpinWait.SpinUntil(() => channel.State == SshChannelState.Closed, TimeSpan.FromSeconds(10)));

        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen();
        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", ((IPEndPoint)target.LocalEndPoint!).Port,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 34572 }, harness.Token);

        ArrayBufferWriter<byte> header = new();
        SshDataWriter writer = new(header);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32(34572);
        writer.WriteUtf8String("127.0.0.1");
        writer.WriteUInt32(40004);
        Stream? scan = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);
        Assert.IsNotNull(scan);

        await WaitUntilAsync(() =>
        {
            lock (harness.Observed.ClosedServerChannels)
            {
                return harness.Observed.ClosedServerChannels.Count > 0;
            }
        }, harness.Token);

        Assert.IsTrue(await closedBetweenSteps.Task.WaitAsync(harness.Token), "接收循环要在两步之间处理完对端的 CLOSE（这一刻摆出来了，用例才有意义）");
        lock (harness.Observed.ClosedServerChannels)
        {
            Assert.DoesNotContain(0u, harness.Observed.ClosedServerChannels, "回的 CLOSE 不许指向对端的 0 号通道");
        }
        Assert.AreEqual(SshChannelState.Open, firstShell.State, "用户的第一条 shell 照旧开着");
        await firstShell.DisposeAsync();
    }

    /// <summary>〔F41〕转发器交出自己的停止原因：连接断了是连接的结束原因，本端释放是 Aborted。</summary>
    [TestMethod]
    public async Task 转发器的Completion交出停止原因()
    {
        await using (Harness harness = await Harness.StartAsync(new TestChannelScript { DropConnectionOnTunnelOpen = true }))
        {
            await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 80);
            Assert.IsFalse(forwarder.Completion.IsCompleted);

            using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);   // 服务端收到开隧道就断开连接

            SshException stopped = await forwarder.Completion.WaitAsync(harness.Token);
            Assert.AreNotEqual(SshFailureReason.Aborted, stopped.Reason, "是连接断了，不是本端停的");
            Assert.AreSame(await harness.Connection.Completion.WaitAsync(harness.Token), stopped);
        }

        await using (Harness harness = await Harness.StartAsync(new TestChannelScript()))
        {
            var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 80);
            await forwarder.DisposeAsync();
            Assert.AreEqual(SshFailureReason.Aborted, (await forwarder.Completion.WaitAsync(harness.Token)).Reason);
            Assert.IsNull(harness.Connection.CloseReason, "停的是转发器，连接照旧");
        }
    }

    /// <summary>〔spec 07 §3.2〕服务端一直不应答开通道：到 ChannelOpenTimeout 放弃，动态转发回 SOCKS 0x06，本地转发重置本机连接。</summary>
    [TestMethod]
    public async Task 开通道等不到应答时到点放弃_动态转发回TTL到期_本地转发重置()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { IgnoreTunnelOpens = true });
        LocalPortForwardOptions quick = new() { ChannelOpenTimeout = TimeSpan.FromMilliseconds(300) };

        await using (var dynamic = LocalPortForwarder.StartDynamic(harness.Connection, quick))
        {
            List<ForwardErrorEventArgs> errors = [];
            dynamic.Error += (_, e) => errors.Add(e);

            using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(dynamic.BoundEndPoint!, harness.Token);
            await client.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, harness.Token);
            byte[] methodReply = new byte[2];
            await client.ReceiveAsync(methodReply, harness.Token);
            await client.SendAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 10, 0, 0, 1, 0x00, 0x50 }, harness.Token);

            byte[] connectReply = new byte[10];
            await client.ReceiveAsync(connectReply, harness.Token);
            Assert.AreEqual((byte)SocksReply.TtlExpired, connectReply[1]);

            await WaitUntilAsync(() => errors.Count > 0, harness.Token);
            Assert.AreEqual(ForwardErrorReason.ChannelOpen, errors[0].Reason);
            Assert.AreEqual("到 10.0.0.1:80 的隧道在 0.3 秒内没有打开（服务端还没应答），这一条被放弃。", errors[0].Message);

            // 没有异常可译：界面按原因、目标与时限说。
            Assert.IsNull(errors[0].Exception);
            Assert.AreEqual("10.0.0.1:80", errors[0].Target);
            Assert.AreEqual(TimeSpan.FromMilliseconds(300), errors[0].Timeout);
        }

        await using (var local = LocalPortForwarder.Start(harness.Connection, "slow", 80, quick))
        {
            using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
            SocketException reset = await ConnectAndExpectResetAsync(client, local.BoundEndPoint!, harness.Token);
            Assert.AreEqual(SocketError.ConnectionReset, reset.SocketErrorCode);
        }

        Assert.AreEqual(TimeSpan.FromSeconds(30), LocalPortForwardOptions.Default.ChannelOpenTimeout);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, new LocalPortForwardOptions { ChannelOpenTimeout = Timeout.InfiniteTimeSpan }.ChannelOpenTimeout);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { ChannelOpenTimeout = TimeSpan.Zero });
    }

    // ------------------------------------------------------------ 远程转发

    /// <summary>〔spec 07 §八〕远程转发撞上并发上限：回 RESOURCE_SHORTAGE，同时计入错误、发 ConnectionLimit 事件（曾经只拒、不报）。</summary>
    [TestMethod]
    public async Task 远程转发撞并发上限时回资源不足并报ConnectionLimit()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34571 });

        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen();
        int targetPort = ((IPEndPoint)target.LocalEndPoint!).Port;

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", targetPort,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 34571, MaxConnections = 1 }, harness.Token);
        List<ForwardErrorEventArgs> errors = [];
        forwarder.Error += (_, e) =>
        {
            lock (errors)
            {
                errors.Add(e);
            }
        };

        ArrayBufferWriter<byte> header = new();
        SshDataWriter writer = new(header);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32(34571);
        writer.WriteUtf8String("127.0.0.1");
        writer.WriteUInt32(40003);

        Stream? first = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);
        Assert.IsNotNull(first);
        using Socket held = await target.AcceptAsync(harness.Token);

        Stream? second = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);
        Assert.IsNull(second, "撞上限的那一条不该确认");
        Assert.AreEqual(SshChannelOpenFailureReason.ResourceShortage, harness.Observed.LastClientOpenFailure);

        await WaitUntilAsync(() =>
        {
            lock (errors)
            {
                return errors.Any(e => e.Reason == ForwardErrorReason.ConnectionLimit);
            }
        }, harness.Token);
        lock (errors)
        {
            ForwardErrorEventArgs limit = errors.First(e => e.Reason == ForwardErrorReason.ConnectionLimit);
            Assert.AreEqual("并发连接数已达上限 1，这一条被拒绝。", limit.Message);
            Assert.AreEqual(1, limit.Limit);
            Assert.AreEqual(1, limit.RejectedCount);
            Assert.IsNull(limit.Exception);
        }
        await first.DisposeAsync();
    }

    [TestMethod]
    public async Task 远程转发请求端口0时从应答载荷取实际端口()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34567,
        });

        // 本机起一个目标服务，供回连使用。
        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen(4);
        int targetPort = ((IPEndPoint)target.LocalEndPoint!).Port;

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", targetPort,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 0 },
            harness.Token);

        // ⚠️ 这是 §4.2 的第一个「必须」：端口给 0 时实际端口在
        //    REQUEST_SUCCESS 的载荷里。取不到的话后面按 (addr, 0) 路由回连，
        //    一条都对不上 —— 症状是「转发看起来建好了，但连过来的全被拒」。
        Assert.AreEqual(34567, forwarder.BoundPort);
        Assert.AreEqual("localhost", forwarder.BindAddress);

        Assert.AreSequenceEqual(
            [("localhost", 0)], harness.Observed.RemoteForwardBinds, "绑定地址要原样传，不做规范化");
    }

    /// <summary>
    /// 监听请求已经上线，调用方等应答时取消了；服务端随后批准 —— 它的监听得撤掉。
    /// 曾经只摘掉本端的处理器：服务端的监听一直开到连接断开，连进来的全被拒，固定端口重试必报「已被占用」。
    /// </summary>
    [TestMethod]
    public async Task 远程转发在应答前被取消_服务端随后批准的监听被撤掉()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34571,
            DelayRemoteForwardReply = TimeSpan.FromMilliseconds(300),
        });

        using (CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await RemotePortForwarder.StartAsync(
                    harness.Connection, "127.0.0.1", 8080,
                    new RemotePortForwardOptions { BindPort = 34571 }, cancel.Token));
        }

        for (int i = 0; i < 200 && !harness.Observed.GlobalRequests.Contains("cancel-tcpip-forward"); i++)
        {
            await Task.Delay(10, harness.Token);
        }

        Assert.Contains("cancel-tcpip-forward", harness.Observed.GlobalRequests, "服务端批准的监听没有被撤掉");
        Assert.IsTrue(harness.Connection.IsAlive);
    }

    /// <summary>
    /// 半死的链路上服务端不回「取消监听」的应答：释放要有时限，不能一直卡到 TCP 放弃（约 15 分钟）。
    /// </summary>
    [TestMethod]
    public async Task 释放远程转发时等取消应答有时限()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34572,
            IgnoreCancelForward = true,
        });

        RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", 8080,
            new RemotePortForwardOptions { BindPort = 34572, CancelReplyTimeout = TimeSpan.FromMilliseconds(300) },
            harness.Token);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await forwarder.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), harness.Token);

        Assert.IsFalse(forwarder.IsActive);
        Assert.IsLessThan(TimeSpan.FromSeconds(2), elapsed.Elapsed,
            "等不到取消应答时应当到点就收尾，而且不再等宽限期");
    }

    [TestMethod]
    public async Task 服务端拒绝远程转发时抛出且不留半挂的转发器()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 0,   // 一律拒绝
        });

        SshForwardException error = await Assert.ThrowsExactlyAsync<SshForwardException>(
            async () => await RemotePortForwarder.StartAsync(
                harness.Connection, "127.0.0.1", 8080,
                new RemotePortForwardOptions { BindPort = 9999 }, harness.Token));

        Assert.Contains("AllowTcpForwarding", error.Message);
        Assert.IsTrue(harness.Connection.IsAlive, "被拒绝不该连累会话");
        Assert.AreEqual(SshForwardFailureKind.RemoteListenRejected, error.FailureKind);
        Assert.AreEqual(9999, error.Port);
        Assert.EndsWith(":9999", error.Endpoint);
    }

    // ------------------------------------------------------------ 直连隧道

    [TestMethod]
    public async Task 直连隧道不在本机开监听端口()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        // 〔决策 §一〕第四种形态：**没有人监听**，流直接交给调用方。
        // 接 /var/run/docker.sock 这类端点时，这是唯一正路 ——
        // 开一个本地端口等于把控制权交给同机的每一个进程。
        await using SshChannel tunnel = await harness.Connection.OpenTcpTunnelAsync(
            "127.0.0.1", 8080, cancellationToken: harness.Token);

        await tunnel.StandardInput.WriteAsync(Text("no listener here"), harness.Token);
        await tunnel.SendEofAsync(harness.Token);

        byte[] received = await ReadAllPipeAsync(tunnel.StandardOutput, harness.Token);

        Assert.AreEqual("NO LISTENER HERE", Encoding.UTF8.GetString(received));
        Assert.AreSequenceEqual(["127.0.0.1:8080"], harness.Observed.TunnelTargets);
    }

    [TestMethod]
    public async Task 到Unix套接字的直连隧道()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using SshChannel tunnel = await harness.Connection.OpenUnixSocketTunnelAsync(
            "/var/run/docker.sock", cancellationToken: harness.Token);

        await tunnel.StandardInput.WriteAsync(Text("docker"), harness.Token);
        await tunnel.SendEofAsync(harness.Token);

        byte[] received = await ReadAllPipeAsync(tunnel.StandardOutput, harness.Token);

        Assert.AreEqual("DOCKER", Encoding.UTF8.GetString(received));
        Assert.AreSequenceEqual(["/var/run/docker.sock"], harness.Observed.TunnelTargets);
    }

    // ------------------------------------------------------------ 出错收尾

    [TestMethod]
    public async Task 链路中途断了本机程序收到重置而不是正常结束()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 1);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
        await client.SendAsync(Text("ping"), harness.Token);
        byte[] buffer = new byte[16];
        int read = await client.ReceiveAsync(buffer, harness.Token);
        Assert.AreEqual("PING", Encoding.UTF8.GetString(buffer, 0, read));

        await harness.DropServerAsync();

        // 本机程序必须知道连接是出错断的。给它一个干净的结尾（FIN），
        // 下载到一半的文件在它看来就是下完了。
        SocketException error = await Assert.ThrowsExactlyAsync<SocketException>(async () =>
        {
            while (await client.ReceiveAsync(buffer, harness.Token) > 0)
            {
            }
        });
        Assert.AreEqual(SocketError.ConnectionReset, error.SocketErrorCode);
    }

    [TestMethod]
    public async Task 远端关了隧道本机那条连接随之收尾()
    {
        // 远端发完就关通道；本机程序一直不说话也不关 —— 转发器不能陪它一直挂着。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = async (_, _, output, cancellationToken) =>
                await output.WriteAsync(Text("bye"), cancellationToken),
        });

        await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 1);
        List<ForwardConnectionEventArgs> closed = [];
        forwarder.ConnectionClosed += (_, e) => closed.Add(e);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);

        byte[] buffer = new byte[16];
        int read = await ReadAllAsync(client, buffer, harness.Token);
        Assert.AreEqual("bye", Encoding.UTF8.GetString(buffer, 0, read));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => closed.Count > 0 && forwarder.ActiveConnections == 0, deadline.Token);
    }

    [TestMethod]
    public async Task SOCKS握手迟迟不来的连接会被关掉()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());

        await using var forwarder = LocalPortForwarder.StartDynamic(
            harness.Connection, new LocalPortForwardOptions { SocksHandshakeTimeout = TimeSpan.FromMilliseconds(200) });
        List<ForwardErrorEventArgs> errors = [];
        forwarder.Error += (_, e) => errors.Add(e);

        // 连上来一句不说。
        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Assert.AreEqual(0, await client.ReceiveAsync(new byte[4], deadline.Token), "转发器这头应当关掉连接");
        }
        catch (SocketException)
        {
            // 被重置也算关掉了。
        }

        await WaitUntilAsync(() => errors.Count > 0, deadline.Token);
        Assert.AreEqual(ForwardErrorReason.SocksHandshake, errors[0].Reason);
        Assert.AreEqual("SOCKS 握手在 0.2 秒内没有完成，这一条被关掉。", errors[0].Message);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), errors[0].Timeout, "超时那一种带着时限，界面据此与「握手非法」分开说");
        Assert.IsNull(errors[0].Target);
    }

    /// <summary>〔FW-E17〕在已经断开的连接上起本地 / 动态转发：照实失败（与远程转发一致），而不是「成功」返回一个不工作的转发器。</summary>
    [TestMethod]
    public async Task 在已断开的连接上起本地转发照实失败()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());
        await harness.DropServerAsync();
        await WaitUntilAsync(() => !harness.Connection.IsAlive, harness.Token);

        Assert.Throws<SshException>(() => LocalPortForwarder.Start(harness.Connection, "t", 1));
        Assert.Throws<SshException>(() => LocalPortForwarder.StartDynamic(harness.Connection));
        await Assert.ThrowsAsync<SshException>(
            async () => await RemotePortForwarder.StartAsync(harness.Connection, "127.0.0.1", 1, cancellationToken: harness.Token));

        await harness.Connection.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => LocalPortForwarder.Start(harness.Connection, "t", 1));
    }

    [TestMethod]
    public async Task 连接断了之后本地转发放出端口()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());

        await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 1);
        int port = ((IPEndPoint)forwarder.BoundEndPoint!).Port;

        await harness.DropServerAsync();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !forwarder.IsActive, deadline.Token);

        // 端口放出来了：重连之后重建同一个转发要靠这个，否则只会得到「端口已被占用」。
        using Socket rebind = new(SocketType.Stream, ProtocolType.Tcp);
        rebind.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>
    /// 〔FW-E16〕转发参数的非法值在设值时就抛 —— 曾经 MaxConnections = 0 时监听已经起来、构造转发器才抛，端口一直占到 GC。
    /// </summary>
    [TestMethod]
    public void 转发参数的非法值在设值时就抛()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { MaxConnections = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { BindPort = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { BindPort = 65536 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { SocksHandshakeTimeout = TimeSpan.Zero });
        Assert.IsNull(new LocalPortForwardOptions().BindAddress, "默认不给地址：同时听 127.0.0.1 与 ::1（Q5）");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RemotePortForwardOptions { MaxConnections = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RemotePortForwardOptions { BindPort = 70000 });
        Assert.ThrowsExactly<ArgumentNullException>(() => new RemotePortForwardOptions { BindAddress = null! });

        Assert.AreEqual(1, new LocalPortForwardOptions { MaxConnections = 1, BindPort = 65535 }.MaxConnections);
    }
    /// <summary>
    /// 〔FW-D6〕撞并发上限时一大波被拒的连接只换来至多每秒一次的 Error 事件（曾经每拒一条报一次，宿主逐条推到界面上）。
    /// </summary>
    [TestMethod]
    public async Task 撞并发上限时错误事件节流()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = async (_, _, _, ct) => await release.Task.WaitAsync(ct),
        });

        await using var forwarder = LocalPortForwarder.Start(
            harness.Connection, "t", 1, new LocalPortForwardOptions { MaxConnections = 1 });
        List<ForwardErrorEventArgs> errors = [];
        forwarder.Error += (_, e) =>
        {
            lock (errors)
            {
                errors.Add(e);
            }
        };

        using Socket holder = new(SocketType.Stream, ProtocolType.Tcp);
        await holder.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
        await WaitUntilAsync(() => forwarder.ActiveConnections == 1, harness.Token);

        for (int i = 0; i < 20; i++)
        {
            using Socket extra = new(SocketType.Stream, ProtocolType.Tcp);
            await extra.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
            try
            {
                _ = await extra.ReceiveAsync(new byte[1], harness.Token);   // 被拒的那一条随即被关掉
            }
            catch (SocketException)
            {
                // 被重置也算。
            }
        }

        int limitEvents;
        lock (errors)
        {
            limitEvents = errors.Count(e => e.Reason == ForwardErrorReason.ConnectionLimit);
        }
        Assert.IsTrue(limitEvents is >= 1 and <= 2, $"20 条被拒只该换来一两次事件，实际 {limitEvents} 次");
        lock (errors)
        {
            Assert.IsTrue(errors.Where(e => e.Reason == ForwardErrorReason.ConnectionLimit).All(e => e.Limit == 1 && e.RejectedCount >= 1));
        }
        release.SetResult();
    }

    /// <summary>ConnectionClosed 触发时，活跃连接数已经不含这一条（订阅者常在这里刷新界面上的连接数）。</summary>
    [TestMethod]
    public async Task 报连接关闭时活跃数已经减掉()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 1);
        int activeSeenOnClose = -1;
        forwarder.ConnectionClosed += (_, _) => activeSeenOnClose = forwarder.ActiveConnections;

        using (Socket client = new(SocketType.Stream, ProtocolType.Tcp))
        {
            await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
            await client.SendAsync(Text("abc"), harness.Token);
            client.Shutdown(SocketShutdown.Send);
            _ = await ReadAllAsync(client, new byte[16], harness.Token);
        }

        await WaitUntilAsync(() => activeSeenOnClose >= 0, harness.Token);
        Assert.AreEqual(0, activeSeenOnClose);
    }

    [TestMethod]
    public async Task 事件订阅者抛异常不影响搬运与计数()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            TunnelHandler = UppercaseEchoAsync,
        });

        await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 1);
        forwarder.ConnectionOpened += (_, _) => throw new InvalidOperationException("订阅者的 bug");
        List<ForwardConnectionEventArgs> closed = [];
        forwarder.ConnectionClosed += (_, e) => closed.Add(e);

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
        await client.SendAsync(Text("abc"), harness.Token);
        client.Shutdown(SocketShutdown.Send);

        byte[] buffer = new byte[16];
        int read = await ReadAllAsync(client, buffer, harness.Token);
        Assert.AreEqual("ABC", Encoding.UTF8.GetString(buffer, 0, read));

        await WaitUntilAsync(() => closed.Count > 0, harness.Token);
        Assert.AreEqual(0, forwarder.ActiveConnections, "活跃连接数不能只加不减");
    }

    [TestMethod]
    public async Task 远程转发应答后紧跟着的回连不会被拒()
    {
        // 服务端回完 REQUEST_SUCCESS 立刻就有人连那个端口。那条回连由接收循环紧接着处理 ——
        // 拿到应答之后才登记处理器、才记下实际端口的话，它已经被当成没人认领拒掉了。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34568,
            OpenForwardedTcpIpAfterGrant = true,
        });

        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen(4);
        Task<Socket> accepting = target.AcceptAsync(harness.Token).AsTask();

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", ((IPEndPoint)target.LocalEndPoint!).Port,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 0 }, harness.Token);

        await WaitUntilAsync(() => harness.Observed.ForwardedOpenAfterGrant is not null, harness.Token);
        Stream? remote = await harness.Observed.ForwardedOpenAfterGrant!.WaitAsync(harness.Token);

        Assert.IsNotNull(remote, "应答之后紧跟着的回连应当被接下");
        Assert.AreEqual(34568, forwarder.BoundPort);
        using Socket accepted = await accepting.WaitAsync(harness.Token);
        await remote.DisposeAsync();
    }

    [TestMethod]
    public async Task 远程转发的处理器在请求发出之前就登记好了()
    {
        // 上一条是真实的时序，但它只在调度不巧时才会出错。这里把回连排在应答**前面**，
        // 把「处理器是不是在请求发出之前就登记了」变成确定的问题：拿到应答再登记的实现一定拒掉它。
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34570,
            OpenForwardedTcpIpBeforeGrant = true,
        });

        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen(4);
        Task<Socket> accepting = target.AcceptAsync(harness.Token).AsTask();

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", ((IPEndPoint)target.LocalEndPoint!).Port,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 34570 }, harness.Token);

        await WaitUntilAsync(() => harness.Observed.ForwardedOpenAfterGrant is not null, harness.Token);
        Stream? remote = await harness.Observed.ForwardedOpenAfterGrant!.WaitAsync(harness.Token);

        Assert.IsNotNull(remote, "处理器应当在请求发出之前就登记好了");
        using Socket accepted = await accepting.WaitAsync(harness.Token);
        await remote.DisposeAsync();
    }

    /// <summary>〔FW-D3〕远程转发的连接事件带着是谁连上了服务端那个端口（forwarded-tcpip 里的 originator）。</summary>
    [TestMethod]
    public async Task 远程转发的连接事件带着来源地址()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34571 });

        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen(4);
        Task<Socket> accepting = target.AcceptAsync(harness.Token).AsTask();

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", ((IPEndPoint)target.LocalEndPoint!).Port,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 34571 }, harness.Token);
        TaskCompletionSource<EndPoint?> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        forwarder.ConnectionOpened += (_, e) => opened.TrySetResult(e.Source);

        ArrayBufferWriter<byte> header = new();
        SshDataWriter writer = new(header);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32(34571);
        writer.WriteUtf8String("203.0.113.7");
        writer.WriteUInt32(51234);
        Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);
        Assert.IsNotNull(remote);
        using Socket accepted = await accepting.WaitAsync(harness.Token);

        EndPoint? source = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), harness.Token);
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234), source);
        await remote.DisposeAsync();
    }

    /// <summary>
    /// 〔FW-D2〕远程转发的本机目标连不上：回 CHANNEL_OPEN_FAILURE（connect failed），而不是先确认、再立刻关掉；
    /// 本地照样记一笔 TargetConnect 错误。
    /// </summary>
    [TestMethod]
    public async Task 远程转发的本机目标连不上时回连接失败()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34570 });

        int deadPort;
        using (Socket probe = new(SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            deadPort = ((IPEndPoint)probe.LocalEndPoint!).Port;   // 关掉之后没人在听
        }

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", deadPort,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 34570 }, harness.Token);
        List<ForwardErrorEventArgs> errors = [];
        forwarder.Error += (_, e) => errors.Add(e);

        ArrayBufferWriter<byte> header = new();
        SshDataWriter writer = new(header);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32(34570);
        writer.WriteUtf8String("127.0.0.1");
        writer.WriteUInt32(40002);
        Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);

        Assert.IsNull(remote, "连不上本机目标时不该确认通道");
        Assert.AreEqual(SshChannelOpenFailureReason.ConnectFailed, harness.Observed.LastClientOpenFailure);
        await WaitUntilAsync(() => errors.Count > 0, harness.Token);
        Assert.AreEqual(ForwardErrorReason.TargetConnect, errors[0].Reason);
        Assert.AreEqual(0, forwarder.ActiveConnections, "槽位要还回去");
        Assert.AreEqual($"127.0.0.1:{deadPort}", errors[0].Target);
    }

    [TestMethod]
    public async Task 远程转发释放的宽限期里照常接在途的回连()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantRemoteForwardPort = 34569,
        });

        using Socket target = new(SocketType.Stream, ProtocolType.Tcp);
        target.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        target.Listen(4);
        Task<Socket> accepting = target.AcceptAsync(harness.Token).AsTask();

        RemotePortForwarder forwarder = await RemotePortForwarder.StartAsync(
            harness.Connection, "127.0.0.1", ((IPEndPoint)target.LocalEndPoint!).Port,
            new RemotePortForwardOptions { BindAddress = "localhost", BindPort = 34569 }, harness.Token);

        Task disposing = forwarder.DisposeAsync().AsTask();
        await WaitUntilAsync(() => harness.Observed.GlobalRequests.Contains("cancel-tcpip-forward"), harness.Token);
        Assert.IsFalse(forwarder.IsActive, "开始释放之后就不算在跑了");

        // 取消已经发出，但服务端在那之前接下的连接还在路上（velashell-docs/zh/ssh/spec/07 §4.3）。
        ArrayBufferWriter<byte> header = new();
        SshDataWriter writer = new(header);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32(34569);
        writer.WriteUtf8String("127.0.0.1");
        writer.WriteUInt32(40001);
        Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);

        Assert.IsNotNull(remote, "宽限期里在途的回连要照常接下");
        using Socket accepted = await accepting.WaitAsync(harness.Token);

        await disposing.WaitAsync(TimeSpan.FromSeconds(10), harness.Token);

        // 释放完成，这个转发器的连接随之结束 —— 本机目标这头读到结尾或重置，而不是一直挂着。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[16];
        try
        {
            while (await accepted.ReceiveAsync(buffer, deadline.Token) > 0)
            {
            }
        }
        catch (SocketException)
        {
            // 重置也是结束。
        }
        await remote.DisposeAsync();
    }

    // ------------------------------------------------------------ 工具

    private static async Task<int> ReadAllAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private static async Task<byte[]> ReadAllPipeAsync(PipeReader reader, CancellationToken cancellationToken)
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

    [TestMethod]
    public async Task 远程Unix套接字转发能建起来并原样带回路径()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantStreamLocalForward = true,
        });

        const string remotePath = "/tmp/velashell-remote.sock";

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartUnixSocketAsync(
            harness.Connection,
            targetSocketPath: "/tmp/velashell-local.sock",
            remoteSocketPath: remotePath,
            cancellationToken: harness.Token);

        // 套接字路径没有「端口 0 换实际端口」那一套 —— 它是请求方给的，
        // 所以原样留着就行。
        Assert.AreEqual(remotePath, forwarder.RemoteSocketPath);
        Assert.AreEqual(remotePath, forwarder.RemoteEndpointName,
            "隧道面板要显示「这条转发开在哪」，两种形态得有统一的说法");

        Assert.Contains(
SshProtocolNames.RequestStreamLocalForward, harness.Observed.GlobalRequests);
        Assert.AreSequenceEqual([remotePath], harness.Observed.StreamLocalForwardBinds);
    }

    [TestMethod]
    public async Task 服务端拒绝Unix套接字转发时抛出且不留半挂的转发器()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantStreamLocalForward = false,
        });

        SshForwardException error = await Assert.ThrowsExactlyAsync<SshForwardException>(
            async () => await RemotePortForwarder.StartUnixSocketAsync(
                harness.Connection, "/tmp/a.sock", "/tmp/b.sock",
                cancellationToken: harness.Token));

        // 失败消息要能指向下一步 —— sshd_config 的开关、路径已存在、目录不可写。
        Assert.Contains("AllowStreamLocalForwarding", error.Message);
        Assert.AreEqual(SshForwardFailureKind.RemoteSocketListenRejected, error.FailureKind);
        Assert.AreEqual("/tmp/b.sock", error.Endpoint);
    }

    [TestMethod]
    public async Task 远程Unix套接字转发能把回连搬到本机套接字()
    {
        // 这一条是真的端到端：服务端开一条 forwarded-streamlocal 回来，
        // 我们连上本机的一个真 Unix 套接字，两边对搬。
        //
        // Unix 套接字在 Windows 10+ 上也支持，所以这条用例不用跳过；
        // 只有个别老平台没有，那时才跳。
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个平台不支持 Unix 域套接字。");
        }

        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantStreamLocalForward = true,
        });

        // 本机目标：一个真的 Unix 套接字，收到什么就回什么（大写）。
        string localPath = Path.Combine(
            Path.GetTempPath(), $"velashell-{Guid.NewGuid():N}.sock");

        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(localPath));
        listener.Listen(4);

        Task<byte[]> served = Task.Run(async () =>
        {
            using Socket accepted = await listener.AcceptAsync(harness.Token);
            byte[] buffer = new byte[64];
            int read = await accepted.ReceiveAsync(buffer, harness.Token);
            byte[] got = buffer[..read];
            await accepted.SendAsync(Encoding.UTF8.GetBytes("PONG"), harness.Token);
            accepted.Shutdown(SocketShutdown.Send);
            return got;
        });

        try
        {
            const string remotePath = "/tmp/velashell-remote.sock";
            await using RemotePortForwarder forwarder = await RemotePortForwarder.StartUnixSocketAsync(
                harness.Connection, localPath, remotePath, cancellationToken: harness.Token);

            // 服务端发起回连。载荷是 socket_path ‖ reserved。
            ArrayBufferWriter<byte> typeSpecific = new();
            SshDataWriter writer = new(typeSpecific);
            writer.WriteUtf8String(remotePath);
            writer.WriteUtf8String("");

            Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
                SshProtocolNames.ChannelForwardedStreamLocal,
                typeSpecific.WrittenMemory, harness.Token);

            Assert.IsNotNull(remote, "路径对得上的回连应当被接受");

            await remote.WriteAsync(Encoding.UTF8.GetBytes("PING"), harness.Token);
            await remote.FlushAsync(harness.Token);

            byte[] arrived = await served.WaitAsync(harness.Token);
            Assert.AreEqual("PING", Encoding.UTF8.GetString(arrived), "远端发来的要原样到本机套接字");

            byte[] back = new byte[16];
            int read = await remote.ReadAsync(back, harness.Token);
            Assert.AreEqual("PONG", Encoding.UTF8.GetString(back, 0, read), "本机的回应要原样送回远端");

            Assert.AreEqual(1, forwarder.TotalConnections);
            await remote.DisposeAsync();
        }
        finally
        {
            try { File.Delete(localPath); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task 路径对不上的Unix套接字回连会被拒绝()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            GrantStreamLocalForward = true,
        });

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartUnixSocketAsync(
            harness.Connection, "/tmp/local.sock", "/tmp/remote.sock",
            cancellationToken: harness.Token);

        ArrayBufferWriter<byte> typeSpecific = new();
        SshDataWriter writer = new(typeSpecific);
        writer.WriteUtf8String("/tmp/someone-elses.sock");
        writer.WriteUtf8String("");

        Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedStreamLocal,
            typeSpecific.WrittenMemory, harness.Token);

        // 路由不上就明确拒绝 —— 沉默地接下来再搬到一个不相干的套接字
        // 才是真正危险的。
        Assert.IsNull(remote, "路径对不上的回连必须被拒绝");
    }

    // ------------------------------------------------------------ 远程动态转发（-R 不给目标）

    /// <summary>服务端开一条 forwarded-tcpip 回连（bind 对得上转发器），给出读写它的流。</summary>
    private static async Task<Stream> OpenDynamicCallbackAsync(Harness harness, int boundPort)
    {
        ArrayBufferWriter<byte> header = new();
        SshDataWriter writer = new(header);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32((uint)boundPort);
        writer.WriteUtf8String("10.9.8.7");
        writer.WriteUInt32(5555);
        Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelForwardedTcpIp, header.WrittenMemory, harness.Token);
        Assert.IsNotNull(remote, "回连应当被接下（动态转发先确认、再握手）");
        return remote;
    }

    /// <summary>远端程序在回连上说 SOCKS5：方法协商 + CONNECT（IPv4 或域名），返回应答码。</summary>
    private static async Task<byte> SocksConnectAsync(Stream remote, string host, int port, CancellationToken cancellationToken)
    {
        await remote.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        await remote.FlushAsync(cancellationToken);
        byte[] method = new byte[2];
        await remote.ReadExactlyAsync(method, cancellationToken);
        Assert.AreEqual(0x00, method[1], "不认证");

        byte[] request = IPAddress.TryParse(host, out IPAddress? ip)
            ? [0x05, 0x01, 0x00, 0x01, .. ip.GetAddressBytes(), (byte)(port >> 8), (byte)port]
            : [0x05, 0x01, 0x00, 0x03, (byte)host.Length, .. Encoding.ASCII.GetBytes(host), (byte)(port >> 8), (byte)port];
        await remote.WriteAsync(request, cancellationToken);
        await remote.FlushAsync(cancellationToken);

        byte[] reply = new byte[10];
        await remote.ReadExactlyAsync(reply, cancellationToken);
        return reply[1];
    }

    /// <summary>
    /// 名单里的目标：本机替远端连上，回成功，之后双向搬运（本机起一个回显服务当目标）；
    /// 转发器的种类是 RemoteDynamic，连接事件的来源是回连里的 originator。
    /// </summary>
    [TestMethod]
    public async Task 远程动态转发_名单里的目标连得上并且双向搬运()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34580 });
        using Socket echo = new(SocketType.Stream, ProtocolType.Tcp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        echo.Listen(4);
        int echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;
        var serving = Task.Run(async () =>
        {
            using Socket accepted = await echo.AcceptAsync(harness.Token);
            byte[] buffer = new byte[4];
            int read = await accepted.ReceiveAsync(buffer, harness.Token);
            await accepted.SendAsync(buffer.AsMemory(0, read), harness.Token);
        });

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartDynamicAsync(
            harness.Connection, RemoteOpenPolicy.Allow($"127.0.0.1:{echoPort}"),
            new RemotePortForwardOptions { BindPort = 0 }, harness.Token);
        Assert.AreEqual(ForwardKind.RemoteDynamic, forwarder.Kind);
        ForwardConnectionEventArgs? opened = null;
        forwarder.ConnectionOpened += (_, e) => opened = e;

        Stream remote = await OpenDynamicCallbackAsync(harness, forwarder.BoundPort);
        Assert.AreEqual(0x00, await SocksConnectAsync(remote, "127.0.0.1", echoPort, harness.Token));

        await remote.WriteAsync("ping"u8.ToArray(), harness.Token);
        await remote.FlushAsync(harness.Token);
        byte[] back = new byte[4];
        await remote.ReadExactlyAsync(back, harness.Token);
        Assert.AreEqual("ping", Encoding.ASCII.GetString(back));
        await serving;

        Assert.IsNotNull(opened);
        Assert.AreEqual($"127.0.0.1:{echoPort}", opened.Target);
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("10.9.8.7"), 5555), opened.Source);
    }

    /// <summary>
    /// 名单外的目标回「规则不允许」（0x02）并报 TargetNotPermitted；按名字比、不先解析 ——
    /// 名单写 127.0.0.1，远端给 localhost 同样不放。
    /// </summary>
    [TestMethod]
    public async Task 远程动态转发_名单外的目标回规则不允许()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34581 });
        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartDynamicAsync(
            harness.Connection, RemoteOpenPolicy.Allow("127.0.0.1:8080"),
            new RemotePortForwardOptions { BindPort = 0 }, harness.Token);
        List<ForwardErrorReason> errors = [];
        forwarder.Error += (_, e) => { lock (errors) { errors.Add(e.Reason); } };

        Stream other = await OpenDynamicCallbackAsync(harness, forwarder.BoundPort);
        Assert.AreEqual(0x02, await SocksConnectAsync(other, "10.0.0.1", 22, harness.Token));
        Assert.AreEqual(0, await other.ReadAsync(new byte[1], harness.Token), "拒了之后这一条就关了");

        Stream byName = await OpenDynamicCallbackAsync(harness, forwarder.BoundPort);
        Assert.AreEqual(0x02, await SocksConnectAsync(byName, "localhost", 8080, harness.Token));

        lock (errors)
        {
            Assert.AreSequenceEqual([ForwardErrorReason.TargetNotPermitted, ForwardErrorReason.TargetNotPermitted], [.. errors]);
        }
    }

    /// <summary>名单里、但本机连不上：按原因回码（连接被拒 0x05），报 TargetConnect。</summary>
    [TestMethod]
    public async Task 远程动态转发_连不上按原因回码()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34582 });
        int closedPort;
        using (Socket probe = new(SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartDynamicAsync(
            harness.Connection, RemoteOpenPolicy.Allow("127.0.0.1:*"),
            new RemotePortForwardOptions { BindPort = 0 }, harness.Token);
        ForwardErrorReason? reason = null;
        forwarder.Error += (_, e) => reason = e.Reason;

        Stream remote = await OpenDynamicCallbackAsync(harness, forwarder.BoundPort);
        Assert.AreEqual((byte)SocksReply.ConnectionRefused, await SocksConnectAsync(remote, "127.0.0.1", closedPort, harness.Token));
        Assert.AreEqual(ForwardErrorReason.TargetConnect, reason);
    }

    /// <summary>放行名单的写法与匹配：通配、IPv6 方括号、端口 *、不分大小写；any / none；写错当场报。</summary>
    [TestMethod]
    public void 放行名单的规则与匹配()
    {
        var policy = RemoteOpenPolicy.Allow("*.corp.example:443", "10.0.0.?:*", "[::1]:22", "Build:8080");

        Assert.IsTrue(policy.Permits("git.corp.example", 443));
        Assert.IsFalse(policy.Permits("git.corp.example", 80));
        Assert.IsFalse(policy.Permits("corp.example", 443), "*. 要求前面还有一段");
        Assert.IsTrue(policy.Permits("10.0.0.7", 5432));
        Assert.IsFalse(policy.Permits("10.0.0.17", 5432));
        Assert.IsTrue(policy.Permits("::1", 22));
        Assert.IsTrue(policy.Permits("build", 8080), "主机名不分大小写");
        Assert.AreEqual("*.corp.example:443 10.0.0.?:* [::1]:22 Build:8080", policy.ToString());

        Assert.IsTrue(RemoteOpenPolicy.Any.Permits("anything", 1));
        Assert.IsFalse(RemoteOpenPolicy.None.Permits("localhost", 22));
        Assert.AreEqual("any", RemoteOpenPolicy.Any.ToString());
        Assert.AreEqual("none", RemoteOpenPolicy.None.ToString());

        Assert.ThrowsExactly<ArgumentException>(() => RemoteOpenPolicy.Allow("localhost"));
        Assert.ThrowsExactly<ArgumentException>(() => RemoteOpenPolicy.Allow("localhost:0"));
        Assert.ThrowsExactly<ArgumentException>(() => RemoteOpenPolicy.Allow(":22"));
        Assert.ThrowsExactly<ArgumentException>(() => RemoteOpenPolicy.Allow("[::1]22"));
    }

    /// <summary>
    /// 写成 IP 的规则只配规范写法的 IP 字面量：<c>*</c> 不能吃过点去配一个以这几段数字开头的域名（它解析到哪由对方的 DNS 说了算），
    /// 简写的 IPv4 也配不上；名字规则不配 IP 字面量；单独的 <c>*</c> 照旧是任何主机。
    /// </summary>
    [TestMethod]
    public void 放行名单_写成IP的规则不配域名也不配简写的地址()
    {
        var subnet = RemoteOpenPolicy.Allow("10.0.0.*:*", "192.168.1.*:22", "127.0.0.1:*");
        Assert.IsTrue(subnet.Permits("10.0.0.7", 22));
        Assert.IsTrue(subnet.Permits("192.168.1.10", 22));
        Assert.IsTrue(subnet.Permits("127.0.0.1", 8080));
        foreach (string name in (string[])["10.0.0.1.attacker.example", "10.0.0.evil.example", "192.168.1.1.attacker.example", "127.0.0.1.nip.example"])
        {
            Assert.IsFalse(subnet.Permits(name, 22), name);
        }
        Assert.IsFalse(RemoteOpenPolicy.Allow("10.0.0.?:*").Permits("10.0.0.7.evil.example", 22));

        // .NET 把这些都当地址连（2130706433 / 0177.0.0.1 / 127.1 / 0x7f.0.0.1 都是 127.0.0.1，010.0.0.1 是 8.0.0.1），文字上和规则对不齐。
        foreach (string shorthand in (string[])["2130706433", "0177.0.0.1", "127.1", "0x7f.0.0.1", "0x7f000001", "10.1", "010.0.0.1"])
        {
            Assert.IsFalse(subnet.Permits(shorthand, 22), shorthand);
        }

        // IPv4 规则不配 IPv4 映射的 IPv6 —— 那要写 IPv6 规则。
        Assert.IsFalse(subnet.Permits("::ffff:127.0.0.1", 22));

        // 名字规则只配域名：碰巧在文字上配得上地址也不算。
        var named = RemoteOpenPolicy.Allow("fe80*:*", "localhost:*");
        Assert.IsTrue(named.Permits("fe80-gw.example", 22));
        Assert.IsFalse(named.Permits("fe80::1", 22));
        Assert.IsTrue(named.Permits("localhost", 22));
        Assert.IsFalse(named.Permits("127.0.0.1", 22), "按名字比、不先解析");

        // 单独的 *：任何主机，IP 字面量（连简写）也算；写法不对的照样不放。
        var star = RemoteOpenPolicy.Allow("*:443");
        foreach (string host in (string[])["intranet.example", "10.0.0.7", "::1", "2130706433"])
        {
            Assert.IsTrue(star.Permits(host, 443), host);
        }
        Assert.IsFalse(star.Permits("intranet.example", 80));
    }

    /// <summary>IPv6 规则两边都换成规范写法再比；只配 IPv6 字面量。</summary>
    [TestMethod]
    public void 放行名单_IPv6按规范写法比()
    {
        Assert.IsTrue(RemoteOpenPolicy.Allow("[0:0:0:0:0:0:0:1]:22").Permits("::1", 22));
        Assert.IsTrue(RemoteOpenPolicy.Allow("[::1]:22").Permits("0:0:0:0:0:0:0:1", 22));
        Assert.IsTrue(RemoteOpenPolicy.Allow("[::ffff:127.0.0.1]:*").Permits("::FFFF:7f00:1", 22));
        Assert.IsTrue(RemoteOpenPolicy.Allow("[2001:db8::*]:*").Permits("2001:0db8:0:0:0:0:0:5", 22), "带通配的拿规范写法去配");
        Assert.IsFalse(RemoteOpenPolicy.Allow("[2001:db8::*]:*").Permits("2001:db9::5", 22));
        Assert.IsTrue(RemoteOpenPolicy.Allow("[10.0.0.1]:22").Permits("10.0.0.1", 22), "方括号只是语法，里面是 IPv4 就是 IPv4 规则");
    }

    /// <summary>
    /// 远端给的主机先过写法检查，<see cref="RemoteOpenPolicy.Any"/> 也不例外：NUL、控制字符、空白、% / 通配符、
    /// 方括号与 %scope、最后一段是数字的非地址、超长的名字一律不放。
    /// </summary>
    [TestMethod]
    public void 放行名单_写法不对的主机一律不放()
    {
        var corp = RemoteOpenPolicy.Allow("*.corp.example:443");
        Assert.IsTrue(corp.Permits("git.corp.example", 443));
        Assert.IsTrue(corp.Permits("_svc.git.corp.example", 443), "下划线是合法的");

        string[] malformed =
        [
            "127.0.0.1\0.corp.example", "a\u0001.corp.example", "a\t.corp.example", "a b.corp.example", "a\r\n.corp.example",
            "a%2e.corp.example", "a/b.corp.example", "*.corp.example", "?.corp.example", "a..corp.example", ".corp.example",
            "git.corp.example\0", "ä.corp.example",
            string.Concat(Enumerable.Repeat("a", 64)) + ".corp.example",
            string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".corp.example",
        ];
        foreach (string host in malformed)
        {
            Assert.IsFalse(corp.Permits(host, 443), host);
            Assert.IsFalse(RemoteOpenPolicy.Any.Permits(host, 443), host);
        }

        foreach (string host in (string[])["", "[::1]", "fe80::1%eth0", "fe80::1%12", "::1.evil", "10.0.0.256", "1.2.3.4.5", "10.0.0.1.", "a.0x7f"])
        {
            Assert.IsFalse(RemoteOpenPolicy.Any.Permits(host, 22), host);
            Assert.IsFalse(RemoteOpenPolicy.Allow("*:*").Permits(host, 22), host);
        }

        // 合法的照旧：绝对名字的末尾点、IDN 转出来的 Punycode。
        Assert.IsTrue(RemoteOpenPolicy.Any.Permits("intranet.example.", 22));
        Assert.IsTrue(RemoteOpenPolicy.Any.Permits("xn--bcher-kva.example", 22));
    }

    /// <summary>
    /// 端到端：远端在 SOCKS 握手里给一个带 NUL 的域名（名字规则在文字上配得上），本机不去拨、回「规则不允许」；
    /// 给一个以名单里那几段数字开头的域名同样回 0x02 —— 而不是拿去本机解析。
    /// </summary>
    [TestMethod]
    public async Task 远程动态转发_绕名单的主机名回规则不允许()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34583 });
        using Socket echo = new(SocketType.Stream, ProtocolType.Tcp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        echo.Listen(4);
        int echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;

        await using RemotePortForwarder forwarder = await RemotePortForwarder.StartDynamicAsync(
            harness.Connection, RemoteOpenPolicy.Allow($"*.corp.example:{echoPort}", $"127.0.0.*:{echoPort}"),
            new RemotePortForwardOptions { BindPort = 0 }, harness.Token);
        List<ForwardErrorReason> errors = [];
        forwarder.Error += (_, e) => { lock (errors) { errors.Add(e.Reason); } };
        int opened = 0;
        forwarder.ConnectionOpened += (_, _) => Interlocked.Increment(ref opened);

        Stream nul = await OpenDynamicCallbackAsync(harness, forwarder.BoundPort);
        Assert.AreEqual(0x02, await SocksConnectAsync(nul, "127.0.0.1\0.corp.example", echoPort, harness.Token));

        Stream dotted = await OpenDynamicCallbackAsync(harness, forwarder.BoundPort);
        Assert.AreEqual(0x02, await SocksConnectAsync(dotted, "127.0.0.1.attacker.invalid", echoPort, harness.Token));

        lock (errors)
        {
            Assert.AreSequenceEqual([ForwardErrorReason.TargetNotPermitted, ForwardErrorReason.TargetNotPermitted], [.. errors]);
        }
        Assert.AreEqual(0, Volatile.Read(ref opened));
    }

    // ------------------------------------------------------------ Unix 域套接字的本地转发

    /// <summary>回显的隧道处理器：记下目标，收到什么回什么。</summary>
    private static Func<string, PipeReader, PipeWriter, CancellationToken, Task> EchoTunnel(List<string> targets) =>
        async (target, input, output, cancellationToken) =>
        {
            lock (targets)
            {
                targets.Add(target);
            }
            while (true)
            {
                ReadResult read = await input.ReadAsync(cancellationToken);
                foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                {
                    await output.WriteAsync(segment, cancellationToken);
                }
                input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                {
                    await output.CompleteAsync();
                    return;
                }
            }
        };

    private static async Task<string> EchoThroughAsync(Socket client, string text, CancellationToken cancellationToken)
    {
        await client.SendAsync(Encoding.ASCII.GetBytes(text), cancellationToken);
        byte[] back = new byte[text.Length];
        int got = 0;
        while (got < back.Length)
        {
            got += await client.ReceiveAsync(back.AsMemory(got), cancellationToken);
        }
        return Encoding.ASCII.GetString(back);
    }

    /// <summary>短一点的套接字路径（Windows 与 Linux 上限都是 108 字节）。</summary>
    private static string ShortSocketPath() => Path.Combine(Path.GetTempPath(), $"vs-{Guid.NewGuid():N}"[..11] + ".sock");

    /// <summary>
    /// <c>-L 端口:/var/run/docker.sock</c>：本机 TCP 监听，出站走 direct-streamlocal，服务端看到的目标是那个路径；双向搬运。
    /// </summary>
    [TestMethod]
    public async Task 本地转发到远端的Unix套接字()
    {
        List<string> targets = [];
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { TunnelHandler = EchoTunnel(targets) });
        await using var forwarder = LocalPortForwarder.StartToUnixSocket(harness.Connection, "/var/run/docker.sock");

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
        Assert.AreEqual("docker", await EchoThroughAsync(client, "docker", harness.Token));

        lock (targets)
        {
            Assert.AreSequenceEqual(["/var/run/docker.sock"], [.. targets]);
        }
    }

    /// <summary>
    /// <c>-L /路径/local.sock:host:port</c>：本机在 Unix 套接字上监听、双向搬运；非 Windows 上文件权限是 0600；
    /// 释放之后套接字文件被删掉。
    /// </summary>
    [TestMethod]
    public async Task 在本机的Unix套接字上监听_释放后删掉文件()
    {
        List<string> targets = [];
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { TunnelHandler = EchoTunnel(targets) });
        string path = ShortSocketPath();

        var forwarder = LocalPortForwarder.Start(
            harness.Connection, "db.internal", 5432, new LocalPortForwardOptions { ListenSocketPath = path });
        try
        {
            Assert.IsTrue(File.Exists(path));
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), "只有自己能连");
            }

            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path), harness.Token);
            Assert.AreEqual("psql", await EchoThroughAsync(client, "psql", harness.Token));
            lock (targets)
            {
                Assert.AreSequenceEqual(["db.internal:5432"], [.. targets]);
            }
        }
        finally
        {
            await forwarder.DisposeAsync();
        }

        Assert.IsFalse(File.Exists(path), "释放之后套接字文件要删掉");
    }

    /// <summary>
    /// 那里已经有文件：默认报 ForwardBindFailed、文件原样留着（可能是别人的）；打开「替换已有的套接字」才删掉重建。
    /// 路径太长当场报。
    /// </summary>
    [TestMethod]
    public async Task 套接字路径已有文件时默认不动它_太长当场报()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());
        string path = ShortSocketPath();
        await File.WriteAllTextAsync(path, "someone else's", harness.Token);
        try
        {
            SshForwardException exists = Assert.ThrowsExactly<SshForwardException>(() => LocalPortForwarder.Start(
                harness.Connection, "h", 1, new LocalPortForwardOptions { ListenSocketPath = path }));
            Assert.AreEqual(SshFailureReason.ForwardBindFailed, exists.Reason);
            Assert.AreEqual(SshForwardFailureKind.SocketPathExists, exists.FailureKind);
            Assert.AreEqual(path, exists.Endpoint);
            Assert.AreEqual("someone else's", await File.ReadAllTextAsync(path, harness.Token));

            await using var replaced = LocalPortForwarder.Start(
                harness.Connection, "h", 1, new LocalPortForwardOptions { ListenSocketPath = path, AllowSocketReplacement = true });
            Assert.IsTrue(replaced.IsActive);
        }
        finally
        {
            File.Delete(path);
        }

        string tooLong = Path.Combine(Path.GetTempPath(), new string('x', 120) + ".sock");
        SshForwardException longPath = Assert.ThrowsExactly<SshForwardException>(() => LocalPortForwarder.Start(
            harness.Connection, "h", 1, new LocalPortForwardOptions { ListenSocketPath = tooLong }));
        Assert.Contains("太长", longPath.Message);
        Assert.AreEqual(SshForwardFailureKind.SocketPathTooLong, longPath.FailureKind);
        Assert.AreEqual(tooLong, longPath.Endpoint);
    }

    // ------------------------------------------------------------ 计量与限速

    /// <summary>
    /// 限速 64 KiB/秒、一来一回各 192 KiB：先用掉一秒的突发额度，剩下 128 KiB 要两秒左右；数据一字节不差。
    /// 搬运期间 Connections 里有这一条（来源、目标、已搬的字节）；搬完就没了。
    /// </summary>
    [TestMethod]
    public async Task 限速的转发按额度放慢_连接快照里看得到这一条()
    {
        List<string> targets = [];
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { TunnelHandler = EchoTunnel(targets) });
        await using var forwarder = LocalPortForwarder.Start(
            harness.Connection, "bulk.internal", 9000, new LocalPortForwardOptions { MaxBytesPerSecond = 64 * 1024 });

        using Socket client = new(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);

        byte[] payload = new byte[192 * 1024];
        Random.Shared.NextBytes(payload);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Task sending = client.SendAsync(payload, harness.Token).AsTask();

        byte[] back = new byte[payload.Length];
        int got = 0;
        bool sawSnapshot = false;
        while (got < back.Length)
        {
            got += await client.ReceiveAsync(back.AsMemory(got), harness.Token);
            if (!sawSnapshot && forwarder.Connections is [{ } live])
            {
                sawSnapshot = true;
                Assert.AreEqual("bulk.internal:9000", live.Target);
                Assert.IsInstanceOfType<IPEndPoint>(live.Source);
                Assert.IsGreaterThan(0L, live.BytesSent);
            }
        }
        await sending;
        elapsed.Stop();

        Assert.AreSequenceEqual(payload, back);
        Assert.IsTrue(sawSnapshot, "搬运期间连接快照里应当有这一条");
        Assert.IsGreaterThan(TimeSpan.FromSeconds(1.5), elapsed.Elapsed, $"限速没起作用（{elapsed.Elapsed.TotalSeconds:0.00} 秒）");

        client.Shutdown(SocketShutdown.Both);
        for (int i = 0; i < 200 && forwarder.Connections.Count > 0; i++)
        {
            await Task.Delay(10, harness.Token);
        }
        Assert.IsEmpty(forwarder.Connections, "搬完就从快照里摘掉");
    }

    // ------------------------------------------------------------ ssh_config 里的转发

    /// <summary>配置里的三种转发都起得来：本地（端口 0）、动态、远程（服务端分配端口）；种类对得上。</summary>
    [TestMethod]
    public async Task 配置里的转发一起起来()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript { GrantRemoteForwardPort = 34590 });
        SshHostConfig config = SshConfigFile.Resolve(SshConfigFile.Parse("""
            Host h
                LocalForward 127.0.0.1:0 db.internal:5432
                DynamicForward 0
                RemoteForward 0 localhost:22
            """), "h");

        IReadOnlyList<PortForwarder> started = await SshConfigFile.StartForwardsAsync(harness.Connection, config, cancellationToken: harness.Token);
        try
        {
            Assert.AreSequenceEqual([ForwardKind.Local, ForwardKind.Remote, ForwardKind.Dynamic], [.. started.Select(f => f.Kind)]);
            Assert.AreEqual(34590, ((RemotePortForwarder)started[1]).BoundPort);
            Assert.IsTrue(started.All(f => f.IsActive));
        }
        finally
        {
            foreach (PortForwarder forwarder in started)
            {
                await forwarder.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 服务端拒了远程转发：没开 ExitOnForwardFailure 时报给回调、其余照起；开了就把已起的全部撤掉、整体失败（消息里有那一行）。
    /// </summary>
    [TestMethod]
    public async Task 配置里的转发起不来时按ExitOnForwardFailure处理()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());
        const string forwards = """
                LocalForward 127.0.0.1:0 db.internal:5432
                RemoteForward 0 localhost:22
            """;

        SshHostConfig lenient = SshConfigFile.Resolve(SshConfigFile.Parse("Host h\n" + forwards), "h");
        List<string> failed = [];
        IReadOnlyList<PortForwarder> started = await SshConfigFile.StartForwardsAsync(
            harness.Connection, lenient, (forward, _) => failed.Add(forward.Line), harness.Token);
        Assert.HasCount(1, started);
        Assert.AreSequenceEqual(["0 localhost:22"], [.. failed]);
        await started[0].DisposeAsync();

        SshHostConfig strict = SshConfigFile.Resolve(SshConfigFile.Parse("Host h\n    ExitOnForwardFailure yes\n" + forwards), "h");
        SshForwardException failure = await Assert.ThrowsAsync<SshForwardException>(
            async () => await SshConfigFile.StartForwardsAsync(harness.Connection, strict, cancellationToken: harness.Token));
        Assert.AreEqual(SshFailureReason.ForwardSetupFailed, failure.Reason);
        Assert.Contains("0 localhost:22", failure.Message);
        Assert.AreEqual(SshForwardFailureKind.ConfigForwardFailed, failure.FailureKind);
        Assert.Contains("0 localhost:22", failure.ConfigLine!);
    }
}
