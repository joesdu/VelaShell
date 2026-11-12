using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 主密码锁着时的连接与重连(velashell-docs《主密码保护设计》§6 那张表):自动重连不弹框、标签说明已锁定、
/// 解锁后各重试一次;手动重连与主动连接弹框;取消了解锁,锁定前留在内存里的明文也不拿去用。
/// </summary>
[TestClass]
public sealed class MasterPasswordConnectFlowTests
{
    [TestMethod]
    public async Task AutoReconnect_WhileLocked_DoesNotPrompt_SaysWhy_AndRetriesOnceAfterUnlock()
    {
        var gate = new FakeGate { IsLocked = true };
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, TaskCompletionSource called) = DisconnectedTab(gate);

        await vm.AutoReconnectTabAsync(tab);

        Assert.AreEqual(0, gate.InteractiveRequests, "自动重连不弹解锁框");
        await workflow.DidNotReceiveWithAnyArgs().ReconnectProfileAutomaticallyAsync(default!, default);
        Assert.AreEqual(Strings.Get("MasterPassword_AutoReconnectLocked"), vm.LastConnectionError);
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);

        gate.IsLocked = false;
        vm.OnSecretsUnlocked();
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await workflow.Received(1).ReconnectProfileAutomaticallyAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());

        vm.OnSecretsUnlocked();
        await workflow.Received(1).ReconnectProfileAutomaticallyAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ManualReconnect_WhileLocked_Prompts_AndProceedsOnceUnlocked()
    {
        var gate = new FakeGate { IsLocked = true, UnlockOnPrompt = true };
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, _) = DisconnectedTab(gate);

        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(1, gate.InteractiveRequests, "手动重连是交互式:弹解锁框");
        await workflow.Received(1).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ManualReconnect_UnlockCancelled_DoesNotUseThePasswordLeftInMemory()
    {
        var gate = new FakeGate { IsLocked = true };
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, _) = DisconnectedTab(gate);

        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(1, gate.InteractiveRequests);
        await workflow.DidNotReceiveWithAnyArgs().ConnectProfileAsync(default!, default);
        Assert.AreEqual(Strings.Get("MasterPassword_ReconnectLocked"), vm.LastConnectionError);
    }

    [TestMethod]
    public async Task Connect_UnlockCancelled_AsksForThePasswordInstead()
    {
        var gate = new FakeGate { IsLocked = true };
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationCancelledException("stop here"));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(),
            masterPassword: gate);
        SessionProfile? asked = null;
        vm.InteractiveAuthenticator = p =>
        {
            asked = p.Clone();
            p.Password = "typed";
            return Task.FromResult<SessionProfile?>(p);
        };

        await vm.TryConnectProfileAsync(Profile());

        Assert.AreEqual(1, gate.InteractiveRequests);
        Assert.IsNotNull(asked, "没解锁:登录框问这一次的密码");
        Assert.IsNull(asked.Password, "锁定前留在内存里的那份明文不拿去用");
        await workflow.Received(1).ConnectProfileAsync(Arg.Is<SessionProfile>(p => p.Password == "typed"), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Connect_WithoutSavedSecrets_NeverPrompts()
    {
        var gate = new FakeGate { IsLocked = true };
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationCancelledException("stop here"));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(),
            masterPassword: gate);
        SessionProfile keyOnly = new() { Name = "k", Host = "h", Port = 22, Username = "root", AuthMethod = AuthMethod.PrivateKey, PrivateKeyPath = "/k" };

        await vm.TryConnectProfileAsync(keyOnly);

        Assert.AreEqual(0, gate.InteractiveRequests, "用不到已存机密的连接不过闸");
    }

    private static SessionProfile Profile() =>
        new() { Name = "P", Host = "h", Port = 22, Username = "root", AuthMethod = AuthMethod.Password, Password = "pw-in-memory" };

    private static (MainWindowViewModel Vm, TerminalTabViewModel Tab, IConnectionWorkflowService Workflow, TaskCompletionSource Called) DisconnectedTab(FakeGate gate)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ =>
                {
                    called.TrySetResult();
                    throw new VelaSshAuthenticationException("denied");
                });
        workflow.ReconnectProfileAutomaticallyAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ =>
                {
                    called.TrySetResult();
                    throw new VelaSshAuthenticationException("denied");
                });
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(),
            masterPassword: gate);
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = Profile(),
            ConnectionStatus = SessionStatus.Disconnected,
        };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        return (vm, tab, workflow, called);
    }
    /// <summary>假的闸口:数交互式请求;<see cref="UnlockOnPrompt" /> 时交互式请求就当用户解开了。</summary>
    private sealed class FakeGate : IMasterPasswordGate
    {
        public bool IsEnabled => true;

        public bool IsLocked { get; set; }

        public bool UnlockOnPrompt { get; init; }

        public int InteractiveRequests { get; private set; }

        public event EventHandler? Unlocked;

        public event EventHandler? Locked;

        public Task<bool> EnsureUnlockedAsync(bool interactive, CancellationToken cancellationToken = default)
        {
            if (!IsLocked)
            {
                return Task.FromResult(true);
            }
            if (!interactive)
            {
                return Task.FromResult(false);
            }
            InteractiveRequests++;
            if (UnlockOnPrompt)
            {
                IsLocked = false;
                Unlocked?.Invoke(this, EventArgs.Empty);
            }
            return Task.FromResult(!IsLocked);
        }

        public void RaiseLocked() => Locked?.Invoke(this, EventArgs.Empty);
    }
}
