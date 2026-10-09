using System.Globalization;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器分组的操作(#571):整组打开、上移 / 下移与按名称排序(连同落库的排序序号)、
/// 导出 / 导入到这一组,以及「更多」菜单里的全部导出、全部折叠 / 展开。
/// </summary>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeGroupOperationsTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();
    private List<ServerGroup> _groups = null!;
    private SessionTreeViewModel _vm = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        // 序号故意不连续(删过分组、按个数取过号):重排之后要被重编成 0、1、2…
        _groups =
        [
            new() { Id = Guid.NewGuid(), Name = "机房 10", SortOrder = 0 },
            new() { Id = Guid.NewGuid(), Name = "机房 2", SortOrder = 4 },
            new() { Id = Guid.NewGuid(), Name = "Alpha", SortOrder = 9 }
        ];
        List<SessionProfile> sessions =
        [
            new() { Name = "web-2", Host = "10.0.0.2", GroupId = _groups[0].Id },
            new() { Name = "web-1", Host = "10.0.0.1", GroupId = _groups[0].Id },
            new() { Name = "db", Host = "10.0.1.1", GroupId = _groups[1].Id },
            new() { Name = "loose", Host = "10.0.2.1" }
        ];
        _repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(_groups));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(sessions));
        _vm = new SessionTreeViewModel(_repository);
        await _vm.LoadCommand.Execute().FirstAsync();
    }

    private List<string> GroupOrder() => [.. _vm.Nodes.Where(static n => n.IsGroup).Select(static n => n.Name)];

    private SessionTreeNodeViewModel Group(string name) => _vm.Nodes.Single(n => n.IsGroup && n.Name == name);

    [TestMethod]
    public async Task MoveGroupDown_ReordersTheTreeAndTheMenu_AndRenumbersEverySortOrder()
    {
        _vm.SelectedNode = Group("机房 10");

        await _vm.MoveGroupDownCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(new[] { "机房 2", "机房 10", "Alpha" }, GroupOrder().ToArray());
        Assert.AreSequenceEqual(new[] { "机房 2", "机房 10", "Alpha" },
            _vm.GroupNodes.Where(static n => n.Id != Guid.Empty).Select(static n => n.Name).ToArray(),
            "「移动到分组」子菜单跟着变");
        Assert.AreEqual(Guid.Empty, _vm.GroupNodes[^1].Id, "「未分组」落点一直在最后");
        Assert.AreSequenceEqual(new[] { 1, 0, 2 }, _groups.Select(static g => g.SortOrder).ToArray(),
            "落库的序号重编成连续的 0、1、2(原先是 0、4、9)");
        Assert.AreEqual(0, Group("机房 2").GroupColorIndex, "文件夹图标的轮换配色跟着位置走");
        Assert.IsTrue(_vm.Rows.IndexOf(Group("机房 2")) < _vm.Rows.IndexOf(Group("机房 10")), "界面绑的行也跟着变");
    }

    [TestMethod]
    public void MoveUpAndDown_AreDisabledAtTheEnds()
    {
        _vm.SelectedNode = Group("机房 10");
        Assert.IsFalse(_vm.CanMoveSelectedGroupUp);
        Assert.IsTrue(_vm.CanMoveSelectedGroupDown);

        _vm.SelectedNode = Group("Alpha");
        Assert.IsTrue(_vm.CanMoveSelectedGroupUp);
        Assert.IsFalse(_vm.CanMoveSelectedGroupDown);

        _vm.SelectedNode = _vm.Nodes.Single(static n => !n.IsGroup);
        Assert.IsFalse(_vm.CanMoveSelectedGroupUp, "选中的是会话,分组的上移 / 下移不可用");
    }

    [TestMethod]
    public async Task MoveGroupUp_ToTheTop_UpdatesWhetherItCanMoveFurther()
    {
        _vm.SelectedNode = Group("机房 2");
        bool? canMoveUp = null;
        using IDisposable _ = _vm.MoveGroupUpCommand.CanExecute.Subscribe(value => canMoveUp = value);

        await _vm.MoveGroupUpCommand.Execute().FirstAsync();

        Assert.AreEqual("机房 2", GroupOrder()[0]);
        Assert.IsFalse(canMoveUp, "挪到顶上之后「上移」立刻变灰,不用重新选一次");
    }

    [TestMethod]
    public async Task SortGroupsByName_UsesNaturalOrder()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");

            await _vm.SortGroupsByNameCommand.Execute().FirstAsync();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        List<string> order = GroupOrder();
        Assert.IsTrue(order.IndexOf("机房 2") < order.IndexOf("机房 10"), "数字按数值比:「机房 2」在「机房 10」前面");
        await _repository.ReceivedWithAnyArgs().SaveGroupAsync(default!);
        Assert.AreSequenceEqual(Enumerable.Range(0, 3).ToArray(),
            _groups.OrderBy(static g => g.SortOrder).Select(static g => g.SortOrder).ToArray());
    }

    [TestMethod]
    public async Task OpenGroup_RaisesItsConnectionsInTreeOrder()
    {
        _vm.SelectedNode = Group("机房 10");
        IReadOnlyList<SessionProfile>? raised = null;
        _vm.OpenManyRequested += profiles => raised = profiles;

        await _vm.OpenGroupCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(new[] { "web-1", "web-2" }, raised!.Select(static p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task OpenGroup_OnAnEmptyGroup_RaisesNothing()
    {
        _vm.SelectedNode = Group("Alpha");
        bool raised = false;
        _vm.OpenManyRequested += _ => raised = true;

        await _vm.OpenGroupCommand.Execute().FirstAsync();

        Assert.IsFalse(raised);
    }

    [TestMethod]
    public async Task ExportAndImport_FromTheGroupMenu_CarryTheGroup()
    {
        SessionTreeNodeViewModel group = Group("机房 10");
        _vm.SelectedNode = group;
        SessionExportRequest? exported = null;
        Guid? importTarget = Guid.Empty;
        _vm.ExportRequested += request => exported = request;
        _vm.ImportFileRequested += target => importTarget = target;

        await _vm.ExportGroupCommand.Execute().FirstAsync();
        await _vm.ImportIntoGroupCommand.Execute().FirstAsync();

        Assert.AreEqual("机房 10", exported!.GroupName);
        CollectionAssert.AreEquivalent(group.Children.Select(static c => c.Id).ToArray(), exported.ProfileIds.ToArray());
        Assert.AreEqual(group.Id, importTarget);
    }

    [TestMethod]
    public async Task ExportAll_And_ImportFile_FromTheMoreMenu()
    {
        SessionExportRequest? exported = null;
        Guid? importTarget = Guid.NewGuid();
        _vm.ExportRequested += request => exported = request;
        _vm.ImportFileRequested += target => importTarget = target;

        await _vm.ExportAllCommand.Execute().FirstAsync();
        await _vm.ImportFileCommand.Execute().FirstAsync();

        Assert.HasCount(4, exported!.ProfileIds);
        Assert.IsNull(exported.GroupName);
        Assert.IsNull(importTarget, "从「更多」菜单导入不指定分组:按文件里的分组放");
    }

    [TestMethod]
    public async Task ExportAll_IsDisabled_WhenThereAreNoConnections()
    {
        ISessionRepository empty = Substitute.For<ISessionRepository>();
        empty.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        empty.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new SessionTreeViewModel(empty);
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.IsFalse(await vm.ExportAllCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task CollapseAll_ThenExpandAll()
    {
        await _vm.CollapseAllCommand.Execute().FirstAsync();
        Assert.IsTrue(_vm.Nodes.Where(static n => n.IsGroup).All(static n => !n.IsExpanded));
        Assert.IsFalse(_vm.Rows.Any(row => !row.IsGroup && !row.IsRootLevel), "折叠后组内的会话都收起来了");

        await _vm.ExpandAllCommand.Execute().FirstAsync();
        Assert.IsTrue(_vm.Nodes.Where(static n => n.IsGroup).All(static n => n.IsExpanded));
    }

    // ———— 拖动分组的落点 ————

    [TestMethod]
    public void DropSlot_FollowsTheRowUnderThePointer()
    {
        // 分组:机房 10(web-1, web-2)/ 机房 2(db)/ Alpha(空);根级 loose。
        SessionTreeNodeViewModel webOne = Group("机房 10").Children.Single(c => c.Name == "web-1");
        SessionTreeNodeViewModel loose = _vm.Nodes.Single(static n => !n.IsGroup);

        Assert.AreEqual(1, _vm.ResolveGroupDropSlot(Group("机房 2"), upperHalf: true), "分组行上半截 = 插在它前面");
        Assert.AreEqual(2, _vm.ResolveGroupDropSlot(Group("机房 2"), upperHalf: false), "下半截 = 插在它后面");
        Assert.AreEqual(1, _vm.ResolveGroupDropSlot(webOne, upperHalf: true), "组内的会话行 = 这一组的后面");
        Assert.AreEqual(3, _vm.ResolveGroupDropSlot(loose, upperHalf: true), "根级会话 = 最后");
        Assert.AreEqual(3, _vm.ResolveGroupDropSlot(null, upperHalf: false), "空白处 = 最后");
    }

    [TestMethod]
    public void DropIndex_IgnoresDropsBackInPlace_AndAccountsForTheGapItLeaves()
    {
        Guid first = Group("机房 10").Id;
        Guid last = Group("Alpha").Id;

        Assert.IsNull(_vm.GroupDropIndex(first, 0), "插回自己前面 = 原地");
        Assert.IsNull(_vm.GroupDropIndex(first, 1), "插回自己后面 = 原地");
        Assert.AreEqual(1, _vm.GroupDropIndex(first, 2), "挪到第二组后面:自己让出的那一格要扣掉");
        Assert.AreEqual(2, _vm.GroupDropIndex(first, 3));
        Assert.AreEqual(0, _vm.GroupDropIndex(last, 0));
        Assert.IsNull(_vm.GroupDropIndex(Guid.NewGuid(), 0));
    }

    [TestMethod]
    public void DropSlot_Description_NamesTheGroupItGoesBefore()
    {
        Assert.Contains("机房 2", _vm.DescribeGroupDropSlot(1));
        Assert.AreNotEqual(_vm.DescribeGroupDropSlot(1), _vm.DescribeGroupDropSlot(3));
    }

    [TestMethod]
    public async Task SaveCsvTemplate_RaisesTheRequest()
    {
        bool raised = false;
        _vm.CsvTemplateRequested += () => raised = true;

        await _vm.SaveCsvTemplateCommand.Execute().FirstAsync();

        Assert.IsTrue(raised);
    }
}
