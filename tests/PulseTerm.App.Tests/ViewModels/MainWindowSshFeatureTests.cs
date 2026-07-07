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

        // 连接历史由工作流写入 SonnetDB;侧边栏“最近连接”刷新时从服务读取。
        var recents = Substitute.For<IRecentConnectionService>();
        recents.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecentConnectionEntry>
            {
                new() { ProfileId = profile.Id, Name = "Prod", GroupName = "生产环境", Host = profile.Host, Port = 22, Username = "root" },
            });

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal, recentConnectionService: recents);

        var tab = await vm.ConnectProfileAsync(profile);

        Assert.IsNotNull(tab);
        Assert.AreEqual("Prod", tab.Title);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreSame(tab, vm.TabBar.ActiveTab);
        Assert.AreEqual(1, vm.TabBar.Tabs.Count());
        Assert.AreEqual("SSH • root@prod.example.com:22", vm.StatusBar.ConnectionInfo);
        Assert.AreEqual(Strings.Connected, vm.StatusBar.Status);
        Assert.AreEqual(1, vm.Sidebar.RecentConnections.Connections.Count());
        Assert.AreEqual("Prod - 生产环境", vm.Sidebar.RecentConnections.Connections[0].DisplayName);
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
    public async Task ReconnectTabAsync_ReusesSameTab_AndRestoresConnectedState()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var sshConnectionService = Substitute.For<ISshConnectionService>();
        var sshClient = Substitute.For<ISshClientWrapper>();
        var shellStream = Substitute.For<IShellStreamWrapper>();
        shellStream.CanRead.Returns(false);
        var terminal = Substitute.For<ITerminalEmulator>();

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
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);

        // The remote drops (exit / reboot).
        tab.MarkDisconnected();
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);

        // Reconnect in place — same tab, not a new one.
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreEqual(1, vm.TabBar.Tabs.Count());
        Assert.AreSame(tab, vm.TabBar.Tabs.First());
    }

    [TestMethod]
    public async Task ReconnectTabAsync_WhenAlreadyConnected_IsNoOp()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var sshConnectionService = Substitute.For<ISshConnectionService>();
        var terminal = Substitute.For<ITerminalEmulator>();

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        var tab = new TerminalTabViewModel(terminal)
        {
            ConnectionStatus = SessionStatus.Connected,
            Profile = new SessionProfile { Host = "h", Port = 22, Username = "u" }
        };

        await vm.ReconnectTabAsync(tab);

        // No connect attempt should have been made.
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsRecentHistory_IntoRecentConnections()
    {
        var recents = Substitute.For<IRecentConnectionService>();
        recents.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecentConnectionEntry>
            {
                new() { Name = "Prod", GroupName = "生产环境", Host = "prod.example.com", Port = 22, Username = "root", ConnectedAt = DateTimeOffset.Now.AddHours(-2) },
                new() { Name = "Dev", Host = "dev.example.com", Port = 22, Username = "pi", ConnectedAt = DateTimeOffset.Now.AddDays(-3) },
            });

        var vm = new MainWindowViewModel(recentConnectionService: recents);

        await vm.InitializeAsync();

        Assert.AreEqual(2, vm.Sidebar.RecentConnections.Connections.Count());
        Assert.AreEqual("Prod - 生产环境", vm.Sidebar.RecentConnections.Connections[0].DisplayName);
        Assert.AreEqual("2 小时前", vm.Sidebar.RecentConnections.Connections[0].RelativeTime);
        Assert.AreEqual("Dev", vm.Sidebar.RecentConnections.Connections[1].DisplayName);
        Assert.AreEqual("3 天前", vm.Sidebar.RecentConnections.Connections[1].RelativeTime);
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
