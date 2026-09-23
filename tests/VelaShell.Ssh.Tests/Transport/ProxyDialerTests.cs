// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md
//
// 假代理跑在真的环回 TCP 上，握手之后在同一条流上直接跑测试 SSH 服务端 ——
// 于是「代理应答后面紧跟着 SSH 标识串」这件事是端到端验到的，不是模拟的。

using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

[TestClass]
public sealed class ProxyDialerTests
{
    private const string TargetHost = "target.internal";

    // ------------------------------------------------------------ SOCKS5

    [TestMethod]
    public async Task 经SOCKS5代理连上且主机名交给代理解析()
    {
        await using FakeSocks5Proxy proxy = FakeSocks5Proxy.Start();

        await using SshConnection connection = await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port));
        SshCommandOutput output = await connection.RunAsync("hello");

        Assert.AreEqual("来自目标", output.StandardOutput);

        // ⚠️ 主机名必须原样交给代理（地址类型 3），不在本机解析。
        (byte addressType, string host, int port) = proxy.Requests.Single();
        Assert.AreEqual(3, addressType);
        Assert.AreEqual(TargetHost, host);
        Assert.AreEqual(22, port);
    }

    [TestMethod]
    public async Task SOCKS5的用户名口令认证()
    {
        await using FakeSocks5Proxy proxy = FakeSocks5Proxy.Start(required: new SshProxyCredentials("alice", "s3cret"));

        await using SshConnection connection = await ConnectAsync(
            DialerChain.Socks5("127.0.0.1", proxy.Port, new SshProxyCredentials("alice", "s3cret")));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
    }

    [TestMethod]
    public async Task SOCKS5要认证而没配凭据时报ProxyAuthRequired()
    {
        await using FakeSocks5Proxy proxy = FakeSocks5Proxy.Start(required: new SshProxyCredentials("alice", "s3cret"));

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyAuthRequired, ex.Reason);
    }

    [TestMethod]
    public async Task SOCKS5拒绝时带着结果码与每一跳()
    {
        await using FakeSocks5Proxy proxy = FakeSocks5Proxy.Start(replyCode: 5);

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        StringAssert.Contains(ex.Message, "目标拒绝连接");

        // 从近到远：到代理成功，代理到目标失败。
        Assert.HasCount(2, ex.Hops);
        Assert.AreEqual(SshDialKind.Tcp, ex.Hops[0].Kind);
        Assert.IsTrue(ex.Hops[0].Succeeded);
        Assert.AreEqual(SshDialKind.Socks5, ex.Hops[1].Kind);
        Assert.IsFalse(ex.Hops[1].Succeeded);
        Assert.AreEqual($"{TargetHost}:22", ex.Hops[1].Target);
    }

    [TestMethod]
    public async Task 代理本身连不上时说清是哪一跳()
    {
        int closedPort = FindClosedPort();

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.Socks5("127.0.0.1", closedPort)));

        Assert.AreEqual(SshFailureReason.TcpRefused, ex.Reason);
        StringAssert.Contains(ex.Message, "SOCKS5 代理");
        Assert.AreEqual(SshDialKind.Tcp, ex.Hops.Single().Kind);
        Assert.IsFalse(ex.Hops.Single().Succeeded);
    }

    [TestMethod]
    public void SOCKS5请求里IP字面量按IP发()
    {
        byte[] v4 = Socks5Dialer.BuildConnectRequest(new SshEndPoint("10.0.0.9", 22));
        Assert.AreEqual(1, v4[3]);
        CollectionAssert.AreEqual(new byte[] { 10, 0, 0, 9 }, v4[4..8]);

        byte[] v6 = Socks5Dialer.BuildConnectRequest(new SshEndPoint("::1", 2222));
        Assert.AreEqual(4, v6[3]);
        Assert.AreEqual(2222, BinaryPrimitives.ReadUInt16BigEndian(v6.AsSpan(20)));

        byte[] idn = Socks5Dialer.BuildConnectRequest(new SshEndPoint("例子.测试", 22));
        Assert.AreEqual(3, idn[3]);
        StringAssert.StartsWith(Encoding.ASCII.GetString(idn, 5, idn[4]), "xn--");
    }

    // ------------------------------------------------------------ HTTP CONNECT

    /// <summary>
    /// 200 应答与 SSH 标识串在<b>同一次写</b>里到达：多读到的字节必须原样交还给 SSH 层。
    /// </summary>
    [TestMethod]
    public async Task 经HTTP代理连上且应答后紧跟的标识串不丢()
    {
        await using FakeHttpProxy proxy = FakeHttpProxy.Start("HTTP/1.1 200 Connection established");

        await using SshConnection connection = await ConnectAsync(
            DialerChain.HttpConnect("127.0.0.1", proxy.Port, new SshProxyCredentials("bob", "pw")));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);

        string request = proxy.Requests.Single();
        StringAssert.StartsWith(request, $"CONNECT {TargetHost}:22 HTTP/1.1\r\n");
        StringAssert.Contains(request, $"Host: {TargetHost}:22\r\n");
        StringAssert.Contains(
            request, $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:pw"))}\r\n");
    }

    [TestMethod]
    public async Task HTTP代理407报ProxyAuthRequired()
    {
        await using FakeHttpProxy proxy = FakeHttpProxy.Start(
            "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.HttpConnect("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyAuthRequired, ex.Reason);
        StringAssert.Contains(ex.Message, "Basic realm");
    }

    [TestMethod]
    public async Task HTTP代理拒绝22端口时给出建议()
    {
        await using FakeHttpProxy proxy = FakeHttpProxy.Start("HTTP/1.1 403 Forbidden");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(DialerChain.HttpConnect("127.0.0.1", proxy.Port)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        StringAssert.Contains(ex.Message, "80/443");
    }

    /// <summary>嵌套：经 HTTP 代理到达 SOCKS5 代理，再由它连目标。跳信息从近到远。</summary>
    [TestMethod]
    public async Task 代理可以嵌套且失败时跳信息从近到远()
    {
        await using FakeHttpProxy http = FakeHttpProxy.Start("HTTP/1.1 502 Bad Gateway");

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(
                DialerChain.Socks5("socks.internal", 1080).Via(DialerChain.HttpConnect("127.0.0.1", http.Port))));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.AreEqual(SshDialKind.Tcp, ex.Hops[0].Kind);
        Assert.IsTrue(ex.Hops[0].Succeeded);
        Assert.AreEqual(SshDialKind.HttpConnect, ex.Hops[1].Kind);
        Assert.AreEqual("socks.internal:1080", ex.Hops[1].Target, "HTTP 代理那一跳要连的是 SOCKS5 代理");
        Assert.IsFalse(ex.Hops[1].Succeeded);
    }

    // ------------------------------------------------------------ 跳板

    [TestMethod]
    public async Task 经跳板连上目标且目标名交给跳板解析()
    {
        List<string> tunnelTargets = [];
        await using JumpHost jump = new(tunnelTargets);

        await using SshConnection connection = await ConnectAsync(new SshJumpDialer(jump.Options));

        Assert.AreEqual("来自目标", (await connection.RunAsync("hello")).StandardOutput);
        Assert.AreEqual($"{TargetHost}:22", tunnelTargets.Single());
    }

    [TestMethod]
    public async Task 跳板拒绝转发时报ProxyRefused且说清哪一跳()
    {
        await using JumpHost jump = new(tunnelTargets: null);   // 不配隧道处理器 = 拒绝

        SshConnectException ex = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await ConnectAsync(new SshJumpDialer(jump.Options)));

        Assert.AreEqual(SshFailureReason.ProxyRefused, ex.Reason);
        Assert.HasCount(2, ex.Hops);
        Assert.IsTrue(ex.Hops[0].Succeeded, "跳板本身是连上了的");
        Assert.AreEqual(SshDialKind.SshJump, ex.Hops[1].Kind);
        Assert.IsFalse(ex.Hops[1].Succeeded);
    }

    // ------------------------------------------------------------ 代理命令

    [TestMethod]
    public void 代理命令的记号替换()
    {
        ProxyCommandDialer dialer = new("nc -X connect -x proxy:3128 %h %p # %r@%n 100%%")
        {
            UserName = "joe",
            OriginalHost = "alias",
        };

        Assert.AreEqual(
            "nc -X connect -x proxy:3128 10.0.0.9 2222 # joe@alias 100%",
            dialer.Expand(new SshEndPoint("10.0.0.9", 2222)));
    }

    [TestMethod]
    public async Task 代理命令失败时带出它在stderr上说的话()
    {
        string command = OperatingSystem.IsWindows()
            ? "echo 代理拒绝了 1>&2 & exit /b 3"
            : "echo 代理拒绝了 >&2; exit 3";

        await using Stream stream = await new ProxyCommandDialer(command)
            .DialAsync(SshDialTarget.Direct(TargetHost, 22));

        IOException ex = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await stream.ReadExactlyAsync(new byte[16]));

        StringAssert.Contains(ex.Message, "退出码 3");
    }

    // ------------------------------------------------------------ 脚手架

    private static async Task<SshConnection> ConnectAsync(ISshTransportDialer dialer)
    {
        SshConnectionOptions options = new($"joe@{TargetHost}:22")
        {
            Dialer = dialer,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        return await options.ConnectAsync();
    }

    /// <summary>在一条流上跑完整的测试 SSH 服务端（握手 → 认证 → 通道）。</summary>
    private static async Task ServeSshAsync(Stream stream, TestChannelScript script, CancellationToken cancellationToken)
    {
        try
        {
            await using TestSshServer server = new(stream);
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);

            TestAuthServer auth = new(
                server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            await auth.RunAsync(cancellationToken);

            TestChannelServer channels = new(server.Transport, script);
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端走了、用例拆场 —— 服务端这一侧的收尾不是被测对象。
        }
    }

    private static TestChannelScript TargetScript() => new()
    {
        StandardOutput = Encoding.UTF8.GetBytes("来自目标"),
        ExitCode = 0,
    };

    private static int FindClosedPort()
    {
        using Socket probe = new(SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;   // 绑了但不 Listen，随后释放 —— 连它会被拒
    }

    /// <summary>一个会说 SOCKS5 的假代理：握手完之后自己就是目标 SSH 服务端。</summary>
    private sealed class FakeSocks5Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly SshProxyCredentials? _required;
        private readonly byte _replyCode;
        private readonly Task _loop;

        private FakeSocks5Proxy(SshProxyCredentials? required, byte replyCode)
        {
            _required = required;
            _replyCode = replyCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<(byte AddressType, string Host, int Port)> Requests { get; } = [];

        public static FakeSocks5Proxy Start(SshProxyCredentials? required = null, byte replyCode = 0) =>
            new(required, replyCode);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    Socket socket = await _listener.AcceptSocketAsync(_cts.Token);
                    _ = HandleAsync(new NetworkStream(socket, ownsSocket: true));
                }
            }
            catch (Exception)
            {
                // 收工。
            }
        }

        private async Task HandleAsync(NetworkStream stream)
        {
            await using NetworkStream owned = stream;
            try
            {
                byte[] head = await ReadAsync(stream, 2);
                byte[] methods = await ReadAsync(stream, head[1]);

                byte choice = _required is null ? (byte)0
                    : methods.Contains((byte)2) ? (byte)2
                    : (byte)0xFF;
                await stream.WriteAsync(new byte[] { 5, choice }, _cts.Token);
                if (choice == 0xFF)
                {
                    return;
                }

                if (choice == 2)
                {
                    _ = await ReadAsync(stream, 1);
                    string user = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                    string password = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
                    bool ok = user == _required!.UserName && password == _required.Password;
                    await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, _cts.Token);
                    if (!ok)
                    {
                        return;
                    }
                }

                byte[] request = await ReadAsync(stream, 4);
                string host = request[3] switch
                {
                    1 => new IPAddress(await ReadAsync(stream, 4)).ToString(),
                    4 => new IPAddress(await ReadAsync(stream, 16)).ToString(),
                    _ => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0])),
                };
                int port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2));
                lock (Requests)
                {
                    Requests.Add((request[3], host, port));
                }

                await stream.WriteAsync(new byte[] { 5, _replyCode, 0, 1, 0, 0, 0, 0, 0, 0 }, _cts.Token);
                if (_replyCode != 0)
                {
                    return;
                }

                await ServeSshAsync(stream, TargetScript(), _cts.Token);
            }
            catch (Exception)
            {
                // 客户端走了。
            }
        }

        private async Task<byte[]> ReadAsync(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, _cts.Token);
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }

    /// <summary>一个会说 HTTP CONNECT 的假代理：200 之后自己就是目标 SSH 服务端。</summary>
    private sealed class FakeHttpProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly string _responseHead;
        private readonly Task _loop;

        private FakeHttpProxy(string responseHead)
        {
            _responseHead = responseHead;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Requests { get; } = [];

        public static FakeHttpProxy Start(string responseHead) => new(responseHead);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    Socket socket = await _listener.AcceptSocketAsync(_cts.Token);
                    _ = HandleAsync(new NetworkStream(socket, ownsSocket: true));
                }
            }
            catch (Exception)
            {
                // 收工。
            }
        }

        private async Task HandleAsync(NetworkStream stream)
        {
            await using NetworkStream owned = stream;
            try
            {
                // 一个字节一个字节读到空行 —— 假代理不能多读，多读的是客户端的 SSH 标识串。
                StringBuilder request = new();
                byte[] one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(one, _cts.Token);
                    request.Append((char)one[0]);
                }
                lock (Requests)
                {
                    Requests.Add(request.ToString());
                }

                byte[] response = Encoding.ASCII.GetBytes(_responseHead + "\r\n\r\n");
                if (!_responseHead.Contains(" 200 ", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(response, _cts.Token);
                    return;
                }

                // ⚠️ 200 与服务端的第一次写（标识串）**合成一次写** —— 验「多读的字节要交还」。
                await ServeSshAsync(new FirstWritePrefixStream(response, stream), TargetScript(), _cts.Token);
            }
            catch (Exception)
            {
                // 客户端走了。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }

    /// <summary>把一段字节拼到第一次写的前面。</summary>
    private sealed class FirstWritePrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private byte[]? _prefix = prefix;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _prefix, null) is { } first)
            {
                await inner.WriteAsync((byte[])[.. first, .. buffer.Span], cancellationToken);
                return;
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>一台内存里的跳板：它的 direct-tcpip 隧道里再跑一台目标 SSH 服务端。</summary>
    private sealed class JumpHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly List<Task> _servers = [];

        public JumpHost(List<string>? tunnelTargets)
        {
            TestChannelScript jumpScript = tunnelTargets is null
                ? new TestChannelScript()
                : new TestChannelScript
                {
                    TunnelHandler = (target, input, output, ct) =>
                    {
                        lock (tunnelTargets)
                        {
                            tunnelTargets.Add(target);
                        }
                        return ServeSshAsync(new DuplexPipeStream(input, output), TargetScript(), ct);
                    },
                };

            Options = new SshConnectionOptions("jumper@jump.example:22")
            {
                Dialer = InMemoryTransport.CreateDialer((server, _, _) =>
                {
                    lock (_servers)
                    {
                        _servers.Add(ServeSshAsync(server, jumpScript, _cts.Token));
                    }
                    return ValueTask.CompletedTask;
                }),
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                Credentials = [new PasswordCredential("hunter2")],
            };
        }

        public SshConnectionOptions Options { get; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            Task[] servers;
            lock (_servers)
            {
                servers = [.. _servers];
            }
            await Task.WhenAll(servers);
            _cts.Dispose();
        }
    }

    /// <summary>一对管道拼成的双向流（隧道的服务端那一头）。</summary>
    private sealed class DuplexPipeStream(PipeReader input, PipeWriter output) : Stream
    {
        private readonly Stream _read = input.AsStream();
        private readonly Stream _write = output.AsStream();

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _write.WriteAsync(buffer, cancellationToken);
            await _write.FlushAsync(cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

        public override void Flush() => _write.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
