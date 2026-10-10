using System.Diagnostics;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>执行线程在客户端之间的调度。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class SchedulingTests
{
    /// <summary>
    /// 一个客户端排了一长串很重的请求(这里是 600 次填满 2000×2000 的像素图),另一个客户端的请求不用等它们全做完:执行线程在客户端之间轮流取。
    /// 原先全部工作排一条队、先来先做 —— 间接 GL 的 glxgears 排满请求时,之后连进来的 xdpyinfo 等了 25 秒。
    /// </summary>
    [TestMethod]
    public async Task 一个客户端排满重请求时_别的客户端照样很快得到回复()
    {
        await using X11Server server = new();
        await using XTestClient heavy = await XTestClient.ConnectAsync(server);
        await using XTestClient light = await XTestClient.ConnectAsync(server);
        uint pixmap = heavy.NewId(), gc = heavy.NewId();
        await heavy.SendAsync(53, 24, b => b.U32(pixmap).U32(heavy.RootWindow).U16(2000).U16(2000));   // CreatePixmap
        await heavy.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4).U32(0xFF0000));             // CreateGC
        await heavy.SyncAsync();

        for (int i = 0; i < 600; i++)
        {
            await heavy.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(0).I16(0).U16(2000).U16(2000));   // PolyFillRectangle
        }
        Task heavyDone = heavy.SyncAsync();
        await Task.Delay(20);   // 让它们都排进去

        Stopwatch waited = Stopwatch.StartNew();
        await light.SyncAsync();
        waited.Stop();
        bool heavyFinishedFirst = heavyDone.IsCompleted;
        await heavyDone;
        Assert.IsFalse(heavyFinishedFirst, $"另一个客户端的回复要等重请求全做完才到(等了 {waited.ElapsedMilliseconds} ms)");
    }
}
