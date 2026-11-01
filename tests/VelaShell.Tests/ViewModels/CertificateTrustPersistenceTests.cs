using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// FTPS 与插件协议的「信任这张证书」:只按 Id 取库里那份、只写证书信任那一项再存(维护者 2026-10-31 定,与终端标签重连同一口径)——
/// 以前把文档手里那份整份写回,别处这段时间里改过的字段被冲掉,登录框里这次手输、没让记住的东西也跟着落盘。临时配置不写。
/// </summary>
/// <remarks>真的 SonnetDB 仓储与真的连接工作流;FTP / 插件会话服务是替身:第一次报证书未受信任,第二次报一个普通错误收场。</remarks>
[TestClass]
[TestCategory("FtpConnectionFlow")]
public sealed class CertificateTrustPersistenceTests
{
    private const string Thumbprint = "AB12CD34";

    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;

    /// <summary>连接链路途中会碰 <c>Dispatcher.UIThread</c>,先把 headless 会话起起来(同 FtpConnectionFlowTests)。</summary>
    [ClassInitialize]
    public static void Init(TestContext _) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CertificateTrustPersistenceTests).Assembly);

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_certtrust_{Guid.NewGuid():N}");
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

    [TestMethod]
    public Task Ftps_TrustingTheCertificate_WritesOnlyTheThumbprint_OntoTheStoredCopy() =>
        // 理由同 FtpConnectionFlowTests:连接链路全程 ConfigureAwait(true),不在 headless UI 线程上等它。
        Task.Run(async () =>
        {
            SessionProfile saved = FtpProfile();
            await _repository.SaveSessionAsync(saved);
            SessionProfile stale = TypedCopyOf(saved);
            await EditElsewhereAsync(saved.Id, stored => stored.Ftp!.MaxConnections = 5);
            IFtpSessionService ftp = Substitute.For<IFtpSessionService>();
            ftp.OpenSessionAsync(Arg.Any<FtpConnectionInfo>(), Arg.Any<CancellationToken>())
               .Returns<Task<Guid>>(
                   _ => throw new VelaFtpCertificateException("untrusted", Thumbprint, "CN=ftp", "CN=ca", DateTimeOffset.UtcNow.AddYears(1), "RemoteCertificateChainErrors"),
                   _ => throw new InvalidOperationException("server went away"));
            MainWindowViewModel vm = CreateViewModel(ftp: ftp);
            vm.FtpCertificateTrustPrompt = (_, _) => Task.FromResult(true);

            Assert.IsNull(await vm.OpenFtpDocumentForProfileAsync(stale));

            SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
            Assert.AreEqual(Thumbprint, stored.Ftp!.TrustedCertificateThumbprint, "信任的指纹记进库里那份");
            Assert.AreEqual("renamed elsewhere", stored.Name, "别处刚改的名字没被文档手里的旧副本盖掉");
            Assert.AreEqual(5, stored.Ftp.MaxConnections, "别处刚改的 FTP 设置也没被盖掉");
            Assert.IsNull(stored.Password, "这次现敲、没让记住的密码不落库");
            Assert.AreEqual(Thumbprint, stale.Ftp!.TrustedCertificateThumbprint, "重连用的那份照样带上信任");
        });

    [TestMethod]
    public Task PluginProtocol_TrustingTheCertificate_WritesOnlyItsSettingField_OntoTheStoredCopy() =>
        Task.Run(async () =>
        {
            SessionProfile saved = PluginProfile();
            await _repository.SaveSessionAsync(saved);
            SessionProfile stale = TypedCopyOf(saved);
            await EditElsewhereAsync(saved.Id, stored => stored.PluginSettings!["region"] = "eu-west-1");
            IPluginProtocolSessionService plugins = Substitute.For<IPluginProtocolSessionService>();
            plugins.OpenSessionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                   .Returns<Task<Guid>>(
                       _ => throw new PluginProtocolCertificateException(
                           "untrusted", "CN=minio", "CN=ca", DateTimeOffset.UtcNow.AddYears(1), Thumbprint, "RemoteCertificateNameMismatch", "tlsThumbprint"),
                       _ => throw new InvalidOperationException("endpoint went away"));
            MainWindowViewModel vm = CreateViewModel(plugins: plugins);
            vm.PluginCertificateTrustPrompt = (_, _) => Task.FromResult(true);

            Assert.IsNull(await vm.OpenPluginDocumentForProfileAsync(stale));

            SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
            Assert.AreEqual(Thumbprint, stored.PluginSettings!["tlsThumbprint"], "指纹记进协议声明的那个字段");
            Assert.AreEqual("eu-west-1", stored.PluginSettings["region"], "别处刚改的协议设置没被盖掉");
            Assert.AreEqual("renamed elsewhere", stored.Name);
            Assert.IsNull(stored.Password);
        });

    /// <summary>不在库里的临时配置(§252):只在本次连接内信任,不落库。</summary>
    [TestMethod]
    public Task Ftps_TemporaryProfile_IsNotSaved() =>
        Task.Run(async () =>
        {
            IFtpSessionService ftp = Substitute.For<IFtpSessionService>();
            ftp.OpenSessionAsync(Arg.Any<FtpConnectionInfo>(), Arg.Any<CancellationToken>())
               .Returns<Task<Guid>>(
                   _ => throw new VelaFtpCertificateException("untrusted", Thumbprint, "CN=ftp", "CN=ca", DateTimeOffset.UtcNow.AddYears(1), "RemoteCertificateChainErrors"),
                   _ => throw new InvalidOperationException("server went away"));
            MainWindowViewModel vm = CreateViewModel(ftp: ftp);
            vm.FtpCertificateTrustPrompt = (_, _) => Task.FromResult(true);
            SessionProfile temporary = TypedCopyOf(FtpProfile());

            Assert.IsNull(await vm.OpenFtpDocumentForProfileAsync(temporary));

            Assert.IsEmpty(await _repository.GetAllSessionsAsync());
            Assert.AreEqual(Thumbprint, temporary.Ftp!.TrustedCertificateThumbprint, "本次连接内照样信任");
        });

    // ---- 工具 ----

    private static SessionProfile FtpProfile() => new()
    {
        Id = Guid.NewGuid(),
        ConnectionType = ConnectionType.FTP,
        Name = "ftp-box",
        Host = "ftp.example.com",
        Port = 21,
        Username = "deploy",
        AuthMethod = AuthMethod.Password,
        RememberPassword = false,
        Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.Explicit, MaxConnections = 2 },
    };

    private static SessionProfile PluginProfile() => new()
    {
        Id = Guid.NewGuid(),
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = "test.s3",
        Name = "minio",
        Host = "minio.example.com",
        Port = 9000,
        Username = "access-key",
        AuthMethod = AuthMethod.Password,
        RememberPassword = false,
        PluginSettings = new(StringComparer.Ordinal) { ["region"] = "us-east-1" },
    };

    /// <summary>文档手里那份:打开时从会话树缓存取来的(之后库里被别处改了),带着这次在登录框里现敲、没让记住的密码。</summary>
    private static SessionProfile TypedCopyOf(SessionProfile saved)
    {
        SessionProfile copy = saved.Clone();
        copy.Password = "typed-now";
        return copy;
    }

    /// <summary>别处(编辑框、云同步)改了库里那份:改名,再加一处协议相关的改动。</summary>
    private async Task EditElsewhereAsync(Guid id, Action<SessionProfile> change)
    {
        SessionProfile stored = (await _repository.GetSessionAsync(id))!;
        stored.Name = "renamed elsewhere";
        change(stored);
        await _repository.SaveSessionAsync(stored);
    }

    private MainWindowViewModel CreateViewModel(IFtpSessionService? ftp = null, IPluginProtocolSessionService? plugins = null) =>
        new(
            connectionWorkflowService: new ConnectionWorkflowService(_repository, Substitute.For<ISshConnectionService>()),
            sessionRepository: _repository,
            sftpService: Substitute.For<ISftpService>(),
            ftpSessionService: ftp,
            pluginProtocolService: plugins);
}
