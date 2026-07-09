using Microsoft.Extensions.DependencyInjection;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Core.Sftp;
using PulseTerm.Core.Tunnels;
using PulseTerm.Infrastructure.Ssh;
using PulseTerm.Infrastructure.Tunnels;
using PulseTerm.Infrastructure.Persistence;
using Renci.SshNet;
using Renci.SshNet.Common;
using PulseConnectionInfo = PulseTerm.Core.Models.ConnectionInfo;

namespace PulseTerm.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPulseTermInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PulseTermStoragePaths>();
        services.AddSingleton<JsonDataStore>();

        // 所有持久化统一走嵌入式 SonnetDB(文档集合 + 时序 measurement)。
        services.AddSingleton<SonnetDbEngine>(serviceProvider =>
            new SonnetDbEngine(serviceProvider.GetRequiredService<PulseTermStoragePaths>()));
        services.AddSingleton<ISecretProtector>(serviceProvider =>
            new AesSecretProtector(serviceProvider.GetRequiredService<PulseTermStoragePaths>()));
        services.AddSingleton<ISessionRepository>(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<PulseTermStoragePaths>();
            return new SonnetDbSessionRepository(
                serviceProvider.GetRequiredService<SonnetDbEngine>(),
                serviceProvider.GetRequiredService<ISecretProtector>(),
                paths.SessionsFile);
        });
        services.AddSingleton<ISettingsService>(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<PulseTermStoragePaths>();
            return new SonnetDbSettingsService(
                serviceProvider.GetRequiredService<SonnetDbEngine>(),
                [paths.RootDirectory, paths.LegacyDotDirectory]);
        });
        services.AddSingleton<PulseTerm.Core.Ssh.IHostKeyService>(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<PulseTermStoragePaths>();
            return new SonnetDbHostKeyService(
                serviceProvider.GetRequiredService<SonnetDbEngine>(),
                Path.Combine(paths.LegacyDotDirectory, "known_hosts.json"));
        });
        services.AddSingleton<IRecentConnectionService, SonnetDbRecentConnectionService>();
        services.AddSingleton<IAuditLogService, SonnetDbAuditLogService>();
        services.AddSingleton<IAppDataStore, SonnetDbAppDataStore>();
        services.AddSingleton<PulseTerm.Core.Ssh.ISshKeyService>(_ => new SshKeyService());

        services.AddSingleton<PulseTerm.Core.Ssh.ISecurityAlertService>(serviceProvider =>
            new PulseTerm.Core.Ssh.SecurityAlertService(
                serviceProvider.GetRequiredService<ISettingsService>(),
                serviceProvider.GetService<IAuditLogService>()));

        services.AddSingleton<ISshConnectionService>(serviceProvider =>
        {
            var hostKeyService = serviceProvider.GetRequiredService<PulseTerm.Core.Ssh.IHostKeyService>();
            var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
            var hostKeyPrompt = serviceProvider.GetService<PulseTerm.Core.Ssh.IHostKeyPrompt>();
            var securityAlerts = serviceProvider.GetService<PulseTerm.Core.Ssh.ISecurityAlertService>();
            return new SshConnectionService(connectionInfo =>
                CreateSshClientWrapper(connectionInfo, hostKeyService, settingsService, hostKeyPrompt, securityAlerts));
        });

        services.AddSingleton<ISftpService>(serviceProvider =>
        {
            var connectionService = serviceProvider.GetRequiredService<ISshConnectionService>();
            var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
            // A dedicated SFTP channel per session, built from the same credentials.
            return new SftpService(connectionService, session =>
            {
                var authMethods = CreateAuthenticationMethods(session.ConnectionInfo);
                var info = new Renci.SshNet.ConnectionInfo(
                    session.ConnectionInfo.Host,
                    session.ConnectionInfo.Port,
                    session.ConnectionInfo.Username,
                    authMethods);
                info.Timeout = ConnectTimeout(settingsService);
                return new SftpClientWrapper(new SftpClient(info));
            }, settingsService);
        });

        services.AddSingleton<ITransferManager, TransferManager>();

        services.AddSingleton<PulseTerm.Core.Services.ISessionMetricsService>(sp =>
            new SessionMetricsService(sp.GetRequiredService<ISshConnectionService>()));
        services.AddSingleton<ITunnelService>(serviceProvider =>
        {
            var connectionService = serviceProvider.GetRequiredService<ISshConnectionService>();
            return new TunnelService(connectionService, sessionId =>
            {
                return connectionService.GetClient(sessionId)
                    ?? throw new InvalidOperationException($"No SSH client found for session {sessionId}.");
            });
        });

        return services;
    }

    /// <summary>连接超时(设置 → 常规 → 连接默认值);设置不可读时退回既有的 10 秒。</summary>
    private static TimeSpan ConnectTimeout(ISettingsService? settingsService)
    {
        try
        {
            var seconds = settingsService?.GetSettingsAsync().GetAwaiter().GetResult()
                .General.ConnectTimeoutSeconds ?? 10;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 600));
        }
        catch
        {
            return TimeSpan.FromSeconds(10);
        }
    }

    /// <summary>主机信任策略(设置 → 安全审计);设置不可读时用默认策略(TOFU + 变更阻断)。</summary>
    private static PulseTerm.Core.Models.SecurityOptions GetSecurityOptions(ISettingsService? settingsService)
    {
        try
        {
            return settingsService?.GetSettingsAsync().GetAwaiter().GetResult().Security
                ?? new PulseTerm.Core.Models.SecurityOptions();
        }
        catch
        {
            return new PulseTerm.Core.Models.SecurityOptions();
        }
    }

    /// <summary>心跳间隔(设置 → 常规):0 = 关闭(SSH.NET 用 -1ms 表示禁用)。</summary>
    private static TimeSpan KeepAliveInterval(ISettingsService? settingsService)
    {
        try
        {
            var seconds = settingsService?.GetSettingsAsync().GetAwaiter().GetResult()
                .General.KeepAliveSeconds ?? 0;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
        }
        catch
        {
            return Timeout.InfiniteTimeSpan;
        }
    }

    private static ISshClientWrapper CreateSshClientWrapper(
        PulseConnectionInfo connectionInfo,
        PulseTerm.Core.Ssh.IHostKeyService? hostKeyService = null,
        ISettingsService? settingsService = null,
        PulseTerm.Core.Ssh.IHostKeyPrompt? hostKeyPrompt = null,
        PulseTerm.Core.Ssh.ISecurityAlertService? securityAlerts = null)
    {
        var authMethods = CreateAuthenticationMethods(connectionInfo);
        var sshConnectionInfo = new Renci.SshNet.ConnectionInfo(
            connectionInfo.Host,
            connectionInfo.Port,
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
                var fingerprint = "SHA256:" + Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(e.HostKey)).TrimEnd('=');

                var verification = hostKeyService
                    .VerifyHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                    .GetAwaiter().GetResult();

                var security = GetSecurityOptions(settingsService);
                var target = $"{connectionInfo.Host}:{connectionInfo.Port}";

                switch (verification)
                {
                    case PulseTerm.Core.Ssh.HostKeyVerification.Trusted:
                        e.CanTrust = true;
                        break;

                    case PulseTerm.Core.Ssh.HostKeyVerification.Unknown:
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
                            _ = securityAlerts?.RaiseAsync("hostkey-rejected",
                                $"已拒绝 {target} 的首次连接指纹:{fingerprint}");
                        }
                        break;

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
                            _ = securityAlerts?.RaiseAsync("hostkey-changed-accepted",
                                $"{target} 的主机指纹已变更,用户确认信任新指纹:{fingerprint}");
                        }
                        else
                        {
                            _ = securityAlerts?.RaiseAsync("hostkey-changed-blocked",
                                $"{target} 的主机指纹与 known_hosts 记录不符,连接已被阻断(疑似中间人)。新指纹:{fingerprint}");
                        }
                        break;
                }
            };
        }

        return new SshClientWrapper(client);
    }

    private static AuthenticationMethod[] CreateAuthenticationMethods(PulseConnectionInfo connectionInfo)
    {
        return connectionInfo.AuthMethod switch
        {
            AuthMethod.Password =>
            // byte[] 重载:SSH.NET 会在认证方法释放时清零该缓冲区(string 重载做不到),
            // 因此密码不会以不可清除的托管字符串常驻在 SSH.NET 内部。
            [new PasswordAuthenticationMethod(
                connectionInfo.Username,
                System.Text.Encoding.UTF8.GetBytes(connectionInfo.Password ?? string.Empty))],

            AuthMethod.PrivateKey =>
            [new PrivateKeyAuthenticationMethod(connectionInfo.Username, CreatePrivateKeyFile(connectionInfo))],

            _ => throw new ArgumentOutOfRangeException(nameof(connectionInfo), connectionInfo.AuthMethod, "Unsupported authentication method.")
        };
    }

    private static PrivateKeyFile CreatePrivateKeyFile(PulseConnectionInfo connectionInfo)
    {
        var privateKeyPath = connectionInfo.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            throw new InvalidOperationException("Private key path is required for private key authentication.");
        }

        return string.IsNullOrWhiteSpace(connectionInfo.PrivateKeyPassphrase)
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, connectionInfo.PrivateKeyPassphrase);
    }
}
