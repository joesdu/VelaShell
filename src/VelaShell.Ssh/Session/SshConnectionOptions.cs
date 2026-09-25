// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/05-connection.md §6.3

using System.Globalization;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>保活策略。</summary>
/// <param name="Interval">
/// 距<b>上次收到任何报文</b>的间隔。<c>Zero</c> 表示不保活。
/// </param>
/// <param name="MaxMissed">连续这么多次没等到应答就判定连接已死。</param>
/// <remarks>
/// <para>
/// <b>计时基准是「上次收到任何报文」，不是「上次发保活」。</b>
/// 连接正忙的时候根本不需要发保活 —— 数据本身就证明了链路活着。
/// </para>
/// <para>
/// 判死时抛的是 <c>KeepAliveTimeout</c> 而不是笼统的 <c>Timeout</c>，
/// 因为上层的自动重连策略只应该对这一类生效。
/// </para>
/// </remarks>
public readonly record struct KeepAlivePolicy(TimeSpan Interval, int MaxMissed = 3)
{
    /// <summary>不保活。</summary>
    public static KeepAlivePolicy Disabled => new(TimeSpan.Zero);

    /// <summary>保活是否启用。</summary>
    public bool IsEnabled => Interval > TimeSpan.Zero;
}

/// <summary>我们**主动**发起密钥重协商的阈值（<c>velashell-docs/zh/ssh/spec/03</c> §8.1）。</summary>
/// <remarks>
/// <para>
/// 主动发起与「接住对端发起」是两件事。后者是必需的 —— 不接就会断线；
/// 前者是<b>安全加固</b>：它把一把会话密钥覆盖的数据量与时间窗压住。
/// </para>
/// <para>
/// <b><see cref="MaxPackets"/> 才是那条不能越过的硬线。</b>
/// SSH 的序号是 32 位的，而 AES-GCM 的 nonce 每个报文推进一次 ——
/// 两者都在 2³² 处出事，而 nonce 重用对 GCM 是<b>灾难性</b>的
/// （可以恢复认证密钥，进而伪造）。字节数与时长是 RFC 4253 §9 的建议，
/// 报文数是密码学上的硬约束。
/// </para>
/// <para>
/// <b>阈值有下限。</b> 重协商本身要做一次非对称运算，调得太频繁
/// 就成了一个自己给自己开的拒绝服务面。
/// </para>
/// </remarks>
/// <para>
/// <b>三条阈值都是「0 表示不看这一条」</b>，因此
/// <c>default(SshRekeyPolicy)</c> 与 <see cref="Disabled"/> 是同一个东西 ——
/// 一个全零的结构体就该是「什么都不做」。有主张的那一组值在
/// <see cref="Default"/> 里，写全了，不靠参数默认值去暗示。
/// </para>
/// <para>
/// （第一版不是这样：<c>MaxInterval</c> 的 <c>default</c> 被翻译成「1 小时」，
/// 结果 <see cref="Disabled"/> 里的时长那一条根本关不掉 ——
/// <c>Disabled.IsEnabled</c> 居然是 <see langword="true"/>。
/// 是那条断言把它揪出来的。）
/// </para>
/// <param name="MaxBytes">任一方向累计字节数上限；0 表示不看。下限 64 MiB。</param>
/// <param name="MaxInterval">距上次密钥交换的时长上限；<c>Zero</c> 表示不看。下限 1 分钟。</param>
/// <param name="MaxPackets">任一方向累计报文数上限；0 表示不看。下限 1024。</param>
public readonly record struct SshRekeyPolicy(
    long MaxBytes = 0,
    TimeSpan MaxInterval = default,
    long MaxPackets = 0)
{
    /// <summary>字节数阈值的下限。</summary>
    public const long MinimumBytes = 64L * 1024 * 1024;

    /// <summary>时长阈值的下限。</summary>
    public static TimeSpan MinimumInterval => TimeSpan.FromMinutes(1);

    /// <summary>报文数阈值的下限。</summary>
    /// <remarks>
    /// 比字节与时长的下限宽松得多：报文数是密码学硬约束那一侧，
    /// 调低它的动机通常是测试或者极端保守，而不是误配。
    /// </remarks>
    public const long MinimumPackets = 1024;

    /// <summary>不主动发起（仍然会接住对端发起的）。</summary>
    public static SshRekeyPolicy Disabled => default;

    /// <summary>默认：1 GiB / 1 小时 / 2³¹ 个报文（RFC 4253 §9 的建议 + 硬约束）。</summary>
    public static SshRekeyPolicy Default =>
        new(MaxBytes: 1L << 30, MaxInterval: TimeSpan.FromHours(1), MaxPackets: 1L << 31);

    /// <summary>有没有任何一条阈值是开着的。</summary>
    public bool IsEnabled => MaxBytes > 0 || MaxPackets > 0 || MaxInterval > TimeSpan.Zero;

    /// <summary>校验阈值没有低于下限。</summary>
    /// <exception cref="ArgumentOutOfRangeException">某条阈值低于下限。</exception>
    public void Validate()
    {
        if (MaxBytes is > 0 and < MinimumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBytes), MaxBytes,
                $"重协商的字节阈值不能低于 {MinimumBytes} —— 太频繁的重协商本身就是一个拒绝服务面。");
        }

        if (MaxPackets is > 0 and < MinimumPackets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPackets), MaxPackets,
                $"重协商的报文数阈值不能低于 {MinimumPackets}。");
        }

        if (MaxInterval > TimeSpan.Zero && MaxInterval < MinimumInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInterval), MaxInterval,
                $"重协商的时长阈值不能低于 {MinimumInterval} —— 理由同上。");
        }
    }
}

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
    public KeepAlivePolicy KeepAlive { get; init; } = KeepAlivePolicy.Disabled;

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
    /// 又不至于让一条闲着的连接每秒都醒一次。**只有用例需要把它调小**，
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

    /// <summary><c>host:port</c> 形式的展示名。</summary>
    public string Target => new SshEndPoint(Host, Port).ToString();

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
