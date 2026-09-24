using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;
using XServerOptions = VelaShell.Core.Models.XServerOptions;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 内置 X 服务端:生命周期(宿主的附着 / 脱离)、SSH 转发拿到的连接器真能接进服务端、什么情况下不接管。
/// 显示号探测注入,监听的是 :10 起的号,避开本机常见的 :0。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public class BuiltInLocalXServerTests
{
    private static ISettingsService Settings(XServerOptions options)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { XServer = options });
        return settings;
    }

    /// <summary>0–9 当作被占用,自动模式挑到 :10。</summary>
    private static Task<bool> LowDisplaysBusy(int display, CancellationToken _) => Task.FromResult(display < 10);

    private static BuiltInLocalXServer Create(XServerOptions options, RecordingHost? host, bool otherDisplay = false) =>
        new(Settings(options), () => host, LowDisplaysBusy, _ => Task.FromResult(otherDisplay));

    [TestMethod]
    public async Task Start_AttachesHost_ThenStop_DetachesIt()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);
        List<XServerState> states = [];
        server.StateChanged += (_, _) => states.Add(server.State);

        XServerStartResult result = await server.StartAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(XServerState.Running, server.State);
        Assert.AreEqual(10, server.DisplayNumber);
        Assert.AreEqual("localhost:10.0", server.Display);
        Assert.IsNotNull(host.Attached, "启动时先把服务端交给宿主");

        await server.StopAsync();

        Assert.AreEqual(XServerState.Stopped, server.State);
        Assert.AreEqual(1, host.Detaches);
        CollectionAssert.AreEqual(new[] { XServerState.Starting, XServerState.Running, XServerState.Stopped }, states);
    }

    /// <summary>设置里选的键盘布局在附着之前交给宿主(空串 = 跟随系统)。</summary>
    [TestMethod]
    [DataRow("de")]
    [DataRow("")]
    public async Task Start_HandsTheChosenKeyboardLayoutToTheHostBeforeAttaching(string layout)
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions { KeyboardLayout = layout }, host);

        Assert.IsTrue((await server.StartAsync()).Success);

        Assert.AreEqual(layout, host.LayoutAtAttach);
    }

    [TestMethod]
    public async Task Start_WithoutHost_FailsWithoutListening()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host: null);

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    [TestMethod]
    public async Task Start_ConfiguredDisplayInUse_Fails()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { DisplayNumber = 3 }, new RecordingHost());

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "3");
    }

    /// <summary>SSH 拿到的连接器接进的是服务端本身:走完 X 的连接建立,拿到 Success。</summary>
    [TestMethod]
    public async Task ResolveForwarding_AutoStarts_AndConnectorReachesTheServer()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.AreEqual("localhost:10.0", resolution.Display);
        Assert.IsNotNull(resolution.Connector);
        await using Stream stream = await resolution.Connector(CancellationToken.None);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "Success —— 经连接器来的连接按本机连接放行");
    }

    [TestMethod]
    public async Task ResolveForwarding_AutoStartOff_DoesNotTakeOver()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { AutoStartForX11Forwarding = false }, new RecordingHost());

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.IsNull(resolution.Connector);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    /// <summary>本机已经有别的 X 显示在用(Windows 上 :0 有人听,其它平台设了 DISPLAY):不插手。</summary>
    [TestMethod]
    public async Task ResolveForwarding_OtherDisplayInUse_DoesNotTakeOver()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost(), otherDisplay: true);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    [TestMethod]
    public async Task ResolveForwarding_WhenRunning_ReturnsDisplayAndConnectorEvenIfAutoStartOff()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { AutoStartForX11Forwarding = false }, new RecordingHost());
        Assert.IsTrue((await server.StartAsync()).Success);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.AreEqual("localhost:10.0", resolution.Display);
        Assert.IsNotNull(resolution.Connector);
    }

    /// <summary>记录附着 / 脱离的宿主;窗口回调一概不管。</summary>
    internal sealed class RecordingHost : IEmbeddedXServerHost
    {
        public X11Server? Attached { get; private set; }

        public int Detaches { get; private set; }

        public Task AttachAsync(X11Server server, CancellationToken cancellationToken)
        {
            Attached = server;
            LayoutAtAttach = KeyboardLayout;
            return Task.CompletedTask;
        }

        public string? KeyboardLayout { get; private set; }

        /// <summary>附着那一刻宿主手里的键盘布局(要在附着之前就交给宿主)。</summary>
        public string? LayoutAtAttach { get; private set; }

        public void UseKeyboardLayout(string layout) => KeyboardLayout = layout;

        public void Detach() => Detaches++;

        public void TopLevelMapped(XTopLevelWindow window)
        {
        }

        public void TopLevelUnmapped(XTopLevelWindow window)
        {
        }

        public void TopLevelChanged(XTopLevelWindow window)
        {
        }

        public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
        {
        }

        public void CursorChanged(XTopLevelWindow? window, int cursorGlyph)
        {
        }

        public void Bell(int percent)
        {
        }

        public void ClipboardChanged(string text)
        {
        }
    }
}
