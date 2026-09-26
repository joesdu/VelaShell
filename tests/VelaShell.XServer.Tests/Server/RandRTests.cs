using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>RANDR(只读):一台覆盖整个根窗口的虚拟显示器,改配置的请求一律失败。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RandRTests
{
    private static readonly X11ServerOptions Options = new() { ScreenWidth = 1920, ScreenHeight = 1080, Dpi = 96 };

    private static async Task<byte> RandRMajorAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("RANDR")).Pad());
        Assert.AreEqual(1, q.Bytes[8], "RANDR 应当存在");
        Assert.AreEqual(129, q.Bytes[11], "first-error");
        return q.Bytes[9];
    }

    [TestMethod]
    public async Task QueryVersion报1点5()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);
        XMessage v = await c.RequestAsync(major, 0, b => b.U32(1).U32(6));
        Assert.AreEqual(1u, v.U32(8));
        Assert.AreEqual(5u, v.U32(12));
    }

    [TestMethod]
    public async Task 资源输出CRTC三者互相对得上()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);

        XMessage res = await c.RequestAsync(major, 25, b => b.U32(c.RootWindow));
        Assert.AreEqual(1, res.U16(16), "一个 CRTC");
        Assert.AreEqual(1, res.U16(18), "一个输出");
        Assert.AreEqual(1, res.U16(20), "一个模式");
        uint crtc = res.U32(32), output = res.U32(36), mode = res.U32(40);
        Assert.AreEqual(1920, res.U16(44));
        Assert.AreEqual(1080, res.U16(46));
        int nameLength = res.U16(22);
        Assert.AreEqual("1920x1080", Encoding.Latin1.GetString(res.Bytes, 72, nameLength));

        XMessage info = await c.RequestAsync(major, 9, b => b.U32(output).U32(0));
        Assert.AreEqual(0, info.Bytes[1], "Success");
        Assert.AreEqual(crtc, info.U32(12));
        Assert.AreEqual(508u, info.U32(16), "毫米宽 = 1920 × 25.4 / 96");
        Assert.AreEqual(0, info.Bytes[24], "Connected");
        Assert.AreEqual(mode, info.U32(40), "模式列表跟在 CRTC 列表后面");

        XMessage crtcInfo = await c.RequestAsync(major, 20, b => b.U32(crtc).U32(0));
        Assert.AreEqual(1920, crtcInfo.U16(16));
        Assert.AreEqual(mode, crtcInfo.U32(20));
        Assert.AreEqual(output, crtcInfo.U32(32));
    }

    [TestMethod]
    public async Task GetMonitors报一台主显示器()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);
        XMessage m = await c.RequestAsync(major, 42, b => b.U32(c.RootWindow).U8(1).U8(0).U8(0).U8(0));
        Assert.AreEqual(1u, m.U32(12), "nmonitors");
        Assert.AreEqual(1, m.Bytes[36], "primary");
        Assert.AreEqual(1920, m.U16(44));
        Assert.AreEqual(1080, m.U16(46));
    }

    [TestMethod]
    public async Task 错误的输出ID报BadOutput_改配置回Failed()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);

        XMessage bad = await c.RequestAsync(major, 9, b => b.U32(0x12345).U32(0));
        Assert.IsTrue(bad.IsError);
        Assert.AreEqual(129, bad.Bytes[1]);

        XMessage set = await c.RequestAsync(major, 21, b => b.U32(0x40).U32(0).U32(0).I16(0).I16(0).U32(0).U16(1).U16(0));
        Assert.IsTrue(set.IsReply);
        Assert.AreEqual(3, set.Bytes[1], "Failed");
    }
}
