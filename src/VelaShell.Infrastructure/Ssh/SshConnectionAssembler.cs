using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Data;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
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
        SshConnectionOptions options = new(info.Username, info.Host, info.Port)
        {
            Dialer = dialer,
            HostKeyPolicy = policy,
            Credentials = await BuildCredentialsAsync(info, cancellationToken).ConfigureAwait(false),
            ConnectTimeout = connectTimeout,
            KeepAlive = KeepAlive(settings, info),
        };

        return await options.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按用户选的认证方式给出凭据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只用用户显式选择的那一种,不做任何隐式回退。</b>库本身也不回退
    /// (不自动读 <c>~/.ssh/id_*</c>、不自动连 ssh-agent),两边是一致的。
    /// VelaShell 没有「用 agent / 默认私钥」这个 UI 选项,静默回退本就非预期 ——
    /// 它既会制造噪声,也可能拿一把用户没打算用的钥去认证。
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
