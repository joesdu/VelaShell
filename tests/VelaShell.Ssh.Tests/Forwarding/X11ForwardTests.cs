// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.5
//
// 这一组盯的是**一件事**：真 cookie 一步都不能离开本机。
// X11 没有客户端隔离 —— 把本机显示交出去，等于把所有图形会话的
// 输入输出交出去。假 cookie 那一层是唯一的门。

using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class X11ForwardTests
{
    /// <summary>本机真实的 cookie —— 它**绝不该**出现在发给服务端的报文里。</summary>
    private static readonly byte[] _realCookie =
        [0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    [TestMethod]
    public async Task 请求X11转发时发出去的是假cookie()
    {
        // ⚠️ 整个机制的安全核心就在这一条。
        await using Fixture fixture = await Fixture.StartAsync();

        await using X11Forwarder forwarder = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session, fixture.Options, fixture.Harness.Token);

        TestX11Request request = fixture.Harness.Channels.Observation.X11Requests.Single();

        Assert.AreEqual(XAuthority.MitMagicCookie1, request.AuthProtocol);
        Assert.AreEqual(7, request.ScreenNumber, "屏幕号要按 DISPLAY 里的来");
        Assert.IsFalse(request.SingleConnection, "默认允许多条 X11 连接");

        // **真 cookie 一个字节都不该出现在线上。**
        Assert.AreNotEqual(
            Convert.ToHexStringLower(_realCookie), request.AuthCookieHex,
            "发给服务端的必须是假 cookie");

        // 而且它确实是个合法的十六进制 cookie（不是原始字节被当成文本）。
        byte[] sent = Convert.FromHexString(request.AuthCookieHex);
        Assert.HasCount(16, sent);
        CollectionAssert.AreNotEqual(_realCookie, sent);
    }

    [TestMethod]
    public async Task 服务端拒绝时抛出且不留半挂的处理器()
    {
        await using Fixture fixture = await Fixture.StartAsync(grantX11: false);

        SshForwardException error = await Assert.ThrowsExactlyAsync<SshForwardException>(
            async () => await X11Forwarder.RequestAsync(
                fixture.Harness.Connection, fixture.Session, fixture.Options, fixture.Harness.Token));

        Assert.Contains("X11Forwarding", error.Message);

        // 处理器要摘干净 —— 不然服务端之后开的 x11 通道会被一个
        // 半挂的转发器接走。
        Stream? channel = await fixture.Harness.Channels.OpenChannelToClientAsync(
            SshAlgorithmNames.ChannelX11, X11Origin(), fixture.Harness.Token);

        Assert.IsNull(channel, "请求失败之后不该还接受 x11 通道");
    }

    [TestMethod]
    public async Task 拿着假cookie连过来会被换成真cookie再转给X_server()
    {
        // 端到端：起一个假的 X server，让服务端开一条 x11 通道，
        // 用假 cookie 走完 X11 建立握手，检查落到 X server 上的是**真** cookie。
        await using Fixture fixture = await Fixture.StartAsync();
        using var xserver = FakeXServer.Start();

        await using X11Forwarder forwarder = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session,
            fixture.Options with { Display = xserver.Display }, fixture.Harness.Token);

        byte[] fakeCookie = Convert.FromHexString(
            fixture.Harness.Channels.Observation.X11Requests.Single().AuthCookieHex);

        await using Stream remote = await OpenX11Async(fixture);

        // 远端 X 客户端发建立报文，带**假** cookie。
        byte[] setup = BuildSetup(bigEndian: true, XAuthority.MitMagicCookie1, fakeCookie);
        await remote.WriteAsync(setup, fixture.Harness.Token);
        await remote.FlushAsync(fixture.Harness.Token);

        byte[] arrived = await xserver.ReadSetupAsync(fixture.Harness.Token);

        Assert.IsTrue(
            X11SetupMessage.TryParse(new ReadOnlySequence<byte>(arrived), out X11SetupMessage.Parsed parsed),
            "落到 X server 上的应当是一个完整的建立报文");

        Assert.AreSequenceEqual(
            _realCookie, parsed.ProtocolData, "转给本机 X server 的必须是**真** cookie —— 假的那个 X server 不认");

        Assert.AreEqual(1, forwarder.AcceptedChannels);
        Assert.AreEqual(0, forwarder.RejectedChannels);
    }

    [TestMethod]
    public async Task Cookie不对的连接会被拒绝而且不碰X_server()
    {
        // ⚠️ 放过去就等于把本机显示交给任何知道端口的人。
        await using Fixture fixture = await Fixture.StartAsync();
        using var xserver = FakeXServer.Start();

        await using X11Forwarder forwarder = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session,
            fixture.Options with { Display = xserver.Display }, fixture.Harness.Token);

        await using Stream remote = await OpenX11Async(fixture);

        // 拿一个我们没发过的 cookie 连过来。
        byte[] wrong = X11SetupMessage.CreateFakeCookie();
        await remote.WriteAsync(
            BuildSetup(bigEndian: false, XAuthority.MitMagicCookie1, wrong), fixture.Harness.Token);
        await remote.FlushAsync(fixture.Harness.Token);

        // 通道会被关掉，而且 **X server 上不该有任何连接**。
        await WaitForAsync(() => forwarder.RejectedChannels == 1, fixture.Harness.Token);

        Assert.AreEqual(0, forwarder.AcceptedChannels, "cookie 不对不该被接受");
        Assert.AreEqual(
            0, xserver.AcceptedConnections,
            "cookie 不对时连**碰都不该碰**本机 X server");
    }

    [TestMethod]
    public async Task 非MIT_MAGIC_COOKIE_1的授权协议会被拒绝()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using var xserver = FakeXServer.Start();

        await using X11Forwarder forwarder = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session,
            fixture.Options with { Display = xserver.Display }, fixture.Harness.Token);

        byte[] fakeCookie = Convert.FromHexString(
            fixture.Harness.Channels.Observation.X11Requests.Single().AuthCookieHex);

        await using Stream remote = await OpenX11Async(fixture);

        // cookie 字节是对的，但协议名不是我们支持的那个。
        await remote.WriteAsync(
            BuildSetup(bigEndian: true, "XDM-AUTHORIZATION-1", fakeCookie), fixture.Harness.Token);
        await remote.FlushAsync(fixture.Harness.Token);

        await WaitForAsync(() => forwarder.RejectedChannels == 1, fixture.Harness.Token);
        Assert.AreEqual(0, xserver.AcceptedConnections);
    }

    [TestMethod]
    public async Task 没请求过X11转发的连接不接受x11通道()
    {
        // 不然任何服务端都能主动往我们本机显示上开通道。
        await using Fixture fixture = await Fixture.StartAsync();

        Stream? channel = await fixture.Harness.Channels.OpenChannelToClientAsync(
            SshAlgorithmNames.ChannelX11, X11Origin(), fixture.Harness.Token);

        Assert.IsNull(channel, "没请求过就一律拒绝");
    }

    [TestMethod]
    public async Task 过期之后不再接受新的x11通道()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using var xserver = FakeXServer.Start();

        await using X11Forwarder forwarder = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session,
            fixture.Options with
            {
                Display = xserver.Display,
                // 立刻过期。非受信模式的有效期是防「会话开着一整天，
                // 远端随时能开新窗口」。
                Timeout = TimeSpan.FromMilliseconds(1),
            },
            fixture.Harness.Token);

        await Task.Delay(30, fixture.Harness.Token);

        Stream? channel = await fixture.Harness.Channels.OpenChannelToClientAsync(
            SshAlgorithmNames.ChannelX11, X11Origin(), fixture.Harness.Token);

        Assert.IsNull(channel, "过期之后的 x11 通道必须被拒");
        Assert.AreEqual(1, forwarder.RejectedChannels);
    }

    /// <summary>
    /// 同一条连接上两个会话各自请求了 X11 转发：各自的 cookie 都要能用，
    /// 释放其中一个也不能影响另一个。
    /// </summary>
    /// <remarks>
    /// 以前处理器只有一个位置：后请求的会把先请求的挤掉 ——
    /// 先那个会话的 X 程序拿着自己的（正确的）cookie 连过来，被当成「cookie 不对」拒绝。
    /// </remarks>
    [TestMethod]
    public async Task 同一连接上两个会话的X11转发各自可用()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using var xserver = FakeXServer.Start();
        X11ForwardOptions options = fixture.Options with { Display = xserver.Display };

        await using X11Forwarder first = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session, options, fixture.Harness.Token);

        await using SshChannel secondSession =
            await fixture.Harness.Connection.OpenSessionChannelAsync(null, fixture.Harness.Token);
        X11Forwarder second = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, secondSession, options, fixture.Harness.Token);

        byte[] firstCookie = Convert.FromHexString(fixture.Harness.Channels.Observation.X11Requests[0].AuthCookieHex);
        byte[] secondCookie = Convert.FromHexString(fixture.Harness.Channels.Observation.X11Requests[1].AuthCookieHex);
        CollectionAssert.AreNotEqual(firstCookie, secondCookie, "两个会话的假 cookie 必须各不相同");

        // 两个 cookie 都能过。
        await using Stream a = await OpenX11Async(fixture);
        await a.WriteAsync(BuildSetup(true, XAuthority.MitMagicCookie1, firstCookie), fixture.Harness.Token);
        await a.FlushAsync(fixture.Harness.Token);
        _ = await xserver.ReadSetupAsync(fixture.Harness.Token);

        await using Stream b = await OpenX11Async(fixture);
        await b.WriteAsync(BuildSetup(true, XAuthority.MitMagicCookie1, secondCookie), fixture.Harness.Token);
        await b.FlushAsync(fixture.Harness.Token);
        _ = await xserver.ReadSetupAsync(fixture.Harness.Token);

        Assert.AreEqual(1, first.AcceptedChannels);
        Assert.AreEqual(1, second.AcceptedChannels);

        // 释放第二个：第一个照常可用，第二个的 cookie 从此不认。
        await second.DisposeAsync();

        await using Stream c = await OpenX11Async(fixture);
        await c.WriteAsync(BuildSetup(false, XAuthority.MitMagicCookie1, firstCookie), fixture.Harness.Token);
        await c.FlushAsync(fixture.Harness.Token);
        _ = await xserver.ReadSetupAsync(fixture.Harness.Token);
        Assert.AreEqual(2, first.AcceptedChannels);

        await using Stream d = await OpenX11Async(fixture);
        await d.WriteAsync(BuildSetup(false, XAuthority.MitMagicCookie1, secondCookie), fixture.Harness.Token);
        await d.FlushAsync(fixture.Harness.Token);
        await WaitForAsync(() => first.RejectedChannels == 1, fixture.Harness.Token);
        Assert.AreEqual(3, xserver.AcceptedConnections, "已释放那个会话的 cookie 不该再碰到 X server");
    }

    [TestMethod]
    public async Task 单连接模式在本端强制()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using var xserver = FakeXServer.Start();

        await using X11Forwarder forwarder = await X11Forwarder.RequestAsync(
            fixture.Harness.Connection, fixture.Session,
            fixture.Options with { Display = xserver.Display, SingleConnection = true },
            fixture.Harness.Token);

        TestX11Request request = fixture.Harness.Channels.Observation.X11Requests.Single();
        Assert.IsTrue(request.SingleConnection);
        byte[] cookie = Convert.FromHexString(request.AuthCookieHex);

        await using Stream first = await OpenX11Async(fixture);
        await first.WriteAsync(BuildSetup(true, XAuthority.MitMagicCookie1, cookie), fixture.Harness.Token);
        await first.FlushAsync(fixture.Harness.Token);
        _ = await xserver.ReadSetupAsync(fixture.Harness.Token);

        // 服务端（这里的测试桩）不管单连接 —— 本端也必须拒。
        await using Stream second = await OpenX11Async(fixture);
        await second.WriteAsync(BuildSetup(true, XAuthority.MitMagicCookie1, cookie), fixture.Harness.Token);
        await second.FlushAsync(fixture.Harness.Token);

        await WaitForAsync(() => forwarder.RejectedChannels == 1, fixture.Harness.Token);
        Assert.AreEqual(1, forwarder.AcceptedChannels);
        Assert.AreEqual(1, xserver.AcceptedConnections);
    }

    /// <summary>交互 shell 也能开 X11 转发（<c>ssh -X</c> 最常见的用法），时序 pty → x11 → env → shell。</summary>
    [TestMethod]
    public async Task 交互shell请求X11转发且时序正确()
    {
        await using Fixture fixture = await Fixture.StartAsync();

        await using SshShell shell = await fixture.Harness.Connection.OpenShellAsync(
            new SshShellOptions
            {
                X11 = fixture.Options,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["LANG"] = "C.UTF-8" },
            },
            fixture.Harness.Token);

        Assert.IsNotNull(shell.X11);

        string[] order = [.. fixture.Harness.Channels.Observation.Requests
            .Where(static r => r is "pty-req" or "x11-req" or "env" or "shell")];
        Assert.AreSequenceEqual(new[] { "pty-req", "x11-req", "env", "shell" }, order);
    }

    [TestMethod]
    public void 非受信模式的xauth一定写进临时文件()
    {
        IReadOnlyList<string> arguments = X11Forwarder.BuildGenerateArguments(
            "/tmp/velashell-x11-abc/xauthfile", X11Display.Parse(":3")!, 1200);

        // ⚠️ 少了 -f，受限 cookie 会覆盖使用者 .Xauthority 里的完全授权 cookie。
        Assert.AreEqual("-f", arguments[0]);
        Assert.AreEqual("/tmp/velashell-x11-abc/xauthfile", arguments[1]);
        Assert.AreSequenceEqual(
            new[] { "generate", ":3", XAuthority.MitMagicCookie1, "untrusted", "timeout", "1200" }, [.. arguments.Skip(2)]);
    }

    [TestMethod]
    public void xauth的显示名保留主机与套接字路径()
    {
        Assert.AreEqual(":0", X11Display.Parse(":0")!.XAuthName);
        Assert.AreEqual(":0", X11Display.Parse("unix:0")!.XAuthName);
        Assert.AreEqual("remote.example:10", X11Display.Parse("remote.example:10.0")!.XAuthName);
        Assert.AreEqual(
            "/private/tmp/com.apple.launchd.x/org.xquartz:0",
            X11Display.Parse("/private/tmp/com.apple.launchd.x/org.xquartz:0")!.XAuthName);
    }

    // ------------------------------------------------------------ 脚手架

    private static async Task<Stream> OpenX11Async(Fixture fixture)
    {
        Stream? remote = await fixture.Harness.Channels.OpenChannelToClientAsync(
            SshAlgorithmNames.ChannelX11, X11Origin(), fixture.Harness.Token);

        Assert.IsNotNull(remote, "请求过 X11 转发之后，x11 通道应当被接受");
        return remote;
    }

    /// <summary><c>x11</c> 通道的 type-specific 字段：来源地址 + 端口。</summary>
    private static ReadOnlyMemory<byte> X11Origin()
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String("localhost");
        writer.WriteUInt32(0);
        return buffer.WrittenMemory;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, cancellationToken);
        }

        Assert.IsTrue(condition(), "等不到预期状态");
    }

    private static byte[] BuildSetup(bool bigEndian, string protocolName, byte[] cookie)
    {
        byte[] name = Encoding.ASCII.GetBytes(protocolName);
        int paddedName = (name.Length + 3) & ~3;
        int paddedData = (cookie.Length + 3) & ~3;

        byte[] message = new byte[X11SetupMessage.HeaderLength + paddedName + paddedData];
        message[0] = bigEndian ? (byte)'B' : (byte)'l';

        Write(message.AsSpan(2), 11);
        Write(message.AsSpan(4), 0);
        Write(message.AsSpan(6), (ushort)name.Length);
        Write(message.AsSpan(8), (ushort)cookie.Length);

        name.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength));
        cookie.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength + paddedName));
        return message;

        void Write(Span<byte> span, ushort value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            }
        }
    }

    /// <summary>一条已经建好的 session 通道，外加它的测试服务端。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TestSshServerHost harness, SshChannel session, X11ForwardOptions options)
        {
            Harness = harness;
            Session = session;
            Options = options;
        }

        public TestSshServerHost Harness { get; }

        public SshChannel Session { get; }

        public X11ForwardOptions Options { get; }

        public static async Task<Fixture> StartAsync(bool grantX11 = true)
        {
            TestSshServerHost harness = await TestSshServerHost.StartAsync(new TestChannelScript
            {
                GrantX11Forward = grantX11,
            });

            SshChannel session = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);

            // 受信模式：读一份我们自己摆好的 .Xauthority，不跑 xauth。
            // 用例不该依赖本机装没装 X。
            X11ForwardOptions options = new()
            {
                Trusted = true,
                Display = X11Display.Parse(":0.7"),
                XAuthorityPath = WriteXAuthority(),
            };

            return new Fixture(harness, session, options);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Harness.DisposeAsync();
        }

        /// <summary>摆一份只含本机条目的 <c>.Xauthority</c>。</summary>
        private static string WriteXAuthority()
        {
            string path = Path.Combine(Path.GetTempPath(), $"velashell-xauth-{Guid.NewGuid():N}");

            ArrayBufferWriter<byte> buffer = new();
            Span<byte> two = stackalloc byte[2];

            BinaryPrimitives.WriteUInt16BigEndian(two, XAuthority.FamilyWild);
            buffer.Write(two);

            WriteBlock(buffer, []);                       // address（通配族不看）
            // 显示号留空 = 通配：用例里的显示号是动态挑的（避开被占用的端口），
            // 写死 "0" 的话匹配不上，就会退回「找不到条目就用随机数据」那条路 ——
            // 那是对的行为，但会让这条用例验不到 cookie 替换。
            WriteBlock(buffer, []);                       // display number（通配）
            WriteBlock(buffer, Encoding.ASCII.GetBytes(XAuthority.MitMagicCookie1));
            WriteBlock(buffer, _realCookie);

            File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
            return path;

            static void WriteBlock(ArrayBufferWriter<byte> target, byte[] value)
            {
                Span<byte> length = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
                target.Write(length);
                target.Write(value);
            }
        }
    }

    /// <summary>一个只做一件事的假 X server：收下建立报文（可以收多条连接）。</summary>
    private sealed class FakeXServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly System.Threading.Channels.Channel<byte[]> _setups =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly List<Socket> _connections = [];

        private int _accepted;

        private FakeXServer(Socket listener, X11Display display)
        {
            _listener = listener;
            Display = display;
            _ = Task.Run(AcceptAsync);
        }

        public X11Display Display { get; }

        /// <summary>接到过几条连接 —— cookie 不对时它必须是 0。</summary>
        public int AcceptedConnections => Volatile.Read(ref _accepted);

        public static FakeXServer Start()
        {
            // 听回环 TCP，端口按 6000+N 反推一个显示号 ——
            // X11Display 就是这么算端口的。
            for (int number = 40; number < 80; number++)
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, X11Display.TcpPortBase + number));
                    socket.Listen(4);
                    return new FakeXServer(socket, X11Display.Parse($"localhost:{number}")!);
                }
                catch (SocketException)
                {
                    socket.Dispose();   // 端口被占，换一个
                }
            }

            throw new InvalidOperationException("找不到可用的 X11 端口。");
        }

        /// <summary>读下一条连接的建立报文。</summary>
        public async Task<byte[]> ReadSetupAsync(CancellationToken cancellationToken) =>
            await _setups.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    // ⚠️ **接下来的套接字要留着，别读完就关。**
                    // 关掉的话搬运循环那一侧立刻拿到「对端已关闭读取端」，
                    // 而那是一个与被测行为毫无关系的失败。
                    Socket accepted = await _listener.AcceptAsync();
                    lock (_connections)
                    {
                        _connections.Add(accepted);
                    }
                    Interlocked.Increment(ref _accepted);

                    _ = Task.Run(async () =>
                    {
                        byte[] buffer = new byte[256];
                        int read = await accepted.ReceiveAsync(buffer);
                        _setups.Writer.TryWrite(buffer[..read]);
                    });
                }
            }
            catch (Exception)
            {
                // 监听被关了 —— 收场。
            }
        }

        public void Dispose()
        {
            _listener.Dispose();
            lock (_connections)
            {
                foreach (Socket socket in _connections)
                {
                    socket.Dispose();
                }
            }
        }
    }
}
