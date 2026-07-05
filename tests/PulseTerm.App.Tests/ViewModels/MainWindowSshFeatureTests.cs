using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using PulseTerm.Core.Ssh;
using PulseTerm.Presentation.Services;
using PulseTerm.Terminal;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public sealed class MainWindowSshFeatureTests
{
    [TestMethod]
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

        Assert.IsNotNull(tab);
        Assert.AreEqual("Prod", tab.Title);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreSame(tab, vm.TabBar.ActiveTab);
        Assert.AreEqual(1, vm.TabBar.Tabs.Count());
        Assert.AreEqual("SSH • root@prod.example.com:22", vm.StatusBar.ConnectionInfo);
        Assert.AreEqual(Strings.Connected, vm.StatusBar.Status);
        Assert.AreEqual(1, vm.Sidebar.RecentConnections.Connections.Count());
    }

    [TestMethod]
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

        Assert.IsNull(tab);
        Assert.AreEqual(0, vm.TabBar.Tabs.Count());
        Assert.IsFalse(string.IsNullOrEmpty(vm.LastConnectionError));
        StringAssert.Contains(vm.LastConnectionError, "认证失败");
    }

    [TestMethod]
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

        Assert.AreEqual(2, vm.Sidebar.RecentConnections.Connections.Count());
    }

    [TestMethod]
    public void StatusBar_FollowsActiveTab_ConnectionInfo()
    {
        var vm = new MainWindowViewModel();

        var tabA = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>(), Substitute.For<IShellStreamWrapper>())
        {
            Title = "A",
            ConnectionStatus = SessionStatus.Connected,
            ConnectionSummary = "SSH • a@host-a:22",
        };
        var tabB = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>(), Substitute.For<IShellStreamWrapper>())
        {
            Title = "B",
            ConnectionStatus = SessionStatus.Connected,
            ConnectionSummary = "SSH • b@host-b:22",
        };

        vm.TabBar.AddTab(tabA);
        vm.TabBar.AddTab(tabB); // B becomes active

        Assert.AreEqual("SSH • b@host-b:22", vm.StatusBar.ConnectionInfo);

        vm.TabBar.ActiveTab = tabA; // switch back to A
        Assert.AreEqual("SSH • a@host-a:22", vm.StatusBar.ConnectionInfo);
    }

    // Named to match SSH.NET's SshAuthenticationException so the VM's type-name mapping applies.
    private sealed class SshAuthenticationException : Exception
    {
        public SshAuthenticationException(string message) : base(message) { }
    }
}
