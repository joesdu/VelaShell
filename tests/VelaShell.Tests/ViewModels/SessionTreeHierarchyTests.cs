using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
[TestCategory("SessionTree")]
public sealed class SessionTreeHierarchyTests
{
    [TestMethod]
    public async Task CreateGroup_PersistsEmptyChild_AndRejectsSameParentDuplicate()
    {
        var root = new ServerGroup { Name = "Projects", SortOrder = 0 };
        List<ServerGroup> stored = [root];
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(stored));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();

        SessionTreeNodeViewModel? child = await viewModel.CreateGroupAsync("Production", root.Id);
        SessionTreeNodeViewModel? duplicate = await viewModel.CreateGroupAsync(" production ", root.Id);

        Assert.IsNotNull(child);
        Assert.AreEqual(root.Id, child.ParentGroup?.Id);
        Assert.AreEqual("Projects > Production", child.GroupPath);
        Assert.IsNull(duplicate);
        await repository.Received(1).SaveGroupAsync(Arg.Is<ServerGroup>(group =>
            group.Name == "Production" && group.ParentGroupId == root.Id));
    }

    [TestMethod]
    public async Task RenameGroup_UpdatesPathsAndFilter_WithoutMovingDescendants()
    {
        var root = new ServerGroup { Name = "Projects", SortOrder = 7 };
        var child = new ServerGroup { Name = "Production", ParentGroupId = root.Id };
        var session = new SessionProfile { Name = "web", GroupId = child.Id };
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { root, child }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { session }));
        var vm = new SessionTreeViewModel(repository);
        await vm.LoadCommand.Execute().FirstAsync();
        vm.FilterText = "Projects";

        Assert.IsTrue(await vm.RenameGroupAsync(root.Id, " Production "));
        await repository.Received(1).SaveGroupAsync(Arg.Is<ServerGroup>(g =>
            g.Id == root.Id && g.Name == "Production" && g.SortOrder == 7));
        Assert.IsEmpty(vm.Rows);
        vm.FilterText = "Production";
        Assert.IsTrue(await vm.RenameGroupAsync(child.Id, "PRODUCTION"));
        SessionTreeNodeViewModel leaf = vm.Rows.Single(n => n.Id == session.Id);
        Assert.AreEqual(child.Id, leaf.ParentGroup?.Id);
        Assert.AreEqual("Production > PRODUCTION", leaf.GroupPath);
        Assert.AreEqual(leaf.GroupPath, vm.GroupNodes.Single(n => n.Id == child.Id).GroupPath);
    }

    [TestMethod]
    public async Task RenameGroup_InvalidOrUnchangedName_DoesNotWrite()
    {
        var root = new ServerGroup { Name = "Projects" };
        var sibling = new ServerGroup { Name = "Operations" };
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { root, sibling }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new SessionTreeViewModel(repository);
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.IsFalse(await vm.RenameGroupAsync(root.Id, "  "));
        Assert.IsFalse(await vm.RenameGroupAsync(root.Id, " operations "));
        Assert.IsFalse(await vm.RenameGroupAsync(root.Id, "A > B"));
        Assert.IsTrue(await vm.RenameGroupAsync(root.Id, " Projects "));
        Assert.AreEqual("Projects", vm.GroupNodes.Single(n => n.Id == root.Id).Name);
        await repository.DidNotReceiveWithAnyArgs().SaveGroupAsync(default!);
    }

    [TestMethod]
    public async Task MoveGroupToAsync_ReparentsAndUpdatesDescendantPaths()
    {
        var root = new ServerGroup { Name = "Projects", SortOrder = 0 };
        var destination = new ServerGroup { Name = "Operations", SortOrder = 1 };
        var child = new ServerGroup { Name = "Production", ParentGroupId = root.Id, SortOrder = 0 };
        var grandchild = new ServerGroup { Name = "Canary", ParentGroupId = child.Id, SortOrder = 0 };
        List<ServerGroup> stored = [root, destination, child, grandchild];
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(stored));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();

        SessionTreeNodeViewModel moving = viewModel.Nodes.Single(node => node.Name == "Projects").Children.Single(node => node.Name == "Production");
        SessionTreeNodeViewModel destinationNode = viewModel.Nodes.Single(node => node.Name == "Operations");
        bool moved = await viewModel.MoveGroupToAsync(moving.Id, destinationNode.Id, 0);

        Assert.IsTrue(moved);
        Assert.AreEqual(destinationNode, moving.ParentGroup);
        Assert.AreEqual("Operations > Production", moving.GroupPath);
        Assert.AreEqual(1, moving.Depth);
        Assert.AreEqual("Operations > Production > Canary", moving.Children.Single().GroupPath);
        Assert.AreEqual(destinationNode.Id, stored.Single(group => group.Id == moving.Id).ParentGroupId);
    }

    [TestMethod]
    public async Task ResolveHierarchyDrop_RejectsDescendantAndPlacesAfterSessionOwner()
    {
        var root = new ServerGroup { Name = "Projects", SortOrder = 0 };
        var sibling = new ServerGroup { Name = "Operations", SortOrder = 1 };
        var child = new ServerGroup { Name = "Production", ParentGroupId = root.Id, SortOrder = 0 };
        List<ServerGroup> stored = [root, sibling, child];
        var session = new SessionProfile { Name = "web", Host = "web", GroupId = root.Id };
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(stored));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile> { session }));
        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();

        SessionTreeNodeViewModel rootNode = viewModel.Nodes.Single(node => node.Name == "Projects");
        SessionTreeNodeViewModel childNode = rootNode.Children.Single(node => node.Name == "Production");
        SessionTreeNodeViewModel sessionNode = rootNode.Children.Single(node => node.Name == "web");
        GroupDropTarget? descendant = viewModel.ResolveHierarchyGroupDrop(rootNode.Id, childNode, GroupDropPlacement.Inside);
        GroupDropTarget? afterOwner = viewModel.ResolveHierarchyGroupDrop(sibling.Id, sessionNode, GroupDropPlacement.End);

        Assert.IsNull(descendant);
        Assert.AreEqual(rootNode.ParentGroup?.Id, afterOwner?.ParentGroupId);
        Assert.AreEqual(1, afterOwner?.Index);
    }
}
