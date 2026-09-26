// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Config;

public static partial class SshConfigFile
{
    /// <summary>跳板链的深度上限（velashell-docs/zh/ssh/spec/09 §7）。</summary>
    public const int MaxJumpDepth = 8;

    /// <summary>
    /// 按 <c>ssh_config</c> 为一台主机造出<b>可以直接拿去连</b>的连接参数。
    /// </summary>
    /// <param name="blocks">解析好的配置（<see cref="LoadAsync"/> / <see cref="Parse"/> 的结果）。</param>
    /// <param name="host">用户输入的主机名（配置里 <c>Host</c> 匹配的对象）。</param>
    /// <param name="settings">配置文件里没有、要由调用方给的东西。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接参数。<c>ProxyJump</c> 的跳板链、<c>ProxyCommand</c> 都已经装进 <see cref="SshConnectionOptions.Dialer"/>。</returns>
    /// <exception cref="SshConnectException">跳板链有环或超过 <see cref="MaxJumpDepth"/>。</exception>
    /// <remarks>
    /// 映射规则见 <c>velashell-docs/zh/ssh/spec/09-dialing.md</c> §7。几个要点：
    /// <list type="bullet">
    ///   <item><c>ProxyJump</c> 上的每个跳板<b>按同一份配置解析</b>（有自己的 User / Port / IdentityFile）。</item>
    ///   <item><c>ProxyJump</c> 与 <c>ProxyCommand</c> 同时出现时 <c>ProxyJump</c> 优先。</item>
    ///   <item><c>ForwardAgent</c> / <c>ForwardX11</c> 是<b>会话</b>参数 —— 见 <see cref="SshHostConfig.ApplyToShell"/>。</item>
    /// </list>
    /// </remarks>
    public static ValueTask<SshConnectionOptions> CreateConnectionOptionsAsync(
        IReadOnlyList<SshConfigBlock> blocks,
        string host,
        SshConfigConnectOptions? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentException.ThrowIfNullOrEmpty(host);

        return CreateCoreAsync(
            blocks, host, settings ?? new SshConfigConnectOptions(), [], userOverride: null, portOverride: null,
            isTarget: true, new IdentityCache(), cancellationToken);
    }

    /// <summary>一次解析里已经读过的 <c>IdentityFile</c>（按完整路径）；<see langword="null"/> 表示读不出来、已跳过。</summary>
    /// <remarks>
    /// 跳板与目标常常用同一把钥。曾经每一跳各读一遍：加密的钥每一跳都要重跑一遍 KDF
    /// （默认参数下约 0.3 秒），口令也每一跳问一次。只在这一次解析里共用，不跨调用缓存 ——
    /// 解密后的私钥本来就要在连接期间留在内存里，这里不多留一分钟。
    /// </remarks>
    private sealed class IdentityCache()
        : Dictionary<string, ISshSigner?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static async ValueTask<SshConnectionOptions> CreateCoreAsync(
        IReadOnlyList<SshConfigBlock> blocks,
        string host,
        SshConfigConnectOptions settings,
        IReadOnlyList<string> chain,
        string? userOverride,
        int? portOverride,
        bool isTarget,
        IdentityCache identities,
        CancellationToken cancellationToken)
    {
        SshHostConfig config = Resolve(blocks, host);

        // 跳板规格里显式写的用户与端口（ProxyJump bob@jump:2222）优先于那台主机的配置。
        string user = userOverride ?? config.User ?? settings.DefaultUserName ?? Environment.UserName;

        SshConnectionOptions options = new(user, config.HostName, portOverride ?? config.Port)
        {
            Credentials =
            [
                .. await LoadIdentityFilesAsync(config, user, settings, identities, cancellationToken).ConfigureAwait(false),
                .. CallerCredentialsFor(settings, isTarget),
            ],
            HostKeyPolicy = MapHostKeyPolicy(config, settings),
        };

        if (config.Compression)
        {
            options = options with { Algorithms = options.Algorithms.WithCompression() };
        }

        if (config.ServerAliveInterval > 0)
        {
            options = options with
            {
                KeepAlive = new SshKeepAlivePolicy(
                    TimeSpan.FromSeconds(config.ServerAliveInterval), Math.Max(1, config.ServerAliveCountMax)),
            };
        }

        if (config.ConnectTimeoutSeconds is { } timeout)
        {
            options = options with { ConnectTimeout = TimeSpan.FromSeconds(timeout) };
        }

        // 〔velashell-docs/zh/ssh/spec/09 §7〕ProxyJump 优先于 ProxyCommand。
        if (IsSet(config.ProxyJump))
        {
            options = options with
            {
                Dialer = await BuildJumpChainAsync(blocks, host, config.ProxyJump!, settings, chain, identities, cancellationToken)
                    .ConfigureAwait(false),
            };
        }
        else if (IsSet(config.ProxyCommand))
        {
            options = options with
            {
                Dialer = new ProxyCommandDialer(config.ProxyCommand!) { UserName = user, OriginalHost = host },
            };
        }

        return settings.Configure?.Invoke(options) ?? options;
    }

