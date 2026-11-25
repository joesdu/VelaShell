using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>连接建立、原子、属性、错误、扩展 —— 协议的骨架。每个用例两种字节序都跑。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ProtocolTests
{
    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 连接建立回复里有一块24位TrueColor屏幕(bool bigEndian)
    {
        await using X11Server server = new();
        await using XTestClient client = await XTestClient.ConnectAsync(server, bigEndian);

        Assert.AreEqual(1, client.SetupReply[0], "应当是 Success");
        Assert.AreNotEqual(0u, client.ResourceBase);
        Assert.AreEqual(X11Server.RootWindowId, client.RootWindow);
        Assert.AreEqual(8, client.SetupReply[34], "min-keycode");
        Assert.AreEqual(255, client.SetupReply[35], "max-keycode");
        Assert.Contains("VelaShell", Encoding.Latin1.GetString(client.SetupReply, 40, 9));
    }

    [TestMethod]
    public async Task 配了cookie时不带正确cookie的连接被拒()
    {
        byte[] cookie = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = cookie });

        await using XTestClient wrong = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: new byte[16]);
        Assert.AreEqual(0, wrong.SetupReply[0], "错的 cookie 应当 Failed");

        await using XTestClient right = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: cookie);
        Assert.AreEqual(1, right.SetupReply[0]);
    }

    [TestMethod]
    public async Task 配了cookie时本机连接也要带上_调用方验过身份的流不查授权()
    {
        byte[] cookie = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = cookie });

        await using XTestClient local = await XTestClient.ConnectAsync(server, isLocal: true);
        Assert.AreEqual(0, local.SetupReply[0], "环回 TCP 也要 cookie:本机别的进程、别的用户都连得到那个端口");

        await using XTestClient authenticated = await XTestClient.ConnectAsync(server, authenticated: true, authName: "MIT-MAGIC-COOKIE-1", authData: [7]);
        Assert.AreEqual(1, authenticated.SetupReply[0], "SSH 的 x11 通道经进程内连接器接进来:转发层已经核对过假 cookie");
    }

    [TestMethod]
    public async Task 授权规则_按对端的来路逐条判断()
    {
        byte[] cookie = [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9];
        await using X11Server withCookie = new(new X11ServerOptions { AuthorizationCookie = cookie });
        await using X11Server without = new();
        const string mit = "MIT-MAGIC-COOKIE-1";
        X11Server.Peer tcpLocal = new(IsLocal: true, SameHost: false, Uid: null, LocalUser: false, Authenticated: false);
        X11Server.Peer tcpRemote = tcpLocal with { IsLocal = false };
        X11Server.Peer ownerSocket = new(IsLocal: true, SameHost: true, Uid: null, LocalUser: true, Authenticated: false);
        X11Server.Peer otherUser = new(IsLocal: true, SameHost: true, Uid: 4242, LocalUser: false, Authenticated: false);

        Assert.IsNull(withCookie.Authorize(mit, cookie, tcpRemote), "对的 cookie");
        Assert.IsNotNull(withCookie.Authorize(mit, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 8], tcpLocal), "错的 cookie");
        Assert.IsNotNull(withCookie.Authorize("", [], tcpLocal), "配了 cookie:环回 TCP 不带就拒");
        Assert.IsNull(withCookie.Authorize("", [], ownerSocket), "只有属主能连的套接字文件:同一个用户");
        Assert.IsNull(withCookie.Authorize(mit, cookie, otherUser), "别的用户带了对的 cookie 也行");
        Assert.IsNull(withCookie.Authorize("", [], tcpLocal with { Authenticated = true }), "调用方验过身份");

        foreach (X11Server server in new[] { withCookie, without })
        {
            Assert.IsNotNull(server.Authorize(mit, [], ownerSocket), "带了授权名却没有 cookie");
            Assert.IsNotNull(server.Authorize("", cookie, ownerSocket), "带了数据却没有授权名");
            Assert.IsNotNull(server.Authorize("unknown", cookie, ownerSocket), "不支持的授权方式不能回退到套接字属主");
        }
        Assert.IsNull(without.Authorize("", [], tcpLocal), "没配 cookie:本机放行");
        Assert.IsNotNull(without.Authorize("", [], tcpRemote), "没配 cookie:外面的拒");
        Assert.IsNotNull(without.Authorize("", [], otherUser), "抽象命名空间里连进来的别的用户:没配 cookie 也拒");
    }

    [TestMethod]
    public async Task Cookie太短构造时就拒绝_构造之后改调用方的数组不影响授权()
    {
        // 空数组曾让任何带空数据的 MIT-MAGIC-COOKIE-1 都通过(FixedTimeEquals([], []) 为真),比不配还宽,远端也进得来。
        Assert.ThrowsExactly<ArgumentException>(() => new X11Server(new X11ServerOptions { AuthorizationCookie = [] }));
        Assert.ThrowsExactly<ArgumentException>(() => new X11Server(new X11ServerOptions { AuthorizationCookie = new byte[15] }));
        Assert.ThrowsExactly<ArgumentException>(() => new X11Server(new X11ServerOptions { AuthorizationCookie = new byte[257] }));

        byte[] cookie = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
        byte[] original = [.. cookie];
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = cookie });
        Array.Clear(cookie);   // 调用方事后改了自己的数组
        X11Server.Peer remote = new(IsLocal: false, SameHost: false, Uid: null, LocalUser: false, Authenticated: false);
        Assert.IsNotNull(server.Authorize("MIT-MAGIC-COOKIE-1", new byte[16], remote), "全零不是配置时的 cookie");
        Assert.IsNull(server.Authorize("MIT-MAGIC-COOKIE-1", original, remote), "授权按构造时的那份");
    }

    [TestMethod]
    public async Task 主机访问控制报开着_增删主机与关掉访问控制回BadAccess()
    {
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = new byte[16] });
        await using XTestClient c = await XTestClient.ConnectAsync(server, authenticated: true);

        // 原先报 Disabled:xhost 显示「clients can connect from any host」,实际不带 cookie 谁也进不来。
        XMessage hosts = await c.RequestAsync(110, 0);   // ListHosts
        Assert.AreEqual(1, hosts.Detail, "mode = Enabled");
        Assert.AreEqual(0, hosts.U16(8), "主机清单是空的");

        // 原先静默成功:xhost +host 看起来生效了,其实什么也没变。
        byte[] address = [10, 0, 0, 1];
        XMessage insert = await c.RequestAsync(109, 0, b => b.U8(0).U8(0).U16((ushort)address.Length).Bytes(address));   // ChangeHosts(Insert, Internet)
        Assert.IsTrue(insert.IsError);
        Assert.AreEqual(10, insert.Detail, "BadAccess");
        XMessage disable = await c.RequestAsync(111, 0);   // SetAccessControl(Disable)
        Assert.AreEqual(10, disable.Detail, "BadAccess");
        XMessage badMode = await c.RequestAsync(111, 7);
        Assert.AreEqual(2, badMode.Detail, "mode 只有 0、1:BadValue");
        await c.SendAsync(111, 1);   // SetAccessControl(Enable):本来就开着,什么也不做
        await c.SyncAsync();
        Assert.IsFalse((await c.RequestAsync(43, 0)).IsError);
    }

    [TestMethod]
    public void 请求读取器按客户端给的长度取字节时越界一律是BadLength_不会回绕()
    {
        // 原先按 _pos + count 判:count 接近 int.MaxValue 时回绕成负数、检查放行,AsSpan 再抛 —— 客户端看到的是 BadImplementation。
        byte[] request = new byte[16];
        Protocol.XRequestReader r = new(request, bigEndian: false);
        r.Skip(4);
        foreach (int count in (int[])[int.MaxValue, int.MaxValue - 7, 13, -1])
        {
            Protocol.XProtocolError error = Assert.ThrowsExactly<Protocol.XProtocolError>(() => r.Bytes(count));
            Assert.AreEqual(Protocol.XErrorCode.Length, error.Code, $"count = {count}");
        }
        Protocol.XProtocolError slice = Assert.ThrowsExactly<Protocol.XProtocolError>(() => r.Slice(int.MaxValue - 4));
        Assert.AreEqual(Protocol.XErrorCode.Length, slice.Code, "Slice 先核长度再分配");
        Assert.HasCount(8, r.Bytes(8), "合法的照常读");
    }

    [TestMethod]
    public async Task 没配cookie时只接受本机连接()
    {
        await using X11Server server = new();
        await using XTestClient remote = await XTestClient.ConnectAsync(server, isLocal: false);
        Assert.AreEqual(0, remote.SetupReply[0]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternAtom与GetAtomName往返(bool bigEndian)
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server, bigEndian);

        XMessage predefined = await c.RequestAsync(16, 0, b => b.U16(7).U16(0).Bytes(Latin1("WM_NAME")));
        Assert.AreEqual(39u, predefined.U32(8), "WM_NAME 是预定义原子 39");

        XMessage created = await c.RequestAsync(16, 0, b => b.U16(12).U16(0).Bytes(Latin1("_NET_WM_NAME")));
        uint atom = created.U32(8);
        Assert.IsGreaterThan(68u, atom);

        XMessage name = await c.RequestAsync(17, 0, b => b.U32(atom));
        Assert.AreEqual("_NET_WM_NAME", Encoding.Latin1.GetString(name.Bytes, 32, name.U16(8)));

        XMessage onlyIfExists = await c.RequestAsync(16, 1, b => b.U16(9).U16(0).Bytes(Latin1("NO_SUCH_X")));
        Assert.AreEqual(0u, onlyIfExists.U32(8));
    }

    [TestMethod]
    public async Task 不存在的窗口报BadWindow且带序号与操作码()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        XMessage error = await c.RequestAsync(3, 0, b => b.U32(0x12345));   // GetWindowAttributes
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(3, error.Detail, "BadWindow");
        Assert.AreEqual(0x12345u, error.U32(4));
        Assert.AreEqual(3, error.Bytes[10], "major opcode");
    }

    [TestMethod]
    public async Task 不认识的操作码报BadRequest()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage error = await c.RequestAsync(126, 0);
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(1, error.Detail);
    }

    /// <summary>大端客户端写进去的 32 位属性,小端客户端读出来要是同一个数(属性按本机序存放)。</summary>
    [TestMethod]
    public async Task 属性跨字节序读写一致()
    {
        await using X11Server server = new();
        await using XTestClient be = await XTestClient.ConnectAsync(server, bigEndian: true);
        await using XTestClient le = await XTestClient.ConnectAsync(server, bigEndian: false);

        uint root = be.RootWindow;
        await be.SendAsync(18, 0, b => b.U32(root).U32(6).U32(6).U8(32).U8(0).U8(0).U8(0).U32(2).U32(0x11223344).U32(7));
        await be.SyncAsync();

        XMessage reply = await le.RequestAsync(20, 0, b => b.U32(root).U32(6).U32(0).U32(0).U32(10));
        Assert.AreEqual(32, reply.Detail, "format");
        Assert.AreEqual(6u, reply.U32(8), "type = CARDINAL");
        Assert.AreEqual(2u, reply.U32(16), "两个 32 位值");
        Assert.AreEqual(0x11223344u, reply.U32(32));
        Assert.AreEqual(7u, reply.U32(36));
    }

    [TestMethod]
    public async Task GetProperty按偏移分段取并报剩余字节()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint root = c.RootWindow;
        byte[] text = Latin1("hello, world");   // 12 字节
        await c.SendAsync(18, 0, b => b.U32(root).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)text.Length).Bytes(text));

        XMessage part = await c.RequestAsync(20, 0, b => b.U32(root).U32(39).U32(31).U32(1).U32(1));
        Assert.AreEqual(4u, part.U32(16), "取到 4 字节");
        Assert.AreEqual(4u, part.U32(12), "bytes-after = 12 − 4 − 4");
        Assert.AreEqual("o, w", Encoding.Latin1.GetString(part.Bytes, 32, 4), "long-offset 1 = 第 4 字节起");
    }

    [TestMethod]
    public async Task BIG_REQUESTS打开后可以发超过256KB的请求()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        XMessage query = await c.RequestAsync(98, 0, b => b.U16(12).U16(0).Bytes(Latin1("BIG-REQUESTS")));
        Assert.AreEqual(1, query.Bytes[8], "present");
        byte major = query.Bytes[9];
        XMessage enable = await c.RequestAsync(major, 0);
        Assert.IsGreaterThan(65535u, enable.U32(8));

        // 一条 300 KB 的 ChangeProperty,走扩展长度。
        byte[] big = new byte[300 * 1024];
        big[^1] = 0x5A;
        uint root = c.RootWindow;
        await c.SendAsync(18, 0, b => b.U32(root).U32(31).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)big.Length).Bytes(big), bigRequest: true);
        XMessage tail = await c.RequestAsync(20, 0, b => b.U32(root).U32(31).U32(0).U32((uint)(big.Length / 4) - 1).U32(1));
        Assert.AreEqual(0x5A, tail.Bytes[35]);
    }

    [TestMethod]
    public async Task ListExtensions列出已实现的扩展()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage reply = await c.RequestAsync(99, 0);
        string names = Encoding.Latin1.GetString(reply.Bytes, 32, reply.Bytes.Length - 32);
        Assert.Contains("BIG-REQUESTS", names);
        Assert.Contains("XC-MISC", names);
    }

    [TestMethod]
    public async Task 选区没有属主时ConvertSelection直接回None()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint root = c.RootWindow;
        await c.SendAsync(24, 0, b => b.U32(root).U32(1).U32(31).U32(39).U32(0));
        XMessage notify = await c.NextEventAsync(31);
        Assert.AreEqual(0u, notify.U32(20), "property = None");
    }

    [TestMethod]
    public async Task 客户端断开后它的顶层窗口从宿主上消失()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        XTestClient c = await XTestClient.ConnectAsync(server);
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(10).I16(10).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));

        await c.DisposeAsync();
        await host.WaitForAsync(() => !host.Mapped.ContainsKey(id));
    }
}
