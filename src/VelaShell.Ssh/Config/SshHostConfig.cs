// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Globalization;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Config;

/// <summary>把 <c>ssh_config</c> 解完之后，某一台主机最终生效的设置。</summary>
public sealed class SshHostConfig
{
    // ⚠️ IDE0028 会建议把它简化成 []。**不能听** —— 那会把
    //    OrdinalIgnoreCase 丢掉，而 ssh_config 的键是不区分大小写的
    //    （`HostName` 与 `hostname` 是同一个键）。
#pragma warning disable IDE0028
    private readonly Dictionary<string, List<string>> _settings = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028

    internal SshHostConfig(string host) => QueriedHost = host;

    /// <summary>当初查的是哪个名字。</summary>
    public string QueriedHost { get; }

    /// <summary>真正要连的主机（<c>HostName</c>，没有就是 <see cref="QueriedHost"/>）。</summary>
    public string HostName => First("HostName") ?? QueriedHost;

    /// <summary>端口。</summary>
    public int Port =>
        int.TryParse(First("Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            ? port
            : 22;

    /// <summary>用户名。</summary>
    public string? User => First("User");

    /// <summary>私钥文件（<c>IdentityFile</c> 可以出现多次，按顺序）。</summary>
    public IReadOnlyList<string> IdentityFiles => All("IdentityFile");

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
    /// 不带单位为秒，几段相加（<c>1h30m</c>）；<c>0</c> 为不过期（<see cref="TimeSpan.Zero"/>）。
    /// </remarks>
    public TimeSpan? ForwardX11Timeout =>
        TryParseTimeSpec(First("ForwardX11Timeout"), out TimeSpan value) ? value : null;

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

    /// <summary><c>UserKnownHostsFile</c>。</summary>
    public string? UserKnownHostsFile => First("UserKnownHostsFile");

    /// <summary>是否只用显式给出的密钥（<c>IdentitiesOnly yes</c>）。</summary>
    public bool IdentitiesOnly => IsYes(First("IdentitiesOnly"));

    /// <summary><c>ForwardAgent</c>。</summary>
    public bool ForwardAgent => IsYes(First("ForwardAgent"));

    /// <summary><c>Compression</c>。</summary>
    public bool Compression => IsYes(First("Compression"));

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
    /// <b>「第一个」是对的，不是「最后一个」。</b>
    /// <c>ssh_config</c> 的规则是<b>先出现的赢</b> —— 与大多数配置格式相反。
    /// 这也是为什么 OpenSSH 的样例里 <c>Host *</c> 总是放在文件末尾。
    /// </remarks>
    public string? First(string key) =>
        _settings.TryGetValue(key, out List<string>? values) && values.Count > 0 ? values[0] : null;

    /// <summary>取某个键的全部值，按出现顺序。</summary>
    public IReadOnlyList<string> All(string key) =>
        _settings.TryGetValue(key, out List<string>? values) ? values : [];

    internal void Add(string key, string value)
    {
        if (!_settings.TryGetValue(key, out List<string>? values))
        {
            values = [];
            _settings[key] = values;
        }
        values.Add(value);
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
            options = options with { AgentForwarding = AgentForwardOptions.Default };
        }

        if (ForwardX11 && options.X11Forwarding is null)
        {
            options = options with
            {
                X11Forwarding = new X11ForwardOptions
                {
                    Trusted = ForwardX11Trusted,
                    BestEffort = true,
                    Timeout = ForwardX11Timeout ?? X11ForwardOptions.Default.Timeout,
                },
            };
        }

        return options;
    }
}
