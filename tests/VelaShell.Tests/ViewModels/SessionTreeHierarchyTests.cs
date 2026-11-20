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
