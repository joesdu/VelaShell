using NSubstitute;
using VelaShell.App.ViewModels;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;

namespace VelaShell.App.Tests.ViewModels;

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

        // 模拟活连接:读循环阻塞在 ReadAsync(不立即 EOF),否则桥会异步触发 Closed 与连接
        // 结果竞态,把刚连上的标签翻回断开(真实连接不会立即 EOF)。
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<int>().Task);

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
        // 设计 gzmsb 调整(cfe16d2):状态栏只显示"SSH • <显示名称>",不暴露用户名/IP/端口(安全要求)。
        Assert.AreEqual("SSH • Prod", vm.StatusBar.ConnectionInfo);
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
    public async Task TryConnectProfileAsync_NetworkFailure_KeepsTab_WithInTabOverlay()
    {
        // 设计 yxjmg:网络/超时失败不销毁标签、不弹全局框,仅在标签页内显示失败覆盖层。
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var sshConnectionService = Substitute.For<ISshConnectionService>();

        var profile = new SessionProfile
        {
            Name = "LAN",
            Host = "192.168.1.50",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        };

        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
            .Returns<Task<SshSession>>(_ => throw new System.Net.Sockets.SocketException());

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => Substitute.For<ITerminalEmulator>());

        var tab = await vm.TryConnectProfileAsync(profile);

        Assert.IsNotNull(tab);
        Assert.AreEqual(1, vm.TabBar.Tabs.Count());
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.ShowDisconnectedOverlay);
        Assert.IsTrue(tab.HasConnectionError);
        Assert.AreEqual("连接失败", tab.DisconnectOverlayTitle);
        Assert.IsFalse(string.IsNullOrEmpty(vm.LastConnectionError));
    }

    [TestMethod]
    public async Task ReconnectTabAsync_ReusesSameTab_AndRestoresConnectedState()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var sshConnectionService = Substitute.For<ISshConnectionService>();
        var sshClient = Substitute.For<ISshClientWrapper>();
        var shellStream = Substitute.For<IShellStreamWrapper>();
        // 模拟活连接:读循环阻塞在 ReadAsync,避免桥立即 EOF 触发 Closed 与连接结果竞态。
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<int>().Task);
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
