using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Data;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Ftp;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Notifications;
using VelaShell.Core.Processes;
using VelaShell.Core.Protocols;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Core.XServer;
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
        services.AddSingleton<ISecretProtector>(sp => new AesSecretProtector(sp.GetRequiredService<VelaShellStoragePaths>()));
        services.AddSingleton<ISessionRepository>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbSessionRepository(sp.GetRequiredService<SonnetDbEngine>(),
                sp.GetRequiredService<ISecretProtector>(), paths.SessionsFile);
        });
        services.AddSingleton<ISettingsService>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbSettingsService(sp.GetRequiredService<SonnetDbEngine>(),
                [paths.RootDirectory]);
        });
        // 会话一键迁移:各来源(Xshell / WinSCP)解析 + 还原密码 + 写入会话仓储。
        // 同时以 ISessionImportService 集合注册 —— 导入对话框打开即遍历全部来源自动扫描,
        // 用户无需先选「从哪个工具导入」;新增来源只要在这里追加一行。
        services.AddSingleton<XshellImportService>(sp =>
            new(sp.GetRequiredService<ISessionRepository>()));
        services.AddSingleton<WinScpImportService>(sp =>
            new(sp.GetRequiredService<ISessionRepository>()));
        services.AddSingleton<SshConfigImportService>(sp =>
            new(sp.GetRequiredService<ISessionRepository>()));
        services.AddSingleton<ISessionImportService>(sp => sp.GetRequiredService<XshellImportService>());
        services.AddSingleton<ISessionImportService>(sp => sp.GetRequiredService<WinScpImportService>());
        services.AddSingleton<ISessionImportService>(sp => sp.GetRequiredService<SshConfigImportService>());
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

        // 本机 X Server(标题栏按钮 / 设置 → X Server):拉起用户装好的 VcXsrv。SSH 的 X11 转发也经它取显示。
        // 它持有子进程,退出时随容器释放而被关掉。
        services.AddSingleton<ILocalXServer>(sp => new VcXsrvLocalXServer(sp.GetRequiredService<ISettingsService>()));

        // SSH connection service
        services.AddSingleton<ISshConnectionService>(sp =>
        {
            IHostKeyService hostKey = sp.GetRequiredService<IHostKeyService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            IHostKeyPrompt? prompt = sp.GetService<IHostKeyPrompt>();
            IAgentSignPrompt? agentPrompt = sp.GetService<IAgentSignPrompt>();
            ISecurityAlertService? alerts = sp.GetService<ISecurityAlertService>();
            IProxyResolver proxyResolver = sp.GetRequiredService<IProxyResolver>();
            ILocalXServer? xServer = sp.GetService<ILocalXServer>();
            return new SshConnectionService(ci =>
                CreateSshClientWrapper(ci, hostKey, settings, prompt, alerts, proxyResolver, xServer, agentPrompt));
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
                    return await SftpFileSystem.ConnectAsync(inner, cancellationToken: ct).ConfigureAwait(false);
                });
            }, settings);
        });
        services.AddSingleton<ITransferManager, TransferManager>();
        services.AddSingleton<IGistSyncService>(sp => new Sync.GistSyncService(
            sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ISessionRepository>(),
            sp.GetRequiredService<IAppDataStore>(), sp.GetRequiredService<IQuickCommandRepository>(),
            sp.GetRequiredService<ISecretProtector>(),
            // 后台活动账本:自动同步是静默的,至少让状态栏的圆环交代一句"正在同步"。
            sp.GetService<IBackgroundActivityService>()));
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
        ILocalXServer? xServer = null, IAgentSignPrompt? agentPrompt = null)
    {
        SshConnectionAssembler.Assembled assembled =
            SshConnectionAssembler.Create(ci, hostKey, settings, prompt, alerts, proxyResolver);

        return new VelaSshClientWrapper(
            assembled.Connect, assembled.ConnectTimeout, assembled.DialerLifetime, ci.Ssh,
            localXServer: xServer, agentPrompt: agentPrompt, target: $"{ci.Username}@{ci.Host}:{ci.Port}");
    }
}
