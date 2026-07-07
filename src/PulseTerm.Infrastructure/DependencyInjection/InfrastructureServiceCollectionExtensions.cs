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
        services.AddSingleton<ISessionRepository>(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<PulseTermStoragePaths>();
            var dataStore = serviceProvider.GetRequiredService<JsonDataStore>();
            return new SessionRepository(dataStore, paths.SessionsFile);
        });

        services.AddSingleton<ISshConnectionService>(serviceProvider =>
        {
            return new SshConnectionService(connectionInfo => CreateSshClientWrapper(connectionInfo));
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

    private static ISshClientWrapper CreateSshClientWrapper(PulseConnectionInfo connectionInfo)
    {
        var authMethods = CreateAuthenticationMethods(connectionInfo);
        var sshConnectionInfo = new Renci.SshNet.ConnectionInfo(
            connectionInfo.Host,
            connectionInfo.Port,
            connectionInfo.Username,
            authMethods);

        var client = new SshClient(sshConnectionInfo);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);

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
