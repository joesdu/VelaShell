using System.Security.Cryptography;
using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>
/// SECURITY 扩展(Security Extension Specification 7.1):签发 / 撤销授权,以及非受信客户端在核心请求上的限制(第三章)。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class SecurityTests
{
    private static readonly byte[] MainCookie = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    private static async Task<(byte Major, byte FirstEvent, byte FirstError)?> QueryAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return q.Bytes[8] == 1 ? (q.Bytes[9], q.Bytes[10], q.Bytes[11]) : null;
    }

    private static async Task<List<string>> ListExtensionsAsync(XTestClient c)
    {
        XMessage reply = await c.RequestAsync(99, 0);
        List<string> names = [];
        for (int i = 0, at = 32; i < reply.Bytes[1]; i++)
        {
            int length = reply.Bytes[at];
            names.Add(Encoding.Latin1.GetString(reply.Bytes, at + 1, length));
            at += 1 + length;
        }
        return names;
    }

    /// <summary>
    /// SecurityGenerateAuthorization:MIT-MAGIC-COOKIE-1,按掩码给的值(timeout、trust-level、group、event-mask 的顺序)。
    /// 布局照协议头 securproto.h(也是 xauth 实际发的):两个长度、value-mask,再是各自补齐的名字与数据、值表。
    /// </summary>
    private static Task<XMessage> GenerateAsync(XTestClient c, byte major, uint mask, params uint[] values) =>
        GenerateAsync(c, major, "MIT-MAGIC-COOKIE-1", mask, values);

    private static Task<XMessage> GenerateAsync(XTestClient c, byte major, string protocol, uint mask, params uint[] values)
    {
        byte[] name = Encoding.Latin1.GetBytes(protocol);
        return c.RequestAsync(major, 1, b =>
        {
            b.U16((ushort)name.Length).U16(0).U32(mask).Bytes(name).Pad();
            foreach (uint v in values)
            {
                b.U32(v);
            }
        });
    }

    private static byte[] CookieOf(XMessage reply) => reply.Bytes[32..(32 + reply.U16(12))];

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        return (await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad())).U32(8);
    }

    /// <summary>受信客户端建一个映射着、选了 KeyPress 的顶层窗口,带一个 STRING 属性。</summary>
    private static async Task<uint> TrustedWindowAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(10).I16(20).U16(50).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x1));   // event-mask:KeyPress
        await c.SendAsync(18, 0, b => b.U32(id).U32(39).U32(31).U8(8).Bytes(new byte[3]).U32(6).Bytes("secret"u8.ToArray()).Pad());   // WM_NAME
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task SECURITY扩展对受信客户端可见_报1点0_对非受信客户端连同不安全的扩展一起看不见()
    {
        await using X11Server server = new();
        await using XTestClient trusted = await XTestClient.ConnectAsync(server);
        await using XTestClient untrusted = await XTestClient.ConnectAsync(server, untrusted: true);

        (byte major, _, _) = (await QueryAsync(trusted, "SECURITY")) ?? throw new AssertFailedException("SECURITY 应当存在");
        XMessage version = await trusted.RequestAsync(major, 0, b => b.U16(1).U16(0));
        Assert.AreEqual((ushort)1, version.U16(8));
        Assert.AreEqual((ushort)0, version.U16(10));

        foreach (string insecure in (string[])["SECURITY", "XTEST", "Composite", "X-Resource", "DPMS", "MIT-SCREEN-SAVER"])
        {
            Assert.IsNull(await QueryAsync(untrusted, insecure), $"{insecure} 对非受信客户端不可见");
        }
        List<string> names = await ListExtensionsAsync(untrusted);
        CollectionAssert.DoesNotContain(names, "XTEST");
        CollectionAssert.Contains(names, "RENDER");
        CollectionAssert.Contains(names, "XInputExtension");
        CollectionAssert.Contains(await ListExtensionsAsync(trusted), "XTEST");

        // 猜到了主操作码也用不了:Request 错误(规范「Extension Security」)。
        XMessage guessed = await untrusted.RequestAsync(major, 0, b => b.U16(1).U16(0));
        Assert.IsTrue(guessed.IsError);
        Assert.AreEqual(1, guessed.Detail, "BadRequest");
    }

    [TestMethod]
    public async Task 签出的cookie连得进来_信任级别照授权_撤销时断开用它连着的客户端并通知签它的客户端()
    {
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = MainCookie });
        await using XTestClient admin = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: MainCookie);
        (byte major, byte firstEvent, _) = (await QueryAsync(admin, "SECURITY"))!.Value;

        // 默认非受信、timeout 60;选了 AuthorizationRevoked。
        XMessage generated = await GenerateAsync(admin, major, 0x8, 1);
        Assert.IsTrue(generated.IsReply);
        uint authId = generated.U32(8);
        byte[] cookie = CookieOf(generated);
        Assert.AreNotEqual(0u, authId);
        Assert.HasCount(16, cookie);

        await using XTestClient guest = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: cookie);
        Assert.AreEqual(1, guest.SetupReply[0], "签出的 cookie 连得进来");
        XClientInfo info = (await server.GetClientsAsync()).Single(c => c.Id == guest.ResourceBase >> 21);
        Assert.AreEqual(XClientTrust.Untrusted, info.Trust);
        Assert.IsNull(await QueryAsync(guest, "XTEST"), "非受信:看不到 XTEST");

        await using XTestClient forged = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: RandomNumberGenerator.GetBytes(16));
        Assert.AreEqual(0, forged.SetupReply[0], "对不上的 cookie 照旧拒");

        // 受信级别的授权。
        byte[] trustedCookie = CookieOf(await GenerateAsync(admin, major, 0x2, 0));
        await using (XTestClient trustedGuest = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: trustedCookie))
        {
            Assert.IsNotNull(await QueryAsync(trustedGuest, "XTEST"), "受信级别的授权连进来的是受信客户端");
        }

        await admin.SendAsync(major, 2, b => b.U32(authId));   // SecurityRevokeAuthorization
        XMessage revoked = await admin.NextEventAsync(firstEvent);
        Assert.AreEqual(authId, revoked.U32(4));
        await guest.ServerTask.WaitAsync(TimeSpan.FromSeconds(5));   // 用它连着的客户端被断开
        await using XTestClient late = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: cookie);
        Assert.AreEqual(0, late.SetupReply[0], "撤销之后再也连不进来");
    }

    [TestMethod]
    public async Task 签发与撤销的错误_认不出的授权方式_group不为None_未知的授权编号()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte major, _, byte firstError) = (await QueryAsync(c, "SECURITY"))!.Value;

        XMessage protocol = await GenerateAsync(c, major, "XDM-AUTHORIZATION-1", 0);
        Assert.IsTrue(protocol.IsError);
        Assert.AreEqual(firstError + 1, protocol.Detail, "BadAuthorizationProtocol");

        XMessage group = await GenerateAsync(c, major, 0x4, 0x12345);
        Assert.IsTrue(group.IsError);
        Assert.AreEqual(2, group.Detail, "没有 Application Group:group 只能是 None,否则 BadValue");

        XMessage level = await GenerateAsync(c, major, 0x2, 2);
        Assert.AreEqual(2, level.Detail, "trust-level 只有 0 / 1");

        XMessage unknown = await c.RequestAsync(major, 2, b => b.U32(0xDEAD));
        Assert.IsTrue(unknown.IsError);
        Assert.AreEqual(firstError, unknown.Detail, "BadAuthorization");
        Assert.AreEqual(0xDEADu, unknown.U32(4));
    }

    [TestMethod]
    public async Task 授权没有连接满timeout秒就作废_有连接时不作废()
    {
        await using X11Server server = new(new X11ServerOptions { AuthorizationCookie = MainCookie });
        await using XTestClient admin = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: MainCookie);
        (byte major, _, _) = (await QueryAsync(admin, "SECURITY"))!.Value;

        byte[] idle = CookieOf(await GenerateAsync(admin, major, 0x1, 1));
        byte[] used = CookieOf(await GenerateAsync(admin, major, 0x1, 1));
        await using XTestClient holder = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: used);
        Assert.AreEqual(1, holder.SetupReply[0]);
        await Task.Delay(1800);

        await using XTestClient tooLate = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: idle);
        Assert.AreEqual(0, tooLate.SetupReply[0], "一秒没人用:作废");
        await using XTestClient second = await XTestClient.ConnectAsync(server, authName: "MIT-MAGIC-COOKIE-1", authData: used);
        Assert.AreEqual(1, second.SetupReply[0], "一直有连接:不作废");
    }

    [TestMethod]
    public async Task 非受信客户端只能指名非受信客户端的资源_根窗口只在规范列出的请求里可用()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient trusted = await XTestClient.ConnectAsync(server);
        await using XTestClient untrusted = await XTestClient.ConnectAsync(server, untrusted: true);
        uint secret = await TrustedWindowAsync(trusted, host);

        XMessage property = await untrusted.RequestAsync(20, 0, b => b.U32(secret).U32(39).U32(0).U32(0).U32(100));
        Assert.IsTrue(property.IsError);
        Assert.AreEqual(3, property.Detail, "受信客户端的窗口:BadWindow,就像它不存在");
        XMessage image = await untrusted.RequestAsync(73, 2, b => b.U32(secret).I16(0).I16(0).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.AreEqual(9, image.Detail, "受信客户端的窗口:BadDrawable");
        XMessage rootImage = await untrusted.RequestAsync(73, 2, b => b.U32(untrusted.RootWindow).I16(0).I16(0).U16(1).U16(1).U32(0xFFFFFFFF));
        Assert.AreEqual(9, rootImage.Detail, "截不了整个屏幕(GetImage 不在根窗口的例外里)");
        await untrusted.SendAsync(25, 0, b => b.U32(secret).U32(0).U8(33).U8(32).U16(0).U32(secret).U32(0).Bytes(new byte[20]));   // SendEvent
        Assert.AreEqual(3, (await untrusted.NextAsync(m => m.IsError)).Detail, "往受信客户端的窗口发事件:BadWindow");

        // 例外:QueryTree / GetGeometry / TranslateCoordinates 不受限;根窗口能当 CreateGC 的 drawable、CreateWindow 的父。
        Assert.IsTrue((await untrusted.RequestAsync(14, 0, b => b.U32(secret))).IsReply, "GetGeometry");
        Assert.IsTrue((await untrusted.RequestAsync(15, 0, b => b.U32(untrusted.RootWindow))).IsReply, "QueryTree");
        uint own = untrusted.NewId(), gc = untrusted.NewId();
        await untrusted.SendAsync(55, 0, b => b.U32(gc).U32(untrusted.RootWindow).U32(0));
        await untrusted.SendAsync(1, 0, b => b.U32(own).U32(untrusted.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        Assert.IsTrue((await untrusted.RequestAsync(17, 0, b => b.U32(39))).IsReply);   // GetAtomName:同步一下,前面的不该出错
        Assert.IsTrue((await untrusted.RequestAsync(3, 0, b => b.U32(own))).IsReply, "自己的窗口照常");

        // 根窗口上只能选 StructureNotify / PropertyChange(规范例外 3.g)。
        await untrusted.SendAsync(2, 0, b => b.U32(untrusted.RootWindow).U32(0x800).U32(0x400000));
        await untrusted.SendAsync(2, 0, b => b.U32(untrusted.RootWindow).U32(0x800).U32(0x1));
        XMessage keys = await untrusted.NextAsync(m => m.IsError);
        Assert.AreEqual(3, keys.Detail, "在根窗口上选 KeyPress:BadWindow");

        // 受信客户端看得见非受信客户端的窗口(限制只是单向的)。
        Assert.IsTrue((await trusted.RequestAsync(3, 0, b => b.U32(own))).IsReply);
    }

    [TestMethod]
    public async Task 根窗口上的CUT_BUFFER对非受信客户端隐藏_它改根窗口的属性被忽略()
    {
        await using X11Server server = new();
        await using XTestClient trusted = await XTestClient.ConnectAsync(server);
        await using XTestClient untrusted = await XTestClient.ConnectAsync(server, untrusted: true);
        uint root = trusted.RootWindow;
        await trusted.SendAsync(18, 0, b => b.U32(root).U32(9).U32(31).U8(8).Bytes(new byte[3]).U32(8).Bytes("password"u8.ToArray()));   // CUT_BUFFER0

        XMessage hidden = await untrusted.RequestAsync(20, 0, b => b.U32(root).U32(9).U32(0).U32(0).U32(100));
        Assert.IsTrue(hidden.IsReply);
        Assert.AreEqual(0u, hidden.U32(8), "回「不存在」:类型 None");
        Assert.AreEqual(8u, (await trusted.RequestAsync(20, 0, b => b.U32(root).U32(9).U32(0).U32(0).U32(100))).U32(16), "受信客户端照常读到");
        XMessage list = await untrusted.RequestAsync(21, 0, b => b.U32(root));
        int count = list.U16(8);
        List<uint> atoms = [.. Enumerable.Range(0, count).Select(i => list.U32(32 + (4 * i)))];
        CollectionAssert.DoesNotContain(atoms, 9u, "ListProperties 不列");
        CollectionAssert.Contains(atoms, await InternAsync(trusted, "RESOURCE_MANAGER"), "共用的属性照常看得见");

        // 改 / 删根窗口上的属性:当作 NoOperation,不报错。
        uint mine = await InternAsync(untrusted, "UNTRUSTED_WAS_HERE");
        await untrusted.SendAsync(18, 0, b => b.U32(root).U32(mine).U32(31).U8(8).Bytes(new byte[3]).U32(4).Bytes("evil"u8.ToArray()));
        await untrusted.SendAsync(19, 0, b => b.U32(root).U32(9));
        Assert.IsTrue((await untrusted.RequestAsync(17, 0, b => b.U32(39))).IsReply, "没有错误");
        Assert.AreEqual(0u, (await trusted.RequestAsync(20, 0, b => b.U32(root).U32(mine).U32(0).U32(0).U32(100))).U32(8), "没写上");
        Assert.AreEqual(8u, (await trusted.RequestAsync(20, 0, b => b.U32(root).U32(9).U32(0).U32(0).U32(100))).U32(16), "没删掉");
    }

    [TestMethod]
    public async Task 键盘归受信客户端时_非受信客户端读不到按键_抓不了键盘_改不了焦点与键位表()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient trusted = await XTestClient.ConnectAsync(server);
        await using XTestClient untrusted = await XTestClient.ConnectAsync(server, untrusted: true);
        uint focus = await TrustedWindowAsync(trusted, host);
        await trusted.SendAsync(42, 1, b => b.U32(focus).U32(0));   // SetInputFocus(PointerRoot 回退)
        uint own = untrusted.NewId();
        await untrusted.SendAsync(1, 0, b => b.U32(own).U32(untrusted.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await untrusted.SendAsync(8, 0, b => b.U32(own));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(own));

        const byte a = 38;
        byte xtest = (await QueryAsync(trusted, "XTEST"))!.Value.Major;
        await trusted.SendAsync(xtest, 2, b => b.U8(2).U8(a).U16(0).U32(0).U32(0).U32(0).U32(0).I16(0).I16(0).U32(0).U32(0));   // 按下 a
        XMessage seen = await trusted.RequestAsync(44, 0);
        Assert.AreNotEqual(0, seen.Bytes[8 + (a / 8)] & (1 << (a % 8)), "受信客户端看得见按着的键");
        XMessage blind = await untrusted.RequestAsync(44, 0);
        Assert.IsTrue(blind.Bytes[8..40].All(v => v == 0), "非受信客户端的 QueryKeymap 全是 0");

        XMessage grab = await untrusted.RequestAsync(31, 1, b => b.U32(own).U32(0).U8(1).U8(1).Bytes(new byte[2]));
        Assert.AreEqual(1, grab.Detail, "GrabKeyboard:AlreadyGrabbed");
        await untrusted.SendAsync(42, 1, b => b.U32(own).U32(0));   // SetInputFocus:什么也不做
        Assert.AreEqual(focus, (await untrusted.RequestAsync(43, 0)).U32(8), "焦点还在受信客户端的窗口上");
        XMessage mapping = await untrusted.RequestAsync(100, 1, b => b.U8(a).U8(1).Bytes(new byte[2]).U32(0x61));   // ChangeKeyboardMapping
        Assert.IsTrue(mapping.IsError);
        Assert.AreEqual(10, mapping.Detail, "BadAccess");

        // 焦点给了非受信客户端的窗口:键盘归它,它看得见按着的键。
        await trusted.SendAsync(42, 1, b => b.U32(own).U32(0));
        Assert.AreEqual(own, (await trusted.RequestAsync(43, 0)).U32(8), "受信客户端把焦点给了它");   // 两条连接之间没有先后:等这条做完
        await untrusted.SendAsync(2, 0, b => b.U32(own).U32(0x800).U32(0x1));   // 选 KeyPress
        XMessage visible = await untrusted.RequestAsync(44, 0);
        Assert.AreNotEqual(0, visible.Bytes[8 + (a / 8)] & (1 << (a % 8)));
        await trusted.SendAsync(xtest, 2, b => b.U8(3).U8(a).U16(0).U32(0).U32(0).U32(0).U32(0).I16(0).I16(0).U32(0).U32(0));
    }

    [TestMethod]
    public async Task 非受信客户端往根窗口只能发ICCCM规定的那几种事件_在根窗口上选不了XI2的输入事件()
    {
        await using X11Server server = new();
        await using XTestClient untrusted = await XTestClient.ConnectAsync(server, untrusted: true);
        uint root = untrusted.RootWindow;
        byte[] clientMessage = [33, 32, 0, 0, .. BitConverter.GetBytes(root), .. new byte[24]];

        // ClientMessage、SubstructureRedirect | SubstructureNotify、不传播:发给窗口管理器的请求,照收(规范例外 3.f)。
        await untrusted.SendAsync(25, 0, b => b.U32(root).U32(0x180000).Bytes(clientMessage));
        Assert.IsTrue((await untrusted.RequestAsync(17, 0, b => b.U32(39))).IsReply);
        // 传播的、或者掩码不对的:BadWindow。
        XMessage propagated = await untrusted.RequestAsync(25, 1, b => b.U32(root).U32(0x180000).Bytes(clientMessage));
        Assert.AreEqual(3, propagated.Detail);
        XMessage keyMask = await untrusted.RequestAsync(25, 0, b => b.U32(root).U32(0x1).Bytes(clientMessage));
        Assert.AreEqual(3, keyMask.Detail);

        // XISelectEvents(根窗口,XIAllDevices,KeyPress | HierarchyChanged):只留下 HierarchyChanged。
        byte xi = (await QueryAsync(untrusted, "XInputExtension"))!.Value.Major;
        uint mask = (1u << 2) | (1u << 11);
        await untrusted.SendAsync(xi, 46, b => b.U32(root).U16(1).U16(0).U16(0).U16(1).U32(mask));
        XMessage selected = await untrusted.RequestAsync(xi, 60, b => b.U32(root));
        Assert.AreEqual((ushort)1, selected.U16(8));
        Assert.AreEqual(1u << 11, selected.U32(36), "键盘事件被拿掉了");
    }

    [TestMethod]
    public async Task 非受信客户端要受信客户端占着的选区回None_GrabServer被忽略_访问控制回Access()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient trusted = await XTestClient.ConnectAsync(server);
        await using XTestClient untrusted = await XTestClient.ConnectAsync(server, untrusted: true);
        uint owner = await TrustedWindowAsync(trusted, host);
        uint selection = await InternAsync(trusted, "MY_SELECTION");
        await trusted.SendAsync(22, 0, b => b.U32(owner).U32(selection).U32(0));   // SetSelectionOwner

        uint requestor = untrusted.NewId();
        await untrusted.SendAsync(1, 0, b => b.U32(requestor).U32(untrusted.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        uint target = await InternAsync(untrusted, "STRING");
        await untrusted.SendAsync(24, 0, b => b.U32(requestor).U32(selection).U32(target).U32(target).U32(0));
        XMessage notify = await untrusted.NextEventAsync(31);
        Assert.AreEqual(0u, notify.U32(20), "SelectionNotify 的 property 为 None");

        // GrabServer:忽略,受信客户端照常被服务。
        await untrusted.SendAsync(36, 0);
        Assert.IsTrue((await trusted.RequestAsync(17, 0, b => b.U32(39))).IsReply);

        XMessage hosts = await untrusted.RequestAsync(110, 0);   // ListHosts
        Assert.AreEqual(10, hosts.Detail, "BadAccess");
    }
}
