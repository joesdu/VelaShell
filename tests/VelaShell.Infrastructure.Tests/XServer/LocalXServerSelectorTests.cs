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
