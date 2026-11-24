using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 整组批量下发命令:打开这一组(或多选的几条)并把开出来的终端拉进同一个同步输入频道。
/// </summary>
/// <remarks>
/// 握手在这里一律失败(连接被拒):标签照样留在原地显示失败覆盖层(设计 yxjmg),也照样入频道 ——
/// 之后重连上来就收得到。要测的是挑频道、只开终端、不动已有标签、频道全满时一个都不开。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public sealed class OpenGroupInSyncChannelTests
{
    private static SessionProfile Ssh(string name) =>
        new() { Name = name, Host = $"{name}.example.com", Username = "root", AuthMethod = AuthMethod.Password, Password = "pw" };

    private static MainWindowViewModel Refusing()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new IOException("connection refused"));
        return new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator());
    }

    [TestMethod]
    public async Task OpensNewTabs_AndPutsThemAllInTheFirstFreeChannel()
    {
        MainWindowViewModel vm = Refusing();
        // 频道 A 已经有人在用:这一批不能混进去,否则命令会打到不相干的机器上。
        var busy = new TerminalTabViewModel(FakeTerminal.Emulator()) { ConnectionStatus = SessionStatus.Connected };
        vm.Layout.AddDocument(new TerminalDocument(busy));
        busy.JoinSyncChannel(SyncInputChannel.A);

        SyncInputChannel? channel = await vm.OpenProfilesInSyncChannelAsync([Ssh("web-1"), Ssh("web-2"), Ssh("web-3")]);

        Assert.AreEqual(SyncInputChannel.B, channel);
        List<TerminalTabViewModel> opened = [.. vm.TerminalTabs.Where(tab => !ReferenceEquals(tab, busy))];
        Assert.HasCount(3, opened, "每条连接各开一个新标签");
        Assert.IsTrue(opened.All(static tab => tab.SyncChannel == SyncInputChannel.B));
        Assert.AreEqual(SyncInputChannel.A, busy.SyncChannel, "已经开着的标签不动");
        // 浮层按新旧排,失败提示与汇总都在里面;汇总是那条说了频道的。
        string summary = vm.Toasts.Toasts.Select(static t => t.Message).Single(m => m.Contains(Strings.Format("Tree_SyncedDone", 3, SyncInputChannel.B), StringComparison.Ordinal));
        Assert.Contains(Strings.Format("Tree_SyncedDone", 3, SyncInputChannel.B), summary);
        Assert.Contains(Strings.Format("Tree_SyncedNotConnected", 3), summary, "没连上的要说出来:握手期间敲的命令它们收不到");
    }

    [TestMethod]
    public async Task OnlyTerminalsAreOpened()
    {
        MainWindowViewModel vm = Refusing();
        var sftp = new SessionProfile { Name = "files", Host = "f", ConnectionType = ConnectionType.SFTP };
        var ftp = new SessionProfile { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP };

        SyncInputChannel? channel = await vm.OpenProfilesInSyncChannelAsync([Ssh("web-1"), sftp, ftp]);

        Assert.AreEqual(SyncInputChannel.A, channel);
        Assert.HasCount(1, vm.TerminalTabs);
        Assert.HasCount(1, vm.Layout.AllDocuments().ToList(), "SFTP / FTP 开出来的是文档,进不了频道,这一批里干脆不开");
        Assert.Contains(m => m.Contains(Strings.Format("Tree_SyncedSkipped", 2), StringComparison.Ordinal), vm.Toasts.Toasts.Select(static t => t.Message));
    }

    [TestMethod]
    public async Task NothingToSync_OpensNothing()
    {
        MainWindowViewModel vm = Refusing();

        SyncInputChannel? channel = await vm.OpenProfilesInSyncChannelAsync(
            [new SessionProfile { Name = "files", Host = "f", ConnectionType = ConnectionType.SFTP }]);

        Assert.IsNull(channel);
        Assert.IsEmpty(vm.Layout.AllDocuments().ToList());
        Assert.AreEqual(Strings.Get("Tree_SyncedNoTerminals"), vm.Toasts.Toasts.Single().Message);
    }

    [TestMethod]
    public async Task AllChannelsBusy_OpensNothing()
    {
        MainWindowViewModel vm = Refusing();
        foreach (SyncInputChannel busyChannel in Enum.GetValues<SyncInputChannel>())
        {
            var tab = new TerminalTabViewModel(FakeTerminal.Emulator()) { ConnectionStatus = SessionStatus.Connected };
            vm.Layout.AddDocument(new TerminalDocument(tab));
            tab.JoinSyncChannel(busyChannel);
        }

        SyncInputChannel? channel = await vm.OpenProfilesInSyncChannelAsync([Ssh("web-1")]);

        Assert.IsNull(channel);
        Assert.HasCount(6, vm.TerminalTabs, "一个都不开");
        Assert.AreEqual(Strings.Get("Tree_SyncedNoFreeChannel"), vm.Toasts.Toasts.Single().Message);
    }

    [TestMethod]
    public async Task TreeMenus_RaiseTheSyncedRequest()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        var group = new ServerGroup { Id = Guid.NewGuid(), Name = "web", SortOrder = 0 };
        List<SessionProfile> sessions =
        [
            new() { Id = Guid.NewGuid(), Name = "web-1", Host = "a", GroupId = group.Id },
            new() { Id = Guid.NewGuid(), Name = "web-2", Host = "b", GroupId = group.Id },
            new() { Id = Guid.NewGuid(), Name = "db", Host = "c" }
        ];
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { group }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(sessions));
        var tree = new SessionTreeViewModel(repository);
        await tree.LoadCommand.Execute().FirstAsync();
        List<IReadOnlyList<SessionProfile>> requests = [];
        tree.OpenManyInSyncChannelRequested += requests.Add;
        bool plainOpen = false;
        tree.OpenManyRequested += _ => plainOpen = true;

        tree.SelectedNode = tree.Nodes.Single(static n => n.IsGroup);
        await tree.OpenGroupInSyncChannelCommand.Execute().FirstAsync();
        tree.SelectedNode = tree.Rows.Single(static r => r.Name == "web-2");
        tree.ToggleMultiSelection(tree.Rows.Single(static r => r.Name == "db"));
        await tree.OpenSelectedInSyncChannelCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(["web-1", "web-2"], [.. requests[0].Select(static p => p.Name)], "整组");
        Assert.AreSequenceEqual(["web-2", "db"], [.. requests[1].Select(static p => p.Name)], "多选,按树上的顺序");
        Assert.IsFalse(plainOpen, "与「打开全部连接」是两条路");
        Assert.AreEqual(Strings.Format("Tree_OpenSelectedSynced", 2), tree.OpenSelectedInSyncChannelText);
    }
}
