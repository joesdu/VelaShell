using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.Tests.TestSupport;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Tests.XServer;

/// <summary>
/// 本机的拖放进 X 窗口(F16):Avalonia 的拖放事件经 <see cref="XDropTarget" /> 转给服务端,服务端替宿主扮演 XDND 的源 ——
/// 一个声明了 XdndAware 的 X 程序收得到 Enter / Position,回了接受之后宿主显示「可以放」,松手之后经 XdndSelection 取得到文本。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XDropTargetUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XDropTargetUiTests).Assembly);

    [TestMethod]
    public void RemotePaths_BecomeEscapedFileUris_AndTextIsEncodedPerType()
    {
        Assert.AreEqual("file:///tmp/velashell-drop-1/a%20b%23.txt", XDropTarget.RemoteFileUri("/tmp/velashell-drop-1/a b#.txt"));
        Assert.AreEqual("file:///tmp/%E4%B8%AD.txt", XDropTarget.RemoteFileUri("/tmp/中.txt"));
        Dictionary<string, ReadOnlyMemory<byte>> text = XDropTarget.TextData("é中");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("é中"), text["UTF8_STRING"].ToArray());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("é中"), text["text/plain;charset=utf-8"].ToArray());
        CollectionAssert.AreEqual(new byte[] { 0xE9, (byte)'?' }, text["text/plain"].ToArray(), "XDND 的 text/plain 默认 Latin-1");
    }

    [TestMethod]
    public async Task TextDraggedOntoAnXWindow_ReachesTheXdndTarget() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        XTestWire wire = await XTestWire.ConnectAsync(client);
        uint window = wire.IdBase | 1;
        await wire.SendAsync(1, 24, b => b.U32(window).U32(wire.Root).I16(10).I16(10).U16(80).U16(60).U16(0).U16(1).U32(0).U32(0));
        uint aware = await wire.InternAsync("XdndAware"), enter = await wire.InternAsync("XdndEnter");
        uint position = await wire.InternAsync("XdndPosition"), status = await wire.InternAsync("XdndStatus");
        uint drop = await wire.InternAsync("XdndDrop"), selection = await wire.InternAsync("XdndSelection");
        uint copy = await wire.InternAsync("XdndActionCopy"), utf8 = await wire.InternAsync("UTF8_STRING");
        uint plainUtf8 = await wire.InternAsync("text/plain;charset=utf-8"), dropped = await wire.InternAsync("DROPPED");
        await wire.SendAsync(18, 0, b => b.U32(window).U32(aware).U32(4).U8(32).Zero(3).U32(1).U32(5));
        await wire.SendAsync(8, 0, b => b.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == window));
        Dispatcher.UIThread.RunJobs();

        DataTransfer data = new();
        DataTransferItem item = new();
        item.SetText("héllo 拖放");
        data.Add(item);
        Point at = new(20, 20);
        native.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        byte[] entered = await wire.NextClientMessageAsync(enter);
        uint source = U32(entered, 12);
        Assert.AreEqual(window, U32(entered, 4));
        Assert.AreEqual(plainUtf8, U32(entered, 20), "UTF-8 的纯文本排在第一个");
        Assert.AreEqual(1u, U32(entered, 16) & 1, "多于三种类型:目标去读 XdndTypeList");
        await wire.NextClientMessageAsync(position);
        native.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        Assert.IsFalse(server.IsDragAccepted, "目标还没表态:不显示「可以放」");

        // 目标接受:回 XdndStatus(之后每来一条位置都回一条)。
        async Task AcceptAsync() => await wire.SendAsync(25, 0, b => b.U32(source).U32(0)
            .U8(33).U8(32).U16(0).U32(source).U32(status).U32(window).U32(1).U32(0).U32(0).U32(copy));
        await AcceptAsync();
        await wire.NextClientMessageAsync(position);   // 等状态期间那次 DragOver 记下的位置,状态回来之后补发
        await AcceptAsync();
        await wire.SyncAsync();
        Assert.IsTrue(server.IsDragAccepted, "目标接受了:宿主据此显示「可以放」");
        native.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        await wire.NextClientMessageAsync(position);
        await AcceptAsync();
        await wire.SyncAsync();

        native.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        await wire.NextClientMessageAsync(position);   // 松手时按最后的位置再报一次,等最后一条状态
        await AcceptAsync();
        byte[] dropMessage = await wire.NextClientMessageAsync(drop);
        uint time = U32(dropMessage, 20);

        await wire.SendAsync(24, 0, b => b.U32(window).U32(selection).U32(utf8).U32(dropped).U32(time));
        await WaitForAsync(() => wire.Events.FirstOrDefault(e => (e[0] & 0x7F) == 31));   // SelectionNotify
        byte[] value = await wire.RequestAsync(20, 1, b => b.U32(window).U32(dropped).U32(0).U32(0).U32(1000));
        Assert.AreEqual("héllo 拖放", Encoding.UTF8.GetString(value, 32, (int)U32(value, 16)));

        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    private static uint U32(byte[] message, int offset) => XTestWire.U32(message, offset);

    private static Task<T> WaitForAsync<T>(Func<T?> probe) where T : class => XTestWire.WaitForAsync(probe);
}
