using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

[TestClass]
public sealed class ServerGroupTreeTests
{
    [TestMethod]
    public void PathsAndMoveTargetsUseParentIds()
    {
        var root = new ServerGroup { Name = "Projects" };
        var a = new ServerGroup { Name = "A", ParentGroupId = root.Id, SortOrder = 1 };
        var b = new ServerGroup { Name = "B", ParentGroupId = root.Id, SortOrder = 0 };
        var prod = new ServerGroup { Name = "Prod", ParentGroupId = a.Id };
        var otherProd = new ServerGroup { Name = "Prod", ParentGroupId = b.Id };
        var tree = new ServerGroupTree([prod, b, root, a, otherProd]);
        Assert.IsEmpty(tree.Validate());
        Assert.AreSequenceEqual([root, b, otherProd, a, prod], tree.OrderedGroups.ToArray());
        Assert.AreEqual("Projects > A > Prod", tree.PathOf(prod.Id));
        Assert.AreSequenceEqual([a, prod], tree.SubtreeOf(a.Id).ToArray());
        Assert.IsFalse(tree.CanMoveUnder(root.Id, prod.Id));
        Assert.IsFalse(tree.CanMoveUnder(prod.Id, prod.Id));
        Assert.IsTrue(tree.CanMoveUnder(a.Id, b.Id));
    }

    [TestMethod]
    public void InvalidReferencesRemainVisibleWithoutMutatingRecords()
    {
        var a = new ServerGroup();
        var b = new ServerGroup { ParentGroupId = a.Id };
        a.ParentGroupId = b.Id;
        var missing = new ServerGroup { ParentGroupId = Guid.NewGuid() };
        var tree = new ServerGroupTree([a, missing, b]);
        Assert.HasCount(3, tree.OrderedGroups);
        Assert.HasCount(3, tree.Validate());
        Assert.IsNull(tree.ParentOf(a.Id));
        Assert.IsNull(tree.ParentOf(missing.Id));
        Assert.AreEqual(b.Id, a.ParentGroupId);
    }

    [TestMethod]
    public void DepthLimitIncludesWholeMovedSubtree()
    {
        List<ServerGroup> chain = [new()];
        for (int i = 0; i < ServerGroupTree.MaxDepth; i++)
        {
            chain.Add(new() { ParentGroupId = chain[^1].Id });
        }
        var root = new ServerGroup();
        var child = new ServerGroup { ParentGroupId = root.Id };
        var tree = new ServerGroupTree([..chain, root, child]);
        Assert.IsEmpty(tree.Validate());
        Assert.IsFalse(tree.CanMoveUnder(root.Id, chain[^2].Id));
        Assert.IsTrue(tree.CanMoveUnder(root.Id, chain[^3].Id));
        child.ParentGroupId = chain[^1].Id;
        Assert.IsNotEmpty(new ServerGroupTree([..chain, child]).Validate());
    }
}
