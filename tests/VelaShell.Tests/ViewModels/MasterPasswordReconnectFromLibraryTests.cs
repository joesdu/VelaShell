using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 重连先从库里重读、再过主密码的闸口:标签开着时库里那份被改成了存着密码 / 插件机密(编辑框、云同步),
/// 标签手里那份却什么机密都没有 —— 闸口看的是重读之后的那份,锁着时手动重连弹框、自动重连不弹并在解锁后重试,
/// 密文绝不拿去连,也绝不交给插件。
/// </summary>
/// <remarks>
/// 真的 SonnetDB 仓储;保护器在解着时用真的 AES 保护器加解密,锁着时照真实契约(新的明文加不了密、载入交回密文原样),
/// 与假闸口共用同一个「锁没锁」。SSH 那一层与插件终端都是替身。
/// </remarks>
[TestClass]
[TestCategory("Reconnect")]
public sealed class MasterPasswordReconnectFromLibraryTests
{
    private const string ProtocolId = "test.telnet";

    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;
    private readonly LockState _lock = new();

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_mpw_reconnect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = new SonnetDbEngine(Path.Combine(_directory, "sonnetdb"));
        _repository = new SonnetDbSessionRepository(
            _engine, new LockableProtector(new AesSecretProtector(Path.Combine(_directory, "secret.key")), _lock));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // 临时目录,删不掉不影响结论。
        }
    }

    /// <summary>手动重连:看的是库里那份(刚存上了密码),锁着就弹框;解开之后用库里那份的明文去连。</summary>
    [TestMethod]
    public async Task ManualReconnect_WhenTheLibraryCopyGainedAPassword_PromptsAndConnectsWithThePlaintext()
    {
        var gate = new FakeGate(_lock) { UnlockOnPrompt = true };
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, _) =
            await SshTabWhoseLibraryCopyGainedAPasswordAsync(gate);

        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(1, gate.InteractiveRequests, "标签副本里没有密码,库里那份有:照样弹解锁框");
        await workflow.Received(1).ConnectProfileAsync(
            Arg.Is<SessionProfile>(p => p.Password == "saved-elsewhere"), Arg.Any<CancellationToken>());
        await workflow.DidNotReceive().ConnectProfileAsync(
            Arg.Is<SessionProfile>(p => StoredSecret.IsUnreadable(p.Password)), Arg.Any<CancellationToken>());
    }

    /// <summary>手动重连、取消了解锁:不连(不拿密文去连、不误报「解不开」),标签上那份也不换成带密文的。</summary>
    [TestMethod]
    public async Task ManualReconnect_WhenTheLibraryCopyGainedAPassword_AndUnlockIsCancelled_DoesNotConnect()
    {
        var gate = new FakeGate(_lock);
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, _) =
            await SshTabWhoseLibraryCopyGainedAPasswordAsync(gate);

        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(1, gate.InteractiveRequests);
        await workflow.DidNotReceiveWithAnyArgs().ConnectProfileAsync(default!, default);
        Assert.AreEqual(Strings.Get("MasterPassword_ReconnectLocked"), vm.LastConnectionError);
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsNull(tab.Profile!.Password, "标签上那份不动:不换成锁着时读到的密文");
    }

    /// <summary>自动重连:不弹框、不连、说明已锁定;解锁之后重试一次,用库里那份的明文。</summary>
    [TestMethod]
    public async Task AutoReconnect_WhenTheLibraryCopyGainedAPassword_WaitsForTheUnlock_ThenRetriesWithThePlaintext()
    {
        var gate = new FakeGate(_lock);
        (MainWindowViewModel vm, TerminalTabViewModel tab, IConnectionWorkflowService workflow, TaskCompletionSource called) =
            await SshTabWhoseLibraryCopyGainedAPasswordAsync(gate);

        await vm.AutoReconnectTabAsync(tab);

        Assert.AreEqual(0, gate.InteractiveRequests, "自动重连不弹解锁框");
        await workflow.DidNotReceiveWithAnyArgs().ReconnectProfileAutomaticallyAsync(default!, default);
        Assert.AreEqual(Strings.Get("MasterPassword_AutoReconnectLocked"), vm.LastConnectionError);

        _lock.Locked = false;
        vm.OnSecretsUnlocked();
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await workflow.Received(1).ReconnectProfileAutomaticallyAsync(
            Arg.Is<SessionProfile>(p => p.Password == "saved-elsewhere"), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 插件终端:库里那份在标签开着时存上了插件机密。锁着时自动重连、手动重连(取消了解锁)都不连,
    /// 交给插件的东西里从来没有密文;解锁之后的那次重试交出的是明文。
    /// </summary>
    [TestMethod]
    public async Task PluginTerminalReconnect_WhileLocked_NeverHandsCiphertextToThePlugin()
    {
        var terminal = new RecordingTerminal();
        var gate = new FakeGate(_lock);
        SessionProfile saved = new()
        {
            Id = Guid.NewGuid(),
            Name = "switch-01",
            Host = "10.0.0.9",
            Port = 23,
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = ProtocolId,
            PluginSettings = new(StringComparer.Ordinal) { ["enterMode"] = "crlf" },
        };
        await _repository.SaveSessionAsync(saved);
        MainWindowViewModel vm = CreatePluginViewModel(terminal, gate);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(saved.Clone());
        Assert.IsNotNull(tab);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);

        SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
        edited.PluginSecrets = new(StringComparer.Ordinal) { ["token"] = "s3cret" };
        await _repository.SaveSessionAsync(edited);
        _lock.Locked = true;
        tab.MarkDisconnected();

        await vm.AutoReconnectTabAsync(tab);
        Assert.AreEqual(0, gate.InteractiveRequests, "自动重连不弹解锁框");
        Assert.AreEqual(Strings.Get("MasterPassword_AutoReconnectLocked"), vm.LastConnectionError);
        await vm.ReconnectTabAsync(tab);
        Assert.AreEqual(1, gate.InteractiveRequests, "手动重连弹框(这次取消了)");
        Assert.AreEqual(1, terminal.Count, "锁着时两次重连都没去连");
        Assert.IsNull(tab.Profile!.PluginSecrets, "标签上那份不换成带密文的");

        _lock.Locked = false;
        vm.OnSecretsUnlocked();
        await terminal.WaitForCountAsync(2, TimeSpan.FromSeconds(10));

        ProtocolConnectRequest[] requests = terminal.Snapshot();
        Assert.AreEqual("s3cret", requests[^1].GetString("token"), "解锁之后的重试交出明文");
        foreach (ProtocolConnectRequest request in requests)
        {
            Assert.IsFalse(StoredSecret.IsUnreadable(request.Password));
            Assert.IsFalse(request.Settings.Values.Any(StoredSecret.IsUnreadable), "交给插件的设置里从来没有密文");
        }
    }

    /// <summary>
    /// 插件终端,锁着时主动连接、取消了解锁:插件机密没有登录框可问,不能剥成空、对插件装作「没设」——
    /// 说明它们被主密码锁着,这次不连;也不先说一句「请在登录框里手输」。
    /// </summary>
    [TestMethod]
    public async Task PluginTerminalConnect_WhileLocked_AndUnlockCancelled_SaysTheSecretsAreLocked_InsteadOfConnectingWithoutThem()
    {
        var terminal = new RecordingTerminal();
        var gate = new FakeGate(_lock);
        SessionProfile saved = new()
        {
            Id = Guid.NewGuid(),
            Name = "switch-01",
            Host = "10.0.0.9",
            Port = 23,
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = ProtocolId,
            PluginSecrets = new(StringComparer.Ordinal) { ["token"] = "s3cret" },
        };
        await _repository.SaveSessionAsync(saved);
        SessionProfile loaded = (await _repository.GetSessionAsync(saved.Id))!;
        Assert.AreEqual("s3cret", loaded.PluginSecrets!["token"], "锁定前载入的:内存里是明文");
        _lock.Locked = true;
        MainWindowViewModel vm = CreatePluginViewModel(terminal, gate);

        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(loaded);

        Assert.AreEqual(1, gate.InteractiveRequests, "主动连接:弹解锁框(这次取消了)");
        Assert.AreEqual(0, terminal.Count, "不拿锁定前留在内存里的明文去连,也不剥成空去连");
        Assert.IsNotNull(tab);
        string expected = Strings.Format("Plugin_UnreadableSecretLocked",
            Strings.Format("Plugin_UnreadableSecretSubject", Strings.Format("Plugin_SecretLabelQuoted", "Token")));
        Assert.AreEqual(expected, vm.LastConnectionError);
        Assert.DoesNotContain(t => t.Message == Strings.Get("MasterPassword_ConnectWhileLocked"), vm.Toasts.Toasts);
    }

    // ---- 工具 ----

    /// <summary>
    /// 库里一条不存密码的 SSH 配置,标签手里是它那份(断开着);标签开着时别处给库里那份存上了密码,随后锁定。
    /// </summary>
    private async Task<(MainWindowViewModel Vm, TerminalTabViewModel Tab, IConnectionWorkflowService Workflow, TaskCompletionSource Called)>
        SshTabWhoseLibraryCopyGainedAPasswordAsync(FakeGate gate)
    {
        SessionProfile saved = new()
        {
            Name = "web", Host = "web.example", Port = 22, Username = "root", AuthMethod = AuthMethod.Password, RememberPassword = false
        };
        await _repository.SaveSessionAsync(saved);

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
            sessionRepository: _repository, masterPassword: gate);
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = saved.Clone(),
            ConnectionStatus = SessionStatus.Disconnected,
        };
        vm.Layout.AddDocument(new TerminalDocument(tab));

        SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
        edited.RememberPassword = true;
        edited.Password = "saved-elsewhere";
        await _repository.SaveSessionAsync(edited);
        _lock.Locked = true;
        Assert.IsTrue(StoredSecret.IsUnreadable((await _repository.GetSessionAsync(saved.Id))!.Password), "锁着时载入的是密文原样");
        return (vm, tab, workflow, called);
    }

    private MainWindowViewModel CreatePluginViewModel(RecordingTerminal terminal, FakeGate gate)
    {
        var registry = new PluginProtocolRegistry();
        registry.Register("test", new ProtocolDescriptor
        {
            Id = ProtocolId,
            DisplayName = "Telnet",
            DefaultPort = 23,
            Fields =
            [
                new() { Key = "enterMode", Label = "Enter", DefaultValue = "crlf" },
                new() { Key = "token", Label = "Token", IsSecret = true },
            ],
        }, terminal);
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        return new MainWindowViewModel(terminalEmulatorFactory: () => emulator, sessionRepository: _repository,
            protocolRegistry: registry, masterPassword: gate);
    }

    /// <summary>保护器与假闸口共用的「锁没锁」。</summary>
    private sealed class LockState
    {
        private volatile bool _locked;

        public bool Locked
        {
            get => _locked;
            set => _locked = value;
        }
    }

    /// <summary>
    /// 锁得上的保护器:解着时交给真的 AES 保护器;锁着时照真实契约 —— 新的明文加不了密(抛 <see cref="SecretsLockedException" />),
    /// 已经是密文的照存,载入时密文原样交回。
    /// </summary>
    private sealed class LockableProtector(AesSecretProtector inner, LockState state) : ISecretProtector
    {
        public bool IsLocked => state.Locked;

        public string? Protect(string? plaintext) =>
            state.Locked && !string.IsNullOrEmpty(plaintext) && !StoredSecret.IsUnreadable(plaintext)
                ? throw new SecretsLockedException()
                : inner.Protect(plaintext);

        public string? Unprotect(string? ciphertext) => state.Locked ? ciphertext : inner.Unprotect(ciphertext);

        public bool TryUnprotect(string? ciphertext, out string? plaintext)
        {
            if (state.Locked && StoredSecret.IsUnreadable(ciphertext))
            {
                plaintext = null;
                return false;
            }
            return inner.TryUnprotect(ciphertext, out plaintext);
        }
    }

    /// <summary>假的闸口:数交互式请求;<see cref="UnlockOnPrompt" /> 时交互式请求就当用户解开了。</summary>
    private sealed class FakeGate(LockState state) : IMasterPasswordGate
    {
        public bool IsEnabled => true;

        public bool IsLocked => state.Locked;

        public bool UnlockOnPrompt { get; init; }

        public int InteractiveRequests { get; private set; }

        public event EventHandler? Unlocked;

        public event EventHandler? Locked;

        public Task<bool> EnsureUnlockedAsync(bool interactive, CancellationToken cancellationToken = default)
        {
            if (!state.Locked)
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
                state.Locked = false;
                Unlocked?.Invoke(this, EventArgs.Empty);
            }
            return Task.FromResult(!state.Locked);
        }

        public void RaiseLocked() => Locked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>记下每次连接请求的替身终端协议;每次连接给一条读侧一直挂着的会话。</summary>
    private sealed class RecordingTerminal : IProtocolTerminal
    {
        private readonly List<ProtocolConnectRequest> _requests = [];

        public int Count
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }

        public ProtocolConnectRequest[] Snapshot()
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            using var timer = new CancellationTokenSource(timeout);
            while (Count < count)
            {
                await Task.Delay(10, timer.Token);
            }
        }

        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken cancellationToken = default)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }
            return Task.FromResult<IProtocolTerminalSession>(new Session());
        }

        private sealed class Session : IProtocolTerminalSession
        {
            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

            public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
