// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    /// <exception cref="SshConnectException">
    /// 跳板链有环或超过 <see cref="MaxJumpDepth"/>；配置里的 <c>ProxyCommand</c> 没有被批准执行
    /// （<see cref="SshConfigConnectOptions.ApproveProxyCommand"/>），或者代入的值不安全。
    /// </exception>
    /// <remarks>
    /// 映射规则见 <c>velashell-docs/zh/ssh/spec/09-dialing.md</c> §7。几个要点：
    /// <list type="bullet">
    ///   <item><c>ProxyJump</c> 上的每个跳板<b>按同一份配置解析</b>（有自己的 User / Port / IdentityFile）。</item>
    ///   <item><c>ProxyJump</c> 与 <c>ProxyCommand</c> 同时出现时 <c>ProxyJump</c> 优先。</item>
    ///   <item><c>ProxyCommand</c> <b>要调用方批准才执行</b>（<see cref="SshConfigConnectOptions.ApproveProxyCommand"/>）。</item>
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
            isTarget: true, resolveDialer: true, new IdentityCache(), cancellationToken);
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/09 §7.2〕<c>Ciphers</c> / <c>KexAlgorithms</c> / <c>MACs</c> / <c>HostKeyAlgorithms</c> 按 OpenSSH 的
    /// <c>+ - ^</c> 写法作用到默认清单上；<c>PubkeyAcceptedAlgorithms</c> 里留着 <c>ssh-rsa</c> 时放开 SHA-1 的 RSA 签名
    /// （老服务器最常见的那一行 <c>+ssh-rsa</c>）。写法不成立是配置错误：说清是哪台主机、哪一项。
    /// </summary>
    private static SshConnectionOptions ApplyAlgorithmLists(SshConnectionOptions options, SshHostConfig config, string host)
    {
        Crypto.SshAlgorithmSet algorithms = options.Algorithms;
        try
        {
            algorithms = Crypto.SshAlgorithmSpec.ApplyTo(algorithms, Crypto.SshAlgorithmCategory.KeyExchange, config.KexAlgorithms);
            algorithms = Crypto.SshAlgorithmSpec.ApplyTo(algorithms, Crypto.SshAlgorithmCategory.HostKey, config.HostKeyAlgorithms);
            algorithms = Crypto.SshAlgorithmSpec.ApplyTo(algorithms, Crypto.SshAlgorithmCategory.Encryption, config.Ciphers);
            algorithms = Crypto.SshAlgorithmSpec.ApplyTo(algorithms, Crypto.SshAlgorithmCategory.Mac, config.Macs);

            bool sha1Rsa = config.PubkeyAcceptedAlgorithms is { } accepted
                && Crypto.SshAlgorithmSpec.Apply(Crypto.SshAlgorithmCategory.HostKey, accepted, SshAlgorithmSetDefaults.PublicKey)
                    .Contains(Protocol.SshAlgorithmNames.SshRsa, StringComparer.Ordinal);
            return options with { Algorithms = algorithms, AllowSha1RsaSignatures = options.AllowSha1RsaSignatures || sha1Rsa };
        }
        catch (Crypto.SshAlgorithmSpecException ex)
        {
            throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"{host} 的配置里算法清单写得不对：{ex.Message}", ex);
        }
    }

    /// <summary><c>PubkeyAcceptedAlgorithms</c> 的「默认」：本库公钥认证默认用的签名算法（不含 SHA-1 的 <c>ssh-rsa</c>）。</summary>
    private static class SshAlgorithmSetDefaults
    {
        public static readonly IReadOnlyList<string> PublicKey =
        [
            Protocol.SshAlgorithmNames.SshEd25519,
            Protocol.SshAlgorithmNames.EcdsaSha2Nistp256,
            Protocol.SshAlgorithmNames.EcdsaSha2Nistp384,
            Protocol.SshAlgorithmNames.EcdsaSha2Nistp521,
            Protocol.SshAlgorithmNames.RsaSha512,
            Protocol.SshAlgorithmNames.RsaSha256,
        ];
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
        bool resolveDialer,
        IdentityCache identities,
        CancellationToken cancellationToken)
    {
        // 〔spec 09 §7.1〕本机用户名总是知道的，Match localuser 照常判；跳板规格里写明的用户（ProxyJump bob@jump）也交给 Match user。
        // 目标的远端用户要等配置求完才知道（User 本身就在配置里），对目标判不了 —— 信息不足就不匹配。曾经只给主机名，localuser 永远判不了。
        SshHostConfig config = Resolve(blocks, new SshConfigMatchContext { Host = host, User = userOverride, LocalUser = Environment.UserName });

        // 跳板规格里显式写的用户与端口（ProxyJump bob@jump:2222）优先于那台主机的配置。
        string user = userOverride ?? config.User ?? settings.DefaultUserName ?? Environment.UserName;

        // 〔FW-E14〕端口配得不对是配置错误：说清是哪台主机、哪个值。曾经原样交给连接参数，抛的是 BCL 的参数异常。
        if (portOverride is { } jumpPort && jumpPort is < 1 or > 65535)
        {
            throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"ProxyJump 里 {host} 的端口 {jumpPort} 不在 1–65535 之间。");
        }
        if (portOverride is null && config.First("Port") is { } configuredPort && !SshHostConfig.TryParsePort(configuredPort, out _))
        {
            throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"{host} 的配置里 Port 是「{PeerText.Sanitize(configuredPort, 32)}」，不是 1–65535 之间的整数。");
        }

        SshConnectionOptions options = new(user, config.HostName, portOverride ?? config.Port)
        {
            Credentials =
            [
                .. await LoadIdentityFilesAsync(config, user, settings, identities, cancellationToken).ConfigureAwait(false),
                .. CallerCredentialsFor(settings, isTarget),
            ],
            HostKeyPolicy = MapHostKeyPolicy(config, user, settings),
        };

        options = ApplyAlgorithmLists(options, config, host);

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

        // 怎么到达这台主机由调用方定（ProxyJump 链里第一跳之后的那些经前一跳到达）：它自己的 ProxyJump / ProxyCommand 不看。
        if (!resolveDialer)
        {
            return settings.Configure?.Invoke(options) ?? options;
        }

        // 〔velashell-docs/zh/ssh/spec/09 §7〕ProxyJump 优先于 ProxyCommand。
        // 指向自己的 ProxyJump（Host *.corp 带出来、跳板忘了写 ProxyJump none）走不通，当成直连 —— 不报「链有环」。
        if (IsSet(config.ProxyJump) && !IsJumpToSelf(config.ProxyJump!, host))
        {
            options = options with
            {
                Dialer = await BuildJumpChainAsync(blocks, host, config.ProxyJump!, settings, chain, identities, cancellationToken)
                    .ConfigureAwait(false),
            };
        }
        else if (IsSet(config.ProxyCommand))
        {
            ProxyCommandDialer dialer = new(config.ProxyCommand!) { UserName = user, OriginalHost = host };

            // 拨号时它要连的就是这一跳自己的主机与端口：照那个展开，批准的就是将要执行的那一行。
            string command = dialer.Expand(new SshEndPoint(options.Host, options.Port));
            await ApproveProxyCommandAsync(settings, host, command, cancellationToken).ConfigureAwait(false);

            options = options with { Dialer = dialer };
        }
        else if (config.AddressFamily != AddressFamily.Unspecified || IsSet(config.BindAddress) || IsSet(config.BindInterface))
        {
            // 〔velashell-docs/zh/ssh/spec/09 §7〕直连时才用得上：经跳板或代理命令时本机这一端不归这台主机的配置管。
            options = options with
            {
                Dialer = new TcpTransportDialer
                {
                    AddressFamily = config.AddressFamily,
                    LocalAddresses = ResolveBindAddresses(config, host),
                },
            };
        }

        // ConnectionAttempts：拨号失败时再试（每次隔一秒）—— 包在最外层，跳板链与代理命令一起重来。
        if (config.ConnectionAttempts > 1)
        {
            options = options with { Dialer = new RetryingDialer(options.Dialer, config.ConnectionAttempts) };
        }

        return settings.Configure?.Invoke(options) ?? options;
    }

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/09 §7〕配置里的 <c>ProxyCommand</c> 要调用方批准才执行
    /// （见 <see cref="SshConfigConnectOptions.ApproveProxyCommand"/>）。
    /// </summary>
    private static async ValueTask ApproveProxyCommandAsync(
        SshConfigConnectOptions settings, string host, string command, CancellationToken cancellationToken)
    {
        if (settings.ApproveProxyCommand is not { } approve)
        {
            throw new SshConnectException(
                SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"{host} 的配置里写着 ProxyCommand（{PeerText.Sanitize(command)}），" +
                "而调用方没有允许执行外部命令（SshConfigConnectOptions.ApproveProxyCommand）。");
        }

        if (!await approve(new SshProxyCommandRequest(host, command), cancellationToken).ConfigureAwait(false))
        {
            throw new SshConnectException(
                SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"没有批准执行 {host} 的 ProxyCommand（{PeerText.Sanitize(command)}）。");
        }
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

            // 第一个跳板用它自己的拨号器（它自己的 ProxyJump / ProxyCommand 照常生效）；其后每一个都经前一个到达。
            // 〔FW-E12〕后面那些的拨号设置根本不去解析：曾经先把它们自己的跳板链整个解析一遍（白问一次口令、
            // 白批准一次 ProxyCommand）再丢掉，那条用不上的链里有环时还会报「链有环」。
            SshConnectionOptions resolved = await CreateCoreAsync(
                blocks, jumpHost, settings, visiting, jumpUser, jumpPort, isTarget: false, resolveDialer: i == 0, identities, cancellationToken)
                .ConfigureAwait(false);

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

    /// <summary>这台主机的 <c>ProxyJump</c> 是不是只有它自己一跳。</summary>
    private static bool IsJumpToSelf(string proxyJump, string host) =>
        ParseProxyJump(proxyJump) is [{ Host: { } only }] && string.Equals(only, host, StringComparison.OrdinalIgnoreCase);

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

    /// <summary><c>BindAddress</c>（一个 IP 地址）或 <c>BindInterface</c>（网卡名，取它的全部地址）；都没写为空。</summary>
    /// <exception cref="SshConnectException">写的不是 IP 地址、或者没有那块网卡（<see cref="SshFailureReason.InvalidConfiguration"/>）。</exception>
    private static IReadOnlyList<IPAddress> ResolveBindAddresses(SshHostConfig config, string host)
    {
        if (IsSet(config.BindAddress))
        {
            return IPAddress.TryParse(config.BindAddress!.Trim(), out IPAddress? address)
                ? [address]
                : throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                    $"{host} 的配置里 BindAddress 是「{PeerText.Sanitize(config.BindAddress, 64)}」，不是一个 IP 地址。");
        }

        if (IsSet(config.BindInterface))
        {
            string name = config.BindInterface!.Trim();
            NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase));
            return nic is null
                ? throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                    $"{host} 的配置里 BindInterface 是「{PeerText.Sanitize(name, 64)}」，本机没有这块网卡。")
                : [.. nic.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address)];
        }

        return [];
    }

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>主机密钥策略：按 <c>StrictHostKeyChecking</c> 与 known_hosts 定下来，<c>HostKeyAlias</c> 再把查、记的名字换成别名。</summary>
    private static IHostKeyPolicy MapHostKeyPolicy(SshHostConfig config, string user, SshConfigConnectOptions settings)
    {
        IHostKeyPolicy policy = MapKnownHostsPolicy(config, user, settings);
        return config.HostKeyAlias is { } alias ? new HostKeyAliasPolicy(policy, alias) : policy;
    }

    private static IHostKeyPolicy MapKnownHostsPolicy(SshHostConfig config, string user, SshConfigConnectOptions settings)
    {
        string? strict = config.StrictHostKeyChecking?.ToLowerInvariant();
        // 写了几个路径时只用第一个；带空格的路径加了引号就是一个（spec/09 §7.4）。〔历史〕曾经按空格切原文，"C:\a b\kh" 只剩 C:\a。
        string? knownHosts = config.Arguments("UserKnownHostsFile").FirstOrDefault(static path => path.Length > 0);

        // ask 与缺省是「交互式」的那一种 —— 调用方给了自己的策略（带着它自己的信任库与询问界面）就用它，
        // 曾经只要配置里写了 UserKnownHostsFile 就另起一个策略，把调用方的那一个丢掉。
        // yes / no / accept-new 是配置明确要求的行为，照配置来。
        if (strict is null or "ask" && settings.HostKeyPolicy is { } callerPolicy)
        {
            return callerPolicy;
        }

        if (strict is null && knownHosts is null)
        {
            return new KnownHostsPolicy { UnknownHost = UnknownHostBehavior.Reject, GlobalKnownHostsFiles = config.GlobalKnownHostsFiles };
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
            return KnownHostsPolicy.WithoutFile(AskOnlyWhenAsking(), unknown);
        }

        // 〔FW-E13〕%h / %r 照这台主机与用户展开（每台主机一个 known_hosts 的写法要靠它）。曾经代入空串：
        // ~/.ssh/kh_%h 成了 ~/.ssh/kh_，所有主机挤进同一个文件。
        return new KnownHostsPolicy(config.Expand(knownHosts, user), AskOnlyWhenAsking())
        {
            UnknownHost = unknown,
            GlobalKnownHostsFiles = config.GlobalKnownHostsFiles,
        };

        // 询问回调只在「问」的时候交出去：yes / accept-new 下它不会被调用，策略会把「给了回调又不问」当成配置矛盾。
        Func<SshHostKeyContext, CancellationToken, ValueTask<bool>>? AskOnlyWhenAsking() =>
            unknown == UnknownHostBehavior.Ask ? settings.AskUnknownHost : null;
    }

    private static async ValueTask<IReadOnlyList<SshCredential>> LoadIdentityFilesAsync(
        SshHostConfig config, string user, SshConfigConnectOptions settings, IdentityCache identities,
        CancellationToken cancellationToken)
    {
        List<SshCredential> credentials = [];
        List<(string Path, ISshSigner Signer)> loaded = [];

        foreach (string path in config.ExpandIdentityFiles(user))
        {
            if (!File.Exists(path))
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
                loaded.Add((path, signer));
            }
        }

        // 证书：CertificateFile 写的，加上每把钥旁边的「钥-cert.pub」（ssh 的默认行为）。按里面的公钥与读出来的私钥配对，
        // 证书排在那把钥前面 —— 与 ssh 一样先出示证书，服务端不认 CA 时再退到裸钥。
        Dictionary<ISshSigner, List<SshCredential>> certificates = await LoadCertificatesAsync(config, user, settings, loaded, cancellationToken)
            .ConfigureAwait(false);
        foreach ((string path, ISshSigner signer) in loaded)
        {
            if (certificates.TryGetValue(signer, out List<SshCredential>? forThisKey))
            {
                credentials.AddRange(forThisKey);
            }
            credentials.Add(new PublicKeyCredential(signer, $"publickey ({Path.GetFileName(path)})"));
        }

        return credentials;
    }

    /// <summary>读证书并与私钥配对；读不出来、配不上的报给 <see cref="SshConfigConnectOptions.IdentityFileSkipped"/>。</summary>
    private static async ValueTask<Dictionary<ISshSigner, List<SshCredential>>> LoadCertificatesAsync(
        SshHostConfig config, string user, SshConfigConnectOptions settings,
        List<(string Path, ISshSigner Signer)> loaded, CancellationToken cancellationToken)
    {
        Dictionary<ISshSigner, List<SshCredential>> result = [with(ReferenceEqualityComparer.Instance)];
        List<(string Path, bool Explicit)> candidates =
        [
            .. config.ExpandCertificateFiles(user).Select(static p => (p, true)),
            .. loaded.Select(static l => (l.Path + "-cert.pub", false)),
        ];
        HashSet<string> seen = [with(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];

        foreach ((string path, bool isExplicit) in candidates)
        {
            if (!File.Exists(path) || !seen.Add(Path.GetFullPath(path)))
            {
                continue;
            }
            try
            {
                OpenSshCertificate certificate = await OpenSshCertificate.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                ISshSigner? key = loaded.FirstOrDefault(l => l.Signer.PublicKey.Blob.Span.SequenceEqual(certificate.Key.Blob.Span)).Signer;
                if (key is null)
                {
                    if (isExplicit)
                    {
                        settings.IdentityFileSkipped?.Invoke(path, new SshCertificateException(SshFailureReason.KeyMismatch,
                            $"证书 {Path.GetFileName(path)} 证的钥不在读出来的 IdentityFile 里，没法用它签名。")
                        {
                            ProblemKind = SshKeyProblemKind.CertificateKeyNotLoaded,
                            Origin = path,
                        });
                    }
                    continue;
                }
                if (!result.TryGetValue(key, out List<SshCredential>? list))
                {
                    list = [];
                    result[key] = list;
                }
                list.Add(new PublicKeyCredential(SshCertificateSigner.Create(certificate, key), $"publickey ({Path.GetFileName(path)})"));
            }
            catch (Exception ex) when (ex is SshCertificateException or IOException or UnauthorizedAccessException or FormatException)
            {
                settings.IdentityFileSkipped?.Invoke(path, ex);
            }
        }
        return result;
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

    /// <summary>
    /// 展开 <c>~</c> 与记号：<c>%d</c>（本机家目录）<c>%u</c>（本机用户）<c>%h</c>（主机）<c>%r</c>（登录用户）<c>%p</c>（端口）
    /// <c>%n</c>（输入的名字）<c>%l</c> / <c>%L</c>（本机主机名，完整 / 第一段）<c>%C</c>（<c>%l%h%p%r</c> 的 SHA-1）
    /// <c>%j</c>（<c>ProxyJump</c>）<c>%k</c>（<c>HostKeyAlias</c>，没有就是主机）<c>%%</c>。不认识的原样留着。
    /// </summary>
    internal static string? ExpandPath(
        string? raw, string? host, string? remoteUser,
        int? port = null, string? originalHost = null, string? proxyJump = null, string? hostKeyAlias = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 引号与转义在分词时已经去掉（spec/09 §7.4）—— 这里不再剥引号：解开之后还在的引号是路径的一部分。
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string value = raw;

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
                'p' => port is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "%p",
                'n' => originalHost ?? host ?? "",
                'l' => LocalHostName(),
                'L' => LocalHostName().Split('.')[0],
                'C' => ConnectionHash(host, port, remoteUser),
                'j' => proxyJump ?? "",
                'k' => hostKeyAlias ?? host ?? "",
                '%' => "%",
                _ => "%" + token,
            });
        }

        return result.ToString();
    }

    /// <summary>本机主机名（<c>%l</c>）；取不到时为空。</summary>
    private static string LocalHostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException)
        {
            return "";
        }
    }

    /// <summary><c>%C</c>：<c>%l%h%p%r</c> 的 SHA-1（十六进制小写）—— 给 ControlPath 之类一个不含特殊字符、又对得上这条连接的名字。</summary>
    private static string ConnectionHash(string? host, int? port, string? remoteUser)
    {
        byte[] input = Encoding.UTF8.GetBytes(
            LocalHostName() + (host ?? "") + (port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "22") + (remoteUser ?? ""));
#pragma warning disable CA5350 // %C 由 ssh_config(5) 规定就是 SHA-1；它只是给文件起名字，不承担任何安全职责
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(input));
#pragma warning restore CA5350
    }
}

