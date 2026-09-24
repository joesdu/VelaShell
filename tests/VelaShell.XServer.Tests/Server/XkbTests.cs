using System.Text;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XKEYBOARD:由核心键位表推出的键位表、修饰状态与 StateNotify、指示灯、名字。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XkbTests
{
    private const ushort UseCoreKbd = 0x100;

    private static async Task<(byte Major, byte Event)> XkbAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(9).U16(0).Bytes(Encoding.Latin1.GetBytes("XKEYBOARD")).Pad());
        Assert.AreEqual(1, q.Bytes[8]);
        XMessage use = await c.RequestAsync(q.Bytes[9], 0, b => b.U16(1).U16(0));
        Assert.AreEqual(1, use.Bytes[1], "supported");
        return (q.Bytes[9], q.Bytes[10]);
    }

    [TestMethod]
    public async Task GetMap的键值段与核心键位表一致且类型下标合法()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        // full = KeyTypes | KeySyms
        XMessage map = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0x3).U16(0).Bytes(new byte[18]));
        Assert.IsTrue(map.IsReply);
        Assert.AreEqual(8, map.Bytes[10], "minKeyCode");
        int nTypes = map.Bytes[15];
        Assert.AreEqual(6, nTypes, "四个规范类型 + 两个四级类型(AltGr 层)");
        int nKeys = map.Bytes[20];
        Assert.AreEqual(248, nKeys);

        int o = 40;
        for (int t = 0; t < nTypes; t++)
        {
            int entries = map.Bytes[o + 5];
            o += 8 + (entries * 8);
        }
        // 键码 8 起逐个走到 38(a):类型 ALPHABETIC、宽 2、键值 a / A。
        for (int code = 8; code < 38; code++)
        {
            o += 8 + (map.U16(o + 6) * 4);
        }
        Assert.AreEqual(2, map.Bytes[o], "a 的类型是 ALPHABETIC");
        Assert.AreEqual(2, map.Bytes[o + 5], "宽度 2");
        Assert.AreEqual('a', map.U32(o + 8));
        Assert.AreEqual('A', map.U32(o + 12));
    }

    [TestMethod]
    public async Task 按Shift发StateNotify_CapsLock锁定并点亮指示灯()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, byte xkbEvent) = await XkbAsync(c);
        // SelectEvents:affectWhich = StateNotify(1<<2),details 全选
        await c.SendAsync(xkb, 1, b => b.U16(UseCoreKbd).U16(1 << 2).U16(0).U16(1 << 2).U16(0).U16(0));
        await c.SyncAsync();

        server.Key(50, true);   // Shift_L
        XMessage state = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == xkbEvent && m.Bytes[1] == 2);
        Assert.AreEqual(1, state.Bytes[9], "有效修饰 = Shift");
        Assert.AreEqual(1, state.Bytes[10], "base = Shift");
        server.Key(50, false);

        server.Key(66, true);   // Caps_Lock 按下即锁定
        server.Key(66, false);
        await c.SyncAsync();
        XMessage current = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0x02, current.Bytes[11], "lockedMods = Lock");
        Assert.AreEqual(0x02, current.Bytes[8], "有效修饰 = Lock");
        XMessage leds = await c.RequestAsync(xkb, 12, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(1u, leds.U32(8), "Caps Lock 灯亮");
    }

    [TestMethod]
    public async Task 两个Shift同时按住_松开一个Shift仍然生效()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        server.Key(50, true);
        server.Key(62, true);
        server.Key(50, false);
        await c.SyncAsync();
        XMessage current = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0x01, current.Bytes[8]);
    }

    [TestMethod]
    public async Task GetNames给出键名与类型名_错误的设备报BadKeyboard()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        XMessage names = await c.RequestAsync(xkb, 17, b => b.U16(UseCoreKbd).U16(0).U32(1 << 9));   // KeyNames
        Assert.AreEqual(248, names.Bytes[19], "nKeys");
        // 键码 9 是第二个:ESC
        Assert.AreEqual("ESC", Encoding.Latin1.GetString(names.Bytes, 32 + 4, 3));
        Assert.AreEqual("AC01", Encoding.Latin1.GetString(names.Bytes, 32 + ((38 - 8) * 4), 4));

        XMessage bad = await c.RequestAsync(xkb, 4, b => b.U16(7).U16(0));
        Assert.IsTrue(bad.IsError);
    }

    [TestMethod]
    public async Task 核心ChangeKeyboardMapping之后XKB也看到新键值()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        // 键码 38 改成 x / X(xmodmap 的做法)
        await c.SendAsync(100, 1, b => b.U8(38).U8(2).U16(0).U32('x').U32('X'));
        XMessage map = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0).U16(0x2).U8(0).U8(0).U8(38).U8(1).Bytes(new byte[14]));
        Assert.AreEqual(38, map.Bytes[17], "firstKeySym");
        Assert.AreEqual('x', map.U32(40 + 8));
    }

    [TestMethod]
    public async Task AltGr层_核心第五六列推出四级键类型_右Alt进Mod5()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);

        // 德语布局的 Q 键:q Q | 组 2 抄组 1 | AltGr → @。右 Alt 改成 ISO_Level3_Shift,从 Mod1 挪到 Mod5。
        server.SetKeyboardMapping(24, 6, ['q', 'Q', 'q', 'Q', '@', '@']);
        server.SetKeyboardMapping(108, 6, [0xfe03, 0xfe03, 0, 0, 0, 0]);
        server.SetModifierMapping([50, 62, 66, 0, 37, 105, 64, 0, 77, 0, 0, 0, 133, 134, 108, 0]);
        await c.SyncAsync();

        XMessage key = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0).U16(0x2).U8(0).U8(0).U8(24).U8(1).Bytes(new byte[14]));
        Assert.AreEqual(5, key.Bytes[40], "FOUR_LEVEL_ALPHABETIC");
        Assert.AreEqual(4, key.Bytes[45], "宽度 4");
        Assert.AreEqual('@', key.U32(48 + 8), "第三级 = AltGr");

        XMessage types = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0x1).U16(0).Bytes(new byte[18]));
        Assert.AreEqual(6, types.Bytes[16], "nTypes:四个规范类型 + 两个四级类型");

        XMessage modifiers = await c.RequestAsync(119, 0);   // GetModifierMapping
        Assert.AreEqual(108, modifiers.Bytes[32 + 14], "Mod5 的第一个键码是右 Alt");
    }

    [TestMethod]
    public async Task 更窄的ChangeKeyboardMapping不收窄整张表()
    {
        // xmodmap -e "keycode 108 = ISO_Level3_Shift" 发的是每键码 1 列:别的键的 Shift 列不能跟着没了。
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendAsync(100, 1, b => b.U8(108).U8(1).U16(0).U32(0xfe03));
        XMessage map = await c.RequestAsync(101, 0, b => b.U8(25).U8(1).U16(0));   // GetKeyboardMapping:w
        Assert.AreEqual(2, map.Bytes[1], "每键码仍是 2 列");
        Assert.AreEqual('w', map.U32(32));
        Assert.AreEqual('W', map.U32(36));
        XMessage alt = await c.RequestAsync(101, 0, b => b.U8(108).U8(1).U16(0));
        Assert.AreEqual(0xfe03u, alt.U32(32));
        Assert.AreEqual(0u, alt.U32(36), "请求没给的列清成 NoSymbol");
    }

    [TestMethod]
    public async Task SetMap上传的键值与修饰键映射写回核心键位表()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);

        // present = KeySyms | ModifierMap:键码 29(US 的 y)改成 z / Z(德语布局),键码 37 只进 Control。
        await c.SendAsync(xkb, 9, b => b.U16(UseCoreKbd).U16(0x2 | 0x4).U16(0).U8(8).U8(255)
            .U8(0).U8(0)                     // firstType、nTypes
            .U8(29).U8(1).U16(2)             // firstKeySym、nKeySyms、totalSyms
            .U8(0).U8(0).U16(0)              // 动作
            .U8(0).U8(0).U8(0)               // 行为
            .U8(0).U8(0).U8(0)               // 显式成分
            .U8(37).U8(1).U8(1)              // firstModMapKey、nModMapKeys、totalModMapKeys
            .U8(0).U8(0).U8(0)               // 虚拟修饰映射
            .U16(0)                          // virtualMods
            .U8(1).U8(0).U8(0).U8(0).U8(1).U8(2).U16(2).U32('z').U32('Z')   // KEYSYMMAP
            .U8(37).U8(0x04).U16(0));                                      // KEYMODMAP(补齐)
        XMessage map = await c.RequestAsync(101, 0, b => b.U8(29).U8(1).U16(0));
        Assert.AreEqual('z', map.U32(32));
        Assert.AreEqual('Z', map.U32(36));

        XMessage modifiers = await c.RequestAsync(119, 0);
        int per = modifiers.Bytes[1];
        byte[] control = modifiers.Bytes.AsSpan(32 + (2 * per), per).ToArray();
        CollectionAssert.Contains(control, (byte)37, "键码 37 在 Control 行");
    }
}
