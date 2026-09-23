// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Config;

/// <summary>把 <c>ssh_config</c> 变成连接参数时，配置文件里没有、要由调用方给的那些东西。</summary>
public sealed record SshConfigConnectSettings
{
    /// <summary>配置里没写 <c>User</c> 时用谁；<see langword="null"/> 取本机当前用户名。</summary>
    public string? DefaultUserName { get; init; }

    /// <summary>
    /// 排在配置里的 <c>IdentityFile</c> <b>之后</b>的凭据（口令、键盘交互、agent 里的钥……）。
    /// </summary>
    public IReadOnlyList<SshCredential> Credentials { get; init; } = [];

    /// <summary>
    /// 加密的 <c>IdentityFile</c> 向谁要口令；参数是文件路径。
    /// 返回 <see langword="null"/> 表示跳过这把钥。<see langword="null"/>（不给回调）时加密的钥一律跳过。
    /// </summary>
    public Func<string, CancellationToken, ValueTask<string?>>? PassphraseProvider { get; init; }

    /// <summary>
    /// 配置里没有 <c>StrictHostKeyChecking</c> / <c>UserKnownHostsFile</c> 时用的主机密钥策略；
    /// <see langword="null"/> 时用 <see cref="SshConnectionOptions"/> 的默认（按 known_hosts，没见过就拒绝）。
    /// </summary>
    public IHostKeyPolicy? HostKeyPolicy { get; init; }

    /// <summary>
    /// <c>StrictHostKeyChecking ask</c>（或缺省）且配了 <c>UserKnownHostsFile</c> 时，没见过的主机问谁。
    /// </summary>
    public Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? AskUnknownHost { get; init; }

