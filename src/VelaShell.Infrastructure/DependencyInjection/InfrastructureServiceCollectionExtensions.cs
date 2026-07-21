using Microsoft.Extensions.DependencyInjection;
using Tmds.Ssh;
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
        services.AddSingleton<VelaShellStoragePaths>();
        services.AddSingleton<SonnetDbEngine>(sp => new(sp.GetRequiredService<VelaShellStoragePaths>()));
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
                [paths.RootDirectory, paths.LegacyDotDirectory]);
        });
        services.AddSingleton<IHostKeyService>(sp =>
        {
            VelaShellStoragePaths paths = sp.GetRequiredService<VelaShellStoragePaths>();
            return new SonnetDbHostKeyService(sp.GetRequiredService<SonnetDbEngine>(),
                Path.Combine(paths.LegacyDotDirectory, "known_hosts.json"));
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

        // SSH connection service
        services.AddSingleton<ISshConnectionService>(sp =>
        {
            IHostKeyService hostKey = sp.GetRequiredService<IHostKeyService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            IHostKeyPrompt? prompt = sp.GetService<IHostKeyPrompt>();
            ISecurityAlertService? alerts = sp.GetService<ISecurityAlertService>();
            return new SshConnectionService(ci =>
                CreateSshClientWrapper(ci, hostKey, settings, prompt, alerts));
        });

        // SFTP service
        services.AddSingleton<ISftpService>(sp =>
        {
            ISshConnectionService connSvc = sp.GetRequiredService<ISshConnectionService>();
            ISettingsService settings = sp.GetRequiredService<ISettingsService>();
            return new SftpService(connSvc, session =>
            {
                ISshClientWrapper? wrapper = connSvc.GetClient(session.SessionId);
                if (wrapper is not TmdsSshClientWrapper tmds)
                    throw new InvalidOperationException("SFTP requires Tmds.Ssh backend.");
                return new TmdsSftpClientWrapper(async () =>
                {
                    // SFTP 复用主连接的 SSH 通道。主连接不在时不得偷偷另建连接:
                    // 新连接无人持有、无人释放(泄漏),且会绕过用户可见的连接生命周期。
                    SshClient inner = tmds.InnerClient
                        ?? throw new VelaSshConnectionException(
                            "SSH connection is not established; cannot open SFTP channel.");
                    return await inner.OpenSftpClientAsync().ConfigureAwait(false);
                });
            }, settings);
        });
        services.AddSingleton<ITransferManager, TransferManager>();
        services.AddSingleton<IGistSyncService>(sp => new Sync.GistSyncService(
            sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ISessionRepository>(),
            sp.GetRequiredService<IAppDataStore>(), sp.GetRequiredService<IQuickCommandRepository>(),
            sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<ISessionMetricsService>(sp =>
            new SessionMetricsService(sp.GetRequiredService<ISshConnectionService>()));
        services.AddSingleton<ITunnelService>(sp =>
        {
            ISshConnectionService connSvc = sp.GetRequiredService<ISshConnectionService>();
            return new TunnelService(connSvc, sid =>
                connSvc.GetClient(sid) ?? throw new InvalidOperationException($"No SSH client for session {sid}."));
        });
        return services;
    }

    // ---- Settings helpers ----

    private static TimeSpan ConnectTimeout(ISettingsService? s)
    {
        try { return TimeSpan.FromSeconds(Math.Clamp(s?.GetSettingsAsync().GetAwaiter().GetResult().General.ConnectTimeoutSeconds ?? 10, 1, 600)); }
        catch { return TimeSpan.FromSeconds(10); }
    }

    private static TimeSpan KeepAliveInterval(ISettingsService? s)
    {
        try
        {
            int sec = s?.GetSettingsAsync().GetAwaiter().GetResult().General.KeepAliveSeconds ?? 0;
            return sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.Zero;
        }
        catch { return TimeSpan.Zero; }
    }

    private static SecurityOptions GetSecurityOptions(ISettingsService? s)
    {
        try { return s?.GetSettingsAsync().GetAwaiter().GetResult().Security ?? new(); }
        catch { return new(); }
    }

    // ---- SSH client factory ----

    private static TmdsSshClientWrapper CreateSshClientWrapper(
        VelaConnectionInfo ci, IHostKeyService? hostKey, ISettingsService? settings,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        return new TmdsSshClientWrapper(BuildSshClientSettings(ci, hostKey, settings, prompt, alerts));
    }

    private static SshClientSettings BuildSshClientSettings(
        VelaConnectionInfo ci, IHostKeyService? hostKey, ISettingsService? settings,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        var s = new SshClientSettings($"{ci.Username}@{ci.Host}")
        {
            Port = ci.Port,
            ConnectTimeout = ConnectTimeout(settings),
            KeepAliveInterval = KeepAliveInterval(settings),
        };
        AddCredential(s, ci);

        // ProxyJump
        if (ci.JumpHost is not null)
            s.Proxy = BuildProxyChain(ci.JumpHost, hostKey, settings, prompt, alerts);

        // Host key verification
        if (hostKey is not null)
            AddHostAuthentication(s, ci, hostKey, settings, prompt, alerts);

        return s;
    }

    private static void AddCredential(SshClientSettings s, VelaConnectionInfo ci)
    {
        s.Credentials ??= [];
        switch (ci.AuthMethod)
        {
            case AuthMethod.Password:
                s.Credentials.Add(new PasswordCredential(ci.Password ?? ""));
                break;
            case AuthMethod.PrivateKey:
                s.Credentials.Add(string.IsNullOrWhiteSpace(ci.PrivateKeyPassphrase)
                    ? new PrivateKeyCredential(ci.PrivateKeyPath!, null, null)
                    : new PrivateKeyCredential(ci.PrivateKeyPath!, ci.PrivateKeyPassphrase, null));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ci), ci.AuthMethod,
                    @"Unsupported authentication method.");
        }
    }

    private static void AddHostAuthentication(
        SshClientSettings s, VelaConnectionInfo ci, IHostKeyService hostKey,
        ISettingsService? ss, IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        SecurityOptions security = GetSecurityOptions(ss);
        s.HostAuthentication = async (ctx, ct) =>
        {
            string fingerprint = ctx.ConnectionInfo.ServerKey.Key.SHA256FingerPrint;
            string keyType = ctx.ConnectionInfo.ServerKey.Key is { } k ? k.GetType().Name : "unknown";
            string host = ci.Host;
            int port = ci.Port;

            HostKeyVerification verification = await hostKey
                .VerifyHostKeyAsync(host, port, keyType, fingerprint, ct).ConfigureAwait(false);
            string target = $"{host}:{port}";

            if (verification == HostKeyVerification.Trusted
                || HostTrustOnceCache.IsTrusted(host, port, fingerprint))
                return true;

            HostKeyDecision decision;
            if (verification == HostKeyVerification.Unknown)
            {
                decision = security.ConfirmFirstFingerprint && prompt is not null
                    ? await prompt.DecideAsync(host, port, keyType, fingerprint, verification)
                    : HostKeyDecision.TrustPermanently;
            }
            else
            {
                decision = !security.BlockOnFingerprintChange && prompt is not null
                    ? await prompt.DecideAsync(host, port, keyType, fingerprint, verification)
                    : HostKeyDecision.Reject;
            }

            if (decision == HostKeyDecision.Reject)
            {
                alerts?.RaiseAsync("hostkey-rejected",
                    Strings.Format("KeySvc_AlertFirstRejected", target, fingerprint));
                return false;
            }
            if (decision == HostKeyDecision.TrustPermanently)
            {
                await hostKey.TrustHostKeyAsync(host, port, keyType, fingerprint, ct);
                if (verification == HostKeyVerification.Changed && alerts is not null)
                    await alerts.RaiseAsync("hostkey-changed-accepted",
                        Strings.Format("KeySvc_AlertChangedAccepted", target, fingerprint));
            }
            else if (decision == HostKeyDecision.TrustOnce)
            {
                HostTrustOnceCache.Remember(host, port, fingerprint);
                if (alerts is not null)
                    await alerts.RaiseAsync("hostkey-trusted-once",
                        verification == HostKeyVerification.Changed
                            ? Strings.Format("KeySvc_AlertChangedTrustOnce", target, fingerprint)
                            : Strings.Format("KeySvc_AlertFirstTrustOnce", target, fingerprint));
            }
            return true;
        };
    }

    private static SshProxy BuildProxyChain(VelaConnectionInfo jumpHost,
        IHostKeyService? hostKey, ISettingsService? ss,
        IHostKeyPrompt? prompt, ISecurityAlertService? alerts)
    {
        var proxy = new SshClientSettings($"{jumpHost.Username}@{jumpHost.Host}")
        {
            Port = jumpHost.Port,
            ConnectTimeout = ConnectTimeout(ss),
        };
        AddCredential(proxy, jumpHost);

        if (jumpHost.JumpHost is not null)
            proxy.Proxy = BuildProxyChain(jumpHost.JumpHost, hostKey, ss, prompt, alerts);

        if (hostKey is not null)
        {
            string host = jumpHost.Host;
            int port = jumpHost.Port;
            proxy.HostAuthentication = async (ctx, ct) =>
            {
                string fp = ctx.ConnectionInfo.ServerKey.Key.SHA256FingerPrint;
                string kt = ctx.ConnectionInfo.ServerKey.Key is { } k ? k.GetType().Name : "unknown";
                HostKeyVerification v = await hostKey.VerifyHostKeyAsync(host, port, kt, fp, ct);
                return v == HostKeyVerification.Trusted
                    || HostTrustOnceCache.IsTrusted(host, port, fp);
            };
        }

        return new SshProxy(proxy);
    }
}
