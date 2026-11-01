using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// FTP / FTPS、插件协议文件标签与工作台弹过登录框、认证通过之后,与 SSH 一样把框里改得到的几项写进库里那份
/// (经 <see cref="IConnectionWorkflowService.SaveLoginChangesAsync" />,照「记住密码」与常规页总开关;临时配置不写)——
/// 以前这几条路径从不落库,登录框上勾了「记住密码」下次照样再问(只在恰好撞上证书提示时随整份写回顺带存过)。
/// </summary>
/// <remarks>真的 SonnetDB 仓储与真的连接工作流;FTP / 插件会话服务与工作台是替身,一次就连上。</remarks>
[TestClass]
[TestCategory("FtpConnectionFlow")]
public sealed class DocumentLoginPromptPersistenceTests
{
    private const string WorkspaceId = "acme.cache";

    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;

    /// <summary>建文档途中会碰 <c>Dispatcher.UIThread</c>,先把 headless 会话起起来(同 FtpConnectionFlowTests)。</summary>
    [ClassInitialize]
    public static void Init(TestContext _) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DocumentLoginPromptPersistenceTests).Assembly);

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_doclogin_{Guid.NewGuid():N}");
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
    /// 弹过登录框、连上了:用户名、口令与「记住密码」写进库里那份(没勾记住时口令不落盘、勾选状态照记),
    /// 别处这段时间里改过的其余字段以库里为准。
    /// </summary>
    [TestMethod]
    [DataRow("ftp", true)]
    [DataRow("ftp", false)]
    [DataRow("plugin", true)]
    [DataRow("plugin", false)]
    [DataRow("workspace", true)]
    [DataRow("workspace", false)]
    public Task AfterTheLoginDialog_TheLoginFieldsAreSaved_AsTheDialogSaid(string kind, bool remember) =>
        // 理由同 FtpConnectionFlowTests:连接链路全程 ConfigureAwait(true),不在 headless UI 线程上等它。
        Task.Run(async () =>
        {
            SessionProfile saved = Profile(kind);
            await _repository.SaveSessionAsync(saved);
            SessionProfile stale = saved.Clone();
            SessionProfile edited = (await _repository.GetSessionAsync(saved.Id))!;
            edited.Name = "renamed elsewhere";
            await _repository.SaveSessionAsync(edited);
            MainWindowViewModel vm = CreateViewModel();
            int prompts = 0;
            vm.InteractiveAuthenticator = profile =>
            {
                prompts++;
                SessionProfile copy = profile.Clone();
                copy.Username = "deploy";
                copy.Password = "typed-now";
                copy.RememberPassword = remember;
                return Task.FromResult<SessionProfile?>(copy);
            };

            Assert.IsTrue(await OpenAsync(vm, kind, stale), "替身一次就连上");

            Assert.AreEqual(1, prompts, "库里那份没存口令:先弹登录框");
            SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
            Assert.AreEqual("deploy", stored.Username, "登录框里改的用户名记下");
            Assert.AreEqual(remember, stored.RememberPassword, "「记住密码」的勾选照记");
            Assert.AreEqual(remember ? "typed-now" : null, stored.Password, "勾了记住才落口令");
            Assert.AreEqual("renamed elsewhere", stored.Name, "别处改过的字段以库里为准,不被文档手里的旧副本盖掉");
        });

    /// <summary>没弹登录框(库里存着口令)就什么都不写:连接手里那份的字段不往库里搬。</summary>
    [TestMethod]
    public Task WithoutTheLoginDialog_NothingIsWritten() =>
        Task.Run(async () =>
        {
            SessionProfile saved = Profile("ftp");
            saved.Username = "stored-user";
            saved.Password = "stored-pw";
            saved.RememberPassword = true;
            await _repository.SaveSessionAsync(saved);
            SessionProfile stale = saved.Clone();
            stale.Name = "stale name in the tab";
            MainWindowViewModel vm = CreateViewModel();
            vm.InteractiveAuthenticator = _ => throw new AssertFailedException("库里有口令,不该弹框");

            Assert.IsTrue(await OpenAsync(vm, "ftp", stale));

            Assert.AreEqual("ftp-box", (await _repository.GetSessionAsync(saved.Id))!.Name);
        });

    /// <summary>不在库里的临时配置(外部拉起、「最近连接」重建的,§252):登录框交回的那份同样不落库。</summary>
    [TestMethod]
    public Task TemporaryProfile_IsNotSaved() =>
        Task.Run(async () =>
        {
            MainWindowViewModel vm = CreateViewModel();
            vm.InteractiveAuthenticator = profile =>
            {
                SessionProfile copy = profile.Clone();
                copy.Username = "deploy";
                copy.Password = "typed-now";
                return Task.FromResult<SessionProfile?>(copy);
            };

            Assert.IsTrue(await OpenAsync(vm, "ftp", Profile("ftp")));

            Assert.IsEmpty(await _repository.GetAllSessionsAsync());
        });

    // ---- 工具 ----

    private static SessionProfile Profile(string kind) => kind switch
    {
        "ftp" => new()
        {
            Id = Guid.NewGuid(),
            ConnectionType = ConnectionType.FTP,
            Name = "ftp-box",
            Host = "ftp.example.com",
            Port = 21,
            AuthMethod = AuthMethod.Password,
            RememberPassword = false,
            Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.Explicit },
        },
        "plugin" => new()
        {
            Id = Guid.NewGuid(),
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = "test.s3",
            Name = "minio",
            Host = "minio.example.com",
            Port = 9000,
            AuthMethod = AuthMethod.Password,
            RememberPassword = false,
        },
        _ => new()
        {
            Id = Guid.NewGuid(),
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = WorkspaceId,
            Name = "cache",
            Host = "cache.example.com",
            Port = 6379,
            AuthMethod = AuthMethod.Password,
            RememberPassword = false,
        },
    };

    private static async Task<bool> OpenAsync(MainWindowViewModel vm, string kind, SessionProfile profile) => kind switch
    {
        "ftp" => await vm.OpenFtpDocumentForProfileAsync(profile) is not null,
        "plugin" => await vm.OpenPluginDocumentForProfileAsync(profile) is not null,
        _ => await vm.OpenWorkspaceDocumentForProfileAsync(profile) is not null,
    };

    private MainWindowViewModel CreateViewModel()
    {
        IFtpSessionService ftp = Substitute.For<IFtpSessionService>();
        ftp.OpenSessionAsync(Arg.Any<FtpConnectionInfo>(), Arg.Any<CancellationToken>()).Returns(_ => Guid.NewGuid());
        IPluginProtocolSessionService plugins = Substitute.For<IPluginProtocolSessionService>();
        plugins.OpenSessionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>()).Returns(_ => Guid.NewGuid());
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        var registry = new PluginProtocolRegistry();
        // 不声明匿名访问:没存口令就得弹登录框。
        registry.RegisterWorkspace(WorkspaceId, new() { Id = WorkspaceId, DisplayName = "Acme Cache", DefaultPort = 6379 }, new ConnectingWorkspace());
        return new(
            connectionWorkflowService: new ConnectionWorkflowService(_repository, Substitute.For<ISshConnectionService>()),
            sessionRepository: _repository,
            sftpService: sftp,
            ftpSessionService: ftp,
            pluginProtocolService: plugins,
            protocolRegistry: registry,
            workspaceLauncher: new PluginWorkspaceLauncher(registry));
    }

    /// <summary>一次就连上的工作台替身。</summary>
    private sealed class ConnectingWorkspace : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IWorkspaceDocument>(new Document());

        private sealed class Document : IWorkspaceDocument
        {
            public WorkspaceStatus Status { get; } = new(ProtocolSessionState.Connected);

            public event EventHandler<WorkspaceStatus>? StatusChanged
            {
                add { }
                remove { }
            }

            public object CreateView() => new();

            public Task ReconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
