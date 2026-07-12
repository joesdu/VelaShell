using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Infrastructure.Tunnels;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddVelaShellInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<VelaShellStoragePaths>();
        services.AddSingleton<JsonDataStore>();

        // 所有持久化统一走嵌入式 SonnetDB(文档集合 + 时序 measurement)。
        services.AddSingleton<SonnetDbEngine>(serviceProvider =>
            new(serviceProvider.GetRequiredService<VelaShellStoragePaths>()));
        services.AddSingleton<ISecretProtector>(serviceProvider =>
            new AesSecretProtector(serviceProvider.GetRequiredService<VelaShellStoragePaths>()));
        services.AddSingleton<ISessionRepository>(serviceProvider =>
        {
            VelaShellStoragePaths paths = serviceProvider.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbSessionRepository(serviceProvider.GetRequiredService<SonnetDbEngine>(),
                serviceProvider.GetRequiredService<ISecretProtector>(),
                paths.SessionsFile);
        });
        services.AddSingleton<ISettingsService>(serviceProvider =>
        {
            VelaShellStoragePaths paths = serviceProvider.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbSettingsService(serviceProvider.GetRequiredService<SonnetDbEngine>(),
                [paths.RootDirectory, paths.LegacyDotDirectory]);
        });
        services.AddSingleton<IHostKeyService>(serviceProvider =>
        {
            VelaShellStoragePaths paths = serviceProvider.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbHostKeyService(serviceProvider.GetRequiredService<SonnetDbEngine>(),
                Path.Combine(paths.LegacyDotDirectory, "known_hosts.json"));
        });
        services.AddSingleton<IRecentConnectionService, SonnetDbRecentConnectionService>();
        services.AddSingleton<IAuditLogService, SonnetDbAuditLogService>();
        services.AddSingleton<IAppDataStore, SonnetDbAppDataStore>();
        services.AddSingleton<ISessionRecordingStore, SonnetDbSessionRecordingStore>();
        services.AddSingleton<ISshKeyService>(_ => new SshKeyService());
        services.AddSingleton<ISecurityAlertService>(serviceProvider =>
            new SecurityAlertService(serviceProvider.GetRequiredService<ISettingsService>(),
                serviceProvider.GetService<IAuditLogService>()));
        services.AddSingleton<ISshConnectionService>(serviceProvider =>
        {
            IHostKeyService hostKeyService = serviceProvider.GetRequiredService<IHostKeyService>();
            ISettingsService settingsService = serviceProvider.GetRequiredService<ISettingsService>();
            IHostKeyPrompt? hostKeyPrompt = serviceProvider.GetService<IHostKeyPrompt>();
            ISecurityAlertService? securityAlerts = serviceProvider.GetService<ISecurityAlertService>();
            return new SshConnectionService(connectionInfo =>
                CreateSshClientWrapper(connectionInfo, hostKeyService, settingsService, hostKeyPrompt, securityAlerts));
        });
        services.AddSingleton<ISftpService>(serviceProvider =>
        {
            ISshConnectionService connectionService = serviceProvider.GetRequiredService<ISshConnectionService>();
            ISettingsService settingsService = serviceProvider.GetRequiredService<ISettingsService>();
            IHostKeyService hostKeyService = serviceProvider.GetRequiredService<IHostKeyService>();
            // A dedicated SFTP channel per session, built from the same credentials.
            return new SftpService(connectionService, session =>
            {
                AuthenticationMethod[] authMethods = CreateAuthenticationMethods(session.ConnectionInfo);
                var info = new ConnectionInfo(session.ConnectionInfo.Host, session.ConnectionInfo.Port, session.ConnectionInfo.Username, authMethods)
                {
                    Timeout = ConnectTimeout(settingsService)
                };
                var sftpClient = new SftpClient(info);

                // SFTP 通道与终端通道同等校验主机指纹(此前未订阅事件 = 默认信任任意指纹,
                // 存在中间人缺口)。终端会话先行建立时指纹已入库或已“仅本次信任”,
                // 这里只做严格比对、不弹窗:不匹配即拒绝。
                string host = session.ConnectionInfo.Host;
                int port = session.ConnectionInfo.Port;
                sftpClient.HostKeyReceived += (_, e) =>
                {
                    string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                    HostKeyVerification verification = hostKeyService
                                                       .VerifyHostKeyAsync(host, port, e.HostKeyName, fingerprint)
                                                       .GetAwaiter().GetResult();
                    e.CanTrust = verification == HostKeyVerification.Trusted ||
                                 HostTrustOnceCache.IsTrusted(host, port, fingerprint);
                };
                return new SftpClientWrapper(sftpClient);
            }, settingsService);
        });
        services.AddSingleton<ITransferManager, TransferManager>();

        // Gist 云同步(设置 → 云同步):设置/连接(含隧道)/代码片段的多端同步。
        services.AddSingleton<IGistSyncService>(serviceProvider =>
            new Sync.GistSyncService(
                serviceProvider.GetRequiredService<ISettingsService>(),
                serviceProvider.GetRequiredService<ISessionRepository>(),
                serviceProvider.GetRequiredService<IAppDataStore>(),
                serviceProvider.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<ISessionMetricsService>(sp =>
            new SessionMetricsService(sp.GetRequiredService<ISshConnectionService>()));
        services.AddSingleton<ITunnelService>(serviceProvider =>
        {
            ISshConnectionService connectionService = serviceProvider.GetRequiredService<ISshConnectionService>();
            return new TunnelService(connectionService, sessionId => connectionService.GetClient(sessionId) ?? throw new InvalidOperationException($"No SSH client found for session {sessionId}."));
        });
        return services;
    }

    /// <summary>连接超时(设置 → 常规 → 连接默认值);设置不可读时退回既有的 10 秒。</summary>
    private static TimeSpan ConnectTimeout(ISettingsService? settingsService)
    {
        try
        {
            int seconds = settingsService?.GetSettingsAsync().GetAwaiter().GetResult()
                                         .General.ConnectTimeoutSeconds ??
                          10;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 600));
        }
        catch
        {
            return TimeSpan.FromSeconds(10);
        }
    }

    /// <summary>主机信任策略(设置 → 安全审计);设置不可读时用默认策略(TOFU + 变更阻断)。</summary>
    private static SecurityOptions GetSecurityOptions(ISettingsService? settingsService)
    {
        try
        {
            return settingsService?.GetSettingsAsync().GetAwaiter().GetResult().Security ?? new SecurityOptions();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>心跳间隔(设置 → 常规):0 = 关闭(SSH.NET 用 -1ms 表示禁用)。</summary>
    private static TimeSpan KeepAliveInterval(ISettingsService? settingsService)
    {
        try
        {
            int seconds = settingsService?.GetSettingsAsync().GetAwaiter().GetResult()
                                         .General.KeepAliveSeconds ??
                          0;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
        }
        catch
        {
            return Timeout.InfiniteTimeSpan;
        }
    }

    private static ISshClientWrapper CreateSshClientWrapper(
        VelaConnectionInfo connectionInfo,
        IHostKeyService? hostKeyService = null,
        ISettingsService? settingsService = null,
        IHostKeyPrompt? hostKeyPrompt = null,
        ISecurityAlertService? securityAlerts = null)
    {
        // 配了跳板(ProxyJump):逐跳建链,每跳按其逻辑主机做指纹校验。
        if (connectionInfo.JumpHost is not null)
        {
            return new JumpChainSshClientWrapper(connectionInfo,
                (logical, connectHost, connectPort) => BuildSshClient(logical, connectHost, connectPort,
                    hostKeyService, settingsService, hostKeyPrompt, securityAlerts));
        }
        return new SshClientWrapper(BuildSshClient(connectionInfo, connectionInfo.Host, connectionInfo.Port,
            hostKeyService, settingsService, hostKeyPrompt, securityAlerts));
    }

    /// <summary>
    /// 构建一跳的 SshClient:凭据与主机指纹校验用逻辑连接信息
    /// <paramref name="connectionInfo" />(host:port 为 known_hosts 键),实际 socket 连
    /// <paramref name="connectHost" />:<paramref name="connectPort" />(直连时相同,经跳板时
    /// 是上一跳的本地转发口 —— 指纹绝不能按 127.0.0.1 记录)。
    /// </summary>
    private static SshClient BuildSshClient(
        VelaConnectionInfo connectionInfo,
        string connectHost,
        int connectPort,
        IHostKeyService? hostKeyService,
        ISettingsService? settingsService,
        IHostKeyPrompt? hostKeyPrompt,
        ISecurityAlertService? securityAlerts)
    {
        AuthenticationMethod[] authMethods = CreateAuthenticationMethods(connectionInfo);
        var sshConnectionInfo = new ConnectionInfo(connectHost,
            connectPort,
            connectionInfo.Username,
            authMethods);
        var client = new SshClient(sshConnectionInfo);
        client.ConnectionInfo.Timeout = ConnectTimeout(settingsService);
        client.KeepAliveInterval = KeepAliveInterval(settingsService);
        if (hostKeyService is not null)
        {
            // 主机信任策略(设置 → 安全审计):首次连接默认 TOFU 自动记录,可改为人工确认
            // (三选项:永久信任 / 仅本次信任 / 取消);指纹变化默认阻断并告警(防中间人),
            // 可改为弹窗人工裁决。事件在握手线程上触发,同步等待本地存储/UI 弹窗是安全的。
            client.HostKeyReceived += (_, e) =>
            {
                string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                HostKeyVerification verification = hostKeyService
                                                   .VerifyHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                                                   .GetAwaiter().GetResult();
                SecurityOptions security = GetSecurityOptions(settingsService);
                string target = $"{connectionInfo.Host}:{connectionInfo.Port}";

                // 已持久信任 → 放行;本次运行内被“仅本次信任”过且指纹一致 → 同样放行
                // (覆盖同会话重连与 SFTP 通道,不重复弹窗)。
                if (verification == HostKeyVerification.Trusted ||
                    HostTrustOnceCache.IsTrusted(connectionInfo.Host, connectionInfo.Port, fingerprint))
                {
                    e.CanTrust = true;
                    return;
                }
                HostKeyDecision decision;
                if (verification == HostKeyVerification.Unknown)
                {
                    // 首次连接:开关关闭 = TOFU 自动永久记录;开启 = 弹窗人工裁决。
                    decision = security.ConfirmFirstFingerprint && hostKeyPrompt is not null
                                   ? hostKeyPrompt.DecideAsync(connectionInfo.Host, connectionInfo.Port,
                                                      e.HostKeyName, fingerprint, verification)
                                                  .GetAwaiter().GetResult()
                                   : HostKeyDecision.TrustPermanently;
                }
                else
                {
                    // 指纹变更:默认阻断;关闭阻断开关后弹窗人工裁决。
                    decision = !security.BlockOnFingerprintChange && hostKeyPrompt is not null
                                   ? hostKeyPrompt.DecideAsync(connectionInfo.Host, connectionInfo.Port,
                                                      e.HostKeyName, fingerprint, verification)
                                                  .GetAwaiter().GetResult()
                                   : HostKeyDecision.Reject;
                }
                e.CanTrust = decision != HostKeyDecision.Reject;
                switch (decision)
                {
                    case HostKeyDecision.TrustPermanently:
                        hostKeyService
                            .TrustHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                            .GetAwaiter().GetResult();
                        if (verification == HostKeyVerification.Changed)
                        {
                            securityAlerts?.RaiseAsync("hostkey-changed-accepted", Strings.Format("KeySvc_AlertChangedAccepted", target, fingerprint));
                        }
                        break;
                    case HostKeyDecision.TrustOnce:
                        HostTrustOnceCache.Remember(connectionInfo.Host, connectionInfo.Port, fingerprint);
                        securityAlerts?.RaiseAsync("hostkey-trusted-once",
                            verification == HostKeyVerification.Changed
                                ? Strings.Format("KeySvc_AlertChangedTrustOnce", target, fingerprint)
                                : Strings.Format("KeySvc_AlertFirstTrustOnce", target, fingerprint));
                        break;
                    case HostKeyDecision.Reject:
                    default:
                        securityAlerts?.RaiseAsync(
                            verification == HostKeyVerification.Changed ? "hostkey-changed-blocked" : "hostkey-rejected",
                            verification == HostKeyVerification.Changed
                                ? Strings.Format("KeySvc_AlertChangedBlocked", target, fingerprint)
                                : Strings.Format("KeySvc_AlertFirstRejected", target, fingerprint));
                        break;
                }
            };
        }
        return client;
    }

    private static AuthenticationMethod[] CreateAuthenticationMethods(VelaConnectionInfo connectionInfo)
    {
        return connectionInfo.AuthMethod switch
        {
            AuthMethod.Password =>
                // byte[] 重载:SSH.NET 会在认证方法释放时清零该缓冲区(string 重载做不到),
                // 因此密码不会以不可清除的托管字符串常驻在 SSH.NET 内部。
                [
                    new PasswordAuthenticationMethod(connectionInfo.Username,
                        Encoding.UTF8.GetBytes(connectionInfo.Password ?? string.Empty))
                ],
            AuthMethod.PrivateKey =>
                [new PrivateKeyAuthenticationMethod(connectionInfo.Username, CreatePrivateKeyFile(connectionInfo))],
            _ => throw new ArgumentOutOfRangeException(nameof(connectionInfo), connectionInfo.AuthMethod, @"Unsupported authentication method.")
        };
    }

    private static PrivateKeyFile CreatePrivateKeyFile(VelaConnectionInfo connectionInfo)
    {
        string? privateKeyPath = connectionInfo.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            throw new InvalidOperationException("Private key path is required for private key authentication.");
        }
        return string.IsNullOrWhiteSpace(connectionInfo.PrivateKeyPassphrase)
                   ? new(privateKeyPath)
                   : new PrivateKeyFile(privateKeyPath, connectionInfo.PrivateKeyPassphrase);
    }
}
