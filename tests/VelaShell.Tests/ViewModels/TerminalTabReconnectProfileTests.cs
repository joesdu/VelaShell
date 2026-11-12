using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Presentation.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 终端标签的普通重连从库里重读配置(维护者 2026-10-31 定,§298 记下的欠账):别处刚改的这次就用上、
/// 连上之后不再拿标签里的旧副本整份盖回库里;这次会话现敲、没被记住的口令照样能用;
/// 配置在库里被删了就按标签副本连、提示一句、不写回;本来就不在库里的临时配置照旧。
/// </summary>
/// <remarks>用真的 SonnetDB 仓储与真的连接工作流:「库里那份变没变」只有落到同一个库里才看得出来。SSH 那一层是替身。</remarks>
[TestClass]
[TestCategory("Reconnect")]
public sealed class TerminalTabReconnectProfileTests
{
    private const string Group14Sha1 = "+diffie-hellman-group14-sha1";

    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;
    private readonly List<ConnectionInfo> _connected = [];
    private readonly List<string> _writes = [];

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_reconnect_{Guid.NewGuid():N}");
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

    /// <summary>
    /// 库里那份在标签开着的时候被别处改了(改名、放开算法、换了认证后命令):重连用的是新的那份,现敲的口令照样叠上;
    /// 连上之后库里那份还是别处改过的样子,没记住的口令也没落进去。
    /// </summary>
    [TestMethod]
    public async Task Reconnect_UsesTheLatestProfile_KeepsTheTypedPassword_AndLeavesTheLibraryCopyAlone()
    {
        SessionProfile saved = await SaveAsync(Saved());
        MainWindowViewModel vm = CreateViewModel();
        TerminalTabViewModel tab = await ConnectAsync(vm, TypedCopyOf(saved));

        SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
        edited.Name = "prod (renamed elsewhere)";
        edited.Ssh = new SshSessionOptions { KexAlgorithms = Group14Sha1 };
        edited.PostAuthCommand = "echo changed-elsewhere";
        await _repository.SaveSessionAsync(edited);

        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        ConnectionInfo used = _connected[^1];
        Assert.AreEqual(Group14Sha1, used.Ssh?.KexAlgorithms, "别处刚放开的算法,这次重连就用上");
        Assert.AreEqual("typed-now", used.Password, "这次会话现敲、没让记住的口令照样能用");
        Assert.AreEqual("prod (renamed elsewhere)", tab.Profile!.Name, "标签手里换成了库里最新的那份");
        SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
        Assert.AreEqual("prod (renamed elsewhere)", stored.Name, "库里那份没被标签里的旧副本盖掉");
        Assert.AreEqual(Group14Sha1, stored.Ssh?.KexAlgorithms);
        Assert.AreEqual("echo changed-elsewhere", stored.PostAuthCommand);
        Assert.IsNull(stored.Password, "没让记住的口令不落库");
        Assert.IsNotNull(stored.LastConnectedAt, "最近连接时间照常记");
    }

