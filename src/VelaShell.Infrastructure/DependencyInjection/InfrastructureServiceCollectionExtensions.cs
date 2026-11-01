using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Ftp;
using VelaShell.Core.Import;
using VelaShell.Core.Layouts;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Notifications;
using VelaShell.Core.Processes;
using VelaShell.Core.Protocols;
using VelaShell.Core.Recording;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.Audit;
using VelaShell.Infrastructure.Credentials;
using VelaShell.Infrastructure.Diagnostics;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Import;
using VelaShell.Infrastructure.Net;
using VelaShell.Infrastructure.Notifications;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.Infrastructure.Sftp;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Infrastructure.Tunnels;
using VelaShell.Infrastructure.XServer;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.DependencyInjection;

/// <summary>
/// Provides extension methods for registering VelaShell infrastructure services in an <see cref="IServiceCollection" />.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    // ---- Persistence & Services (unchanged) ----
    /// <summary>
    /// Registers the VelaShell infrastructure services, including persistence, SSH connection management, SFTP, and related services, into the provided <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="VelaSshConnectionException"></exception>
    public static IServiceCollection AddVelaShellInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // 后台活动账本(状态栏右下角的圆环)。注册在最前面:插件运行时等生产者都要它,
        // 而它自己不依赖任何东西。
        services.AddSingleton<IBackgroundActivityService, BackgroundActivityService>();
        services.AddSingleton<VelaShellStoragePaths>();
        // 认领 Program.Main 起的那次后台预热(见 StartupWarmup):数据库在 Avalonia 初始化的
        // 同时就已经开着了。没预热过(测试、设计期)就地新建,行为不变。
        services.AddSingleton(sp =>
            StartupWarmup.Claim(sp.GetRequiredService<VelaShellStoragePaths>()));
        // 敏感字段加密。本机加密密钥:macOS / Linux 交给系统密钥链(钥匙串 / Secret Service),用不了回落到数据目录的
        // 密钥文件;Windows 上密钥文件以 DPAPI 包裹(MasterKeyResolver)。解析结果另以 IMasterKeyStatusSource 交给界面如实提示。
        services.AddSingleton(sp => new AesSecretProtector(sp.GetRequiredService<VelaShellStoragePaths>()));
        services.AddSingleton<ISecretProtector>(sp => sp.GetRequiredService<AesSecretProtector>());
        services.AddSingleton<IMasterKeyStatusSource>(sp => sp.GetRequiredService<AesSecretProtector>());
        // 可选主密码的闸口与管理入口(事实来源是包裹格式本身,在 AesSecretProtector 里)。解锁框由界面层注册
        // IMasterPasswordPrompt,这里按需去取:headless 宿主取不到,交互式请求也只能是「没解开」。
        services.AddSingleton(sp => new MasterPasswordService(
            sp.GetRequiredService<AesSecretProtector>(),
            () => sp.GetService<IMasterPasswordPrompt>(),
            sp.GetService<IAuditLogService>()));
        services.AddSingleton<IMasterPasswordService>(sp => sp.GetRequiredService<MasterPasswordService>());
        services.AddSingleton<IMasterPasswordGate>(sp => sp.GetRequiredService<MasterPasswordService>());
        // 连接配置、设置、共享凭据三处仓储外面各套一层审计(Audit/):配置的增删改、设置变了哪几类、
        // 共享凭据改动与引用它的连接数都在落盘这一层记,不靠各个界面入口自己记。
        services.AddSingleton<ISessionRepository>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new AuditedSessionRepository(
                new SonnetDbSessionRepository(sp.GetRequiredService<SonnetDbEngine>(),
                    sp.GetRequiredService<ISecretProtector>(), paths.SessionsFile),
                sp.GetService<IAuditLogService>());
        });
        services.AddSingleton<ISettingsService>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            // 设置里的代理密码落盘前同样经 ISecretProtector 加密(与连接配置里的密码同一强度)。
            var settings = new SonnetDbSettingsService(sp.GetRequiredService<SonnetDbEngine>(), [paths.RootDirectory],
                sp.GetRequiredService<ISecretProtector>());
            // 主密码锁着时没解开的那份代理密码,解锁之后就地填回(不用等重启)。
            sp.GetRequiredService<IMasterPasswordGate>().Unlocked += (_, _) => settings.RevealProxyPasswordAfterUnlock();
            return new AuditedSettingsService(settings, sp.GetService<IAuditLogService>());
        });
        // 共享凭据(#550):连接配置只存引用,连接那一刻由解析器按来源取回。
        // 来源以集合注册(先注册具体类型,再转发),外部密码管理器接进来时在这里追加一行。
        services.AddSingleton<ISharedCredentialRepository>(sp =>
            new AuditedSharedCredentialRepository(
                new SonnetDbSharedCredentialRepository(sp.GetRequiredService<SonnetDbEngine>(),
                    sp.GetRequiredService<ISecretProtector>()),
                sp.GetRequiredService<ISessionRepository>(),
                sp.GetService<IAuditLogService>()));
        services.AddSingleton<SharedCredentialProvider>(sp =>
            new(sp.GetRequiredService<ISharedCredentialRepository>()));
        services.AddSingleton<ICredentialProvider>(sp => sp.GetRequiredService<SharedCredentialProvider>());
        services.AddSingleton<ICredentialResolver>(sp =>
            new CredentialResolver(sp.GetServices<ICredentialProvider>()));
        // 会话一键迁移:各来源(Xshell / WinSCP)解析 + 还原密码 + 写入会话仓储。
        // 同时以 ISessionImportService 集合注册 —— 导入对话框打开即遍历全部来源自动扫描,
        // 用户无需先选「从哪个工具导入」;新增来源只要在这里追加一行。
        services.AddSingleton<XshellImportService>(sp =>
            new(sp.GetRequiredService<ISessionRepository>()));
        services.AddSingleton<WinScpImportService>(sp =>
            new(sp.GetRequiredService<ISessionRepository>()));
        services.AddSingleton<SshConfigImportService>(sp =>
            new(sp.GetRequiredService<ISessionRepository>(), sp.GetService<IAppDataStore>()));
        services.AddSingleton<ISessionImportService>(sp => sp.GetRequiredService<XshellImportService>());
        services.AddSingleton<ISessionImportService>(sp => sp.GetRequiredService<WinScpImportService>());
        services.AddSingleton<ISessionImportService>(sp => sp.GetRequiredService<SshConfigImportService>());
        // 连接文件的导入导出(#571):VelaShell JSON 与 CSV。隧道随连接一起走,所以要通用文档存储。
        services.AddSingleton<ISessionArchiveService>(sp =>
            new SessionArchiveService(sp.GetRequiredService<ISessionRepository>(),
                sp.GetService<ISharedCredentialRepository>(), sp.GetService<IAppDataStore>()));
        services.AddSingleton<IHostKeyService>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbHostKeyService(sp.GetRequiredService<SonnetDbEngine>(),
                Path.Combine(paths.RootDirectory, "known_hosts.json"));
        });
        services.AddSingleton<IRecentConnectionService, SonnetDbRecentConnectionService>();
        services.AddSingleton<IAuditLogService, SonnetDbAuditLogService>();
        services.AddSingleton<IAppDataStore, SonnetDbAppDataStore>();
        services.AddSingleton<IQuickCommandRepository>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbQuickCommandRepository(sp.GetRequiredService<IAppDataStore>(),
                paths.LegacyQuickCommandsFile);
        });
        services.AddSingleton<ISessionRecordingStore, SonnetDbSessionRecordingStore>();
        services.AddSingleton<ISshKeyService>(_ => new SshKeyService());
        // 系统密钥库(Windows CNG:软件密钥库、TPM、智能卡):连接配置里「系统密钥库」认证的选钥列表。
        services.AddSingleton<ISystemKeyStore, SystemKeyStore>();
        services.AddSingleton<ISecurityAlertService>(sp => new SecurityAlertService(
            sp.GetRequiredService<ISettingsService>(), sp.GetService<IAuditLogService>()));

        // 统一代理解析:全部出站通道(SSH / FTP / HttpClient)共用的唯一代理出口。
        services.AddSingleton<IProxyResolver>(sp =>
            new ProxyResolver(sp.GetRequiredService<ISettingsService>()));

        // 网络恢复 / 睡眠唤醒的信号源:断开的会话据此立即重连一次,而不是干等 keepalive 超时。
        services.AddSingleton<IConnectivityMonitor>(_ => new ConnectivityMonitor());

        // 消息中心(侧边栏铃铛)与它订阅的资讯源。
        services.AddSingleton<INotificationCenter>(sp =>
            new NotificationCenter(sp.GetRequiredService<IAppDataStore>()));
        services.AddSingleton<IAnnouncementFeed>(sp =>
        {
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            return new HttpAnnouncementFeed(
                async () => (await settings.GetSnapshotAsync().ConfigureAwait(false)).Notifications.FeedUrl,
                HttpAnnouncementFeed.DescribeAudience);
        });

        // 本机 X Server(标题栏按钮 / 设置 → X Server):内置的 X 服务端(默认),或拉起用户装好的 VcXsrv。
        // SSH 的 X11 转发也经它取显示。两者都随容器释放而停下(内置的关掉窗口,VcXsrv 的杀掉子进程)。
        // 内置引擎的窗口由界面层画:它注册 IEmbeddedXServerHost,这里在启动时才取,容器里没有时启动报错。
        services.AddSingleton(sp => new BuiltInLocalXServer(
            sp.GetRequiredService<ISettingsService>(), () => sp.GetService<IEmbeddedXServerHost>()));
        services.AddSingleton(sp => new VcXsrvLocalXServer(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<ILocalXServer>(sp => new LocalXServerSelector(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<BuiltInLocalXServer>(),
            sp.GetRequiredService<VcXsrvLocalXServer>()));

        // SSH connection service
        services.AddSingleton<ISshConnectionService>(sp =>
        {
            IHostKeyService hostKey = sp.GetRequiredService<IHostKeyService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            IHostKeyPrompt? prompt = sp.GetService<IHostKeyPrompt>();
            IAgentSignPrompt? agentPrompt = sp.GetService<IAgentSignPrompt>();
            IKeyboardInteractivePrompt? keyboardPrompt = sp.GetService<IKeyboardInteractivePrompt>();
            Func<TailscaleSshCheckNotification, Task>? tailscaleCheckPrompt = sp.GetService<Func<TailscaleSshCheckNotification, Task>>();
            ISecurityAlertService? alerts = sp.GetService<ISecurityAlertService>();
            IProxyResolver proxyResolver = sp.GetRequiredService<IProxyResolver>();
            ILocalXServer? xServer = sp.GetService<ILocalXServer>();
            return new SshConnectionService(ci =>
                CreateSshClientWrapper(ci, hostKey, settings, prompt, alerts, proxyResolver, xServer, agentPrompt, keyboardPrompt, tailscaleCheckPrompt));
        });

        // SFTP service
        // FTP / FTPS 后端:自带连接池(FTP 一条控制连接同时只能跑一条命令,不像 SFTP 能多路复用)。
        services.AddSingleton<FtpFileService>();
        services.AddSingleton<IFtpSessionService>(sp => sp.GetRequiredService<FtpFileService>());
        // 插件协议(S3、WebDAV… 由插件提供)。注册表在发现期就登记清单声明的协议页签,
        // 插件激活后补上实现;适配器把它翻译成宿主的 ISftpService。
        // 宿主本身对这些协议一无所知 —— 连它们的客户端库都不在宿主进程的依赖里。
        services.AddSingleton<PluginProtocolRegistry>(sp =>
        {
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            return new()
            {
                // 限速与时间戳策略是全局用户偏好,对 SFTP/FTP/插件协议一视同仁 ——
                // 每次传输现读一次,用户中途调了限速正在跑的传输也跟着变。
                TransferOptionsProvider = async _ =>
                {
                    TransferOptions transfer = (await settings.GetSnapshotAsync().ConfigureAwait(false)).Transfer;
                    long up = transfer.BandwidthLimitEnabled ? (long)Math.Max(0, transfer.UploadLimitMBps) * 1024 * 1024 : 0;
                    long down = transfer.BandwidthLimitEnabled ? (long)Math.Max(0, transfer.DownloadLimitMBps) * 1024 * 1024 : 0;
                    return new(up, down, transfer.PreserveTimestamps);
                },
            };
        });
        services.AddSingleton<PluginProtocolFileService>(sp => new(sp.GetRequiredService<PluginProtocolRegistry>()));
        services.AddSingleton<IPluginProtocolSessionService>(sp => sp.GetRequiredService<PluginProtocolFileService>());
        // 工作台连接类型(Redis、MySQL… 由插件提供):界面是插件自己的,所以这里没有数据面要翻译,
        // 只有"解析注册表 → 组装请求 → 翻译异常 → 会话登记"这几件宿主该做的事。
        services.AddSingleton(sp =>
        {
            PluginProtocolRegistry registry = sp.GetRequiredService<PluginProtocolRegistry>();
            var launcher = new PluginWorkspaceLauncher(registry);
            // 连接类型被注销(插件停用/卸载)→ 关掉它名下还开着的文档。
            // 订阅放在这里而不是构造函数里:注册表是宿主单例,让它反向持有 launcher 的引用
            // 只应发生一次,而 launcher 本身也是单例。
            registry.Unregistered += launcher.OnUnregistered;
            return launcher;
        });
        // 本机文件拖进经 SSH 转发来的 X 程序:经那个会话的 SFTP 传到远端临时目录,再交远端路径(F16);
        // 拖放时连着那台主机的会话都断了之后,下一次连上或往那台主机拖放时删掉(它订阅了 SessionConnected,所以是单例)。
        services.AddSingleton<IXServerDropUploader>(sp => new SftpXServerDropUploader(
            sp.GetRequiredService<ISshConnectionService>(), sp.GetRequiredService<ISftpService>()));
        // 反方向:从经 SSH 转发来的 X 程序往本机拖出文件,先经那个会话的 SFTP 取回本机的临时目录(F16 的另一半)。
        services.AddSingleton<IXServerDragDownloader>(sp => new SftpXServerDragDownloader(
            sp.GetRequiredService<ISshConnectionService>(), sp.GetRequiredService<ISftpService>()));
        // 远程文件服务对外仍是唯一的 ISftpService;路由按会话归属分派到 SFTP / FTP / 插件协议,
        // 文件浏览器、传输管理器、限速、拖放因此零改动。
        services.AddSingleton<ISftpService>(sp => new RoutingRemoteFileService(
            sp.GetRequiredService<SftpService>(),
            sp.GetRequiredService<FtpFileService>(),
            sp.GetRequiredService<FtpFileService>(),
            sp.GetRequiredService<PluginProtocolFileService>(),
            sp.GetRequiredService<PluginProtocolFileService>()));
        services.AddSingleton(sp =>
        {
            ISshConnectionService connSvc = sp.GetRequiredService<ISshConnectionService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            return new SftpService(connSvc, session =>
            {
                ISshClientWrapper? wrapper = connSvc.GetClient(session.SessionId);
                if (wrapper is not VelaSshClientWrapper ssh)
                    throw new InvalidOperationException("SFTP requires the VelaShell.Ssh backend.");
                return new VelaSftpClientWrapper(async ct =>
                {
                    // SFTP 复用主连接的 SSH 通道。主连接不在时不得偷偷另建连接:
                    // 新连接无人持有、无人释放(泄漏),且会绕过用户可见的连接生命周期。
                    SshConnection inner = ssh.InnerConnection
                        ?? throw new VelaSshConnectionException(
                            "SSH connection is not established; cannot open SFTP channel.");
                    // 文件名按会话的字符集解读(GBK 服务器上的中文名才显示得对);UTF-8 时不传,库走无损往返。
                    return await SftpFileSystem.ConnectAsync(inner, SftpConnectOptions.For(session.ConnectionInfo), ct)
                        .ConfigureAwait(false);
                });
            }, settings);
        });
        services.AddSingleton<IGistSyncService>(sp => new Sync.GistSyncService(
            sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ISessionRepository>(),
            sp.GetRequiredService<IAppDataStore>(), sp.GetRequiredService<IQuickCommandRepository>(),
            sp.GetRequiredService<ISecretProtector>(),
            // 后台活动账本:自动同步是静默的,至少让状态栏的圆环交代一句"正在同步"。
            sp.GetService<IBackgroundActivityService>(),
            // 共享凭据随连接配置一起同步,否则另一台设备上的引用全是悬空的。
            sp.GetRequiredService<ISharedCredentialRepository>(),
            // 布局模板:与界面共用这一个实例,本机改动才推得出去、拉下来的界面才看得见。
            sp.GetRequiredService<LayoutTemplateStore>()));
        services.AddSingleton(sp => new LayoutTemplateStore(sp.GetService<IAppDataStore>()));
        services.AddSingleton<ISessionMetricsService>(sp =>
            new SessionMetricsService(sp.GetRequiredService<ISshConnectionService>()));
        services.AddSingleton<IRemoteProcessService>(sp =>
            new RemoteProcessService(sp.GetRequiredService<ISshConnectionService>()));
        services.AddSingleton<ITraceRouteService, PingTraceRouteService>();
        services.AddSingleton<IIpGeolocationService>(sp =>
        {
            var paths = new VelaShellStoragePaths();
            string? configured = null;
            try
            {
                configured = sp.GetService<ISettingsService>().GetSnapshotBlocking()
                               .General.GeoIpDatabasePath;
            }
            catch
            {
                // 设置读不出来就用默认目录,不该因此让追踪窗口开不了。
            }
            return new MmdbIpGeolocationService(configured, paths.GeoIpDirectory);
        });
        services.AddSingleton<ITunnelService>(sp =>
        {
            ISshConnectionService connSvc = sp.GetRequiredService<ISshConnectionService>();
            return new TunnelService(connSvc, sid =>
                connSvc.GetClient(sid) ?? throw new InvalidOperationException($"No SSH client for session {sid}."));
        });
        return services;
    }

    // ---- SSH 客户端工厂 ----

    /// <summary>
    /// 按一份连接信息装配出 SSH 包装器。
    /// </summary>
    /// <remarks>
    /// 原先这里跟着四个私有方法（<c>BuildSshClientSettings</c> / <c>AddCredential</c> /
    /// <c>BuildProxyChain</c> / <c>AddHostAuthentication</c>，约 230 行），把「怎么连上去」
    /// 整个摊在 DI 注册文件里。它们已经搬进 <see cref="SshConnectionAssembler" />：
    /// 那本来就是同一件事的几个部分，而放在这里让人要在两百行里跳着找。
    /// </remarks>
    private static VelaSshClientWrapper CreateSshClientWrapper(
        VelaConnectionInfo ci, IHostKeyService? hostKey, ISettingsService? settings,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts, IProxyResolver? proxyResolver = null,
        ILocalXServer? xServer = null, IAgentSignPrompt? agentPrompt = null,
        IKeyboardInteractivePrompt? keyboardPrompt = null,
        Func<TailscaleSshCheckNotification, Task>? tailscaleCheckPrompt = null)
    {
        SshConnectionAssembler.Assembled assembled =
            SshConnectionAssembler.Create(ci, hostKey, settings, prompt, alerts, proxyResolver, keyboardPrompt, tailscaleCheckPrompt);

        return new VelaSshClientWrapper(
            assembled.Connect, assembled.ConnectTimeout, ci.Ssh,
            localXServer: xServer, agentPrompt: agentPrompt, target: HostAddress.FormatTarget(ci.Username, ci.Host, ci.Port), hostKeys: hostKey,
            banners: assembled.Banners)
        {
            Route = assembled.Route,
        };
    }
}
