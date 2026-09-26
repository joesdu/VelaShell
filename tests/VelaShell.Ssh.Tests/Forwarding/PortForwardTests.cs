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
using VelaShell.Ssh.Crypto;
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
        Assert.AreSequenceEqual(new[] { "10.0.0.9:80" }, harness.Observed.TunnelTargets, "目标要如实传给服务端");
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
            await client.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
            byte[] buffer = new byte[16];
            _ = await ReadAllAsync(client, buffer, harness.Token);
        }

        await WaitUntilAsync(() => errors.Count > 0, harness.Token);

        // **单条连接的失败绝不影响转发器本身。**一条隧道要能跑几天，
        // 期间必然有连不上的目标。把这些当成致命错误，隧道就没法用了。
        Assert.AreEqual(ForwardErrorReason.ChannelOpen, errors[0].Reason);
        Assert.Contains("AllowTcpForwarding", errors[0].Message);
        Assert.IsTrue(forwarder.IsActive, "转发器必须还活着");

        // 再来一条，仍然能被接受（并仍然失败）。
        using Socket second = new(SocketType.Stream, ProtocolType.Tcp);
        await second.ConnectAsync(forwarder.BoundEndPoint!, harness.Token);
        await WaitUntilAsync(() => errors.Count > 1, harness.Token);
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
        Assert.AreSequenceEqual(new[] { "db.internal:5201" }, harness.Observed.TunnelTargets, "域名要原样送到服务端 —— 本地解析会让内网域名直接失效，而且泄漏访问目标");
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

    // ------------------------------------------------------------ 远程转发

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
            new[] { ("localhost", 0) }, harness.Observed.RemoteForwardBinds, "绑定地址要原样传，不做规范化");
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
        Assert.AreSequenceEqual(new[] { "127.0.0.1:8080" }, harness.Observed.TunnelTargets);
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
        Assert.AreSequenceEqual(new[] { "/var/run/docker.sock" }, harness.Observed.TunnelTargets);
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

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
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

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
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
    }

    [TestMethod]
    public async Task 连接断了之后本地转发放出端口()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript());

        await using var forwarder = LocalPortForwarder.Start(harness.Connection, "t", 1);
        int port = ((IPEndPoint)forwarder.BoundEndPoint!).Port;

        await harness.DropServerAsync();

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !forwarder.IsActive, deadline.Token);

        // 端口放出来了：重连之后重建同一个转发要靠这个，否则只会得到「端口已被占用」。
        using Socket rebind = new(SocketType.Stream, ProtocolType.Tcp);
        rebind.Bind(new IPEndPoint(IPAddress.Loopback, port));
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
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
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
        Assert.AreSequenceEqual(new[] { remotePath }, harness.Observed.StreamLocalForwardBinds);
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
}
