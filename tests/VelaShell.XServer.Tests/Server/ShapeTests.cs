using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>SHAPE 扩展:非矩形窗口的可见区域、绘图裁剪、输入命中与宿主看到的形状。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ShapeTests
{
    private static async Task<byte> ShapeMajorAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("SHAPE")));
        Assert.AreEqual(1, q.Bytes[8], "SHAPE 应当存在");
        Assert.AreEqual(64, q.Bytes[10], "first-event");
        return q.Bytes[9];
    }

    private static async Task<(uint Top, XTopLevelWindow Handle)> MapTopAsync(XTestClient c, RecordingHost host, uint bg, uint mask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x802).U32(bg).U32(mask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    [TestMethod]
    public async Task QueryVersion报1点1()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        XMessage v = await c.RequestAsync(major, 0);
        Assert.AreEqual(1, v.U16(8));
        Assert.AreEqual(1, v.U16(10));
    }

    [TestMethod]
    public async Task 子窗口的边界形状之外露出父窗口背景()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, XTopLevelWindow handle) = await MapTopAsync(c, host, 0x000000);

        uint child = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(child).U32(top).I16(10).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        // 形状只留左上 5×5,再映射。
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(child).I16(0).I16(0).I16(0).I16(0).U16(5).U16(5));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SyncAsync();

        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        Assert.AreEqual(0xFFFFFFu, px[(12 * w) + 12], "形状里面是子窗口背景");
        Assert.AreEqual(0x000000u, px[(20 * w) + 20], "形状外面露出父窗口");

        XMessage rects = await c.RequestAsync(major, 8, b => b.U32(child).U8(0).U8(0).U8(0).U8(0));
        Assert.AreEqual(1u, rects.U32(8));
        Assert.AreEqual(5, rects.U16(36));
    }

    [TestMethod]
    public async Task 设形状发ShapeNotify且宿主拿到顶层形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, XTopLevelWindow handle) = await MapTopAsync(c, host, 0x123456);
        Assert.IsNull(handle.Snapshot.Shape);

        await c.SendAsync(major, 6, b => b.U32(top).U8(1).U8(0).U8(0).U8(0));   // SelectInput
        await c.SendAsync(major, 1, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0)
            .I16(0).I16(0).U16(30).U16(40).I16(30).I16(10).U16(30).U16(10));
        XMessage notify = await c.NextEventAsync(64);
        Assert.AreEqual(0, notify.Detail, "kind = Bounding");
        Assert.AreEqual(top, notify.U32(4));
        Assert.AreEqual(60, notify.U16(12), "extents 宽");
        Assert.AreEqual(1, notify.Bytes[20], "shaped");

        await host.WaitForAsync(() => handle.Snapshot.Shape is not null);
        Assert.AreEqual(1200 + 300, handle.Snapshot.Shape!.Sum(r => r.Width * r.Height));

        // Mask 源为 None:回到默认矩形。
        await c.SendAsync(major, 2, b => b.U8(0).U8(0).U8(0).U8(0).U32(top).I16(0).I16(0).U32(0));
        await host.WaitForAsync(() => handle.Snapshot.Shape is null);
    }

    [TestMethod]
    public async Task 输入形状之外的点击落到父窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await ShapeMajorAsync(c);
        (uint top, _) = await MapTopAsync(c, host, 0, mask: 0x4);   // ButtonPress

        uint child = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(child).U32(top).I16(0).I16(0).U16(40).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4));
        await c.SendAsync(8, 0, b => b.U32(child));
        // 输入形状(kind 2)只留左上 10×10。
        await c.SendAsync(major, 1, b => b.U8(0).U8(2).U8(0).U8(0).U32(child).I16(0).I16(0).I16(0).I16(0).U16(10).U16(10));
        await c.SyncAsync();

        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: false);
        XMessage inside = await c.NextEventAsync(4);
        Assert.AreEqual(child, inside.U32(12), "形状里面点到子窗口");

        server.InjectPointerButton(host.Mapped[top], 30, 30, 1, pressed: true);
        XMessage outside = await c.NextEventAsync(4);
        Assert.AreEqual(top, outside.U32(12), "形状外面穿过子窗口落到父窗口");
    }
}
