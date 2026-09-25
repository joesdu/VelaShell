using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>连接层的健壮性:主动断开真的断开、超大请求不会拖垮服务端。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RobustnessTests
{
    [TestMethod]
    public async Task KillClient之后被杀的客户端连接立即结束()
    {
        await using X11Server server = new();
        await using XTestClient killer = await XTestClient.ConnectAsync(server);
        await using XTestClient victim = await XTestClient.ConnectAsync(server);
        uint window = victim.NewId();
        await victim.SendAsync(1, 0, b => b.U32(window).U32(victim.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await victim.SyncAsync();

        await killer.SendAsync(113, 0, b => b.U32(window));   // KillClient
        await killer.SyncAsync();

        // 被杀的一方什么也不发 —— 以前服务端的读端会一直挂在它的连接上;现在 ServeAsync 立即结束(TCP / Unix 套接字随之关闭)。
        await victim.ServerTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task 超大尺寸的PutImage与CopyArea回错误而不是耗尽内存()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(16).U16(16));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0));

        // PutImage 声称 65535×65535,只带 4 字节数据。
        XMessage put = await c.RequestAsync(72, 2, b => b.U32(pixmap).U32(gc).U16(65535).U16(65535).I16(0).I16(0).U8(0).U8(24).U16(0).U32(0));
        Assert.IsTrue(put.IsError, "数据不够:BadLength");

        // CopyArea 65535×65535:只拷与源、目标相交的部分,不分配请求尺寸的缓冲。
        await c.SendAsync(62, 0, b => b.U32(pixmap).U32(pixmap).U32(gc).I16(0).I16(0).I16(1).I16(1).U16(65535).U16(65535));
        await c.SyncAsync();   // 服务端还活着、还在回应
    }
}