    /// <summary>
    /// <c>ProxyJump a,b</c>：先按配置连 <c>a</c>（它自己的 ProxyJump / ProxyCommand 照常生效），
    /// 再经 <c>a</c> 连 <c>b</c>，最后经 <c>b</c> 连目标。
    /// </summary>
    private static async ValueTask<ISshTransportDialer> BuildJumpChainAsync(
        IReadOnlyList<SshConfigBlock> blocks,
        string host,
        string proxyJump,
        SshConfigConnectOptions settings,
        IReadOnlyList<string> chain,
        IdentityCache identities,
        CancellationToken cancellationToken)
    {
        List<string> visiting = [.. chain, host];
        if (visiting.Count > MaxJumpDepth)
        {
            throw new SshConnectException(
                SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"ProxyJump 链超过了 {MaxJumpDepth} 层（{string.Join(" → ", visiting)}）。");
        }

        IReadOnlyList<SshProxyJumpHop> hops = ParseProxyJump(proxyJump);
        List<SshConnectionOptions> jumpOptions = [];

        for (int i = 0; i < hops.Count; i++)
        {
            (string? jumpUser, string jumpHost, int? jumpPort) = hops[i];

            if (visiting.Contains(jumpHost, StringComparer.OrdinalIgnoreCase))
            {
                throw new SshConnectException(
                    SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                    $"ProxyJump 链有环：{string.Join(" → ", visiting)} → {jumpHost}。");
            }

            SshConnectionOptions resolved = await CreateCoreAsync(
                blocks, jumpHost, settings, visiting, jumpUser, jumpPort, isTarget: false, identities, cancellationToken)
                .ConfigureAwait(false);

            // 第一个跳板用它自己的拨号器；其后每一个都经前一个到达。
            if (i > 0)
            {
                resolved = resolved with { Dialer = new SshJumpDialer(jumpOptions[i - 1]) };
            }

            jumpOptions.Add(resolved);
        }

        if (jumpOptions.Count == 0)
        {
            throw new SshConnectException(
                SshFailureReason.InvalidConfiguration, SshPhase.Dialing, $"ProxyJump 的值「{proxyJump}」里没有跳板。");
        }

        return new SshJumpDialer(jumpOptions[^1]);
    }

