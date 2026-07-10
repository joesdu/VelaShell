using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class InteractiveAuthFlowTests
{
    private static SshSession CreateSession(SessionProfile profile) =>
        new()
        {
            SessionId = Guid.NewGuid(),
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

    [TestMethod]
    public async Task MissingPassword_PromptsAuthenticator_BeforeConnecting()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? client = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);
        var profile = new SessionProfile { Name = "P", Host = "h", Port = 22, Username = "root", AuthMethod = AuthMethod.Password };
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(args => CreateSession((SessionProfile)args[0]));
        ssh.GetClient(Arg.Any<Guid>()).Returns(client);
        client.CreateShellStream("xterm-256color", 120, 32, 0, 0, 4096).Returns(shell);
        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            p.Password = "prompted-pass";
            return Task.FromResult<SessionProfile?>(p);
        };
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.AreEqual(1, prompted);
        Assert.IsNotNull(tab);
        await workflow.Received(1).ConnectProfileAsync(Arg.Is<SessionProfile>(p => p.Password == "prompted-pass"), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task PromptCancelled_ReturnsNull_WithoutError()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());
        vm.InteractiveAuthenticator = _ => Task.FromResult<SessionProfile?>(null);
        var profile = new SessionProfile { Name = "P", Host = "h", Username = "root", AuthMethod = AuthMethod.Password };
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNull(tab);
        Assert.IsNull(vm.LastConnectionError);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task AuthFailure_RepromptsUpToThreeAttempts()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? ssh = Substitute.For<ISshConnectionService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new SshAuthenticationException("denied"));
        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            p.Password = $"try-{prompted}";
            return Task.FromResult<SessionProfile?>(p);
        };

        // 密码已填 → 第一次直接连接失败,随后重试两次均需弹窗。
        var profile = new SessionProfile { Name = "P", Host = "h", Username = "root", AuthMethod = AuthMethod.Password, Password = "wrong" };
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

        // 重试用尽后保留失败标签,标签页内显示“连接失败”覆盖层(设计 yxjmg),不再销毁标签。
        Assert.IsNotNull(tab);
        Assert.AreEqual(1, vm.TabBar.Tabs.Count());
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.ShowDisconnectedOverlay);
        Assert.IsTrue(tab.HasConnectionError);
        Assert.AreEqual(2, prompted);
        await workflow.Received(3).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        StringAssert.Contains(vm.LastConnectionError, "认证失败");
    }

    // Named to match SSH.NET's SshAuthenticationException so the VM's type-name mapping applies.
    private sealed class SshAuthenticationException : Exception
    {
        public SshAuthenticationException(string message) : base(message) { }
    }
}
