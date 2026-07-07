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

        services.AddSingleton<ISshConnectionService>(serviceProvider =>
        {
            var hostKeyService = serviceProvider.GetRequiredService<PulseTerm.Core.Ssh.IHostKeyService>();
            return new SshConnectionService(connectionInfo => CreateSshClientWrapper(connectionInfo, hostKeyService));
        });

        services.AddSingleton<ISftpService>(serviceProvider =>
        {
            var connectionService = serviceProvider.GetRequiredService<ISshConnectionService>();
            // A dedicated SFTP channel per session, built from the same credentials.
            return new SftpService(connectionService, session =>
            {
                var authMethods = CreateAuthenticationMethods(session.ConnectionInfo);
                var info = new Renci.SshNet.ConnectionInfo(
                    session.ConnectionInfo.Host,
                    session.ConnectionInfo.Port,
                    session.ConnectionInfo.Username,
                    authMethods);
                info.Timeout = TimeSpan.FromSeconds(10);
                return new SftpClientWrapper(new SftpClient(info));
            });
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

    private static ISshClientWrapper CreateSshClientWrapper(
        PulseConnectionInfo connectionInfo,
        PulseTerm.Core.Ssh.IHostKeyService? hostKeyService = null)
    {
        var authMethods = CreateAuthenticationMethods(connectionInfo);
        var sshConnectionInfo = new Renci.SshNet.ConnectionInfo(
            connectionInfo.Host,
            connectionInfo.Port,
            connectionInfo.Username,
            authMethods);

        var client = new SshClient(sshConnectionInfo);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);

        if (hostKeyService is not null)
        {
            // TOFU 策略:首次连接记录指纹(known_hosts,SonnetDB);指纹变化即拒绝,
            // 防中间人。事件在握手线程上触发,同步等待本地存储是安全的。
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = "SHA256:" + Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(e.HostKey)).TrimEnd('=');

                var verification = hostKeyService
                    .VerifyHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                    .GetAwaiter().GetResult();

                switch (verification)
                {
                    case PulseTerm.Core.Ssh.HostKeyVerification.Trusted:
                        e.CanTrust = true;
                        break;
                    case PulseTerm.Core.Ssh.HostKeyVerification.Unknown:
                        e.CanTrust = true;
                        hostKeyService
                            .TrustHostKeyAsync(connectionInfo.Host, connectionInfo.Port, e.HostKeyName, fingerprint)
                            .GetAwaiter().GetResult();
                        break;
                    default:
                        // 指纹与记录不符:拒绝连接。
                        e.CanTrust = false;
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
            [new PasswordAuthenticationMethod(connectionInfo.Username, connectionInfo.Password ?? string.Empty)],

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
