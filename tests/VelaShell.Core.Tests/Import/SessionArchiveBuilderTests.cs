using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>导出范围:选中的连接 + 它们的跳板(逐级),只带被引用到的分组、凭据与隧道。</summary>
[TestClass]
[TestCategory("SessionArchive")]
public sealed class SessionArchiveBuilderTests
{
    [TestMethod]
    public void Select_PullsInJumpHostsTransitively_AndSurvivesCycles()
    {
        var outer = new SessionProfile { Name = "outer", Host = "1.1.1.1" };
        var inner = new SessionProfile { Name = "inner", Host = "2.2.2.2", JumpHostProfileId = outer.Id };
        var app = new SessionProfile { Name = "app", Host = "3.3.3.3", JumpHostProfileId = inner.Id };
        var loopA = new SessionProfile { Name = "loop-a", Host = "4.4.4.4" };
        var loopB = new SessionProfile { Name = "loop-b", Host = "5.5.5.5", JumpHostProfileId = loopA.Id };
        loopA.JumpHostProfileId = loopB.Id;
        var unrelated = new SessionProfile { Name = "unrelated", Host = "6.6.6.6" };

        SessionExportSelection selection = SessionArchiveBuilder.Select(
            [app.Id, loopA.Id, Guid.NewGuid()], [outer, inner, app, loopA, loopB, unrelated], []);

        Assert.AreEqual(2, selection.RequestedCount, "不存在的 Id 忽略");
        Assert.AreEqual(3, selection.DependencyCount);
        CollectionAssert.AreEquivalent(new[] { "app", "inner", "outer", "loop-a", "loop-b" }, selection.Sessions.Select(static s => s.Name).ToArray());
    }

    [TestMethod]
    public void Order_FollowsTheTree_GroupsBySortOrder_UngroupedLast_NamesWithin()
    {
        var first = new ServerGroup { Id = Guid.NewGuid(), Name = "B group", SortOrder = 0 };
        var second = new ServerGroup { Id = Guid.NewGuid(), Name = "A group", SortOrder = 1 };
        SessionProfile[] sessions =
        [
            new() { Name = "loose", GroupId = null },
            new() { Name = "z", GroupId = second.Id },
            new() { Name = "b", GroupId = first.Id },
            new() { Name = "A", GroupId = first.Id }
        ];

        IReadOnlyList<SessionProfile> ordered = SessionArchiveBuilder.Order(sessions, [second, first]);

        Assert.AreSequenceEqual(new[] { "A", "b", "z", "loose" }, ordered.Select(static s => s.Name).ToArray());
    }

    [TestMethod]
    public void Build_OnlyCarriesWhatTheSelectionReferences()
    {
        var usedGroup = new ServerGroup { Id = Guid.NewGuid(), Name = "used" };
        var unusedGroup = new ServerGroup { Id = Guid.NewGuid(), Name = "unused" };
        var usedCredential = new SharedCredential { Id = Guid.NewGuid(), Name = "used", Password = "pw" };
        var unusedCredential = new SharedCredential { Id = Guid.NewGuid(), Name = "unused" };
        var session = new SessionProfile
        {
            Name = "s",
            Host = "h",
            GroupId = usedGroup.Id,
            CredentialSource = CredentialReference.ForShared(usedCredential.Id)
        };
        var other = new SessionProfile { Name = "other", Host = "o" };
        var tunnel = new TunnelConfig { Type = TunnelType.LocalForward, Name = "t", LocalHost = "127.0.0.1", LocalPort = 1 };
        SessionExportSelection selection = SessionArchiveBuilder.Select([session.Id], [session, other], [usedGroup, unusedGroup]);

        SessionArchive archive = SessionArchiveBuilder.Build(selection, [usedGroup, unusedGroup], [usedCredential, unusedCredential],
            new Dictionary<Guid, List<TunnelConfig>> { [session.Id] = [tunnel], [other.Id] = [tunnel] },
            DateTime.UtcNow, "test");

        Assert.AreEqual("used", archive.Groups.Single().Name);
        Assert.AreEqual("used", archive.SharedCredentials!.Single().Name);
        Assert.AreEqual(session.Id, archive.Tunnels!.Keys.Single());
        Assert.AreEqual("s", archive.Sessions.Single().Name);
        Assert.AreNotSame(session, archive.Sessions.Single(), "装进导出的是副本");
    }

    [TestMethod]
    public void Build_WithoutCredentialsOrTunnels_LeavesThoseSectionsNull()
    {
        var session = new SessionProfile { Name = "s", Host = "h" };

        SessionArchive archive = SessionArchiveBuilder.Build(
            SessionArchiveBuilder.Select([session.Id], [session], []), [], [], new Dictionary<Guid, List<TunnelConfig>>(),
            DateTime.UtcNow, null);

        Assert.IsNull(archive.SharedCredentials);
        Assert.IsNull(archive.Tunnels);
        Assert.IsEmpty(archive.Groups);
    }
}
