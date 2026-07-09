using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Presentation.Services;
using PulseTerm.Terminal;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public sealed class InteractiveAuthFlowTests
{
    private static SshSession CreateSession(SessionProfile profile) => new()
    {
        SessionId = Guid.NewGuid(),
        ConnectionInfo = new ConnectionInfo
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
            Password = profile.Password,
        },
        Status = SessionStatus.Connected,
    };

    [TestMethod]
    public async Task MissingPassword_PromptsAuthenticator_BeforeConnecting()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var ssh = Substitute.For<ISshConnectionService>();
        var client = Substitute.For<ISshClientWrapper>();
        var shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(false);

        var profile = new SessionProfile { Name = "P", Host = "h", Port = 22, Username = "root", AuthMethod = AuthMethod.Password };

        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(args => CreateSession((SessionProfile)args[0]));
        ssh.GetClient(Arg.Any<Guid>()).Returns(client);
        client.CreateShellStream("xterm-256color", 120, 32, 0, 0, 4096, null).Returns(shell);

        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());
        var prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            p.Password = "prompted-pass";
            return Task.FromResult<SessionProfile?>(p);
        };

        var tab = await vm.TryConnectProfileAsync(profile);

        Assert.AreEqual(1, prompted);
        Assert.IsNotNull(tab);
        await workflow.Received(1).ConnectProfileAsync(
            Arg.Is<SessionProfile>(p => p.Password == "prompted-pass"), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task PromptCancelled_ReturnsNull_WithoutError()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var ssh = Substitute.For<ISshConnectionService>();
        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());
        vm.InteractiveAuthenticator = _ => Task.FromResult<SessionProfile?>(null);

        var profile = new SessionProfile { Name = "P", Host = "h", Username = "root", AuthMethod = AuthMethod.Password };
        var tab = await vm.TryConnectProfileAsync(profile);

        Assert.IsNull(tab);
        Assert.IsNull(vm.LastConnectionError);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task AuthFailure_RepromptsUpToThreeAttempts()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var ssh = Substitute.For<ISshConnectionService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns<Task<SshSession>>(_ => throw new SshAuthenticationException("denied"));

        var vm = new MainWindowViewModel(workflow, ssh, () => Substitute.For<ITerminalEmulator>());
        var prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            p.Password = $"try-{prompted}";
            return Task.FromResult<SessionProfile?>(p);
        };

        // 密码已填 → 第一次直接连接失败,随后重试两次均需弹窗。
        var profile = new SessionProfile { Name = "P", Host = "h", Username = "root", AuthMethod = AuthMethod.Password, Password = "wrong" };
        var tab = await vm.TryConnectProfileAsync(profile);

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