    /// <summary>最后再改一遍 —— 对<b>每一跳</b>（含跳板）的连接参数都会调用。</summary>
    public Func<SshConnectionOptions, SshConnectionOptions>? Configure { get; init; }
}

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
        SshConfigConnectSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentException.ThrowIfNullOrEmpty(host);

        return CreateCoreAsync(
            blocks, host, settings ?? new SshConfigConnectSettings(), [], userOverride: null, portOverride: null,
            cancellationToken);
    }

    private static async ValueTask<SshConnectionOptions> CreateCoreAsync(
        IReadOnlyList<SshConfigBlock> blocks,
        string host,
        SshConfigConnectSettings settings,
        IReadOnlyList<string> chain,
        string? userOverride,
        int? portOverride,
        CancellationToken cancellationToken)
    {
        SshHostConfig config = Resolve(blocks, host);

        // 跳板规格里显式写的用户与端口（ProxyJump bob@jump:2222）优先于那台主机的配置。
        string user = userOverride ?? config.User ?? settings.DefaultUserName ?? Environment.UserName;

        SshConnectionOptions options = new(user, config.HostName, portOverride ?? config.Port)
        {
            Credentials =
            [
                .. await LoadIdentityFilesAsync(config, user, settings, cancellationToken).ConfigureAwait(false),
                .. settings.Credentials,
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
                KeepAlive = new KeepAlivePolicy(
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
                Dialer = await BuildJumpChainAsync(blocks, host, config.ProxyJump!, settings, chain, cancellationToken)
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
        SshConfigConnectSettings settings,
        IReadOnlyList<string> chain,
        CancellationToken cancellationToken)
    {
        List<string> visiting = [.. chain, host];
        if (visiting.Count > MaxJumpDepth)
        {
            throw new SshConnectException(
                SshFailureReason.ProxyRefused, SshPhase.Dialing,
                $"ProxyJump 链超过了 {MaxJumpDepth} 层（{string.Join(" → ", visiting)}）。");
        }

        string[] hops = proxyJump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<SshConnectionOptions> jumpOptions = [];

        for (int i = 0; i < hops.Length; i++)
        {
            (string? jumpUser, string jumpHost, int? jumpPort) = ParseJumpSpec(hops[i]);

            if (visiting.Contains(jumpHost, StringComparer.OrdinalIgnoreCase))
            {
                throw new SshConnectException(
                    SshFailureReason.ProxyRefused, SshPhase.Dialing,
                    $"ProxyJump 链有环：{string.Join(" → ", visiting)} → {jumpHost}。");
            }

            SshConnectionOptions resolved = await CreateCoreAsync(
                blocks, jumpHost, settings, visiting, jumpUser, jumpPort, cancellationToken).ConfigureAwait(false);

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
                SshFailureReason.ProxyRefused, SshPhase.Dialing, $"ProxyJump 的值「{proxyJump}」里没有跳板。");
        }

        return new SshJumpDialer(jumpOptions[^1]);
    }

    /// <summary>解析 <c>[user@]host[:port]</c>（IPv6 写成 <c>[addr]:port</c>）。</summary>
    internal static (string? User, string Host, int? Port) ParseJumpSpec(string spec)
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
            return (user, address, port);
        }

        int colon = spec.LastIndexOf(':');
        if (colon > 0 && spec.IndexOf(':', StringComparison.Ordinal) == colon
            && int.TryParse(spec[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int p))
        {
            port = p;
            spec = spec[..colon];
        }

        return (user, spec, port);
    }

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    private static IHostKeyPolicy MapHostKeyPolicy(SshHostConfig config, SshConfigConnectSettings settings)
    {
        string? strict = config.StrictHostKeyChecking?.ToLowerInvariant();
        string? knownHosts = config.UserKnownHostsFile?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (strict is null && knownHosts is null)
        {
            return settings.HostKeyPolicy
                ?? new KnownHostsPolicy { UnknownHost = UnknownHostBehavior.Reject };
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

        return new KnownHostsPolicy(ExpandPath(knownHosts, null, null), settings.AskUnknownHost)
        {
            UnknownHost = unknown,
        };
    }

    private static async ValueTask<IReadOnlyList<SshCredential>> LoadIdentityFilesAsync(
        SshHostConfig config, string user, SshConfigConnectSettings settings, CancellationToken cancellationToken)
    {
        List<SshCredential> credentials = [];

        foreach (string raw in config.IdentityFiles)
        {
            string? path = ExpandPath(raw, config.HostName, user);
            if (path is null || !File.Exists(path))
            {
                continue;   // ssh 同样静默跳过不存在的 IdentityFile（默认列表里的大多数都不存在）
            }

            ISshSigner? signer = await TryLoadKeyAsync(path, settings, cancellationToken).ConfigureAwait(false);
            if (signer is not null)
            {
                credentials.Add(new PublicKeyCredential(signer, $"publickey ({Path.GetFileName(path)})"));
            }
        }

        return credentials;
    }

    private static async ValueTask<ISshSigner?> TryLoadKeyAsync(
        string path, SshConfigConnectSettings settings, CancellationToken cancellationToken)
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

public sealed partial class SshHostConfig
{
    /// <summary>
    /// 把配置里的<b>会话</b>项（<c>ForwardAgent</c>、<c>ForwardX11</c>、<c>ForwardX11Trusted</c>）套到 shell 参数上。
    /// </summary>
    /// <param name="template">起点；<see langword="null"/> 用默认 shell 参数。</param>
    /// <remarks>
    /// <para>
    /// 它们不是连接参数：同一条连接上的不同会话可以各开各的转发。
    /// 模板里已经显式设了的不会被覆盖。
    /// </para>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕<c>ForwardX11 yes</c> 产生的 X11 选项是
    /// <b>尽力而为</b>的（<see cref="X11ForwardOptions.BestEffort"/>）：本机没有显示、没有 <c>xauth</c>、
    /// 服务端拒绝时 shell 照常启动，原因见 <see cref="SshShell.X11SetupFailure"/> ——
    /// 一份存量配置不该让所有会话都起不来。模板里调用方自己给的 X11 选项保持原样（显式的，失败就抛）。
    /// </para>
    /// </remarks>
    public SshShellOptions ApplyToShell(SshShellOptions? template = null)
    {
        SshShellOptions options = template ?? SshShellOptions.Default;

        if (ForwardAgent && options.AgentForwarding is null)
        {
            options = options with { AgentForwarding = AgentForwardPolicy.Default };
        }

        if (ForwardX11 && options.X11 is null)
        {
            options = options with
            {
                X11 = new X11ForwardOptions { Trusted = ForwardX11Trusted, BestEffort = true },
            };
        }

        return options;
    }
}
