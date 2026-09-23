using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;
using LibraryCertificate = VelaShell.Ssh.Keys.OpenSshCertificate;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把一份 <see cref="VelaConnectionInfo" /> 装配成一条可用的 SSH 连接。
/// </summary>
/// <remarks>
/// <para>
/// 这里集中了原先散在 <c>InfrastructureServiceCollectionExtensions</c> 里的
/// <c>BuildSshClientSettings</c> / <c>AddCredential</c> / <c>BuildProxyChain</c> /
/// <c>AddHostAuthentication</c> 四段 —— 它们本来就是同一件事的四个部分,
/// 放在 DI 注册里只是历史原因,而那让「怎么连上去」这件事读起来要在两百行里跳着找。
/// </para>
/// <para>
/// <b>跳板链的方向</b>:<see cref="VelaConnectionInfo.JumpHost" /> 是「要先连谁」,
/// 所以最内层(没有自己跳板的那一跳)才是真正出网的那一跳 ——
/// 网络代理只作用于它,其余各跳都跑在 SSH 通道里。
/// </para>
/// </remarks>
internal static class SshConnectionAssembler
{
    /// <summary>
    /// 装配结果:连接工厂,以及跟着包装器一起释放的拨号器(跳板链持有跳板连接)。
    /// </summary>
    internal readonly record struct Assembled(
        Func<CancellationToken, ValueTask<SshConnection>> Connect,
        IAsyncDisposable? DialerLifetime,
        TimeSpan ConnectTimeout);

    /// <summary>按连接信息装配。</summary>
    public static Assembled Create(
        VelaConnectionInfo info,
        IHostKeyService? hostKey,
        ISettingsService? settings,
        IHostKeyPrompt? prompt,
        ISecurityAlertService? alerts,
        IProxyResolver? proxyResolver)
    {
        ArgumentNullException.ThrowIfNull(info);

        // 链上每一跳共用同一个策略实例 —— 跳板与终点走同一套信任判定。
        IHostKeyPolicy policy = hostKey is null
            ? new DangerousAcceptAnyHostKeyPolicy()
            : new VelaHostKeyPolicy(hostKey, settings, prompt, alerts);

        TimeSpan connectTimeout = ConnectTimeout(settings);

        // 最内层跳板真正出网,代理装在它身上;外层每一跳用 SshJumpDialer 包住内层。
        ISshTransportDialer dialer = new ProxyTransportDialer(proxyResolver);
        SshJumpDialer? outermostJump = null;

        foreach (VelaConnectionInfo hop in JumpChainInnerToOuter(info))
        {
            ISshTransportDialer inner = dialer;
            SshJumpDialer jump = new(ct => ConnectAsync(hop, policy, settings, inner, connectTimeout, ct));
            dialer = jump;
            outermostJump = jump;
        }

        ISshTransportDialer finalDialer = dialer;
        return new Assembled(
            ct => ConnectAsync(info, policy, settings, finalDialer, connectTimeout, ct),
            outermostJump,
            connectTimeout);
    }

    /// <summary>
    /// 跳板链,**从最内层往外**。
    /// </summary>
    /// <remarks>
    /// <c>info.JumpHost</c> 是「连 info 之前要先连的那台」,所以链表本身是由外到内的;
    /// 装配拨号器要反过来 —— 先有最内层那条真实出站,才谈得上在它上面开通道。
    /// </remarks>
    private static List<VelaConnectionInfo> JumpChainInnerToOuter(VelaConnectionInfo info)
    {
        List<VelaConnectionInfo> hops = [];
        for (VelaConnectionInfo? hop = info.JumpHost; hop is not null; hop = hop.JumpHost)
        {
            hops.Add(hop);
        }
        hops.Reverse();
        return hops;
    }