    /// <summary>
    /// 解析 <c>ProxyJump</c> 的值：逗号分隔的 <c>[user@]host[:port]</c>，按经过的先后排列
    /// （也认 <c>ssh://</c> 写法与 <c>[IPv6]:port</c>）。
    /// </summary>
    /// <param name="value"><c>ProxyJump</c> 的值。</param>
    /// <returns><c>none</c>、空值时为空列表。离目标最近的那一跳是最后一个。</returns>
    public static IReadOnlyList<SshProxyJumpHop> ParseProxyJump(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseJumpSpec)];
    }

    /// <summary>解析 <c>[user@]host[:port]</c>（IPv6 写成 <c>[addr]:port</c>）。</summary>
    internal static SshProxyJumpHop ParseJumpSpec(string spec)
    {
        // ssh:// 形式也认 —— ssh_config(5) 允许。
        if (spec.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["ssh://".Length..].TrimEnd('/');
        }

        string? user = null;
        int at = spec.LastIndexOf('@');
        if (at >= 0)
        {
            user = spec[..at];
            spec = spec[(at + 1)..];
        }

        int? port = null;
        if (spec.StartsWith('['))
        {
            int close = spec.IndexOf(']', StringComparison.Ordinal);
            string address = close > 0 ? spec[1..close] : spec.Trim('[', ']');
            if (close > 0 && close + 1 < spec.Length && spec[close + 1] == ':'
                && int.TryParse(spec[(close + 2)..], System.Globalization.CultureInfo.InvariantCulture, out int p6))
            {
                port = p6;
            }
            return new SshProxyJumpHop(user, address, port);
        }

        int colon = spec.LastIndexOf(':');
        if (colon > 0 && spec.IndexOf(':', StringComparison.Ordinal) == colon
            && int.TryParse(spec[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int p))
        {
            port = p;
            spec = spec[..colon];
        }

        return new SshProxyJumpHop(user, spec, port);
    }

    /// <summary>调用方给的凭据里，哪些可以交给这一跳。</summary>
    /// <remarks>
    /// ⚠️ <b>口令与键盘交互只给最终目标。</b>调用方给的口令是为目标主机准备的；曾经每一跳都拿到同一份凭据，
    /// 目标的口令就这样发给了跳板 —— 跳板的管理员（或者攻下了跳板的人）就此拿到它。
    /// 公钥凭据（agent 里的钥、内存里的钥）照常给跳板：出示公钥不泄露秘密，
    /// 而经 agent 登跳板正是最常见的用法。跳板自己的 <c>IdentityFile</c> 照常从配置读。
    /// </remarks>
    private static IEnumerable<SshCredential> CallerCredentialsFor(SshConfigConnectOptions settings, bool isTarget) =>
        isTarget ? settings.Credentials : settings.Credentials.OfType<PublicKeyCredential>();

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    private static IHostKeyPolicy MapHostKeyPolicy(SshHostConfig config, SshConfigConnectOptions settings)
    {
        string? strict = config.StrictHostKeyChecking?.ToLowerInvariant();
        string? knownHosts = config.UserKnownHostsFile?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        // ask 与缺省是「交互式」的那一种 —— 调用方给了自己的策略（带着它自己的信任库与询问界面）就用它，
        // 曾经只要配置里写了 UserKnownHostsFile 就另起一个策略，把调用方的那一个丢掉。
        // yes / no / accept-new 是配置明确要求的行为，照配置来。
        if (strict is null or "ask" && settings.HostKeyPolicy is { } callerPolicy)
        {
            return callerPolicy;
        }

        if (strict is null && knownHosts is null)
        {
            return new KnownHostsPolicy { UnknownHost = UnknownHostBehavior.Reject };
        }

        // 〔velashell-docs/zh/ssh/spec/09 §7〕yes → 没见过就拒；accept-new / no → 接受并记下；ask / 缺省 → 问。
        // ⚠️ 「no」在 OpenSSH 里连「密钥变了」也放行 —— 我们**不跟**：密钥变化永远拒绝，
        //    那是中间人防护的全部意义；要接受新密钥，去 known_hosts 里删掉旧的那一行。
        UnknownHostBehavior unknown = strict switch
        {
            "yes" => UnknownHostBehavior.Reject,
            "accept-new" or "no" or "off" => UnknownHostBehavior.AcceptAndPersist,
            _ => settings.AskUnknownHost is null ? UnknownHostBehavior.Reject : UnknownHostBehavior.Ask,
        };

        // UserKnownHostsFile none / /dev/null：明确不用 known_hosts —— 每台主机都是没见过的，接受了也不记。
        // 曾经把它们当成路径：Windows 上去找当前目录里一个叫 none 的文件。
        if (knownHosts is not null
            && (string.Equals(knownHosts, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(knownHosts, "/dev/null", StringComparison.Ordinal)))
        {
            return KnownHostsPolicy.WithoutFile(settings.AskUnknownHost, unknown);
        }

        return new KnownHostsPolicy(ExpandPath(knownHosts, null, null), settings.AskUnknownHost)
        {
            UnknownHost = unknown,
        };
    }

    private static async ValueTask<IReadOnlyList<SshCredential>> LoadIdentityFilesAsync(
        SshHostConfig config, string user, SshConfigConnectOptions settings, IdentityCache identities,
        CancellationToken cancellationToken)
    {
        List<SshCredential> credentials = [];

        foreach (string raw in config.IdentityFiles)
        {
            string? path = ExpandPath(raw, config.HostName, user);
            if (path is null || !File.Exists(path))
            {
                continue;   // ssh 同样静默跳过不存在的 IdentityFile（默认列表里的大多数都不存在）
            }

            // 这次解析里读过（或跳过过）就不再读、不再问口令、不再报跳过（见 IdentityCache）。
            string fullPath = Path.GetFullPath(path);
            if (!identities.TryGetValue(fullPath, out ISshSigner? signer))
            {
                try
                {
                    signer = await TryLoadKeyAsync(path, settings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SshPrivateKeyException or IOException or UnauthorizedAccessException)
                {
                    // 这一把读不出来只跳过它自己 —— 别的钥、别的凭据照常去试（见 IdentityFileSkipped）。
                    settings.IdentityFileSkipped?.Invoke(path, ex);
                    signer = null;
                }

                identities[fullPath] = signer;
            }

            if (signer is not null)
            {
                credentials.Add(new PublicKeyCredential(signer, $"publickey ({Path.GetFileName(path)})"));
            }
        }

        return credentials;
    }

    private static async ValueTask<ISshSigner?> TryLoadKeyAsync(
        string path, SshConfigConnectOptions settings, CancellationToken cancellationToken)
    {
        try
        {
            return await SshPrivateKeyFile.LoadAsync(path, passphrase: null, cancellationToken).ConfigureAwait(false);
        }
        catch (SshPrivateKeyException first) when (first.NeedsPassphrase)
        {
            if (settings.PassphraseProvider is null)
            {
                return null;
            }

            string? passphrase = await settings.PassphraseProvider(path, cancellationToken).ConfigureAwait(false);
            return passphrase is null
                ? null
                : await SshPrivateKeyFile.LoadAsync(path, passphrase, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>展开 <c>~</c> 与 <c>%d %u %h %r %%</c>。</summary>
    internal static string? ExpandPath(string? raw, string? host, string? remoteUser)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string value = raw.Trim().Trim('"');

        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = home + value[1..];
        }

        StringBuilder result = new(value.Length + 16);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 1 >= value.Length)
            {
                result.Append(value[i]);
                continue;
            }

            char token = value[++i];
            result.Append(token switch
            {
                'd' => home,
                'u' => Environment.UserName,
                'h' => host ?? "",
                'r' => remoteUser ?? "",
                '%' => "%",
                _ => "%" + token,
            });
        }

        return result.ToString();
    }
}

