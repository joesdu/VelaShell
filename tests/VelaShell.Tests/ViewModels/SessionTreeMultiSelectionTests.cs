using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 资源管理器的多选(#571,由 #524 的 Ctrl 双选扩展而来):Ctrl 加选 / 减选、Shift 区间、它何时结束,
/// 以及恰好两条时的「在双栏 SFTP 中打开」。
/// </summary>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeMultiSelectionTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();
    private SessionTreeViewModel _vm = null!;
    private SessionProfile _alpha = null!;
    private SessionProfile _beta = null!;
    private SessionProfile _gamma = null!;
    private SessionProfile _bucket = null!;
    private SessionProfile _loose = null!;
    private ServerGroup _group = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        _group = new ServerGroup { Id = Guid.NewGuid(), Name = "Prod", SortOrder = 0 };
        _alpha = Profile("alpha", ConnectionType.SSH, _group.Id);
        _beta = Profile("beta", ConnectionType.SFTP, _group.Id);
        _gamma = Profile("gamma", ConnectionType.FTP, _group.Id);
        _bucket = Profile("bucket", ConnectionType.Plugin, _group.Id);
        _loose = Profile("loose", ConnectionType.SSH, null);
        _repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { _group }));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { _alpha, _beta, _gamma, _bucket, _loose }));
        _vm = new SessionTreeViewModel(_repository);
        await _vm.LoadCommand.Execute().FirstAsync();
    }

    private static SessionProfile Profile(string name, ConnectionType type, Guid? groupId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Username = "admin",
        ConnectionType = type,
        GroupId = groupId,
    };

    private SessionTreeNodeViewModel Node(SessionProfile profile) =>
        _vm.Rows.Single(n => !n.IsGroup && n.Id == profile.Id);

    private SessionTreeNodeViewModel GroupNode => _vm.Nodes.Single(n => n.IsGroup);

    // ———— Ctrl ————

    [TestMethod]
    public void CtrlClick_AfterAPlainSelection_PairsTheTwo_FirstOneFirst()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ToggleMultiSelection(Node(_beta));

        Assert.AreSequenceEqual([Node(_alpha), Node(_beta)], _vm.MultiSelection.ToArray());
        Assert.IsTrue(Node(_alpha).IsMultiMarked);
        Assert.IsTrue(Node(_beta).IsMultiMarked);
        Assert.AreSame(Node(_beta), _vm.SelectedNode, "选中项落在最后点的那条上。");
        Assert.IsTrue(_vm.IsPairSelected);
        Assert.IsTrue(_vm.CanOpenDualSelection);
    }

    [TestMethod]
    public void CtrlClick_AThirdOne_IsAddedToo_NothingIsPushedOut()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));

        _vm.ToggleMultiSelection(Node(_gamma));

        Assert.AreSequenceEqual([Node(_alpha), Node(_beta), Node(_gamma)], _vm.MultiSelection.ToArray(),
            "#524 时第三条会把最早那条顶掉;多选之后一直往里加");
        Assert.IsTrue(_vm.HasMultiSelection);
        Assert.IsFalse(_vm.IsPairSelected);
        Assert.IsFalse(_vm.CanOpenDualSelection, "双栏只收恰好两条");
        Assert.IsFalse(_vm.ShowDualSftpUnsupportedHint, "三条时双栏那一项整个不出现,不需要解释为什么灰");
    }

    [TestMethod]
    public void CtrlClick_OneOfThePair_FallsBackToAPlainSelectionOfTheOther()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));

        _vm.ToggleMultiSelection(Node(_beta));

        Assert.IsEmpty(_vm.MultiSelection);
        Assert.IsFalse(Node(_beta).IsMultiMarked);
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    [TestMethod]
    public void CtrlClick_TheOnlySelectedRow_DeselectsIt()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ToggleMultiSelection(Node(_alpha));

        Assert.IsEmpty(_vm.MultiSelection);
        Assert.IsNull(_vm.SelectedNode);
    }

    [TestMethod]
    public void CtrlClick_WithNothingSelected_JustSelectsTheRow()
    {
        _vm.ToggleMultiSelection(Node(_alpha));

        Assert.IsEmpty(_vm.MultiSelection, "一条不成多选,就是普通单选。");
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    [TestMethod]
    public void CtrlClick_AGroupRow_IsIgnored()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ToggleMultiSelection(GroupNode);

        Assert.IsEmpty(_vm.MultiSelection);
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    // ———— Shift ————

    [TestMethod]
    public void ShiftClick_SelectsTheVisibleRange_SkippingGroupRows()
    {
        // 行序:Prod 组(alpha / beta / bucket / gamma,组内按名称)→ 根级 loose。
        _vm.SelectedNode = Node(_beta);

        _vm.ExtendSelectionTo(Node(_loose), additive: false);

        Assert.AreSequenceEqual([Node(_beta), Node(_bucket), Node(_gamma), Node(_loose)], _vm.MultiSelection.ToArray());
        Assert.AreSame(Node(_loose), _vm.SelectedNode);
        Assert.IsFalse(GroupNode.IsMultiMarked);
    }

    [TestMethod]
    public void ShiftClick_Upwards_KeepsTheAnchorFirst()
    {
        _vm.SelectedNode = Node(_gamma);

        _vm.ExtendSelectionTo(Node(_alpha), additive: false);

        Assert.AreSequenceEqual([Node(_gamma), Node(_bucket), Node(_beta), Node(_alpha)], _vm.MultiSelection.ToArray(),
            "起点在前:从哪一行开始选的,那一行就是「先选的」");
    }

    [TestMethod]
    public void ShiftClick_Again_ReplacesTheRange_FromTheSameAnchor()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ExtendSelectionTo(Node(_gamma), additive: false);

        _vm.ExtendSelectionTo(Node(_beta), additive: false);

        Assert.AreSequenceEqual([Node(_alpha), Node(_beta)], _vm.MultiSelection.ToArray(), "Shift 再点一次是换一个终点,起点不动");
    }

    [TestMethod]
    public void CtrlShiftClick_AddsTheRangeToTheExistingSelection()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_loose));

        _vm.ExtendSelectionTo(Node(_gamma), additive: true);

        CollectionAssert.AreEquivalent(new[] { Node(_alpha), Node(_loose), Node(_gamma) }, _vm.MultiSelection.ToArray(),
            "起点是最近一次 Ctrl 加选的 loose,往上选到 gamma;原先的 alpha 留着");
    }

    [TestMethod]
    public void PlainClick_OnTheAlreadySelectedRow_MovesTheShiftAnchorThere()
    {
        // 代码评审指出:单击的正好是已经选中的那一行时列表不再赋值,起点若只靠 SelectedNode 的 setter 就挪不动。
        _vm.SelectedNode = Node(_alpha);
        _vm.ExtendSelectionTo(Node(_gamma), additive: false);
        Assert.AreSame(Node(_gamma), _vm.SelectedNode);

        _vm.StartSelectionAt(Node(_gamma)); // 视图在普通单击时调它
        _vm.ExtendSelectionTo(Node(_loose), additive: false);

        Assert.AreSequenceEqual([Node(_gamma), Node(_loose)], _vm.MultiSelection.ToArray(), "从刚单击的 gamma 选起,而不是更早的 alpha");
    }

    [TestMethod]
    public void ShiftClick_OnTheAnchorItself_IsAPlainSelection()
    {
        _vm.SelectedNode = Node(_alpha);

        _vm.ExtendSelectionTo(Node(_alpha), additive: false);

        Assert.IsEmpty(_vm.MultiSelection);
        Assert.AreSame(Node(_alpha), _vm.SelectedNode);
    }

    // ———— 结束 ————

    [TestMethod]
    public void SelectingARowOutsideTheSelection_EndsIt()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));

        _vm.SelectedNode = Node(_gamma);

        Assert.IsEmpty(_vm.MultiSelection);
        Assert.IsFalse(Node(_alpha).IsMultiMarked);
        Assert.IsFalse(Node(_beta).IsMultiMarked);
    }

    [TestMethod]
    public void CollapsingTheGroupOfTheSelectedRow_EndsTheSelection()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));

        GroupNode.IsExpanded = false;

        Assert.IsEmpty(_vm.MultiSelection, "收进折叠分组里看不见的行不该还算选中。");
    }

    [TestMethod]
    public void CollapsingAnotherGroup_OnlyDropsTheRowsItHides()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));
        _vm.ToggleMultiSelection(Node(_loose)); // 选中项落在根级的 loose 上

        GroupNode.IsExpanded = false;

        Assert.IsEmpty(_vm.MultiSelection, "alpha / beta 被收进去,只剩 loose 一条,退回普通单选");
        Assert.AreSame(_vm.Nodes.Single(n => n.Id == _loose.Id), _vm.SelectedNode);
    }

    // ———— 双栏 ————

    [TestMethod]
    public async Task OpenDualSftp_RaisesBothProfiles_FirstSelectedOnTheLeft()
    {
        _vm.SelectedNode = Node(_gamma);
        _vm.ToggleMultiSelection(Node(_alpha));
        (SessionProfile Left, SessionProfile Right)? raised = null;
        _vm.OpenDualSftpRequested += (left, right) => raised = (left, right);

        await _vm.OpenDualSftpCommand.Execute().FirstAsync();

        Assert.IsNotNull(raised);
        Assert.AreSame(_gamma, raised.Value.Left);
        Assert.AreSame(_alpha, raised.Value.Right);
    }

    [TestMethod]
    public async Task APluginFileProtocolInThePair_CanBeOpenedSideBySide()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_bucket));

        Assert.IsTrue(_vm.CanOpenDualSelection);
        Assert.IsTrue(await _vm.OpenDualSftpCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task TheHostFilter_CanRuleAPairOut_AndTheHintShows()
    {
        _vm.DualSftpFilter = profile => profile.ConnectionType != ConnectionType.Plugin;
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_bucket));

        Assert.IsTrue(_vm.IsPairSelected);
        Assert.IsFalse(_vm.CanOpenDualSelection);
        Assert.IsTrue(_vm.ShowDualSftpUnsupportedHint);
        Assert.IsFalse(await _vm.OpenDualSftpCommand.CanExecute.FirstAsync());
    }

    // ———— 多选菜单 ————

    [TestMethod]
    public async Task OpenSelected_RaisesTheProfilesInTreeOrder()
    {
        _vm.SelectedNode = Node(_loose);
        _vm.ToggleMultiSelection(Node(_gamma));
        _vm.ToggleMultiSelection(Node(_alpha));
        IReadOnlyList<SessionProfile>? raised = null;
        _vm.OpenManyRequested += profiles => raised = profiles;

        await _vm.OpenSelectedCommand.Execute().FirstAsync();

        Assert.IsNotNull(raised);
        Assert.AreSequenceEqual(new[] { _alpha, _gamma, _loose }, raised.ToArray(), "按树上从上到下的顺序打开,标签就按这个顺序排");
    }

    [TestMethod]
    public void MenuTexts_CarryTheCount()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));
        _vm.ToggleMultiSelection(Node(_gamma));

        Assert.Contains("3", _vm.OpenSelectedText);
        Assert.Contains("3", _vm.DeleteSelectedText);
    }

    [TestMethod]
    public async Task DeleteSelected_AsksFirst_AndKeepsEverythingWhenDeclined()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));
        string? asked = null;
        _vm.ConfirmDeleteSessions = message =>
        {
            asked = message;
            return Task.FromResult(false);
        };

        await _vm.DeleteSelectedCommand.Execute().FirstAsync();

        Assert.IsNotNull(asked);
        Assert.Contains("2", asked);
        await _repository.DidNotReceive().DeleteSessionAsync(Arg.Any<Guid>());
        Assert.HasCount(2, _vm.MultiSelection, "取消了就什么都不动,选择也留着");
    }

    [TestMethod]
    public async Task DeleteSelected_RemovesEveryOne_FromStoreAndTree()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ExtendSelectionTo(Node(_loose), additive: false);
        _vm.ConfirmDeleteSessions = _ => Task.FromResult(true);

        await _vm.DeleteSelectedCommand.Execute().FirstAsync();

        foreach (SessionProfile profile in (SessionProfile[])[_alpha, _beta, _bucket, _gamma, _loose])
        {
            await _repository.Received(1).DeleteSessionAsync(profile.Id);
        }
        Assert.IsFalse(_vm.Rows.Any(row => !row.IsGroup));
        Assert.IsTrue(_vm.HasNoSessions);
        Assert.IsEmpty(_vm.MultiSelection);
    }

    [TestMethod]
    public async Task MoveSelectionToGroup_MovesEveryOne_AndDeletesTheEmptiedGroup()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ExtendSelectionTo(Node(_gamma), additive: false); // 整个 Prod 组
        SessionTreeNodeViewModel ungrouped = _vm.GroupNodes.Single(n => n.Id == Guid.Empty);

        await _vm.MoveSelectionToGroupCommand.Execute(ungrouped).FirstAsync();

        Assert.IsFalse(_vm.Nodes.Any(n => n.IsGroup), "源分组被挪空,按老规矩连分组一起删掉");
        await _repository.Received(1).DeleteGroupAsync(_group.Id);
        Assert.IsTrue(new[] { _alpha, _beta, _bucket, _gamma }.All(p => p.GroupId is null));
    }

    [TestMethod]
    public async Task BatchEditAndExport_RaiseTheSelection()
    {
        _vm.SelectedNode = Node(_alpha);
        _vm.ToggleMultiSelection(Node(_beta));
        IReadOnlyList<SessionProfile>? edited = null;
        SessionExportRequest? exported = null;
        _vm.BatchEditRequested += profiles => edited = profiles;
        _vm.ExportRequested += request => exported = request;

        await _vm.BatchEditCommand.Execute().FirstAsync();
        await _vm.ExportSelectedCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual(new[] { _alpha, _beta }, edited!.ToArray());
        Assert.AreSequenceEqual(new[] { _alpha.Id, _beta.Id }, exported!.ProfileIds.ToArray());
        Assert.IsNull(exported.GroupName);
    }
}