    private static async ValueTask<SshConnection> ConnectAsync(
        VelaConnectionInfo info,
        IHostKeyPolicy policy,
        ISettingsService? settings,
        ISshTransportDialer dialer,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        // agent 这一路的签名要回到 agent 去做,所以 agent 客户端得一直活到认证结束 ——
        // 连接建好之后就不再需要它(重协商不会重新认证),在这里释放。
        SshAgentClient? agent = null;
        try
        {
            IReadOnlyList<SshCredential> credentials;
            if (info.AuthMethod == AuthMethod.Agent)
            {
                agent = await ConnectAgentAsync(cancellationToken).ConfigureAwait(false);
                credentials = await AgentCredentialsAsync(agent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                credentials = await BuildCredentialsAsync(info, cancellationToken).ConfigureAwait(false);
            }

            SshConnectionOptions options = new(info.Username, info.Host, info.Port)
            {
                Dialer = dialer,
                HostKeyPolicy = policy,
                Credentials = credentials,
                ConnectTimeout = connectTimeout,
                KeepAlive = KeepAlive(settings, info),
                Algorithms = Algorithms(info),
            };

            SshConnection connection = await options.ConnectAsync(cancellationToken).ConfigureAwait(false);

            // 「自动加载密钥到 Agent」:认证成功之后才加(配错的钥不该进 agent),而且丢到后台 ——
            // agent 没在跑时要等满三秒才知道,那段等待不该落在连接路径上。
            if (AddKeysToAgent(settings)
                && SshAgentKeyLoader.TryGetKeyToAdd(info, credentials, out InMemorySshSigner key, out string comment))
            {
                _ = Task.Run(() => SshAgentKeyLoader.AddAsync(key, comment, ConnectLocalAgentAsync), CancellationToken.None);
            }

            return connection;
        }
        finally
        {
            if (agent is not null)
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 这一跳的算法集:开了压缩就把 <c>zlib@openssh.com</c> 排在 <c>none</c> 前面。
    /// </summary>
    /// <remarks>
    /// 压缩是**协商**出来的:服务端没开(<c>Compression no</c>)时自动落回不压缩,
    /// 不会因此连不上。用的是 <c>zlib@openssh.com</c>(认证之后才开始压缩)而不是
    /// 老式的 <c>zlib</c> —— 后者在认证之前就压缩,是历史上 CRIME 一类攻击的入口。
    /// </remarks>
    internal static SshAlgorithmSet Algorithms(VelaConnectionInfo info) =>
        info.Ssh is { Compression: true }
            ? SshAlgorithmSet.Default.WithCompression()
            : SshAlgorithmSet.Default;

    /// <summary>
    /// 本机 agent 的端点:Windows 上默认是 OpenSSH Authentication Agent 服务的命名管道。
    /// </summary>
    /// <remarks>
    /// Windows 上 <c>SSH_AUTH_SOCK</c> 只在它本身就是命名管道时才采用(1Password、KeePassXC
    /// 之类会这样配);它更常指向 Git Bash / WSL 的 Unix 套接字,那是另一套 agent,.NET 连不上。
    /// 其它平台交给库按 <c>SSH_AUTH_SOCK</c> 取。
    /// </remarks>
    internal static string? AgentEndpoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        string? socket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        return socket is not null && socket.StartsWith(@"\\.\pipe\", StringComparison.Ordinal) ? socket : null;
    }

    /// <summary>连本机 agent 的上限。</summary>
    /// <remarks>
    /// Windows 上 agent 服务没起时命名管道根本不存在,而不带超时的管道连接会<b>一直重试</b>
    /// 直到管道出现 —— 用户看到的是连接转圈转到整条连接超时,原因只字不提。
    /// 本机 IPC 用不了多久,三秒足够分辨「在跑」与「没在跑」。
    /// </remarks>
    private static readonly TimeSpan AgentConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>连本机 agent,带 <see cref="AgentConnectTimeout" /> 上限;超时以 <see cref="OperationCanceledException" /> 报出。</summary>
    internal static async ValueTask<SshAgentClient> ConnectLocalAgentAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AgentConnectTimeout);
        return await SshAgentClient.ConnectAsync(AgentEndpoint(), timeout.Token).ConfigureAwait(false);
    }

    private static async ValueTask<SshAgentClient> ConnectAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectLocalAgentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshAgentException ex)
        {
            throw new VelaSshAuthenticationException(Strings.Format("SshErr_AgentUnavailable", ex.Message), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VelaSshAuthenticationException(
                Strings.Format("SshErr_AgentUnavailable", Strings.Get("SshErr_AgentNotRunning")), ex);
        }
    }

    /// <summary>agent 里的每一把钥都作为一个候选凭据,由库按顺序逐把试。</summary>
    /// <remarks>
    /// agent 里一把钥都没有时直接说清楚,而不是把一个空凭据列表交给库 ——
    /// 那样用户拿到的是一句笼统的「认证方法已用尽」,看不出问题在本机。
    /// </remarks>
    private static async ValueTask<IReadOnlyList<SshCredential>> AgentCredentialsAsync(
        SshAgentClient agent, CancellationToken cancellationToken)
    {
        IReadOnlyList<SshCredential> credentials;
        try
        {
            credentials = await agent.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshAgentException ex)
        {
            throw new VelaSshAuthenticationException(Strings.Format("SshErr_AgentUnavailable", ex.Message), ex);
        }
        return credentials.Count > 0
            ? credentials
            : throw new VelaSshAuthenticationException(Strings.Get("SshErr_AgentNoKeys"));
    }

