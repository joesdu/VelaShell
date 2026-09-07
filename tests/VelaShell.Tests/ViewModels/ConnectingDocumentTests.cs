using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 文档型连接(独立 SFTP、FTP、插件协议、插件工作台)在握手完成前必须**已经有一个标签**。
/// <para>
/// 这四条路径原先都是连上了才 <c>AddDocument</c>,链路一慢用户点完之后屏幕上什么都不会
/// 发生 —— 没有标签、没有圆点、右下角也没有转圈,看起来就是「点了没反应」(#385 反馈)。
/// 这组用例钉住三件事:占位标签在连接**期间**就在场、连上之后**原位**换成真文档、
/// 以及这段时间右下角后台活动圆环真的在转。
/// </para>
/// </summary>
[TestClass]
public sealed class ConnectingDocumentTests
{
    private const string WorkspaceId = "acme.cache";

    /// <summary>握手挂在一个由用例控制的信号上,好在"连接中"这一帧上做断言。</summary>
    private sealed class GatedWorkspaceProvider(TaskCompletionSource<IWorkspaceDocument> gate)
        : IWorkspaceProvider
    {
        /// <summary>已经收到过几次握手请求。</summary>
        public int Opens { get; private set; }

        /// <summary>最后一次握手带进来的取消令牌,用于验证「取消」真的传下去了。</summary>
        public CancellationToken LastToken { get; private set; }

        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            Opens++;
            LastToken = cancellationToken;
            // 取消令牌一响就让这次握手以取消收场(真实插件同理)。
            cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            return gate.Task;
        }
    }

    /// <summary>连上之后交出来的空文档:这组用例只关心标签的更替,不关心内容。</summary>
    private sealed class StubWorkspaceDocument : IWorkspaceDocument
    {
        public WorkspaceStatus Status { get; } = new(ProtocolSessionState.Connected);

        /// <summary>这份替身从不改状态,事件只为满足接口。</summary>
        public event EventHandler<WorkspaceStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public object CreateView() => new();

        public Task ReconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (MainWindowViewModel Vm, SessionProfile Profile, GatedWorkspaceProvider Provider)
        Arrange(TaskCompletionSource<IWorkspaceDocument> gate, IBackgroundActivityService? activity = null)
    {
        var registry = new PluginProtocolRegistry();
        var provider = new GatedWorkspaceProvider(gate);
        registry.RegisterWorkspace(
            WorkspaceId,
            new()
            {
                Id = WorkspaceId,
                DisplayName = "Acme Cache",
                DefaultPort = 6379,
                // 匿名可连:否则用例测到的是弹凭据框,而不是占位标签。
                Features = WorkspaceFeatures.AnonymousAccess
            },
            provider);
        var vm = new MainWindowViewModel(
            protocolRegistry: registry,
            workspaceLauncher: new PluginWorkspaceLauncher(registry),
            backgroundActivity: activity);
        return (vm, new()
        {
            Name = "本地 Redis",
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = WorkspaceId,
            Host = "127.0.0.1",
            Port = 6379
        }, provider);
    }

    private static ConnectingDocument SoleConnectingDocument(MainWindowViewModel vm)
    {
        ConnectingDocument[] placeholders = [.. vm.Layout.AllDocuments().OfType<ConnectingDocument>()];
        Assert.HasCount(1, placeholders);
        return placeholders[0];
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task WhileConnecting_ThePlaceholderTabIsAlreadyThere()
    {
        var gate = new TaskCompletionSource<IWorkspaceDocument>();
        (MainWindowViewModel vm, SessionProfile profile, _) = Arrange(gate);

        Task<PluginWorkspaceDocument?> opening = vm.OpenWorkspaceDocumentForProfileAsync(profile);

        ConnectingDocument placeholder = SoleConnectingDocument(vm);
        Assert.IsTrue(placeholder.IsConnecting, "握手还没回来时标签是「连接中」态。");
        Assert.IsFalse(placeholder.HasError);
        Assert.AreEqual("本地 Redis", placeholder.Title);
        Assert.AreEqual("Acme Cache", placeholder.TypeLabel, "协议解析完要把类型名换成展示名。");
        Assert.AreSame(placeholder, vm.Layout.ActiveDocument, "新开的连接就是当前标签。");

        gate.SetResult(new StubWorkspaceDocument());
        Assert.IsNotNull(await opening);
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task OnceConnected_TheRealDocumentTakesThePlaceholderSlot()
    {
        var gate = new TaskCompletionSource<IWorkspaceDocument>();
        (MainWindowViewModel vm, SessionProfile profile, _) = Arrange(gate);

        // 先占一个位:占位标签会排在它后面,换手之后真文档必须还在同一格。
        var first = new ConnectingDocument(new() { Name = "先来的", Host = "first.example" }, "SFTP");
        vm.Layout.AddDocument(first);

        Task<PluginWorkspaceDocument?> opening = vm.OpenWorkspaceDocumentForProfileAsync(profile);
        ConnectingDocument placeholder = vm.Layout.AllDocuments()
            .OfType<ConnectingDocument>()
            .Single(d => !ReferenceEquals(d, first));
        DockGroup group = vm.Layout.FindGroup(placeholder)!;
        int slot = group.Documents.IndexOf(placeholder);
        Assert.AreEqual(1, slot, "占位标签排在先来的那个后面。");

        gate.SetResult(new StubWorkspaceDocument());
        PluginWorkspaceDocument? document = await opening;

        Assert.IsNotNull(document);
        Assert.DoesNotContain(placeholder, vm.Layout.AllDocuments(),
            "连上之后占位标签就该退场,不能与真文档并存。");
        Assert.AreSame(document, group.Documents[slot], "真文档接手占位标签原来那一格,不许跳到最右边。");
        Assert.AreSame(document, vm.Layout.ActiveDocument, "激活状态一并交接。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task ClosingThePlaceholder_CancelsTheHandshakeAndTakesTheTabWithIt()
    {
        var gate = new TaskCompletionSource<IWorkspaceDocument>();
        (MainWindowViewModel vm, SessionProfile profile, GatedWorkspaceProvider provider) = Arrange(gate);

        Task<PluginWorkspaceDocument?> opening = vm.OpenWorkspaceDocumentForProfileAsync(profile);
        ConnectingDocument placeholder = SoleConnectingDocument(vm);

        // 覆盖层上的「取消」与标签上的 × 走同一条路:关掉这个占位标签。
        vm.Layout.RequestClose(placeholder);

        Assert.IsNull(await opening);
        Assert.IsTrue(provider.LastToken.IsCancellationRequested, "取消要一路传到插件的握手里。");
        Assert.IsEmpty(vm.Layout.AllDocuments(), "取消之后不留空壳标签。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task WhileConnecting_TheBackgroundActivityRingIsSpinning()
    {
        var activity = new BackgroundActivityService();
        var gate = new TaskCompletionSource<IWorkspaceDocument>();
        (MainWindowViewModel vm, SessionProfile profile, _) = Arrange(gate, activity);

        Task<PluginWorkspaceDocument?> opening = vm.OpenWorkspaceDocumentForProfileAsync(profile);

        Assert.HasCount(1, activity.Activities);
        Assert.AreEqual("本地 Redis", activity.Activities[0].Detail);
        // 账本 → 状态栏这一跳在真实运行时由 Dispatcher.Post 完成(见 WireBackgroundActivity),
        // 无界面用例里没有调度器,所以这里直接喂一次,验证的是"有活动 ⇒ 圆环可见"这条映射。
        vm.StatusBar.ApplyBackgroundActivities(activity.Activities);
        Assert.IsTrue(vm.StatusBar.HasBackgroundActivity, "右下角圆环这段时间必须是可见的。");

        gate.SetResult(new StubWorkspaceDocument());
        Assert.IsNotNull(await opening);

        Assert.IsEmpty(activity.Activities, "连上之后这条活动要销账,圆环不能一直转。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ReplaceDocument_KeepsTheSlotAndTheActiveTabOfTheOneItReplaces()
    {
        var workspace = new DockWorkspace();
        var left = new ConnectingDocument(new() { Name = "左", Host = "a" }, "SFTP");
        var middle = new ConnectingDocument(new() { Name = "中", Host = "b" }, "SFTP");
        var right = new ConnectingDocument(new() { Name = "右", Host = "c" }, "SFTP");
        workspace.AddDocument(left);
        workspace.AddDocument(middle);
        workspace.AddDocument(right);
        // 用户在等连接时切去了别的标签:换手不该把焦点抢回来。
        workspace.ActivateDocument(left);
        var replacement = new ConnectingDocument(new() { Name = "换上来的", Host = "d" }, "FTP");
        var removed = new List<DockDocument>();
        var added = new List<DockDocument>();
        workspace.DocumentRemoved += removed.Add;
        workspace.DocumentAdded += added.Add;

        workspace.ReplaceDocument(middle, replacement);

        Assert.AreEqual(1, workspace.PrimaryGroup.Documents.IndexOf(replacement));
        Assert.DoesNotContain(middle, workspace.PrimaryGroup.Documents);
        Assert.AreSame(left, workspace.ActiveDocument, "被换掉的不是活动标签,焦点就不该动。");
        // 视图层的内容控件缓存靠这两条事件收放,漏一条就是一个再也回收不掉的旧视图。
        Assert.AreSequenceEqual([middle], removed);
        Assert.AreSequenceEqual([replacement], added);
    }
}
