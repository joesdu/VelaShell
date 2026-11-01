using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 同一标签的重连不并行:读库、过主密码闸口(可能停在解锁框上)的这段时间标签还是断开的,
/// 第二个请求(连按回车、自动重连计时器到点)并入正在进行的那一个,不再各连一次。
/// </summary>
[TestClass]
[TestCategory("Reconnect")]
public sealed class ReconnectReentrancyTests
{
    /// <summary>第一个重连还在读库:再按一次回车、自动重连到点,都不另起一次连接。</summary>
    [TestMethod]
    public async Task SecondRequest_WhileTheFirstIsReadingTheLibrary_IsMergedIntoIt()
    {
        SessionProfile saved = Profile();
        var read = new TaskCompletionSource<SessionProfile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(saved.Id).Returns(_ => read.Task);
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow) = DisconnectedTab(saved, repository, gate: null);

        Task first = vm.ReconnectTabAsync(tab);
        Assert.IsFalse(first.IsCompleted, "第一个停在读库上");
        await vm.ReconnectTabAsync(tab).WaitAsync(TimeSpan.FromSeconds(10));
        await vm.AutoReconnectTabAsync(tab).WaitAsync(TimeSpan.FromSeconds(10));
        read.SetResult(saved.Clone());
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        await workflow.Received(1).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        await workflow.DidNotReceiveWithAnyArgs().ReconnectProfileAutomaticallyAsync(default!, default);
    }

    /// <summary>第一个停在解锁框上:第二个手动重连不再弹一个框,自动重连也不排进「解锁后重试」;解开之后只连一次。</summary>
    [TestMethod]
    public async Task SecondRequest_WhileTheFirstWaitsForTheUnlockPrompt_IsMergedIntoIt()
    {
        SessionProfile saved = Profile();
        saved.Password = "saved";
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetSessionAsync(saved.Id).Returns(_ => Task.FromResult<SessionProfile?>(saved.Clone()));
        var gate = new HoldingGate();
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow) = DisconnectedTab(saved, repository, gate);

        Task first = vm.ReconnectTabAsync(tab);
        await gate.Prompted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await vm.ReconnectTabAsync(tab).WaitAsync(TimeSpan.FromSeconds(10));
        await vm.AutoReconnectTabAsync(tab).WaitAsync(TimeSpan.FromSeconds(10));
        gate.Unlock();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        vm.OnSecretsUnlocked();

        Assert.AreEqual(1, gate.InteractiveRequests, "同一时刻只有第一个在问");
        await workflow.Received(1).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        await workflow.DidNotReceiveWithAnyArgs().ReconnectProfileAutomaticallyAsync(default!, default);
    }

    // ---- 工具 ----

    private static SessionProfile Profile() =>
        new() { Id = Guid.NewGuid(), Name = "web", Host = "web.example", Port = 22, Username = "root", AuthMethod = AuthMethod.Password };

    private static (MainWindowViewModel Vm, TerminalTabViewModel Tab, IConnectionWorkflowService Workflow) DisconnectedTab(
        SessionProfile saved, ISessionRepository repository, IMasterPasswordGate? gate)
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationException("denied"));
        workflow.ReconnectProfileAutomaticallyAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationException("denied"));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(),
            sessionRepository: repository, masterPassword: gate);
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = saved.Clone(),
            ConnectionStatus = SessionStatus.Disconnected,
        };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        return (vm, tab, workflow);
    }

    /// <summary>锁着的闸口:交互式请求停在「解锁框」上,等测试 <see cref="Unlock" />。</summary>
    private sealed class HoldingGate : IMasterPasswordGate
    {
        private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _interactiveRequests;
        private volatile bool _locked = true;

        public bool IsEnabled => true;

        public bool IsLocked => _locked;

        public int InteractiveRequests => Volatile.Read(ref _interactiveRequests);

        public TaskCompletionSource Prompted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? Unlocked;

        public event EventHandler? Locked;

        public Task<bool> EnsureUnlockedAsync(bool interactive, CancellationToken cancellationToken = default)
        {
            if (!_locked)
            {
                return Task.FromResult(true);
            }
            if (!interactive)
            {
                return Task.FromResult(false);
            }
            Interlocked.Increment(ref _interactiveRequests);
            Prompted.TrySetResult();
            return _answer.Task;
        }

        public void Unlock()
        {
            _locked = false;
            _answer.TrySetResult(true);
        }

        public void RaiseLocked() => Locked?.Invoke(this, EventArgs.Empty);

        public void RaiseUnlocked() => Unlocked?.Invoke(this, EventArgs.Empty);
    }
}