    /// <summary>
    /// 按用户选的认证方式给出凭据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只用用户显式选择的那一种,不做任何隐式回退。</b>库本身也不回退
    /// (不自动读 <c>~/.ssh/id_*</c>、不自动连 ssh-agent),两边是一致的。
    /// 要用 agent,得在认证方式里显式选「SSH Agent」(<see cref="AuthMethod.Agent" />,
    /// 不经过这里,见 <c>ConnectAsync</c>);选了别的方式就不会再去碰 agent ——
    /// 静默回退既会制造噪声,也可能拿一把用户没打算用的钥去认证。
    /// </para>
    /// <para>
    /// <b>密码那一路同时应答 <c>keyboard-interactive</c>。</b>很多服务端
    /// (尤其关了 <c>PasswordAuthentication</c> 却开着 PAM 的)只接受后者,
    /// 而用户填的就是同一个密码 —— 这是 <see cref="PasswordCredential" />
    /// 的默认行为,这里不去关它。
    /// </para>
    /// </remarks>
    internal static async ValueTask<IReadOnlyList<SshCredential>> BuildCredentialsAsync(
        VelaConnectionInfo info, CancellationToken cancellationToken)
    {
        switch (info.AuthMethod)
        {
            case AuthMethod.Password:
                return [new PasswordCredential(info.Password ?? "")];

            case AuthMethod.PrivateKey:
                {
                    ISshSigner signer = await LoadSignerAsync(
                        info.PrivateKeyPath!, info.PrivateKeyPassphrase, cancellationToken).ConfigureAwait(false);
                    return [new PublicKeyCredential(signer, info.PrivateKeyPath)];
                }

            case AuthMethod.Certificate:
                {
                    ISshSigner signer = await LoadSignerAsync(
                        info.PrivateKeyPath!, info.PrivateKeyPassphrase, cancellationToken).ConfigureAwait(false);

                    LibraryCertificate certificate = await LibraryCertificate
                        .LoadAsync(info.CertificatePath!, cancellationToken).ConfigureAwait(false);

                    // Create 当场核对「证书与私钥是不是一对」—— 不核对的话配错了的表现是
                    // 服务端一句 Permission denied,与「CA 不被信任」「主体不匹配」没法区分。
                    return [new PublicKeyCredential(
                        SshCertificateSigner.Create(certificate, signer), info.CertificatePath)];
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(info), info.AuthMethod, "Unsupported authentication method.");
        }
    }

    /// <summary>
    /// 读一把私钥。
    /// </summary>
    /// <remarks>
    /// 支持的格式:OpenSSH(<b>含加密</b>)、PKCS#1、PKCS#8(含加密)、SEC1、PuTTY 的 <c>.ppk</c>。
    /// <para>
    /// 上一版这里有一段 <c>OpenSshPrivateKey.TryConvertToOpenSsh</c> 的转换:
    /// 因为那个底层库**只认 OpenSSH 格式**,用户导入的传统 PEM 会被静默跳过,
    /// 认证以一句 "skipped: publickey" 失败。现在库原生认这些格式,那段转换整个不需要了。
    /// </para>
    /// </remarks>
    private static ValueTask<ISshSigner> LoadSignerAsync(
        string path, string? passphrase, CancellationToken cancellationToken) =>
        SshPrivateKeyFile.LoadAsync(
            path, string.IsNullOrWhiteSpace(passphrase) ? null : passphrase, cancellationToken);

    /// <summary>设置 → 密钥管理 →「自动加载密钥到 Agent」。读不到设置时按默认值(关)。</summary>
    private static bool AddKeysToAgent(ISettingsService? settings)
    {
        try
        {
            return settings?.GetSnapshotBlocking().Keys.AddKeysToAgent ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan ConnectTimeout(ISettingsService? settings)
    {
        try
        {
            return TimeSpan.FromSeconds(
                Math.Clamp(settings.GetSnapshotBlocking().General.ConnectTimeoutSeconds, 1, 600));
        }
        catch
        {
            return TimeSpan.FromSeconds(10);
        }
    }

    /// <summary>
    /// 保活心跳间隔:本次连接有会话级覆盖就用它,否则跟随全局设置。
    /// </summary>
    /// <remarks>
    /// 覆盖值随 <see cref="VelaConnectionInfo.KeepAliveSeconds" /> 一路带下来(F-06)。
    /// 跳板链上每一跳各带各的。
    /// </remarks>
    private static KeepAlivePolicy KeepAlive(ISettingsService? settings, VelaConnectionInfo info)
    {
        try
        {
            int seconds = info.KeepAliveSeconds ?? settings.GetSnapshotBlocking().General.KeepAliveSeconds;
            return seconds > 0 ? new KeepAlivePolicy(TimeSpan.FromSeconds(seconds)) : KeepAlivePolicy.Disabled;
        }
        catch
        {
            return KeepAlivePolicy.Disabled;
        }
    }
}
