using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
[TestCategory("SessionTree")]
public sealed class SessionTreeNestedSessionTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();
    private readonly ServerGroup _root = new() { Name = "Projects" };
    private readonly ServerGroup _project = new() { Name = "A" };
    private readonly ServerGroup _leaf = new() { Name = "Production" };
    private readonly SessionProfile _server = new() { Name = "web" };
    private readonly SessionProfile _pin = new() { Name = "db", IsPinned = true };
    private SessionTreeViewModel _vm = null!;

    [TestInitialize]
    public async Task Load()
    {
        _project.ParentGroupId = _root.Id;
        _leaf.ParentGroupId = _project.Id;
        _server.GroupId = _pin.GroupId = _leaf.Id;
        _repository.GetAllGroupsAsync().Returns(Task.FromResult<List<ServerGroup>>([_project, _leaf, _root]));
        _repository.GetAllSessionsAsync().Returns(Task.FromResult<List<SessionProfile>>([_server, _pin]));
        _vm = new(_repository);
        await _vm.LoadCommand.Execute().FirstAsync();
    }

    [TestMethod]
    public async Task FilteringAndFollowingExpandAncestorChainWhilePinStaysVisible()
    {
        Assert.AreSequenceEqual(["db", "Projects", "A", "Production", "web"], [.. _vm.Rows.Select(n => n.Name)]);
        await _vm.CollapseAllCommand.Execute().FirstAsync();
        Assert.AreSequenceEqual(["db", "Projects"], [.. _vm.Rows.Select(n => n.Name)]);
        _vm.FilterText = "projects web";
        Assert.AreSequenceEqual(["Projects", "A", "Production", "web"], [.. _vm.Rows.Select(n => n.Name)]);
        _vm.ClearFilter();
        Assert.AreSequenceEqual(["db", "Projects"], [.. _vm.Rows.Select(n => n.Name)]);
        Assert.IsTrue(_vm.SelectSession(_server.Id));
        Assert.AreEqual(3, _vm.SelectedNode!.Depth);
        _vm.SetSessionSyncChannel(_server.Id, "B");
        Assert.AreEqual("B", _vm.SelectedNode.SyncChannelLetter);
        _vm.Nodes.Single().IsExpanded = false;
        Assert.AreEqual(_root.Id, _vm.SelectedNode.Id);
    }

    [TestMethod]
    public async Task ConfirmedDeletionCollectsEachLeafOnceAndDeletesChildrenFirst()
    {
        _vm.SelectedNode = _vm.Nodes.Single();
        _vm.ConfirmDeleteGroup = _ => Task.FromResult(false);
        await _vm.DeleteGroupCommand.Execute().FirstAsync();
        await _repository.DidNotReceive().DeleteSessionAsync(Arg.Any<Guid>());
        _vm.ConfirmDeleteGroup = _ => Task.FromResult(true);
        await _vm.DeleteGroupCommand.Execute().FirstAsync();
        await _repository.Received(1).DeleteSessionAsync(_server.Id);
        await _repository.Received(1).DeleteSessionAsync(_pin.Id);
        Received.InOrder(() =>
        {
            _ = _repository.DeleteGroupAsync(_leaf.Id);
            _ = _repository.DeleteGroupAsync(_project.Id);
            _ = _repository.DeleteGroupAsync(_root.Id);
        });
        Assert.IsEmpty(_vm.Nodes);
    }
}
