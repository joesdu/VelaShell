using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Presentation.Services;

namespace PulseTerm.Presentation.Tests.Services;

public sealed class ConnectionWorkflowServiceTests
{
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
    private readonly ISshConnectionService _sshConnectionService = Substitute.For<ISshConnectionService>();

    [Fact]
    public async Task SaveProfileAsync_PersistsProfile()
    {
        var service = CreateService();
        var profile = CreateProfile();

        var result = await service.SaveProfileAsync(profile);

        result.Should().BeSameAs(profile);
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [Fact]
    public async Task ConnectProfileAsync_SavesLastConnectedAt()
    {
        var service = CreateService();
        var profile = CreateProfile();
        var session = new SshSession
        {
            ConnectionInfo = new ConnectionInfo
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

        var result = await service.ConnectProfileAsync(profile);

        result.Should().BeSameAs(session);
        profile.LastConnectedAt.Should().NotBeNull();
        await _sessionRepository.Received(1).SaveSessionAsync(profile);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenConnectFails_ReturnsFailureResult()
    {
        var service = CreateService();
        var profile = CreateProfile();
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns<Task<SshSession>>(_ => throw new InvalidOperationException("boom"));

        var result = await service.TestConnectionAsync(profile);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task GetSavedProfilesAsync_ReturnsSortedProfiles()
    {
        var service = CreateService();
        var first = CreateProfile("b", DateTime.UtcNow.AddMinutes(-10));
        var second = CreateProfile("a", DateTime.UtcNow);
        _sessionRepository.GetAllSessionsAsync().Returns([first, second]);

        var result = await service.GetSavedProfilesAsync();

        result[0].Should().BeSameAs(second);
        result[1].Should().BeSameAs(first);
    }

    private ConnectionWorkflowService CreateService() => new(_sessionRepository, _sshConnectionService);

    private static SessionProfile CreateProfile(string name = "server", DateTime? lastConnectedAt = null)
    {
        return new SessionProfile
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
