// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/05-connection.md §6.3

using System.Globalization;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>建立一条 SSH 连接需要的一切。</summary>
public sealed record SshConnectionOptions
{
    /// <summary>用 <c>user@host:port</c> 形式构造。</summary>
    /// <param name="target">
    /// 形如 <c>root@10.0.0.1:22</c>、<c>joe@example.com</c>、<c>example.com</c>。
    /// IPv6 要写成 <c>joe@[::1]:22</c>。
    /// </param>
    public SshConnectionOptions(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        (UserName, Host, Port) = ParseTarget(target);
    }

    /// <summary>分开给用户名与主机。</summary>
    public SshConnectionOptions(string userName, string host, int port = 22)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        UserName = userName;
        Host = host;
        Port = port;
    }

    /// <summary>用户名。</summary>
    public string UserName { get; }

    /// <summary>主机。</summary>
    public string Host { get; }

    /// <summary>端口。</summary>
    public int Port { get; }

    /// <summary>
    /// 凭据，<b>按尝试顺序</b>。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/04 §2.2〕<b>这就是全部，库不做任何隐式回退</b> ——
    /// 不自动读 <c>~/.ssh/id_*</c>，不自动连 ssh-agent。
    /// 要用它们就显式加进来（<c>SshPrivateKeyFile.LoadAsync</c> /
    /// <c>SshAgentClient.GetCredentialsAsync</c>）。
    /// </remarks>
    public IReadOnlyList<SshCredential> Credentials { get; init; } = [];

    /// <summary>主机密钥策略。</summary>
    /// <remarks>
    /// 默认是<b>按 <c>known_hosts</c> 且没见过就拒绝</b> ——
    /// 交互式客户端要换成带询问回调的那一种。
    /// 默认不能是「接受任何密钥」：那等于关掉中间人防护。
    /// </remarks>
    public IHostKeyPolicy HostKeyPolicy { get; init; } =
        new KnownHostsPolicy { UnknownHost = UnknownHostBehavior.Reject };

    /// <summary>拨号器。</summary>
    public ISshTransportDialer Dialer { get; init; } = TcpTransportDialer.Shared;

    /// <summary>外层连接的计时器（这条连接是另一条连接的跳板那一跳时）。</summary>
    internal SshConnectDeadline? OuterDeadline { get; init; }

    /// <summary>算法清单。</summary>
    public SshAlgorithmSet Algorithms { get; init; } = SshAlgorithmSet.Default;

    /// <summary>通道限额。</summary>
    public SshConnectionLimits Limits { get; init; } = SshConnectionLimits.Default;

    /// <summary>
    /// 连接超时 —— <b>不含主机密钥裁决的时间</b>。
    /// </summary>
    /// <remarks>
    /// 裁决要弹窗问用户，而弹窗摆着的时间算进连接超时的话，
    /// 用户点完「信任」这一轮已经被判死，只能原地补连一次。
    /// 那个「补连一次」在两个计时分开之后就不必存在了。
    /// </remarks>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>主机密钥裁决的超时。默认无限 —— 等用户看指纹。</summary>
    public TimeSpan HostKeyDecisionTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// 认证超时。
    /// </summary>
    /// <remarks>默认两分钟 —— 用户可能要去掏手机看动态码。</remarks>
    public TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>保活策略。</summary>
    public SshKeepAlivePolicy KeepAlive { get; init; } = SshKeepAlivePolicy.Disabled;

    /// <summary>我们主动发起重协商的阈值。默认 1 GiB / 1 小时 / 2³¹ 个报文。</summary>
    /// <remarks>
    /// 与保活不同，<b>这一条默认是开着的</b> —— 它防的是 nonce 回绕那一类
    /// 灾难性后果，不是一个可选的优化。<see cref="SshRekeyPolicy.Disabled"/>
    /// 能关掉主动发起，但接住对端发起的那一半永远开着。
    /// </remarks>
    public SshRekeyPolicy Rekey { get; init; } = SshRekeyPolicy.Default;

    /// <summary>阈值多久看一眼。</summary>
    /// <remarks>
    /// internal：阈值下限是 1 分钟 / 64 MiB / 1024 个报文，5 秒的粒度对它们足够，
    /// 又不至于让一条闲着的连接每秒都醒一次。<b>只有用例需要把它调小</b>，
    /// 否则一条验阈值的用例要干等 5 秒。
    /// </remarks>
    internal TimeSpan RekeyCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>一次重协商最多等多久（见 <c>SshConnection.RekeyTimeout</c>）。</summary>
    /// <remarks>internal：同上，只有用例需要把它调小。</remarks>
    internal TimeSpan RekeyTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>服务端横幅的回调。<b>文本来自未认证的对端，是注入面。</b></summary>
    public Func<string, CancellationToken, ValueTask>? BannerHandler { get; init; }

    /// <summary>是否允许 RSA 降级到 SHA-1 签名。默认<b>否</b>。</summary>
    public bool AllowSha1RsaSignatures { get; init; }

    /// <summary>要连的主机与端口（不含用户名；展示时是 <c>host:port</c>，IPv6 带方括号）。</summary>
    public SshEndPoint EndPoint => new(Host, Port);

    private static (string User, string Host, int Port) ParseTarget(string target)
    {
        string rest = target;
        string user = Environment.UserName;

        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            user = rest[..at];
            rest = rest[(at + 1)..];
        }

        if (user.Length == 0)
        {
            throw new ArgumentException($"目标 {target} 里的用户名是空的。", nameof(target));
        }

        // IPv6 要写成 [::1]:22 —— 不加方括号的话冒号分不清是地址还是端口。
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ArgumentException($"目标 {target} 里的方括号没有闭合。", nameof(target));
            }

            string address = rest[1..close];
            string tail = rest[(close + 1)..];

            return (user, address, tail.StartsWith(':') ? ParsePort(tail[1..], target) : 22);
        }

        int colon = rest.LastIndexOf(':');
        return colon < 0
            ? (user, rest, 22)
            : (user, rest[..colon], ParsePort(rest[(colon + 1)..], target));
    }

    private static int ParsePort(string text, string target)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"目标 {target} 里的端口 “{text}” 不合法。", nameof(target));
        }
        return port;
    }
}
