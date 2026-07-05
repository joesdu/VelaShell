using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using PulseTerm.Core.Ssh;
using PulseTerm.Presentation.Services;
using PulseTerm.Terminal;

namespace PulseTerm.App.Tests.ViewModels;

public sealed class MainWindowSshFeatureTests
{
    [Fact]
    public async Task ConnectProfileAsync_AddsTerminalTab_AndUpdatesStatusBar()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var sshConnectionService = Substitute.For<ISshConnectionService>();
        var sshClient = Substitute.For<ISshClientWrapper>();
        var shellStream = Substitute.For<IShellStreamWrapper>();
        var terminal = Substitute.For<ITerminalEmulator>();

        shellStream.CanRead.Returns(false);

        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };

        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
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

        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStream("xterm-256color", 120, 32, 0, 0, 4096, null).Returns(shellStream);

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);

        var tab = await vm.ConnectProfileAsync(profile);

        tab.Should().NotBeNull();
        tab.Title.Should().Be("Prod");
        tab.ConnectionStatus.Should().Be(SessionStatus.Connected);
        vm.TabBar.ActiveTab.Should().BeSameAs(tab);
        vm.TabBar.Tabs.Should().ContainSingle();
        vm.StatusBar.ConnectionInfo.Should().Be("SSH • root@prod.example.com:22");
        vm.StatusBar.Status.Should().Be(Strings.Connected);
        vm.Sidebar.RecentConnections.Connections.Should().ContainSingle();
    }

    [Fact]
    public async Task TryConnectProfileAsync_AuthFailure_DoesNotThrow_AndReportsError()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var sshConnectionService = Substitute.For<ISshConnectionService>();

        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "wrong"
        };

        // Simulate SSH.NET rejecting the password (matched by type name in the VM).
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
            .Returns<Task<SshSession>>(_ => throw new SshAuthenticationException("Permission denied (password)."));

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => Substitute.For<ITerminalEmulator>());

        var tab = await vm.TryConnectProfileAsync(profile);

        tab.Should().BeNull();
        vm.TabBar.Tabs.Should().BeEmpty();
        vm.LastConnectionError.Should().NotBeNullOrEmpty();
        vm.LastConnectionError.Should().Contain("认证失败");
    }

    [Fact]
    public async Task InitializeAsync_LoadsSavedSessions_IntoRecentConnections()
    {
        var repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(new List<SessionProfile>
        {
            new() { Name = "Prod", Host = "prod.example.com", Port = 22, Username = "root" },
            new() { Name = "Dev", Host = "dev.example.com", Port = 22, Username = "pi" },
        });

        var vm = new MainWindowViewModel(sessionRepository: repository);

        await vm.InitializeAsync();

        vm.Sidebar.RecentConnections.Connections.Should().HaveCount(2);
    }

    // Named to match SSH.NET's SshAuthenticationException so the VM's type-name mapping applies.
    private sealed class SshAuthenticationException : Exception
    {
        public SshAuthenticationException(string message) : base(message) { }
    }
}
