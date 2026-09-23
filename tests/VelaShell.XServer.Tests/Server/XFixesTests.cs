using System.Text;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XFIXES 扩展:区域对象、选区属主追踪、光标隐藏与窗口形状区域。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XFixesTests
{
    private const uint Primary = 1;   // 预定义原子 PRIMARY

    private static async Task<byte> XFixesMajorAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("XFIXES")).Pad());
        Assert.AreEqual(1, q.Bytes[8], "XFIXES 应当存在");
        Assert.AreEqual(65, q.Bytes[10], "first-event");
        Assert.AreEqual(128, q.Bytes[11], "first-error");
        return q.Bytes[9];
    }

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task QueryVersion最高报5点0()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        XMessage v = await c.RequestAsync(major, 0, b => b.U32(6).U32(0));
        Assert.AreEqual(5u, v.U32(8));
        Assert.AreEqual(0u, v.U32(12));
        XMessage old = await c.RequestAsync(major, 0, b => b.U32(2).U32(0));
        Assert.AreEqual(2u, old.U32(8), "客户端要的更低就回它要的");
    }

    [TestMethod]
    public async Task 区域的并与取回以及销毁后报BadRegion()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);

        uint a = c.NewId(), b = c.NewId();
        await c.SendAsync(major, 5, x => x.U32(a).I16(0).I16(0).U16(10).U16(10));
        await c.SendAsync(major, 5, x => x.U32(b).I16(20).I16(0).U16(5).U16(5));
        await c.SendAsync(major, 13, x => x.U32(a).U32(b).U32(a));   // a = a ∪ b

        XMessage fetch = await c.RequestAsync(major, 19, x => x.U32(a));
        Assert.IsTrue(fetch.IsReply);
        Assert.AreEqual(0, fetch.I16(8), "extents x");
        Assert.AreEqual(25, fetch.U16(12), "extents 宽");
        Assert.AreEqual(10, fetch.U16(14), "extents 高");
        int count = (fetch.Bytes.Length - 32) / 8;
        int area = 0;
        for (int i = 0; i < count; i++)
        {
            area += fetch.U16(32 + (i * 8) + 4) * fetch.U16(32 + (i * 8) + 6);
        }
        Assert.AreEqual(100 + 25, area);

        await c.SendAsync(major, 10, x => x.U32(a));
        XMessage error = await c.RequestAsync(major, 19, x => x.U32(a));
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(128, error.Bytes[1], "BadRegion = first-error + 0");
    }

    [TestMethod]
    public async Task 选区属主变更与属主断开都发SelectionNotify()
    {
        await using X11Server server = new();
        await using XTestClient watcher = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(watcher);
        await watcher.SendAsync(major, 2, b => b.U32(watcher.RootWindow).U32(Primary).U32(0x7));
        await watcher.SyncAsync();

        XTestClient owner = await XTestClient.ConnectAsync(server);
        uint window = owner.NewId();
        await owner.SendAsync(1, 0, b => b.U32(window).U32(owner.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(1).U32(0).U32(0));
        await owner.SendAsync(22, 0, b => b.U32(window).U32(Primary).U32(0));   // SetSelectionOwner
        await owner.SyncAsync();

        XMessage set = await watcher.NextEventAsync(65);
        Assert.AreEqual(0, set.Bytes[1], "subtype = SetSelectionOwner");
        Assert.AreEqual(watcher.RootWindow, set.U32(4));
        Assert.AreEqual(window, set.U32(8), "owner");
        Assert.AreEqual(Primary, set.U32(12));

        await owner.DisposeAsync();
        XMessage closed = await watcher.NextEventAsync(65);
        Assert.AreEqual(2, closed.Bytes[1], "subtype = SelectionClientClose");
        Assert.AreEqual(0u, closed.U32(8));
    }

    [TestMethod]
    public async Task HideCursor让宿主隐藏光标_ShowCursor恢复()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        uint top = await MapTopAsync(c, host);
        server.PointerMotion(top, 5, 5);

        await c.SendAsync(major, 29, b => b.U32(top));
        await host.WaitForAsync(() => host.Log.Contains("cursor -2"));

        await c.SendAsync(major, 30, b => b.U32(top));
        await host.WaitForAsync(() => host.Log.LastOrDefault(e => e.StartsWith("cursor", StringComparison.Ordinal)) == "cursor -1");

        XMessage error = await c.RequestAsync(major, 30, b => b.U32(top));
        Assert.IsTrue(error.IsError, "没隐藏过就 ShowCursor 是 BadMatch");
        Assert.AreEqual(8, error.Bytes[1]);
    }

    [TestMethod]
    public async Task SetWindowShapeRegion设出宿主可见的顶层形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow handle = host.Mapped[top];

        uint region = c.NewId();
        await c.SendAsync(major, 5, b => b.U32(region).I16(0).I16(0).U16(20).U16(10));
        await c.SendAsync(major, 21, b => b.U32(top).U8(0).U8(0).U8(0).U8(0).I16(5).I16(5).U32(region));
        await host.WaitForAsync(() => handle.Shape is not null);
        Assert.AreEqual(200, handle.Shape!.Sum(r => r.Width * r.Height));
        Assert.AreEqual(5, handle.Shape!.Min(r => r.X), "偏移生效");

        await c.SendAsync(major, 21, b => b.U32(top).U8(0).U8(0).U8(0).U8(0).I16(0).I16(0).U32(0));
        await host.WaitForAsync(() => handle.Shape is null);
    }
}
