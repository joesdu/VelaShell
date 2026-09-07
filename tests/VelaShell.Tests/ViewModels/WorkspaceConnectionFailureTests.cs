using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 工作台连接(Redis 等由插件全权渲染界面的类型)连不上时**必须有提示**。
/// <para>
/// 这条路径原先与 SSH 有个关键差别:标签页要连上才建,连不上就没有标签,失败没有地方可画,
/// 于是只能弹一扇模态框。#385 之后标签在握手**开始前**就建好(<see cref="ConnectingDocument" />),
/// 失败就落回它自己那个标签里 —— 与终端标签页内的失败覆盖层同一条纪律(设计 yxjmg),
/// 不再拿模态框挡住用户手上正在做的别的事。
/// </para>
/// <para>这组用例守的就是:失败**有地方看**,而且看的是那个标签,不是一扇框。</para>
/// </summary>
[TestClass]
public sealed class WorkspaceConnectionFailureTests
{
    private const string WorkspaceId = "acme.cache";

    /// <summary>连接必失败的工作台:一次握手都不放过去。</summary>
    private sealed class FailingWorkspaceProvider(Exception failure) : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IWorkspaceDocument>(failure);
    }

    private static (MainWindowViewModel Vm, SessionProfile Profile) Arrange(Exception failure)
    {
        var registry = new PluginProtocolRegistry();
        registry.RegisterWorkspace(
            WorkspaceId,
            new()
            {
                Id = WorkspaceId,
                DisplayName = "Acme Cache",
                DefaultPort = 6379,
                // 匿名可连:与 Redis 一样,不填口令不该弹登录框 —— 否则这组用例
                // 测到的是弹凭据,而不是失败提示。
                Features = WorkspaceFeatures.AnonymousAccess
            },
            new FailingWorkspaceProvider(failure));
        var vm = new MainWindowViewModel(
            protocolRegistry: registry,
            workspaceLauncher: new PluginWorkspaceLauncher(registry));
        return (vm, new()
        {
            Name = "本地 Redis",
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = WorkspaceId,
            Host = "127.0.0.1",
            Port = 6379
        });
    }

    /// <summary>
    /// 端点不可达(本机没起 Redis):不开工作台文档,失败留在那个「连接中」占位标签上。
    /// </summary>
    [TestMethod]
    public async Task ConnectionRefused_LeavesTheFailureOnItsOwnTab()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolConnectionException("连不上 127.0.0.1:6379:Connection refused"));
        var reported = new List<string>();
        vm.ConnectionFailureReporter = (_, message) =>
        {
            reported.Add(message);
            return Task.CompletedTask;
        };

        PluginWorkspaceDocument? document = await vm.OpenWorkspaceDocumentForProfileAsync(profile);

        Assert.IsNull(document);
        // 有地方可画之后就不再弹模态框(与终端标签同口径)。
        Assert.IsEmpty(reported);
        ConnectingDocument placeholder = SoleConnectingDocument(vm);
        Assert.IsTrue(placeholder.HasError, "连不上之后占位标签要转成失败卡片。");
        Assert.Contains("Connection refused", placeholder.OverlayDetail);
        // 匿名连接(Redis 常态)不该拼出 "@127.0.0.1:6379" 这种前面缺了一截的目标。
        Assert.Contains("127.0.0.1:6379", placeholder.OverlayDetail);
        Assert.DoesNotContain("@127.0.0.1", placeholder.OverlayDetail);
        // 状态栏与 LastConnectionError 仍是同一条消息:插件代开会话那条路径靠它区分
        // "没连上"与"人不同意"(见 HostSessionOpener)。
        Assert.AreEqual(placeholder.OverlayDetail, vm.LastConnectionError);
    }

    /// <summary>取出工作区里唯一那个「连接中」占位标签。</summary>
    private static ConnectingDocument SoleConnectingDocument(MainWindowViewModel vm)
    {
        ConnectingDocument[] placeholders = [.. vm.Layout.AllDocuments().OfType<ConnectingDocument>()];
        Assert.HasCount(1, placeholders);
        return placeholders[0];
    }

    /// <summary>
    /// 没挂提示钩子(headless 单测、插件代开会话)时连接流程照旧,只是不弹框。
    /// </summary>
    [TestMethod]
    public async Task WithoutAReporter_TheFailureStillLandsOnLastConnectionError()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolConnectionException("端点不可达"));

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.IsNotNull(vm.LastConnectionError);
        Assert.Contains("端点不可达", vm.LastConnectionError);
    }

    /// <summary>
    /// 三次凭据都没过之后,循环走完了最后一条原因也要落到标签上 —— 否则用户对着密码框
    /// 输三遍,得到的是一片安静。
    /// </summary>
    [TestMethod]
    public async Task ExhaustedAuthenticationRetries_LeavesTheLastFailureOnItsOwnTab()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolAuthenticationException("WRONGPASS 口令不对"));
        profile.Username = "default";
        int prompts = 0;
        vm.InteractiveAuthenticator = candidate =>
        {
            prompts++;
            candidate.Password = "nope";
            return Task.FromResult<SessionProfile?>(candidate);
        };

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.AreEqual(3, prompts);
        ConnectingDocument placeholder = SoleConnectingDocument(vm);
        Assert.IsTrue(placeholder.HasError);
        Assert.Contains("WRONGPASS", placeholder.OverlayDetail);
    }

    /// <summary>
    /// 用户在凭据框上点取消是"不连了",不是失败:不弹提示,并且要把上一条错误清掉
    /// —— <c>HostSessionOpener</c> 正是靠 <c>LastConnectionError</c> 为空来区分这两者的。
    /// </summary>
    [TestMethod]
    public async Task CancelledCredentialPrompt_ReportsNothing()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolConnectionException("不该走到这里"));
        profile.Username = "default";
        vm.InteractiveAuthenticator = _ => Task.FromResult<SessionProfile?>(null);
        int reports = 0;
        vm.ConnectionFailureReporter = (_, _) =>
        {
            reports++;
            return Task.CompletedTask;
        };

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.AreEqual(0, reports);
        Assert.IsNull(vm.LastConnectionError);
        // "不连了"也要把占位标签收走 —— 留下一个空壳等于用户取消了个寂寞。
        Assert.IsEmpty(vm.Layout.AllDocuments());
    }
}
