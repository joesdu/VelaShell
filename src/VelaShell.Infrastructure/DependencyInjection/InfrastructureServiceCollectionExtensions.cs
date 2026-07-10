using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
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
            // A dedicated SFTP channel per session, built from the same credentials.
            return new SftpService(connectionService, session =>
            {
                AuthenticationMethod[] authMethods = CreateAuthenticationMethods(session.ConnectionInfo);
                var info = new ConnectionInfo(session.ConnectionInfo.Host, session.ConnectionInfo.Port, session.ConnectionInfo.Username, authMethods)
                {
                    Timeout = ConnectTimeout(settingsService)
                };
                return new SftpClientWrapper(new(info));
            }, settingsService);
        });
        services.AddSingleton<ITransferManager, TransferManager>();
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
            // 主机信任策略(设置 → 安全审计):首次连接默认 TOFU 自动记录,可改为人工确认;
            // 指纹变化默认阻断并告警(防中间人),可改为弹窗人工裁决。
            // 事件在握手线程上触发,同步等待本地存储/UI 弹窗是安全的。
            client.HostKeyReceived += (_, e) =>
            {
                string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                HostKeyVerification verification = hostKeyService
                                                   .VerifyHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                                                   .GetAwaiter().GetResult();
                SecurityOptions security = GetSecurityOptions(settingsService);
                string target = $"{connectionInfo.Host}:{connectionInfo.Port}";
                switch (verification)
                {
                    case HostKeyVerification.Trusted:
                        e.CanTrust = true;
                        break;
                    case HostKeyVerification.Unknown:
                        bool trustNew = true;
                        if (security.ConfirmFirstFingerprint && hostKeyPrompt is not null)
                        {
                            trustNew = hostKeyPrompt
                                       .ConfirmAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName,
                                           fingerprint, verification)
                                       .GetAwaiter().GetResult();
                        }
                        e.CanTrust = trustNew;
                        if (trustNew)
                        {
                            hostKeyService
                                .TrustHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                                .GetAwaiter().GetResult();
                        }
                        else
                        {
                            securityAlerts?.RaiseAsync("hostkey-rejected", $"已拒绝 {target} 的首次连接指纹:{fingerprint}");
                        }
                        break;
                    case HostKeyVerification.Changed:
                    default: // Changed:与 known_hosts 记录不符
                        bool trustChanged = false;
                        if (!security.BlockOnFingerprintChange && hostKeyPrompt is not null)
                        {
                            trustChanged = hostKeyPrompt
                                           .ConfirmAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName,
                                               fingerprint, verification)
                                           .GetAwaiter().GetResult();
                        }
                        e.CanTrust = trustChanged;
                        if (trustChanged)
                        {
                            hostKeyService
                                .TrustHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                                .GetAwaiter().GetResult();
                            securityAlerts?.RaiseAsync("hostkey-changed-accepted", $"{target} 的主机指纹已变更,用户确认信任新指纹:{fingerprint}");
                        }
                        else
                        {
                            securityAlerts?.RaiseAsync("hostkey-changed-blocked", $"{target} 的主机指纹与 known_hosts 记录不符,连接已被阻断(疑似中间人)。新指纹:{fingerprint}");
                        }
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
