using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.XServer;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>标题栏的 X Server 按钮:停之前有 X 程序连着就先确认,说清会断开几个;没人连着直接停。</summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XServerToggleViewModelTests
{
    static XServerToggleViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    /// <summary>提示不会自动消失(用例里用不到计时)。</summary>
    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static ILocalXServer RunningServer(int clients)
    {
        ILocalXServer server = Substitute.For<ILocalXServer>();
        server.IsSupported.Returns(true);
        server.State.Returns(XServerState.Running);
        server.CountConnectedClientsAsync().Returns(clients);
        return server;
    }

    [TestMethod]
    public async Task Stop_WithConnectedPrograms_AsksFirst_AndKeepsRunningWhenDeclined()
    {
        ILocalXServer server = RunningServer(clients: 3);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        List<int> asked = [];
        bool answer = false;
        XServerToggleViewModel vm = new(server, toasts) { ConfirmStopAsync = n => { asked.Add(n); return Task.FromResult(answer); } };

        await vm.ToggleCommand.Execute().FirstAsync();
        Assert.AreSequenceEqual([3], asked.ToArray(), "说清会断开几个");
        await server.DidNotReceive().StopAsync();

        answer = true;
        await vm.ToggleCommand.Execute().FirstAsync();
        await server.Received(1).StopAsync();
    }

    [TestMethod]
    public async Task Stop_WithNoProgramsConnected_DoesNotAsk()
    {
        ILocalXServer server = RunningServer(clients: 0);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        bool asked = false;
        XServerToggleViewModel vm = new(server, toasts) { ConfirmStopAsync = _ => { asked = true; return Task.FromResult(false); } };

        await vm.ToggleCommand.Execute().FirstAsync();
        Assert.IsFalse(asked);
        await server.Received(1).StopAsync();
    }

    // ------------------------------------------------------------------ 浮层(F3):连着的 X 程序、断开、解除卡住

    private static ILocalXServer BuiltInRunning(params IReadOnlyList<XServerClient>[] snapshots)
    {
        ILocalXServer server = RunningServer(clients: snapshots.Length == 0 ? 0 : snapshots[0].Count);
        server.CanManageClients.Returns(true);
        server.Display.Returns("localhost:10.0");
        if (snapshots.Length > 0)
        {
            server.GetClientsAsync().Returns(snapshots[0], snapshots[1..]);
        }
        return server;
    }

    private static XServerClient Client(string key, int id, string name = "", string title = "", string? source = null,
        int windows = 1, long memory = 1024, bool grab = false) =>
        new(key, id, name, title, source, windows, memory, Retained: false, HoldsServerGrab: grab);

    /// <summary>浮层自己的刷新计时器不打扰用例:每一次刷新都由用例显式触发。</summary>
    private static XServerToggleViewModel PanelViewModel(ILocalXServer server, ToastHostViewModel toasts) =>
        new(server, toasts) { RefreshInterval = TimeSpan.FromHours(1) };

    [TestMethod]
    public async Task Button_WhileTheBuiltInEngineRuns_OpensThePanel_InsteadOfStopping()
    {
        ILocalXServer server = BuiltInRunning([Client("1:1", 1)]);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        XServerToggleViewModel vm = PanelViewModel(server, toasts);

        await vm.ButtonCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.IsPanelOpen, "原先一点就停,所有会话的 X 程序一起断开");
        Assert.AreEqual("localhost:10.0", vm.Display);
        await server.DidNotReceive().StopAsync();

        await vm.ButtonCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.IsPanelOpen, "再点收起");
    }

    [TestMethod]
    public async Task Button_WithAnEngineThatCannotListPrograms_StillStartsAndStops()
    {
        ILocalXServer server = RunningServer(clients: 0);   // VcXsrv:列不出程序
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        XServerToggleViewModel vm = new(server, toasts);

        await vm.ButtonCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.IsPanelOpen);
        await server.Received(1).StopAsync();
    }

    [TestMethod]
    public async Task Refresh_UpdatesRowsInPlace_AddsNewcomers_AndDropsLeavers()
    {
        ILocalXServer server = BuiltInRunning(
            [Client("1:1", 1, name: "XTerm", title: "user@box: ~", source: "user@box:22"), Client("1:2", 2)],
            [Client("1:2", 2, name: "Gedit", memory: 4 << 20, grab: true), Client("1:3", 3, title: "xclock")]);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        XServerToggleViewModel vm = PanelViewModel(server, toasts);

        await vm.RefreshClientsAsync();
        Assert.HasCount(2, vm.Clients);
        XServerClientItemViewModel xterm = vm.Clients[0], second = vm.Clients[1];
        Assert.AreEqual(("XTerm", "user@box: ~", "user@box:22"), (xterm.Name, xterm.Detail, xterm.Source));
        Assert.AreEqual(Core.Resources.Strings.Format("XServer_PanelUnnamed", 2), second.Name, "没有窗口、没写 WM_CLASS:按编号称呼");
        Assert.AreEqual(Core.Resources.Strings.Get("XServer_PanelLocal"), second.Source, "本机程序没有会话标签");
        Assert.IsTrue(vm.HasClients);

        await vm.RefreshClientsAsync();
        Assert.HasCount(2, vm.Clients);
        Assert.AreSame(second, vm.Clients[0], "同一个键就地更新,不重建(悬停时冒出来的按钮不会一闪就没)");
        Assert.AreEqual("Gedit", second.Name);
        Assert.IsTrue(second.HoldsServerGrab);
        Assert.AreEqual("xclock", vm.Clients[1].Name, "没写 WM_CLASS 时退到窗口标题");
    }

    [TestMethod]
    public async Task Disconnect_AsksFirst_ThenDisconnectsByKey()
    {
        ILocalXServer server = BuiltInRunning([Client("1:7", 7, name: "XTerm")], []);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        List<string> asked = [];
        bool answer = false;
        XServerToggleViewModel vm = PanelViewModel(server, toasts);
        vm.ConfirmDisconnectAsync = name => { asked.Add(name); return Task.FromResult(answer); };
        await vm.RefreshClientsAsync();
        XServerClientItemViewModel row = vm.Clients.Single();

        await vm.DisconnectCommand.Execute(row).FirstAsync();
        Assert.AreSequenceEqual(["XTerm"], asked.ToArray());
        server.DidNotReceive().DisconnectClient(Arg.Any<string>());

        answer = true;
        await vm.DisconnectCommand.Execute(row).FirstAsync();
        server.Received(1).DisconnectClient("1:7");
        Assert.IsFalse(vm.HasClients, "断开之后立即刷新");
    }

    [TestMethod]
    public async Task BreakGrabs_ReleasesThemThroughTheServer_AndSaysSo()
    {
        ILocalXServer server = BuiltInRunning([]);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        XServerToggleViewModel vm = PanelViewModel(server, toasts);

        await vm.BreakGrabsCommand.Execute().FirstAsync();
        server.Received(1).BreakGrabs();
        Assert.AreEqual(ToastSeverity.Info, toasts.Toasts.Single().Severity);
    }

    [TestMethod]
    public void GrabStall_WarnsWithADisconnectAction_AndUpdatesTheSameToastNextTime()
    {
        ILocalXServer server = BuiltInRunning();
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        XServerToggleViewModel vm = PanelViewModel(server, toasts);

        vm.ShowGrabStall(new XServerGrabStallNotice("1:4", 4, "XTerm", "user@stuck:22", TimeSpan.FromSeconds(10)));
        ToastViewModel toast = toasts.Toasts.Single();
        Assert.AreEqual(ToastSeverity.Warning, toast.Severity);
        StringAssert.Contains(toast.Message, "XTerm");
        StringAssert.Contains(toast.Message, "user@stuck:22");
        Assert.IsNotNull(toast.ActionLabel);

        vm.ShowGrabStall(new XServerGrabStallNotice("1:4", 4, "XTerm", "user@stuck:22", TimeSpan.FromSeconds(70)));
        Assert.AreSame(toast, toasts.Toasts.Single(), "同一个程序再报一次:就地更新那一条");
        StringAssert.Contains(toast.Message, "70");

        toast.Action!.Invoke();
        server.Received(1).DisconnectClient("1:4");
    }

    [TestMethod]
    public async Task Panel_ClosesAndEmpties_WhenTheServerStops()
    {
        ILocalXServer server = BuiltInRunning([Client("1:1", 1)]);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        XServerToggleViewModel vm = PanelViewModel(server, toasts);
        await vm.ButtonCommand.Execute().FirstAsync();
        await vm.RefreshClientsAsync();
        Assert.IsTrue(vm.IsPanelOpen && vm.HasClients);

        server.State.Returns(XServerState.Stopped);
        vm.RefreshLocalizedText();   // 与 StateChanged 走同一个 Refresh
        Assert.IsFalse(vm.IsPanelOpen);
        Assert.IsEmpty(vm.Clients);
        vm.IsPanelOpen = true;
        Assert.IsFalse(vm.IsPanelOpen, "没在运行时开不出来");
    }
}
