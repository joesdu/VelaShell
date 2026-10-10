using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>按引擎在内置与 VcXsrv 之间转发;正在运行的那个优先。</summary>
[TestClass]
[TestCategory("XServer")]
public class LocalXServerSelectorTests
{
    private static ISettingsService Settings(string engine)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { XServer = new XServerOptions { Engine = engine } });
        return settings;
    }

    private static ILocalXServer Engine(bool supported = true)
    {
        ILocalXServer engine = Substitute.For<ILocalXServer>();
        engine.IsSupported.Returns(supported);
        engine.State.Returns(XServerState.Stopped);
        engine.StartAsync(Arg.Any<CancellationToken>()).Returns(XServerStartResult.Ok);
        engine.ResolveForwardingDisplayAsync(Arg.Any<CancellationToken>()).Returns(XServerDisplayResolution.None);
        return engine;
    }

    [TestMethod]
    public async Task Start_UsesTheConfiguredEngine()
    {
        ILocalXServer builtIn = Engine(), vcXsrv = Engine();

        await new LocalXServerSelector(Settings(XServerEngines.BuiltIn), builtIn, vcXsrv).StartAsync();
        await builtIn.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await vcXsrv.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());

        await new LocalXServerSelector(Settings(XServerEngines.VcXsrv), builtIn, vcXsrv).StartAsync();
        await vcXsrv.Received(1).StartAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>VcXsrv 不可用的平台上,设置里写着 vcxsrv(比如从 Windows 同步过来的配置)也按内置处理。</summary>
    [TestMethod]
    public async Task Start_VcXsrvUnsupported_FallsBackToBuiltIn()
    {
        ILocalXServer builtIn = Engine(), vcXsrv = Engine(supported: false);

        await new LocalXServerSelector(Settings(XServerEngines.VcXsrv), builtIn, vcXsrv).StartAsync();

        await builtIn.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await vcXsrv.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>引擎换了但另一个还在运行:按钮与 SSH 仍然用正在运行的那个,不去再起一个。</summary>
    [TestMethod]
    public async Task RunningEngine_WinsOverTheSetting()
    {
        ILocalXServer builtIn = Engine(), vcXsrv = Engine();
        vcXsrv.State.Returns(XServerState.Running);
        vcXsrv.Display.Returns("localhost:1.0");
        LocalXServerSelector selector = new(Settings(XServerEngines.BuiltIn), builtIn, vcXsrv);

        Assert.AreEqual("localhost:1.0", selector.Display);
        await selector.StartAsync();
        await selector.ResolveForwardingDisplayAsync();

        await vcXsrv.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await vcXsrv.Received(1).ResolveForwardingDisplayAsync(Arg.Any<CancellationToken>());
        await builtIn.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 会话开 shell 时绑了内置引擎的连接器;之后用户停掉内置引擎、改用 VcXsrv:老会话的新 x11 通道按本机 TCP 连到此刻在运行的那个,
    /// 原先连接器只认内置引擎,远端只看到 Failed to open display。
    /// </summary>
    [TestMethod]
    public async Task BuiltInConnector_FallsBackToTcp_WhenTheBuiltInEngineIsNoLongerRunning()
    {
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int display = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port - XServerCommandLine.TcpPort(0);

        ILocalXServer builtIn = Engine(), vcXsrv = Engine();
        builtIn.State.Returns(XServerState.Running);
        builtIn.ResolveForwardingDisplayAsync(Arg.Any<CancellationToken>()).Returns(new XServerDisplayResolution(
            "localhost:0.0", Connector: (_, _) => throw new InvalidOperationException("The built-in X server is not running.")));
        LocalXServerSelector selector = new(Settings(XServerEngines.BuiltIn), builtIn, vcXsrv);
        XServerDisplayResolution resolution = await selector.ResolveForwardingDisplayAsync();

        builtIn.State.Returns(XServerState.Stopped);   // 用户停掉内置引擎、开了 VcXsrv
        vcXsrv.State.Returns(XServerState.Running);
        vcXsrv.DisplayNumber.Returns(display);
        Task<System.Net.Sockets.TcpClient> accepted = listener.AcceptTcpClientAsync();
        await using Stream stream = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None);
        using System.Net.Sockets.TcpClient peer = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(peer.Connected, "连到了此刻在运行的 X 服务端的 6000+N");

        vcXsrv.State.Returns(XServerState.Stopped);   // 都没在运行:连解析时的显示地址,连不上按 IOException 报
        await Assert.ThrowsAsync<IOException>(async () => await LocalXServerSelector.ConnectTcpAsync(display + 1, CancellationToken.None));
    }

    /// <summary>
    /// 没有 VcXsrv 的平台(Linux / macOS):内置引擎停了,老会话的 x11 通道不退到本机 TCP —— 环回 6000+N 上不会是我们的服务端,
    /// 只可能是别的用户的、或者 sshd 自己的 X11 转发端口。按「本机显示连不上」报。
    /// </summary>
    [TestMethod]
    public async Task BuiltInConnector_DoesNotFallBackToTcp_WhereThereIsNoVcXsrv()
    {
        ILocalXServer builtIn = Engine(), vcXsrv = Engine(supported: false);
        builtIn.State.Returns(XServerState.Running);
        builtIn.ResolveForwardingDisplayAsync(Arg.Any<CancellationToken>()).Returns(new XServerDisplayResolution(
            ":10", Connector: (_, _) => throw new InvalidOperationException("The built-in X server is not running.")));
        LocalXServerSelector selector = new(Settings(XServerEngines.BuiltIn), builtIn, vcXsrv);
        XServerDisplayResolution resolution = await selector.ResolveForwardingDisplayAsync();
        builtIn.State.Returns(XServerState.Stopped);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None));
    }

    [TestMethod]
    public void StateChanged_IsForwardedFromBothEngines_AndOnEngineChange()
    {
        ILocalXServer builtIn = Engine(), vcXsrv = Engine();
        ISettingsService settings = Settings(XServerEngines.BuiltIn);
        LocalXServerSelector selector = new(settings, builtIn, vcXsrv);
        int raised = 0;
        selector.StateChanged += (_, _) => raised++;

        builtIn.StateChanged += Raise.Event();
        vcXsrv.StateChanged += Raise.Event();
        settings.SettingsSaved += Raise.Event<Action<AppSettings>>(new AppSettings { XServer = new XServerOptions { Engine = XServerEngines.VcXsrv } });

        Assert.AreEqual(3, raised);
        Assert.IsTrue(selector.IsSupported);
    }
}
