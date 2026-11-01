using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 插件终端协议(Telnet、串口…)的重连与 SSH 标签同一个口径(维护者 2026-10-31 定):从库里重读配置,别处刚改的协议设置这次就用上;
/// 只在登录身份没变时叠上这次会话现敲的口令;配置被删了按标签副本连、提示一次、不写回;本来就不在库里的临时配置照旧。
/// </summary>
/// <remarks>真的 SonnetDB 仓储 + 真的协议注册表,插件那一侧是一个记下每次连接请求的替身终端。</remarks>
[TestClass]
[TestCategory("Reconnect")]
public sealed class PluginTerminalReconnectProfileTests
{
    private const string ProtocolId = "test.telnet";

    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;
    private readonly RecordingTerminal _terminal = new();

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_plugin_reconnect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = new SonnetDbEngine(Path.Combine(_directory, "sonnetdb"));
        _repository = new SonnetDbSessionRepository(_engine, new AesSecretProtector(Path.Combine(_directory, "secret.key")));
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

    /// <summary>库里那份的协议设置在标签开着时被别处改了:重连交给插件的是新值,现敲的口令照样带上,库里那份不被盖。</summary>
    [TestMethod]
    public async Task Reconnect_UsesTheLatestPluginSettings_AndKeepsTheTypedPassword()
    {
        SessionProfile saved = await SaveAsync(Saved());
        MainWindowViewModel vm = CreateViewModel();
        TerminalTabViewModel tab = await ConnectAsync(vm, TypedCopyOf(saved));
        Assert.AreEqual("crlf", _terminal.Requests[^1].GetString("enterMode"));

        SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
        edited.PluginSettings = new(StringComparer.Ordinal) { ["enterMode"] = "crnul" };
        edited.Name = "switch-01 (renamed elsewhere)";
        await _repository.SaveSessionAsync(edited);

        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        ProtocolConnectRequest request = _terminal.Requests[^1];
        Assert.AreEqual("crnul", request.GetString("enterMode"), "别处刚改的协议设置,这次重连就交给插件");
        Assert.AreEqual("typed-now", request.Password, "这次会话现敲、没让记住的口令照样能用");
        Assert.AreEqual("switch-01 (renamed elsewhere)", tab.Profile!.Name);
        SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
        Assert.AreEqual("crnul", stored.PluginSettings!["enterMode"]);
        Assert.IsNull(stored.Password, "没让记住的口令不落库");
    }

    /// <summary>别处把这条配置换成了另一个协议:这个标签是 Telnet 终端,照旧按标签副本连,口令也不跟到别的协议上去。</summary>
    [TestMethod]
    public async Task Reconnect_AfterTheProtocolWasChangedElsewhere_KeepsTheTabsOwnCopy()
    {
        SessionProfile saved = await SaveAsync(Saved());
        MainWindowViewModel vm = CreateViewModel();
        TerminalTabViewModel tab = await ConnectAsync(vm, TypedCopyOf(saved));
        SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
        edited.PluginProtocolId = "test.serial";
        await _repository.SaveSessionAsync(edited);

        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreEqual(ProtocolId, tab.Profile!.PluginProtocolId);
        Assert.HasCount(2, _terminal.Requests);
    }

    /// <summary>标签开着时配置被删了:按标签副本连(口令照用),提示一次,不写回库里。</summary>
    [TestMethod]
    public async Task Reconnect_AfterTheProfileWasDeleted_UsesTheTabCopy_AndTellsTheUserOnce()
    {
        SessionProfile saved = await SaveAsync(Saved());
        MainWindowViewModel vm = CreateViewModel();
        TerminalTabViewModel tab = await ConnectAsync(vm, TypedCopyOf(saved));
        await _repository.DeleteSessionAsync(saved.Id);

        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);
        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreEqual("crlf", _terminal.Requests[^1].GetString("enterMode"));
        Assert.AreEqual("typed-now", _terminal.Requests[^1].Password);
        string notice = Strings.Format("Msg_ReconnectProfileDeleted", "switch-01");
        Assert.AreEqual(1, vm.Toasts.Toasts.Count(t => t.Message == notice));
        Assert.IsEmpty(await _repository.GetAllSessionsAsync());
    }

    /// <summary>本来就不在库里的临时配置:照旧按标签副本连,不提示,不落库。</summary>
    [TestMethod]
    public async Task Reconnect_OfATemporaryProfile_IsUnchanged()
    {
        MainWindowViewModel vm = CreateViewModel();
        TerminalTabViewModel tab = await ConnectAsync(vm, TypedCopyOf(Saved()));

        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreEqual("typed-now", _terminal.Requests[^1].Password);
        Assert.DoesNotContain(t => t.Message == Strings.Format("Msg_ReconnectProfileDeleted", "switch-01"), vm.Toasts.Toasts);
        Assert.IsEmpty(await _repository.GetAllSessionsAsync());
    }

    // ---- 工具 ----

    private static SessionProfile Saved() => new()
    {
        Id = Guid.NewGuid(),
        Name = "switch-01",
        Host = "10.0.0.9",
        Port = 23,
        Username = "admin",
        AuthMethod = AuthMethod.Password,
        RememberPassword = false,
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = ProtocolId,
        PluginSettings = new(StringComparer.Ordinal) { ["enterMode"] = "crlf" },
    };

    /// <summary>标签手里那份:带着这次现敲、没让记住的口令。</summary>
    private static SessionProfile TypedCopyOf(SessionProfile saved)
    {
        SessionProfile copy = saved.Clone();
        copy.Password = "typed-now";
        return copy;
    }

    private async Task<SessionProfile> SaveAsync(SessionProfile profile)
    {
        await _repository.SaveSessionAsync(profile);
        return profile;
    }

    private static async Task<TerminalTabViewModel> ConnectAsync(MainWindowViewModel vm, SessionProfile profile)
    {
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        return tab;
    }

    private MainWindowViewModel CreateViewModel()
    {
        var registry = new PluginProtocolRegistry();
        registry.Register("test", new ProtocolDescriptor
        {
            Id = ProtocolId,
            DisplayName = "Telnet",
            DefaultPort = 23,
            Fields = [new() { Key = "enterMode", Label = "Enter", DefaultValue = "crlf" }],
        }, _terminal);
        ITerminalEmulator terminal = FakeTerminal.Emulator();
        return new MainWindowViewModel(terminalEmulatorFactory: () => terminal, sessionRepository: _repository, protocolRegistry: registry);
    }

    /// <summary>记下每次连接请求的替身终端协议;每次连接给一条新的、读侧一直挂着的会话(真实传输阻塞在套接字上就是这个样子)。</summary>
    private sealed class RecordingTerminal : IProtocolTerminal
    {
        public List<ProtocolConnectRequest> Requests { get; } = [];

        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
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
