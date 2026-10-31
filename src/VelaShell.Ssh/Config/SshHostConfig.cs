// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Config;

/// <summary>把 <c>ssh_config</c> 解完之后，某一台主机最终生效的设置。</summary>
public sealed class SshHostConfig
{
    // ⚠️ IDE0028 会建议把它简化成 []。**不能听** —— 那会把
    //    OrdinalIgnoreCase 丢掉，而 ssh_config 的键是不区分大小写的
    //    （`HostName` 与 `hostname` 是同一个键）。
    private readonly Dictionary<string, List<Entry>> _settings = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>一个键的一次出现。</summary>
    /// <param name="Raw">关键字之后的原文（注释已去掉，引号与转义还在）。</param>
    /// <param name="Text"><see cref="First"/> / <see cref="All"/> 交出的值：参数之间以一个空格连接；命令键是原文。</param>
    /// <param name="Arguments">按 spec/09 §7.4 解开的参数；命令键只有一个（原文）。</param>
    private readonly record struct Entry(string Raw, string Text, IReadOnlyList<string> Arguments);

    /// <param name="host">查的是哪个名字。</param>
    /// <param name="originalHost">使用者输入的那个名字（<c>HostName</c> 里的 <c>%h</c> 换成它）；缺省就是 <paramref name="host"/>。</param>
    internal SshHostConfig(string host, string? originalHost = null)
    {
        QueriedHost = host;
        HostName = originalHost ?? host;
    }

    /// <summary>当初查的是哪个名字。</summary>
    public string QueriedHost { get; }

    /// <summary>真正要连的主机（<c>HostName</c>，没有就是 <see cref="QueriedHost"/>）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §七〕<c>HostName</c> 里的 <c>%h</c> 换成使用者输入的名字、<c>%%</c> 换成 <c>%</c>
    /// （<c>Host *.prod</c> 配 <c>HostName %h.example.com</c> 是常见写法）。曾经原样交出去：建连拿字面量
    /// <c>%h.example.com</c> 去连，<c>DnsFailure</c>；<c>IdentityFile</c> 等处代入的 <c>%h</c> 也是这个没展开的值。
    /// </remarks>
    public string HostName => First("HostName") is { } configured ? ExpandHostTokens(configured, field) : QueriedHost;

