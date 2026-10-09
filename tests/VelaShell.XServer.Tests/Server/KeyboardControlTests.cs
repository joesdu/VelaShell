using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>键盘与指针:自动重复(核心、XKB、XI2 三种看法)、ChangeKeyboardControl / GetKeyboardControl、响铃音量、按钮与修饰键映射、焦点的退回。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class KeyboardControlTests
{
    private const byte KeyPress = 2, KeyRelease = 3, GenericEvent = 35;
    private const ushort UseCoreKbd = 0x100;

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host, uint eventMask)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0)
            .U32(0x800).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static async Task<byte> ExtensionAsync(XTestClient c, string name)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(Encoding.Latin1.GetBytes(name)).Pad());
        Assert.AreEqual(1, q.Bytes[8], name);
        return q.Bytes[9];
    }

    /// <summary>收齐到目前为止的核心按键事件:(事件码, 键码)。</summary>
    private static async Task<List<(byte Code, byte Key)>> KeyEventsAsync(XTestClient c)
    {
        await c.SyncAsync();
        List<(byte, byte)> events = [];
        while (true)
        {
            try
            {
                XMessage m = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode is KeyPress or KeyRelease, timeoutMs: 80);
                events.Add((m.EventCode, m.Detail));
            }
            catch (OperationCanceledException)
            {
                return events;
            }
        }
    }

    /// <summary>ChangeKeyboardControl:按位给的值,每个 4 字节。等它执行完再返回(之后宿主注入的按键按新设置处理)。</summary>
    private static async Task ChangeKeyboardControlAsync(XTestClient c, uint mask, params uint[] values)
    {
        await c.SendAsync(102, 0, b =>
        {
            b.U32(mask);
            foreach (uint v in values)
            {
                b.U32(v);
            }
        });
        await c.SyncAsync();
    }

    [TestMethod]
    public async Task 自动重复_核心客户端收到成对的松开与按下_开了DetectableAutoRepeat的只收按下_XI2的按下带KeyRepeat()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient plain = await XTestClient.ConnectAsync(server);
        await using XTestClient detectable = await XTestClient.ConnectAsync(server);
        await using XTestClient xi2 = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(plain, host, 0x1 | 0x2);                           // KeyPress | KeyRelease
        await detectable.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x1 | 0x2));
        byte xkb = await ExtensionAsync(detectable, "XKEYBOARD");
        await detectable.RequestAsync(xkb, 0, b => b.U16(1).U16(0));
        XMessage flags = await detectable.RequestAsync(xkb, 21, b => b.U16(UseCoreKbd).U16(0).U32(1).U32(1).U32(0).U32(0).U32(0));
        Assert.AreEqual(1u, flags.U32(12), "PerClientFlags:DetectableAutoRepeat 开了");
        byte xi = await ExtensionAsync(xi2, "XInputExtension");
        await xi2.RequestAsync(xi, 47, b => b.U16(2).U16(2));
        await xi2.SendAsync(xi, 46, b => b.U32(top).U16(1).U16(0).U16(1).U16(1).U8((1 << 2) | (1 << 3)).U8(0).U8(0).U8(0));
        await xi2.SyncAsync();
        await plain.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);

        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: true, repeat: true);
        server.InjectKey(XKeycodes.A, pressed: true, repeat: true);
        server.InjectKey(XKeycodes.A, pressed: false);

        CollectionAssert.AreEqual(
            new List<(byte, byte)> { (KeyPress, 38), (KeyRelease, 38), (KeyPress, 38), (KeyRelease, 38), (KeyPress, 38), (KeyRelease, 38) },
            await KeyEventsAsync(plain), "协议:自动重复的键交替产生 KeyPress 与 KeyRelease");
        CollectionAssert.AreEqual(
            new List<(byte, byte)> { (KeyPress, 38), (KeyPress, 38), (KeyPress, 38), (KeyRelease, 38) },
            await KeyEventsAsync(detectable), "XKB Detectable Autorepeat:只在真的松开时收到 KeyRelease");

        List<(int Type, uint Flags)> xiEvents = [];
        for (int i = 0; i < 4; i++)
        {
            XMessage e = await xi2.NextAsync(m => m.EventCode == GenericEvent && m.Bytes[1] == xi && m.U16(8) is 2 or 3);
            xiEvents.Add((e.U16(8), e.U32(56)));
        }
        CollectionAssert.AreEqual(new List<(int, uint)> { (2, 0), (2, 1u << 16), (2, 1u << 16), (3, 0) }, xiEvents,
            "XI2:重复的 KeyPress 带 KeyRepeat,中间没有 KeyRelease");
    }

    [TestMethod]
    public async Task 修饰键不重复_xset_r_off与逐键关掉的键不重复_XKB的控制看得到同一份设置()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0x1 | 0x2);
        await c.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);

        server.InjectKey(XKeycodes.ShiftLeft, pressed: true);
        server.InjectKey(XKeycodes.ShiftLeft, pressed: true, repeat: true);   // Windows 上按住 Shift 一直有 KeyDown
        server.InjectKey(XKeycodes.ShiftLeft, pressed: false);
        Assert.HasCount(2, await KeyEventsAsync(c), "修饰键不重复:只有按下与松开");

        await ChangeKeyboardControlAsync(c, 0x80, 0);   // auto-repeat-mode Off(xset r off)
        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: true, repeat: true);
        server.InjectKey(XKeycodes.A, pressed: false);
        Assert.HasCount(2, await KeyEventsAsync(c), "全局关了:重复丢掉");
        XMessage control = await c.RequestAsync(103, 0);
        Assert.AreEqual(0, control.Bytes[1], "global-auto-repeat = Off");

        await ChangeKeyboardControlAsync(c, 0x80, 1);                       // 全局开回来
        await ChangeKeyboardControlAsync(c, 0x40 | 0x80, XKeycodes.A, 0);   // 只关 a
        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: true, repeat: true);
        server.InjectKey(XKeycodes.A, pressed: false);
        server.InjectKey(XKeycodes.S, pressed: true);
        server.InjectKey(XKeycodes.S, pressed: true, repeat: true);
        server.InjectKey(XKeycodes.S, pressed: false);
        List<(byte Code, byte Key)> events = await KeyEventsAsync(c);
        Assert.AreEqual(2, events.Count(e => e.Key == XKeycodes.A), "a 不重复");
        Assert.AreEqual(4, events.Count(e => e.Key == XKeycodes.S), "s 照常重复");

        control = await c.RequestAsync(103, 0);
        Assert.AreEqual(1, control.Bytes[1]);
        byte[] perKey = control.Bytes[20..52];
        Assert.AreEqual(0, perKey[XKeycodes.A >> 3] & (1 << (XKeycodes.A & 7)), "a 的位清了");
        Assert.AreNotEqual(0, perKey[XKeycodes.S >> 3] & (1 << (XKeycodes.S & 7)));
        Assert.AreEqual(0, perKey[XKeycodes.ShiftLeft >> 3] & (1 << (XKeycodes.ShiftLeft & 7)), "修饰键默认不重复");

        // XKB 的 RepeatKeys / PerKeyRepeat 与核心是同一份:GetControls 看得到,SetControls 改了核心也看得到。
        byte xkb = await ExtensionAsync(c, "XKEYBOARD");
        await c.RequestAsync(xkb, 0, b => b.U16(1).U16(0));
        XMessage controls = await c.RequestAsync(xkb, 6, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(1u, controls.U32(56) & 1, "enabledControls:RepeatKeys");
        Assert.AreEqual(0, controls.Bytes[60 + (XKeycodes.A >> 3)] & (1 << (XKeycodes.A & 7)), "perKeyRepeat 里 a 也关着");
        // SetControls:changeControls = ControlsEnabled | RepeatKeys,关掉 RepeatKeys,重复间隔 30 / 25 ms。
        await c.SendAsync(xkb, 7, b => b.U16(UseCoreKbd).Bytes(new byte[18]).U32(1).U32(0).U32(0x80000000 | 1)
            .U16(300).U16(25).Bytes(new byte[28]).Bytes(new byte[32]));
        await c.SyncAsync();
        control = await c.RequestAsync(103, 0);
        Assert.AreEqual(0, control.Bytes[1], "XKB 关了 RepeatKeys:核心的全局自动重复也是关");
        controls = await c.RequestAsync(xkb, 6, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(300, controls.U16(20), "repeatDelay");
        Assert.AreEqual(25, controls.U16(22), "repeatInterval");

        XMessage noMode = await c.RequestAsync(102, 0, b => b.U32(0x40).U32(XKeycodes.A));
        Assert.IsTrue(noMode.IsError);
        Assert.AreEqual(8, noMode.Bytes[1], "只给 key 不给 auto-repeat-mode 是 BadMatch");
        XMessage zeroDelay = await c.RequestAsync(xkb, 7, b => b.U16(UseCoreKbd).Bytes(new byte[18]).U32(0).U32(0).U32(1)
            .U16(0).U16(25).Bytes(new byte[28]).Bytes(new byte[32]));
        Assert.IsTrue(zeroDelay.IsError, "RepeatKeys 的延迟为 0 是 BadValue");
    }

    [TestMethod]
    public async Task 左手的按钮映射生效_发MappingNotify_按钮数与XI一致_要改的按钮按着时Busy()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0x4 | 0x8);   // ButtonPress | ButtonRelease

        XMessage map = await c.RequestAsync(117, 0);
        Assert.AreEqual(9, map.Bytes[1], "GetPointerMapping 与 XI 一样报 9 个按钮");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, map.Bytes[32..41]);

        // xmodmap -e "pointer = 3 2 1":xmodmap 自己把后面的按钮补成恒等,长度与 GetPointerMapping 一致。
        XMessage set = await c.RequestAsync(116, 9, b => b.Bytes([3, 2, 1, 4, 5, 6, 7, 8, 9]));
        Assert.AreEqual(0, set.Bytes[1], "Success");
        XMessage notify = await c.NextEventAsync(34);
        Assert.AreEqual(2, notify.Bytes[4], "MappingNotify request = Pointer");

        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);
        XMessage press = await c.NextEventAsync(4);
        Assert.AreEqual(3, press.Detail, "物理的左键报成按钮 3");

        XMessage busy = await c.RequestAsync(116, 9, b => b.Bytes([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.AreEqual(1, busy.Bytes[1], "物理左键正按着,要改它的映射:Busy");
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: false);
        XMessage release = await c.NextEventAsync(5);
        Assert.AreEqual(3, release.Detail, "松开按同一份映射");
        Assert.AreEqual(0x400, release.U16(28) & 0x1F00, "松开之前的 state 里按着的是 Button3");

        XMessage wrongLength = await c.RequestAsync(116, 5, b => b.Bytes([3, 2, 1, 4, 5]));
        Assert.IsTrue(wrongLength.IsError, "长度与 GetPointerMapping 不一致是 BadValue");
        XMessage duplicate = await c.RequestAsync(116, 9, b => b.Bytes([1, 1, 3, 4, 5, 6, 7, 8, 9]));
        Assert.IsTrue(duplicate.IsError, "非零元素重复是 BadValue");
    }

    /// <summary>RevertToParent 一路退到根:焦点是根(以 PointerRoot 表示),按键照样送到指针所在的窗口 —— 原先给的是 None,键盘输入一直被丢掉。</summary>
    [TestMethod]
    public async Task RevertToParent一路退到根时焦点是根_按键照样送到指针所在的窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0x1);
        uint other = await MapTopAsync(c, host, 0x1);
        await c.SendAsync(42, 2, b => b.U32(top).U32(0));   // SetInputFocus(top, RevertToParent)
        await c.SendAsync(10, 0, b => b.U32(top));          // UnmapWindow:焦点按 revert-to 退回
        XMessage focus = await c.RequestAsync(43, 0);
        Assert.AreEqual(1u, focus.U32(8), "退到根(PointerRoot)而不是 None");
        Assert.AreEqual(0, focus.Bytes[1], "新的 revert-to 是 None");

        server.InjectPointerMotion(host.Mapped[other], 5, 5);
        server.InjectKey(XKeycodes.A, pressed: true);
        XMessage key = await c.NextEventAsync(KeyPress);
        Assert.AreEqual(other, key.U32(12), "按键送到指针所在的窗口");
    }

    /// <summary>
    /// 宿主再推一次键位表(激活 X 窗口、切了布局):只改与上次不同的键,修饰键表只挪右 Alt —— 用户用 xmodmap 交换的 Caps / Ctrl 保留。
    /// 原先每次整张覆盖修饰键表、连同固定键的键值一起还原。
    /// </summary>
    [TestMethod]
    public async Task 宿主重推键位表保留xmodmap交换的Caps与Ctrl_只挪右Alt()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        static XKeymap Us(bool altGr) => new XKeymap("us") { AltGr = altGr }
            .Map(XKeycodes.ControlLeft, 0xffe3, 0xffe3).Map(XKeycodes.A, 'a', 'A');
        server.SetKeymap(Us(altGr: false));
        await c.SyncAsync();

        // xmodmap:左 Ctrl 当 Caps Lock、Caps Lock 当 Ctrl。
        await c.SendAsync(100, 1, b => b.U8(XKeycodes.ControlLeft).U8(1).U16(0).U32(0xffe5));
        await c.SendAsync(100, 1, b => b.U8(XKeycodes.CapsLock).U8(1).U16(0).U32(0xffe3));
        XMessage current = await c.RequestAsync(119, 0);
        int per = current.Bytes[1];
        byte[] swapped = current.Bytes[32..(32 + (8 * per))];
        Array.Clear(swapped, per, 2 * per);
        swapped[per] = XKeycodes.ControlLeft;        // Lock
        swapped[2 * per] = XKeycodes.CapsLock;       // Control
        Assert.AreEqual(0, (await c.RequestAsync(118, (byte)per, b => b.Bytes(swapped))).Bytes[1]);

        server.SetKeymap(Us(altGr: false));          // 宿主再推一次(用户激活了 X 窗口)
        await c.SyncAsync();
        XMessage ctrl = await c.RequestAsync(101, 0, b => b.U8(XKeycodes.ControlLeft).U8(1).U16(0));
        Assert.AreEqual(0xffe5u, ctrl.U32(32), "左 Ctrl 仍是 Caps_Lock");
        XMessage after = await c.RequestAsync(119, 0);
        CollectionAssert.AreEqual(swapped, after.Bytes[32..(32 + (8 * after.Bytes[1]))], "修饰键表原样");

        server.SetKeymap(Us(altGr: true));           // 换成有 AltGr 的布局:右 Alt 从 Mod1 挪到 Mod5,别的不动
        await c.SyncAsync();
        XMessage altGr = await c.RequestAsync(119, 0);
        int per2 = altGr.Bytes[1];
        byte[] map = altGr.Bytes[32..(32 + (8 * per2))];
        Assert.DoesNotContain(XKeycodes.AltRight, map[(3 * per2)..(4 * per2)], "Mod1 里没有右 Alt 了");
        Assert.Contains(XKeycodes.AltRight, map[(7 * per2)..(8 * per2)], "Mod5 里有右 Alt");
        Assert.Contains(XKeycodes.ControlLeft, map[per2..(2 * per2)], "Lock 仍是左 Ctrl");
    }

    [TestMethod]
    public async Task SetModifierMapping校验键码_修饰键按着时Busy_XI的GetDeviceKeyMapping不回绕()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage current = await c.RequestAsync(119, 0);
        int per = current.Bytes[1];
        byte[] map = current.Bytes[32..(32 + (8 * per))];

        byte[] bad = [.. map];
        bad[per * 5] = 3;   // Mod3 放一个不存在的键码
        XMessage value = await c.RequestAsync(118, (byte)per, b => b.Bytes(bad));
        Assert.IsTrue(value.IsError, "键码不在 8–255:BadValue");

        // 交换 Caps Lock 与左 Ctrl(xmodmap 常见的设置),左 Ctrl 正按着:Busy,什么都不改。
        byte[] swapped = [.. map];
        swapped[per * 1] = XKeycodes.ControlLeft;
        swapped[per * 2] = XKeycodes.CapsLock;
        server.InjectKey(XKeycodes.ControlLeft, pressed: true);
        await c.SyncAsync();
        XMessage busy = await c.RequestAsync(118, (byte)per, b => b.Bytes(swapped));
        Assert.AreEqual(1, busy.Bytes[1], "Busy");
        CollectionAssert.AreEqual(map, (await c.RequestAsync(119, 0)).Bytes[32..(32 + (8 * per))], "修饰键表没变");
        server.InjectKey(XKeycodes.ControlLeft, pressed: false);
        await c.SyncAsync();
        XMessage ok = await c.RequestAsync(118, (byte)per, b => b.Bytes(swapped));
        Assert.AreEqual(0, ok.Bytes[1], "松开之后照常生效");

        XMessage q = await c.RequestAsync(98, 0, b => b.U16(15).U16(0).Bytes(Encoding.Latin1.GetBytes("XInputExtension")).Pad());
        XMessage wrap = await c.RequestAsync(q.Bytes[9], 24, b => b.U8(3).U8(250).U8(10).U8(0));
        Assert.IsTrue(wrap.IsError, "250 + 10 超出 255:BadValue(原先按字节回绕)");
    }

    [TestMethod]
    public async Task ChangeKeyboardControl的响铃音量生效_xset_b_off不出声_LED按led_mode点亮()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        await ChangeKeyboardControlAsync(c, 0x2 | 0x4 | 0x8, 0, 880, 0xFFFFFFFF);   // bell-percent 0、pitch 880、duration −1(默认)
        await c.SendAsync(104, 0);                                                   // Bell 0
        await host.WaitForAsync(() => host.Log.Contains("bell 0"));
        XMessage control = await c.RequestAsync(103, 0);
        Assert.AreEqual(0, control.Bytes[13], "bell-percent");
        Assert.AreEqual(880, control.U16(14), "bell-pitch");
        Assert.AreEqual(100, control.U16(16), "bell-duration 恢复默认");

        await ChangeKeyboardControlAsync(c, 0x10 | 0x20, 3, 1);   // LED 3 On
        control = await c.RequestAsync(103, 0);
        Assert.AreEqual(0x4u, control.U32(8), "led-mask");
        await ChangeKeyboardControlAsync(c, 0x20, 0);               // 只给 led-mode:所有 LED 灭
        control = await c.RequestAsync(103, 0);
        Assert.AreEqual(0u, control.U32(8));

        XMessage badPercent = await c.RequestAsync(102, 0, b => b.U32(0x2).U32(0xFFFFFFFE));   // −2
        Assert.IsTrue(badPercent.IsError);
        Assert.AreEqual(2, badPercent.Bytes[1], "−1 以外的负值是 BadValue");
        XMessage ledWithoutMode = await c.RequestAsync(102, 0, b => b.U32(0x10).U32(3));
        Assert.AreEqual(8, ledWithoutMode.Bytes[1], "只给 led 不给 led-mode 是 BadMatch");
    }

    /// <summary>GetKeyboardMapping 取一个键码第一列的键值。</summary>
    private static async Task<uint> KeysymAsync(XTestClient c, byte keycode) =>
        (await c.RequestAsync(101, 0, b => b.U8(keycode).U8(1).U16(0))).U32(32);

    [TestMethod]
    public async Task InjectText_键位表里有的字直接按_没有的借一个空键码改成Unicode键值_再输入同一个字不再改()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0x1 | 0x2);
        server.FocusTopLevel(host.Mapped[top]);

        server.InjectText("a中\n");
        await c.SyncAsync();
        XMessage mapping = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 34);
        Assert.AreEqual(1, mapping.Bytes[4], "MappingNotify(Keyboard)");
        byte borrowed = mapping.Bytes[5];
        Assert.AreEqual(1, mapping.Bytes[6]);
        Assert.AreEqual(0x01004E2Du, await KeysymAsync(c, borrowed), "U+4E2D 的 Unicode 键值");
        CollectionAssert.AreEqual(
            new List<(byte, byte)> { (KeyPress, 38), (KeyRelease, 38), (KeyPress, borrowed), (KeyRelease, borrowed), (KeyPress, 36), (KeyRelease, 36) },
            await KeyEventsAsync(c), "a 按键位表里的 a 键;换行按 Return");

        // 再输入同一个字:用同一个键码,不再发 MappingNotify;控制字符不输入。
        server.InjectText("\u0007中");
        CollectionAssert.AreEqual(new List<(byte, byte)> { (KeyPress, borrowed), (KeyRelease, borrowed) }, await KeyEventsAsync(c));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 34, timeoutMs: 100));
        Assert.ThrowsExactly<ArgumentException>(() => server.InjectText(new string('x', X11Server.MaxInjectedTextLength + 1)));
    }

    [TestMethod]
    public async Task InjectText_空键码用完时挪用最久没用的_刚用过的等一会儿_字一个不少()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, 0x1);
        server.FocusTopLevel(host.Mapped[top]);

        // 300 个不同的汉字多于空着的键码:用完之后等刚用过的放开再挪用,一个字都不丢。
        string text = string.Concat(Enumerable.Range(0x4E00, 300).Select(cp => char.ConvertFromUtf32(cp)));
        server.InjectText(text);
        int presses = 0;
        while (presses < 300)
        {
            await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == KeyPress, timeoutMs: 5000);
            presses++;
        }
        Assert.AreEqual(300, presses);
    }
}
