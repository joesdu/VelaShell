using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器的「启动时折叠分组」与「置顶连接」(#474)。
/// </summary>
/// <remarks>
/// 两件事挨在一起测是有原因的:置顶之所以做成「提到整棵树的最前」而不是「在组内上浮」,
/// 正是为了在分组全折上时还看得见 —— 分开测就测不到它们真正要一起成立的那条性质。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreePinAndCollapseTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();

    private ServerGroup _group = null!;

    /// <summary>摆一棵「Prod {alpha, zeta}」+ 根级 root-one 的树。</summary>
    private void StubTree()
    {
        _group = new() { Id = Guid.NewGuid(), Name = "Prod", SortOrder = 0 };
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { _group }));
        StubSessions(Session("alpha", _group.Id), Session("zeta", _group.Id), Session("root-one"));
    }

    /// <summary>替换会话集合(分组沿用 <see cref="StubTree" /> 建好的那个)。</summary>
    private void StubSessions(params SessionProfile[] sessions) =>
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(sessions.ToList()));

    private static SessionProfile Session(string name, Guid? groupId = null, bool pinned = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = $"{name}.example.com",
            Username = "admin",
            GroupId = groupId,
            IsPinned = pinned
        };

    private static ISettingsService Settings(bool collapse)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync()
                .Returns(_ => new AppSettings { General = { CollapseGroupsByDefault = collapse } });
        return settings;
    }

    private async Task<SessionTreeViewModel> LoadAsync(ISettingsService? settings = null)
    {
        SessionTreeViewModel vm = new(_repository, settings);
        await vm.LoadCommand.Execute().FirstAsync();
        return vm;
    }

    [TestMethod]
    public async Task WithoutTheSetting_GroupsStayExpanded()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));

        Assert.IsTrue(vm.Nodes[0].IsExpanded);
        Assert.HasCount(4, vm.Rows); // 分组 + 两个成员 + 根级会话
    }

    [TestMethod]
    public async Task CollapseOnStartup_LoadsGroupsCollapsed()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: true));

        Assert.IsFalse(vm.Nodes[0].IsExpanded);
        Assert.HasCount(2, vm.Rows); // 只剩分组行与根级会话
    }

    /// <summary>无头宿主/单测里没有设置服务:按展开处理,不能把树变成一堆折叠的空壳。</summary>
    [TestMethod]
    public async Task WithoutASettingsService_GroupsStayExpanded()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync();

        Assert.IsTrue(vm.Nodes[0].IsExpanded);
    }

    /// <summary>
    /// 手动展开之后重建树(新建/编辑连接、云同步都会触发),不能把刚展开的分组折回去。
    /// </summary>
    [TestMethod]
    public async Task ManualExpansion_SurvivesATreeRebuild()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: true));
        Assert.IsFalse(vm.Nodes[0].IsExpanded);

        vm.Nodes[0].IsExpanded = true;
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.Nodes[0].IsExpanded);
    }

    /// <summary>反过来也要记住:手动折叠过的分组,重建后不能又自己展开。</summary>
    [TestMethod]
    public async Task ManualCollapse_SurvivesATreeRebuild()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));

        vm.Nodes[0].IsExpanded = false;
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.IsFalse(vm.Nodes[0].IsExpanded);
    }

    [TestMethod]
    public async Task PinnedSession_IsHoistedToTheTopOfTheTree()
    {
        StubTree();
        StubSessions(Session("alpha", _group.Id), Session("root-one"), Session("pinned-one", null, pinned: true));
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));

        Assert.AreEqual("pinned-one", vm.Rows[0].Name);
        Assert.IsTrue(vm.Rows[0].IsPinned);

        // 节点在树里的位置没被动过:分组归属、拖放落点那些规则都还按原样成立。
        Assert.Contains(node => node.Id == vm.Rows[0].Id, vm.Nodes);
    }

    /// <summary>这条是整个设计的理由:分组折上了,置顶的连接照样在最前面看得见。</summary>
    [TestMethod]
    public async Task PinnedSessionInACollapsedGroup_IsStillVisible()
    {
        StubTree();
        StubSessions(Session("alpha", _group.Id), Session("zeta", _group.Id, pinned: true));
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: true));

        Assert.AreEqual("zeta", vm.Rows[0].Name);
        Assert.HasCount(2, vm.Rows); // 置顶行 + 折叠着的分组行
    }

    /// <summary>
    /// 置顶的组内会话只能在行里出现一次 —— 同一个实例摊两遍,选中与容器复用都会错乱。
    /// </summary>
    [TestMethod]
    public async Task PinnedSessionInAnExpandedGroup_AppearsExactlyOnce()
    {
        StubTree();
        StubSessions(Session("alpha", _group.Id), Session("zeta", _group.Id, pinned: true));
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));

        Assert.HasCount(1, vm.Rows.Where(row => row.Name == "zeta"));
        Assert.AreEqual("zeta", vm.Rows[0].Name);
        Assert.AreEqual("Prod", vm.Rows[1].Name);
        Assert.AreEqual("alpha", vm.Rows[2].Name);
    }

    /// <summary>置顶的会话被提到树顶显示,缩进得跟着显示位置走,否则它会比分组行更靠右。</summary>
    [TestMethod]
    public async Task PinnedGroupMember_RendersAtRootIndent()
    {
        StubTree();
        StubSessions(Session("zeta", _group.Id, pinned: true));
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));

        Assert.IsFalse(vm.Rows[0].IsRootLevel); // 数据事实:它仍属于 Prod
        Assert.IsTrue(vm.Rows[0].ShowsAtRootIndent); // 显示事实:按根级缩进画
    }

    [TestMethod]
    public async Task TogglePin_HoistsTheRowAndSavesTheProfile()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));
        SessionTreeNodeViewModel zeta = vm.Nodes[0].Children.First(node => node.Name == "zeta");
        vm.SelectedNode = zeta;

        await vm.TogglePinCommand.Execute().FirstAsync();

        Assert.IsTrue(zeta.IsPinned);
        Assert.AreEqual("zeta", vm.Rows[0].Name);
        await _repository.Received(1)
            .SaveSessionAsync(Arg.Is<SessionProfile>(p => p.Name == "zeta" && p.IsPinned));
    }

    /// <summary>取消置顶把会话放回它自己那一组,分组归属自始至终没动过。</summary>
    [TestMethod]
    public async Task Unpin_ReturnsTheRowToItsGroup()
    {
        StubTree();
        StubSessions(Session("alpha", _group.Id), Session("zeta", _group.Id, pinned: true));
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));
        vm.SelectedNode = vm.Rows[0];

        await vm.TogglePinCommand.Execute().FirstAsync();

        Assert.AreEqual("Prod", vm.Rows[0].Name);
        Assert.AreEqual("alpha", vm.Rows[1].Name);
        Assert.AreEqual("zeta", vm.Rows[2].Name);
        await _repository.Received(1)
            .SaveSessionAsync(Arg.Is<SessionProfile>(p =>
                p.Name == "zeta" && !p.IsPinned && p.GroupId == _group.Id));
    }

    /// <summary>分组行没有置顶这回事;命令对它不可用,免得在分组上右键出来一个死菜单项。</summary>
    [TestMethod]
    public async Task TogglePin_IsUnavailableForGroups()
    {
        StubTree();
        SessionTreeViewModel vm = await LoadAsync(Settings(collapse: false));
        vm.SelectedNode = vm.Nodes[0];

        Assert.IsFalse(await vm.TogglePinCommand.CanExecute.FirstAsync());
    }
}