    /// <summary>展开 <c>HostName</c> 认的两个记号：<c>%h</c> 与 <c>%%</c>；别的原样留着。</summary>
    private static string ExpandHostTokens(string value, string originalHost)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder result = new(value.Length + originalHost.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 1 < value.Length && value[i + 1] is 'h' or '%')
            {
                result.Append(value[++i] == 'h' ? originalHost : "%");
            }
            else
            {
                result.Append(value[i]);
            }
        }
        return result.ToString();
    }

    /// <summary>端口；没配、或者配的不是 1–65535 之间的整数时是 22。</summary>
    /// <remarks>
    /// 〔FW-E14〕配得不对的端口在建连时（<see cref="SshConfigFile.CreateConnectionOptionsAsync"/>）当成配置错误报出来；
    /// 这里只交出一个能用的值。曾经 <c>Port -1</c> / <c>Port 99999</c> 原样交出去，建连时抛的是 BCL 的参数异常，
    /// 宿主导入时又自己夹了一次。
    /// </remarks>
    public int Port => TryParsePort(First("Port"), out int port) ? port : 22;

    /// <summary>是不是 1–65535 之间的整数（不带正负号、不带空白）。</summary>
    internal static bool TryParsePort(string? text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>用户名。</summary>
    public string? User => First("User");

    /// <summary>私钥文件（<c>IdentityFile</c> 可以出现多次，按顺序），配置里的原文。</summary>
    /// <remarks>要拿来读文件，用 <see cref="ExpandIdentityFiles"/>。</remarks>
    public IReadOnlyList<string> IdentityFiles => All("IdentityFile");

    /// <summary>
    /// 展开好的私钥文件路径（按出现的顺序）：<c>~</c> 与 <c>%d</c> <c>%u</c> <c>%h</c> <c>%r</c> <c>%%</c> 照这台主机与用户展开，
    /// 与连接时读私钥用的是同一套；<c>IdentityFile none</c> 不算。
    /// </summary>
    /// <param name="remoteUser">登录用户（<c>%r</c>）；不给就用 <see cref="User"/>。</param>
    /// <remarks>
    /// 文件存不存在不管，相对路径也原样留着 —— 那是读的时候的事。
    /// 曾经展开是 internal 的，宿主导入 <c>ssh_config</c> 时只好自己再写一份（而且不认 <c>%h</c> / <c>%r</c>）。
    /// </remarks>
    public IReadOnlyList<string> ExpandIdentityFiles(string? remoteUser = null) =>
    [
        .. IdentityFiles
            .Where(static value => !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            .Select(value => Expand(value, remoteUser))
            .OfType<string>(),
    ];

    /// <summary><c>CertificateFile</c>：与 <c>IdentityFile</c> 配对的证书，展开记号之后的路径（不管存在与否）。</summary>
    /// <param name="remoteUser">登录用户（<c>%r</c>）；不给就用 <see cref="User"/>。</param>
    public IReadOnlyList<string> ExpandCertificateFiles(string? remoteUser = null) =>
    [
        .. All("CertificateFile")
            .Where(static value => !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            .Select(value => Expand(value, remoteUser))
            .OfType<string>(),
    ];

    /// <summary>按这台主机展开路径里的记号（见 <c>SshConfigFile.ExpandPath</c>）。</summary>
    internal string? Expand(string? raw, string? remoteUser = null) =>
        SshConfigFile.ExpandPath(raw, HostName, remoteUser ?? User, Port, QueriedHost, ProxyJump, First("HostKeyAlias"));

    /// <summary>
    /// 会话要设的环境变量：<c>SetEnv 名=值</c>（可以多个、多行，先出现的赢），再加上 <c>SendEnv</c>
    /// 通配（<c>*</c> / <c>?</c>，不分大小写）选中的本机环境变量（<c>SetEnv</c> 里有的不覆盖）。
    /// </summary>
    /// <remarks>
    /// 服务端的 <c>AcceptEnv</c> 只放行少数变量，被拒是常态（velashell-docs/zh/ssh/spec/05 §5.2）。
    /// <c>SendEnv -模式</c>（去掉之前的模式）这里不处理：一律只增不减。
    /// 每一对是一个参数（spec/09 §7.4）：<c>SetEnv A="x y"</c> 设的是 <c>A=x y</c>。〔历史〕曾经按空白切开再去掉引号，值里的空格把它切断。
    /// </remarks>
    public IReadOnlyDictionary<string, string> SessionEnvironment()
    {
        Dictionary<string, string> result = [with(StringComparer.Ordinal)];
        foreach (string pair in Entries("SetEnv").SelectMany(static e => e.Arguments))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                result.TryAdd(pair[..equals], pair[(equals + 1)..]);
            }
        }

        string[] patterns = [.. Entries("SendEnv").SelectMany(static e => e.Arguments)
            .Where(static p => p.Length > 0 && !p.StartsWith('-'))];
        if (patterns.Length > 0)
        {
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is string name && variable.Value is string value
                    && patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
                {
                    result.TryAdd(name, value);
                }
            }
        }
        return result;
    }

    /// <summary><c>ConnectionAttempts</c>：拨号失败时一共试几次（每次隔一秒），默认 1。</summary>
    public int ConnectionAttempts =>
        int.TryParse(First("ConnectionAttempts"), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 1
            ? Math.Min(value, 100)
            : 1;

    /// <summary>跳板（<c>ProxyJump</c>）。</summary>
    public string? ProxyJump => First("ProxyJump");

    /// <summary><c>ProxyCommand</c>：经一个外部程序连接（<c>none</c> 表示不用）。</summary>
    public string? ProxyCommand => First("ProxyCommand");

    /// <summary><c>ConnectTimeout</c>（秒）；没配为 <see langword="null"/>。</summary>
    public int? ConnectTimeoutSeconds =>
        int.TryParse(First("ConnectTimeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value > 0
            ? value
            : null;

    /// <summary><c>ForwardX11</c>。</summary>
    public bool ForwardX11 => IsYes(First("ForwardX11"));

    /// <summary><c>ForwardX11Trusted</c>。</summary>
    public bool ForwardX11Trusted => IsYes(First("ForwardX11Trusted"));

    /// <summary><c>ForwardX11Timeout</c>：X11 转发的有效期；没写或写不对为 <see langword="null"/>（用默认）。</summary>
    /// <remarks>
    /// ssh_config 的时间格式：数字后跟 <c>s</c> / <c>m</c> / <c>h</c> / <c>d</c> / <c>w</c>（大小写均可），
    /// 不带单位为秒，几段相加（<c>1h30m</c>）；<c>0</c> 为不过期（<see cref="Timeout.InfiniteTimeSpan"/>）。
    /// </remarks>
    public TimeSpan? ForwardX11Timeout =>
        TryParseTimeSpec(First("ForwardX11Timeout"), out TimeSpan value)
            ? value == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : value
            : null;

    /// <summary>解析 ssh_config 的时间格式（见 <see cref="ForwardX11Timeout"/>）。</summary>
    /// <remarks>写不对（空、带别的字符、单位不认识、溢出）就返回 <see langword="false"/> —— 不猜。</remarks>
    internal static bool TryParseTimeSpec(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string spec = text.Trim();
        long totalSeconds = 0;
        int i = 0;

        while (i < spec.Length)
        {
            int start = i;
            while (i < spec.Length && spec[i] is >= '0' and <= '9')
            {
                i++;
            }

            if (i == start
                || !long.TryParse(spec.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            {
                return false;
            }

            long unit = 1;
            if (i < spec.Length)
            {
                unit = char.ToLowerInvariant(spec[i]) switch
                {
                    's' => 1,
                    'm' => 60,
                    'h' => 60 * 60,
                    'd' => 24 * 60 * 60,
                    'w' => 7 * 24 * 60 * 60,
                    _ => 0,
                };

                if (unit == 0)
                {
                    return false;
                }
                i++;
            }

            try
            {
                totalSeconds = checked(totalSeconds + (number * unit));
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (totalSeconds > (long)TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    /// <summary><c>StrictHostKeyChecking</c> 的原文（<c>yes</c> / <c>no</c> / <c>ask</c> / <c>accept-new</c>）。</summary>
    public string? StrictHostKeyChecking => First("StrictHostKeyChecking");

    /// <summary><c>UserKnownHostsFile</c>（写了几个路径时以空格连接；连接时只用第一个）。</summary>
    public string? UserKnownHostsFile => First("UserKnownHostsFile");

    /// <summary>
    /// <c>GlobalKnownHostsFile</c>：只读的全局 known_hosts（可以几个、空格分隔，展开记号）；没写或写 <c>none</c> 时为空。
    /// </summary>
    /// <remarks>
    /// 写了才读 —— 不去找系统目录里默认的那一份。带空格的路径加引号写（spec/09 §7.4），是一个路径。
    /// </remarks>
    public IReadOnlyList<string> GlobalKnownHostsFiles =>
        Arguments("GlobalKnownHostsFile") is { Count: > 0 } paths
        && !(paths is [var only] && string.Equals(only, "none", StringComparison.OrdinalIgnoreCase))
            ? [.. paths.Where(static path => path.Length > 0).Select(path => Expand(path)).OfType<string>()]
            : [];

    /// <summary><c>HostKeyAlias</c>：查、记主机密钥时代替主机名的别名；没写为 <see langword="null"/>。</summary>
    public string? HostKeyAlias =>
        First("HostKeyAlias")?.Trim() is { Length: > 0 } alias && !string.Equals(alias, "none", StringComparison.OrdinalIgnoreCase)
            ? alias
            : null;

    /// <summary><c>AddressFamily</c>：<c>inet</c> → IPv4、<c>inet6</c> → IPv6，其余（<c>any</c>、没写）→ 不限。</summary>
    public AddressFamily AddressFamily => First("AddressFamily")?.Trim().ToLowerInvariant() switch
    {
        "inet" => AddressFamily.InterNetwork,
        "inet6" => AddressFamily.InterNetworkV6,
        _ => AddressFamily.Unspecified,
    };

    /// <summary><c>BindAddress</c>：本机这一端从哪个地址发起连接；没写为 <see langword="null"/>。</summary>
    public string? BindAddress => First("BindAddress");

    /// <summary><c>BindInterface</c>：本机这一端从哪块网卡（按名字）发起连接；没写为 <see langword="null"/>。</summary>
    public string? BindInterface => First("BindInterface");

    /// <summary>按 <c>IdentityAgent</c> 决定认证时用哪个 agent。</summary>
    /// <param name="endpoint">agent 的端点；<see langword="null"/> 是默认的那个（<c>SSH_AUTH_SOCK</c> / Windows 的 OpenSSH agent 管道）。</param>
    /// <returns>写着 <c>none</c>（或者给的环境变量没设）时为 <see langword="false"/>：不用 agent。</returns>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §7〕ssh_config(5) 的写法：<c>none</c>、<c>SSH_AUTH_SOCK</c>、以 <c>$</c> 开头的环境变量（值是路径）、
    /// 一个路径（展开 <c>~</c> 与记号）。本库不替调用方建 agent 凭据（不做隐式回退，spec/04 §2.2）—— 调用方照它连 agent。
    /// </remarks>
    public bool TryGetIdentityAgent(out string? endpoint)
    {
        endpoint = null;
        string? value = First("IdentityAgent");
        if (string.IsNullOrEmpty(value) || string.Equals(value, "SSH_AUTH_SOCK", StringComparison.Ordinal))
        {
            return true;
        }
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (value.StartsWith('$'))
        {
            endpoint = Environment.GetEnvironmentVariable(value[1..]);
            return !string.IsNullOrEmpty(endpoint);
        }
        endpoint = Expand(value);
        return true;
    }

    /// <summary>是否只用显式给出的密钥（<c>IdentitiesOnly yes</c>）。</summary>
    public bool IdentitiesOnly => IsYes(First("IdentitiesOnly"));

    /// <summary><c>ForwardAgent</c> 开着没有：<c>yes</c>、给了 agent 套接字路径、给了设着的 <c>$环境变量</c> 都算开。</summary>
    public bool ForwardAgent => TryGetForwardedAgent(out _);

    /// <summary>按 <c>ForwardAgent</c> 的值决定要不要转发、转发哪个 agent。</summary>
    /// <param name="endpoint">要转发的 agent；<see langword="null"/> 是默认的那个（<c>SSH_AUTH_SOCK</c> / Windows 的 OpenSSH agent 管道）。</param>
    /// <returns>要转发时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/09 §7〕ssh_config(5) 的写法有四种：<c>yes</c> / <c>no</c>（大小写均可）、
    /// 一个 agent 套接字的路径（展开 <c>~</c> 与 <c>%d %u %h %r</c>）、以 <c>$</c> 开头的环境变量名（变量的值是路径）。
    /// 曾经只认 <c>yes</c>：写了路径的配置被当成 <c>no</c>，转发悄悄没开。
    /// </para>
    /// <para>
    /// 环境变量没设或为空时不转发 —— 没有一个可转发的 agent，宣告出去也只是让远端白连一次。
    /// </para>
    /// </remarks>
    internal bool TryGetForwardedAgent(out string? endpoint)
    {
        endpoint = null;
        string? value = First("ForwardAgent");

        if (string.IsNullOrEmpty(value) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith('$'))
        {
            string? fromEnvironment = value.Length > 1 ? Environment.GetEnvironmentVariable(value[1..]) : null;
            endpoint = string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
            return endpoint is not null;
        }

        endpoint = Expand(value);
        return endpoint is not null;
    }

    /// <summary><c>Ciphers</c>：加密算法清单（OpenSSH 的 <c>+ - ^</c> 写法，见 <see cref="Crypto.SshAlgorithmSpec"/>）。</summary>
    public string? Ciphers => First("Ciphers");

    /// <summary><c>KexAlgorithms</c>：密钥交换算法清单（写法同上）。</summary>
    public string? KexAlgorithms => First("KexAlgorithms");

    /// <summary><c>MACs</c>：MAC 算法清单（写法同上）。</summary>
    public string? Macs => First("MACs");

    /// <summary><c>HostKeyAlgorithms</c>：主机密钥算法清单（写法同上）。</summary>
    public string? HostKeyAlgorithms => First("HostKeyAlgorithms");

    /// <summary><c>PubkeyAcceptedAlgorithms</c>（旧名 <c>PubkeyAcceptedKeyTypes</c>）：公钥认证用哪些签名算法（写法同上）。</summary>
    public string? PubkeyAcceptedAlgorithms => First("PubkeyAcceptedAlgorithms") ?? First("PubkeyAcceptedKeyTypes");

    /// <summary><c>Compression</c>。</summary>
    public bool Compression => IsYes(First("Compression"));

    /// <summary><c>GatewayPorts</c>：本地转发没写监听地址时绑全部网卡而不是环回。</summary>
    public bool GatewayPorts => IsYes(First("GatewayPorts"));

    /// <summary><c>ExitOnForwardFailure</c>：配置里的转发有一条起不来就整体失败（脚本与自动化场景要它）。</summary>
    public bool ExitOnForwardFailure => IsYes(First("ExitOnForwardFailure"));

    /// <summary><c>ClearAllForwardings</c>：不起配置里的任何转发。</summary>
    public bool ClearAllForwardings => IsYes(First("ClearAllForwardings"));

    /// <summary>
    /// 配置里的转发（<c>LocalForward</c> / <c>RemoteForward</c> / <c>DynamicForward</c>，按种类、各自按出现顺序），解析成结构化的两头。
    /// <c>ClearAllForwardings yes</c> 时为空。
    /// </summary>
    /// <param name="onInvalid">
    /// 写法不对的那一行交给它（那一行的原文、异常），跳过这一行、其余照解 —— 导入这类「一行写错不连累别的行」的场合用；
    /// <see langword="null"/>（默认）时写错一条就抛 —— 起转发前先把写法全部查一遍（spec/09 §7.3）。
    /// </param>
    /// <exception cref="Diagnostics.SshConnectException">某一条写法不对（<see cref="Diagnostics.SshFailureReason.InvalidConfiguration"/>），且没给 <paramref name="onInvalid"/>。</exception>
    /// <remarks>
    /// 与其它键不同，这三个键是<b>累加</b>的：每一行都是一条转发（ssh_config(5)）。两头照 spec/09 §7.4 分词 —— 带空格的套接字路径加引号写，是一头。
    /// </remarks>
    public IReadOnlyList<SshConfigForward> GetForwards(Action<string, Diagnostics.SshConnectException>? onInvalid = null)
    {
        if (ClearAllForwardings)
        {
            return [];
        }

        List<SshConfigForward> forwards = [];
        foreach ((SshConfigForwardKind kind, string keyword) in ForwardKeywords)
        {
            foreach (Entry entry in Entries(keyword))
            {
                try
                {
                    forwards.Add(SshConfigForward.Parse(kind, entry.Arguments, entry.Raw));
                }
                catch (Diagnostics.SshConnectException ex) when (onInvalid is not null)
                {
                    onInvalid($"{keyword} {entry.Raw}", ex);
                }
            }
        }
        return forwards;
    }

    private static readonly (SshConfigForwardKind Kind, string Keyword)[] ForwardKeywords =
    [
        (SshConfigForwardKind.Local, "LocalForward"),
        (SshConfigForwardKind.Remote, "RemoteForward"),
        (SshConfigForwardKind.Dynamic, "DynamicForward"),
    ];

    /// <summary>
    /// <c>PermitRemoteOpen</c>：远程动态转发的放行名单。没写时是 <see cref="RemoteOpenPolicy.Any"/>（OpenSSH 的默认）——
    /// 写进配置的 <c>RemoteForward 端口</c> 就是配置作者的明确选择；<c>any</c> / <c>none</c> 照字面。
    /// </summary>
    /// <exception cref="Diagnostics.SshConnectException">规则写法不对（<see cref="Diagnostics.SshFailureReason.InvalidConfiguration"/>）。</exception>
    public RemoteOpenPolicy GetPermitRemoteOpen()
    {
        string? value = First("PermitRemoteOpen");
        if (value is null || value.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteOpenPolicy.Any;
        }
        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteOpenPolicy.None;
        }
        try
        {
            return RemoteOpenPolicy.Allow([.. Arguments("PermitRemoteOpen").Where(static rule => rule.Length > 0)]);
        }
        catch (ArgumentException ex)
        {
            throw new Diagnostics.SshConnectException(Diagnostics.SshFailureReason.InvalidConfiguration, Diagnostics.SshPhase.Dialing,
                $"PermitRemoteOpen 写得不对：{ex.Message}", ex);
        }
    }

    /// <summary><c>ServerAliveInterval</c>（秒，<c>0</c> = 关）。</summary>
    public int ServerAliveInterval =>
        int.TryParse(
            First("ServerAliveInterval"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    /// <summary><c>ServerAliveCountMax</c>。</summary>
    public int ServerAliveCountMax =>
        int.TryParse(
            First("ServerAliveCountMax"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 3;

    /// <summary>取某个键的第一个值。</summary>
    /// <remarks>
    /// <para>
    /// <b>「第一个」是对的，不是「最后一个」。</b>
    /// <c>ssh_config</c> 的规则是<b>先出现的赢</b> —— 与大多数配置格式相反。
    /// 这也是为什么 OpenSSH 的样例里 <c>Host *</c> 总是放在文件末尾。
    /// </para>
    /// <para>
    /// 值是照 velashell-docs/zh/ssh/spec/09 §7.4 分好词、去掉引号与反斜杠转义之后的，与 <c>ssh -G</c> 读出来的一样
    /// （<c>IdentityAgent \\\\.\pipe\x</c> 交出 <c>\\.\pipe\x</c>）；写了几个参数时以一个空格连接。
    /// <c>ProxyCommand</c> / <c>RemoteCommand</c> / <c>LocalCommand</c> / <c>KnownHostsCommand</c> 是到行尾的原文。
    /// </para>
    /// </remarks>
    public string? First(string key) =>
        _settings.TryGetValue(key, out List<Entry>? entries) && entries.Count > 0 ? entries[0].Text : null;

    /// <summary>取某个键的全部值，按出现顺序（每个值的样子同 <see cref="First"/>）。</summary>
    public IReadOnlyList<string> All(string key) =>
        _settings.TryGetValue(key, out List<Entry>? entries) ? [.. entries.Select(static e => e.Text)] : [];

    /// <summary>某个键第一次出现时的各个参数（引号与转义已解开）；没有时为空。</summary>
    internal IReadOnlyList<string> Arguments(string key) =>
        _settings.TryGetValue(key, out List<Entry>? entries) && entries.Count > 0 ? entries[0].Arguments : [];

    private List<Entry> Entries(string key) =>
        _settings.TryGetValue(key, out List<Entry>? entries) ? entries : [];

    /// <param name="key">关键字。</param>
    /// <param name="raw">关键字之后的原文（<see cref="SshConfigBlock.Settings"/> 里存的那样）。</param>
    internal void Add(string key, string raw)
    {
        if (!_settings.TryGetValue(key, out List<Entry>? entries))
        {
            entries = [];
            _settings[key] = entries;
        }

        if (SshConfigSyntax.TakesRestOfLine(key))
        {
            entries.Add(new Entry(raw, raw, [raw]));
            return;
        }

        // 引号没配对的行解析时已经丢掉了（spec/09 §7.4），这里配不上只会是调用方直接塞进来的原文 —— 当成一个参数。
        IReadOnlyList<string> arguments = SshConfigSyntax.TrySplit(raw, out List<string>? words) ? words : [raw];
        entries.Add(new Entry(raw, string.Join(' ', arguments), arguments));
    }

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把配置里的<b>会话</b>项（<c>ForwardAgent</c>、<c>ForwardX11</c>、<c>ForwardX11Trusted</c>、<c>ForwardX11Timeout</c>）套到 shell 参数上。
    /// </summary>
    /// <param name="template">起点；<see langword="null"/> 用默认 shell 参数。</param>
    /// <remarks>
    /// <para>
    /// 它们不是连接参数：同一条连接上的不同会话可以各开各的转发。
    /// 模板里已经显式设了的不会被覆盖。
    /// </para>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕<c>ForwardX11 yes</c> 与 <c>ForwardAgent</c> 产生的选项都按
    /// <see cref="ForwardFailureMode.Continue"/> 请求：本机没有显示、没有 <c>xauth</c>、本机 agent 没在跑、
    /// 服务端拒绝时 shell 照常启动，原因见 <see cref="SshShell.X11SetupFailure"/> / <see cref="SshShell.AgentSetupFailure"/> ——
    /// 一份存量配置不该让所有会话都起不来。模板里调用方自己给的选项保持原样（显式的，失败就抛）。
    /// </para>
    /// </remarks>
    public SshShellOptions ApplyToShell(SshShellOptions? template = null)
    {
        SshShellOptions options = template ?? SshShellOptions.Default;

        if (options.AgentForwarding is null && TryGetForwardedAgent(out string? agentEndpoint))
        {
            options = options with
            {
                AgentForwarding = new AgentForwardOptions
                {
                    AgentEndpoint = agentEndpoint,
                    FailureMode = ForwardFailureMode.Continue,
                },
            };
        }

        // SetEnv / SendEnv：模板里显式给了环境变量就不动（显式的赢）。
        if (options.Environment.Count == 0 && SessionEnvironment() is { Count: > 0 } environment)
        {
            options = options with { Environment = environment };
        }

        // ObscureKeystrokeTiming：yes 是 20 毫秒的节拍（OpenSSH 的默认），interval:N 是 N 毫秒，no 不混淆。
        if (options.ObscureKeystrokeTiming is null && First("ObscureKeystrokeTiming") is { } obscure)
        {
            string value = obscure.Trim();
            TimeSpan? interval = value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromMilliseconds(20)
                : value.StartsWith("interval:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value["interval:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int ms) && ms is >= 1 and <= 1000
                    ? TimeSpan.FromMilliseconds(ms)
                    : null;
            if (interval is not null)
            {
                options = options with { ObscureKeystrokeTiming = interval };
            }
        }

        // RemoteCommand：在伪终端里跑这条命令而不是登录 shell（模板里显式给了命令就不动）。
        if (options.Command is null && First("RemoteCommand") is { } remoteCommand
            && !remoteCommand.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { Command = remoteCommand };
        }

        if (ForwardX11 && options.X11Forwarding is null)
        {
            options = options with
            {
                X11Forwarding = new X11ForwardOptions
                {
                    IsTrusted = ForwardX11Trusted,
                    FailureMode = ForwardFailureMode.Continue,
                    Timeout = ForwardX11Timeout ?? X11ForwardOptions.Default.Timeout,
                },
            };
        }

        return options;
    }
}
