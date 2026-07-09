using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Data;

[TestClass]
[TestCategory("DataStore")]
public class SessionRepositoryTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _sessionsPath;
    private readonly JsonDataStore _dataStore;

    public SessionRepositoryTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _sessionsPath = Path.Combine(_testDirectory, "sessions.json");
        _dataStore = new JsonDataStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task GetAllGroupsAsync_FileDoesNotExist_ShouldReturnEmptyList()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);

        var groups = await repo.GetAllGroupsAsync();

        Assert.AreEqual(0, groups.Count());
    }

    [TestMethod]
    public async Task SaveSessionAsync_NewSession_ShouldAddToList()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Server",
            Host = "192.168.1.100"
        };

        await repo.SaveSessionAsync(session);
        var retrieved = await repo.GetSessionAsync(session.Id);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual("Test Server", retrieved!.Name);
        Assert.AreEqual("192.168.1.100", retrieved.Host);
    }

    [TestMethod]
    public async Task GetAllSessionsAsync_ReturnsPersistedSessions()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var first = new SessionProfile { Id = Guid.NewGuid(), Name = "A", Host = "a.example.com" };
        var second = new SessionProfile { Id = Guid.NewGuid(), Name = "B", Host = "b.example.com" };

        await repo.SaveSessionAsync(first);
        await repo.SaveSessionAsync(second);

        var sessions = await repo.GetAllSessionsAsync();

        Assert.AreEqual(2, sessions.Count());
        Assert.IsTrue(sessions.Any(session => session.Id == first.Id));
        Assert.IsTrue(sessions.Any(session => session.Id == second.Id));
    }

    [TestMethod]
    public async Task SaveSessionAsync_ExistingSession_ShouldUpdate()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            Host = "192.168.1.100"
        };

        await repo.SaveSessionAsync(session);
        session.Name = "Updated";
        await repo.SaveSessionAsync(session);
        var retrieved = await repo.GetSessionAsync(session.Id);

        Assert.AreEqual("Updated", retrieved!.Name);
    }

    [TestMethod]
    public async Task GetSessionAsync_NonExistentId_ShouldReturnNull()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);

        var result = await repo.GetSessionAsync(Guid.NewGuid());

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteSessionAsync_ShouldRemoveSessionAndUpdateGroups()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var sessionId = Guid.NewGuid();
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Sessions = new List<Guid> { sessionId }
        };
        var session = new SessionProfile { Id = sessionId, Name = "Test" };

        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(session);
        await repo.DeleteSessionAsync(sessionId);

        var retrievedSession = await repo.GetSessionAsync(sessionId);
        var groups = await repo.GetAllGroupsAsync();

        Assert.IsNull(retrievedSession);
        Assert.IsFalse(groups.First().Sessions.Contains(sessionId));
    }

    [TestMethod]
    public async Task SaveGroupAsync_NewGroup_ShouldAddToList()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Icon = "server",
            SortOrder = 1
        };

        await repo.SaveGroupAsync(group);
        var groups = await repo.GetAllGroupsAsync();

        Assert.AreEqual(1, groups.Count());
        Assert.AreEqual("Production", groups.First().Name);
        Assert.AreEqual("server", groups.First().Icon);
    }

    [TestMethod]
    public async Task SaveGroupAsync_ExistingGroup_ShouldUpdate()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            SortOrder = 1
        };

        await repo.SaveGroupAsync(group);
        group.Name = "Updated";
        group.SortOrder = 5;
        await repo.SaveGroupAsync(group);
        var groups = await repo.GetAllGroupsAsync();

        Assert.AreEqual("Updated", groups.First().Name);
        Assert.AreEqual(5, groups.First().SortOrder);
    }

    [TestMethod]
    public async Task DeleteGroupAsync_ShouldRemoveGroupAndClearSessionGroupIds()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var groupId = Guid.NewGuid();
        var group = new ServerGroup { Id = groupId, Name = "Test" };
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Session",
            GroupId = groupId
        };

        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(session);
        await repo.DeleteGroupAsync(groupId);

        var groups = await repo.GetAllGroupsAsync();
        var retrievedSession = await repo.GetSessionAsync(session.Id);

        Assert.AreEqual(0, groups.Count());
        Assert.IsNull(retrievedSession!.GroupId);
    }

    [TestMethod]
    public async Task MultipleSessionsAndGroups_ShouldPersistCorrectly()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var group1 = new ServerGroup { Id = Guid.NewGuid(), Name = "Group1" };
        var group2 = new ServerGroup { Id = Guid.NewGuid(), Name = "Group2" };
        var session1 = new SessionProfile { Id = Guid.NewGuid(), Name = "Session1", GroupId = group1.Id };
        var session2 = new SessionProfile { Id = Guid.NewGuid(), Name = "Session2", GroupId = group2.Id };

        await repo.SaveGroupAsync(group1);
        await repo.SaveGroupAsync(group2);
        await repo.SaveSessionAsync(session1);
        await repo.SaveSessionAsync(session2);

        var groups = await repo.GetAllGroupsAsync();
        var retrievedSession1 = await repo.GetSessionAsync(session1.Id);
        var retrievedSession2 = await repo.GetSessionAsync(session2.Id);

        Assert.AreEqual(2, groups.Count());
        Assert.AreEqual(group1.Id, retrievedSession1!.GroupId);
        Assert.AreEqual(group2.Id, retrievedSession2!.GroupId);
    }
}
