using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

[TestClass]
public sealed class ConnectionWorkflowServiceTests
{
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
    private readonly ISshConnectionService _sshConnectionService = Substitute.For<ISshConnectionService>();

    [TestMethod]
    public async Task SaveProfileAsync_PersistsProfile()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        SessionProfile result = await service.SaveProfileAsync(profile);
        Assert.AreSame(profile, result);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [TestMethod]
    public async Task ConnectProfileAsync_SavesLastConnectedAt()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        var session = new SshSession
        {
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(session);
        SshSession result = await service.ConnectProfileAsync(profile);
        Assert.AreSame(session, result);
        Assert.IsNotNull(profile.LastConnectedAt);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [TestMethod]
    public async Task TestConnectionAsync_WhenConnectFails_ReturnsFailureResult()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile profile = CreateProfile();
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns<Task<SshSession>>(_ => throw new InvalidOperationException("boom"));
        ConnectionTestResult result = await service.TestConnectionAsync(profile);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("boom", result.ErrorMessage);
    }

    [TestMethod]
    public async Task GetSavedProfilesAsync_ReturnsSortedProfiles()
    {
        ConnectionWorkflowService service = CreateService();
        SessionProfile first = CreateProfile("b", DateTime.UtcNow.AddMinutes(-10));
        SessionProfile second = CreateProfile("a", DateTime.UtcNow);
        _sessionRepository.GetAllSessionsAsync().Returns([first, second]);
        IReadOnlyList<SessionProfile> result = await service.GetSavedProfilesAsync();
        Assert.AreSame(second, result[0]);
        Assert.AreSame(first, result[1]);
    }

    private ConnectionWorkflowService CreateService() => new(_sessionRepository, _sshConnectionService);

    private static SessionProfile CreateProfile(string name = "server", DateTime? lastConnectedAt = null)
    {
        return new()
        {
            Name = name,
            Host = "localhost",
            Port = 22,
            Username = "tester",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            LastConnectedAt = lastConnectedAt
        };
    }
}