    /// <summary>
    /// 标签开着的时候配置被删了:按标签里那份连(口令照用),浮层提示一句(自动重连一次次来也只提示一次),不把它写回库里。
    /// </summary>
    [TestMethod]
    public async Task Reconnect_AfterTheProfileWasDeleted_UsesTheTabCopy_TellsTheUserOnce_AndDoesNotResurrectIt()
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
        Assert.AreEqual("typed-now", _connected[^1].Password);
        string notice = Strings.Format("Msg_ReconnectProfileDeleted", "prod");
        Assert.ContainsSingle(t => t.Message == notice, vm.Toasts.Toasts, "提示一句,不是每次重连都提示");
        Assert.IsEmpty(await _repository.GetAllSessionsAsync(), "删掉的配置不会被一次重连写回来");
    }

    /// <summary>本来就不在库里的临时配置(外部拉起、最近连接重建的,§252):照旧按标签副本连,不提示,也不落库。</summary>
    [TestMethod]
    public async Task Reconnect_OfATemporaryProfile_IsUnchanged()
    {
        MainWindowViewModel vm = CreateViewModel();
        TerminalTabViewModel tab = await ConnectAsync(vm, TypedCopyOf(Saved()));

        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreEqual("typed-now", _connected[^1].Password);
        Assert.DoesNotContain(t => t.Message == Strings.Format("Msg_ReconnectProfileDeleted", "prod"), vm.Toasts.Toasts, "临时配置本来就不在库里,不是「被删了」");
        Assert.IsEmpty(await _repository.GetAllSessionsAsync());
    }

    /// <summary>
    /// 「在此处打开终端」开出来的标签:目录挂在标签上(不在配置里),重连从库里重读配置之后照样 cd 回那里,
    /// 不被库里那份的初始目录顶掉。
    /// </summary>
    [TestMethod]
    public async Task Reconnect_OfAnOpenTerminalHereTab_StillCdsIntoItsFolder()
    {
        SessionProfile saved = Saved();
        saved.PostAuthCommand = "echo after-cd";
        saved.PostAuthCommandDelaySeconds = 0;
        saved.Terminal = new TerminalOverrides { StartupDirectory = "/srv/library" };
        await SaveAsync(saved);
        MainWindowViewModel vm = CreateViewModel();

        TerminalTabViewModel? tab = await vm.OpenTerminalHereAsync(TypedCopyOf(saved), "/srv/here");
        Assert.IsNotNull(tab);
        await WaitForWriteAsync("echo after-cd", occurrence: 1);
        SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
        edited.Terminal = new TerminalOverrides { StartupDirectory = "/srv/changed-elsewhere" };
        await _repository.SaveSessionAsync(edited);
        tab.MarkDisconnected();
        await vm.ReconnectTabAsync(tab);
        await WaitForWriteAsync("echo after-cd", occurrence: 2);

        Assert.AreEqual("/srv/here", tab.StartupDirectoryOverride);
        Assert.AreEqual("/srv/changed-elsewhere", tab.Profile!.Terminal!.StartupDirectory, "配置本身照库里最新的");
        lock (_writes)
        {
            Assert.AreEqual(2, _writes.Count(w => w.Contains("cd '/srv/here'", StringComparison.Ordinal)), "首连与重连各 cd 一次");
            Assert.DoesNotContain(w => w.Contains("/srv/library", StringComparison.Ordinal) || w.Contains("/srv/changed-elsewhere", StringComparison.Ordinal),
                _writes, "标签自己的目录压过配置里的初始目录");
        }
    }

    /// <summary>
    /// 首连弹过登录框:框里改的登录字段(用户名、口令与「记住密码」)写进库里那份,其余字段以库里为准;
    /// 没勾记住密码时口令不落库(以前这件事夹在「连上就整份写回」里完成,现在由弹过框的那条路自己写)。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FirstConnect_ThroughTheLoginDialog_SavesOnlyTheLoginFields(bool remember)
    {
        SessionProfile saved = Saved();
        saved.Username = string.Empty;
        await SaveAsync(saved);
        MainWindowViewModel vm = CreateViewModel();
        vm.InteractiveAuthenticator = profile =>
        {
            SessionProfile copy = profile.Clone();
            copy.Name = "name the dialog never touches";
            copy.Username = "deploy";
            copy.Password = "typed-now";
            copy.RememberPassword = remember;
            return Task.FromResult<SessionProfile?>(copy);
        };

        TerminalTabViewModel tab = await ConnectAsync(vm, (await _repository.GetSessionAsync(saved.Id))!);

        Assert.AreEqual("deploy", _connected[^1].Username);
        SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
        Assert.AreEqual("deploy", stored.Username);
        Assert.AreEqual(remember, stored.RememberPassword);
        Assert.AreEqual(remember ? "typed-now" : null, stored.Password);
        Assert.AreEqual("prod", stored.Name, "登录框交回的副本里别的字段不往库里写");
        Assert.IsNotNull(stored.LastConnectedAt);
        Assert.IsTrue(tab.ProfileSeenInLibrary);
    }

    // ---- 工具 ----

    private static SessionProfile Saved() => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        Host = "prod.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        RememberPassword = false,
    };

    /// <summary>标签手里那份:登录框交回的副本,带着这次现敲、没让记住的口令。</summary>
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
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        ssh.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
           .Returns(call =>
           {
               var info = (ConnectionInfo)call[0];
               lock (_connected)
               {
                   _connected.Add(info);
               }
               return new SshSession { ConnectionInfo = info, Status = SessionStatus.Connected };
           });
        ssh.GetClient(Arg.Any<Guid>()).Returns(client);
        client.CreateShellStreamAsync(Arg.Any<string>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<int>(),
                  Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>())
              .Returns(_ => CapturingShell());
        var workflow = new ConnectionWorkflowService(_repository, ssh);
        ITerminalEmulator terminal = FakeTerminal.Emulator();
        return new MainWindowViewModel(workflow, ssh, () => terminal, sessionRepository: _repository);
    }

    /// <summary>一条永远不吐数据的 shell 流(活连接),写进去的每一段都记下来。</summary>
    private IShellStreamWrapper CapturingShell()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanRead.Returns(true);
        shell.CanWrite.Returns(true);
        shell.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(new TaskCompletionSource<int>().Task);
        shell.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 string payload = System.Text.Encoding.UTF8.GetString((byte[])call[0], (int)call[1], (int)call[2]);
                 lock (_writes)
                 {
                     _writes.Add(payload);
                 }
                 return Task.CompletedTask;
             });
        return shell;
    }

    /// <summary>等到第 <paramref name="occurrence" /> 段含 <paramref name="marker" /> 的写入(注入推迟到对端安静之后,替身流一路等到 3 秒上限)。</summary>
    private async Task WaitForWriteAsync(string marker, int occurrence)
    {
        for (int i = 0; i < 500; i++)
        {
            lock (_writes)
            {
                if (_writes.Count(w => w.Contains(marker, StringComparison.Ordinal)) >= occurrence)
                {
                    return;
                }
            }
            await Task.Delay(20);
        }
        Assert.Fail($"等了 10 秒,第 {occurrence} 次「{marker}」还没写出去。");
    }
}
