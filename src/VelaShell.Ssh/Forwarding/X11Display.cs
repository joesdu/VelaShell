// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X11 核心协议  DISPLAY 的形态与 6000+N 的端口约定
//   行为规格:     velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.6

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace VelaShell.Ssh.Forwarding;

/// <summary>解析出来的 <c>DISPLAY</c>。</summary>
/// <param name="Host">
/// 主机名；空串表示本机（走 Unix 套接字）。
/// </param>
/// <param name="Number">显示号（<c>:0</c> 里的 <c>0</c>）。</param>
/// <param name="Screen">屏幕号（<c>:0.2</c> 里的 <c>2</c>）。</param>
/// <param name="UnixSocketPath">
/// 直接给了套接字路径时（macOS 的 launchd 会这么干），这里是那个路径。
/// </param>
public sealed record X11Display(string Host, int Number, int Screen, string? UnixSocketPath = null)
{
    /// <summary>X11 的 TCP 端口基数 —— 显示 <c>N</c> 在 <c>6000+N</c>。</summary>
    public const int TcpPortBase = 6000;

    /// <summary>这是不是本机显示（挑 cookie 用：本机显示认 FamilyLocal 条目）。</summary>
    /// <remarks>连哪里看的是 <see cref="UsesLocalSocket"/>，不是它。</remarks>
    public bool IsLocal =>
        UsesLocalSocket
        || string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>这个显示走不走本机套接字：空 host、<c>unix</c>，或者直接给了套接字路径。</summary>
    /// <remarks>
    /// <para>
    /// <b><c>localhost:N</c> 不在此列</b> —— 按 X 的约定它就是 TCP <c>6000+N</c>，
    /// 嵌套 <c>ssh -X</c> 时 sshd 给的正是这种形态。
    /// </para>
    /// <para>
    /// 曾经把它也当成本机套接字、先去试 Linux 的抽象套接字：抽象命名空间不做任何权限检查，
    /// 同一台机器上的别的用户抢先绑上 <c>@/tmp/.X11-unix/X10</c>，就能收到我们换上的**真** cookie。
    /// </para>
    /// </remarks>
    public bool UsesLocalSocket =>
        UnixSocketPath is not null
        || Host.Length == 0
        || string.Equals(Host, "unix", StringComparison.Ordinal);

    /// <summary>给 <c>xauth</c> 用的显示名。</summary>
    /// <remarks>
    /// 本机的 Unix 套接字显示写成 <c>:N</c>；其余的要带上主机或套接字路径 ——
    /// macOS launchd 的 <c>/private/tmp/…/org.xquartz:0</c>、远程的 <c>host:N</c>
    /// 只剩一个 <c>:N</c> 的话，<c>xauth</c> 会去找（或生成）另一个显示的条目。
    /// </remarks>
    public string XAuthName =>
        UnixSocketPath is { } path ? path
        : Host.Length == 0 || string.Equals(Host, "unix", StringComparison.Ordinal) ? $":{Number}"
        : Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Number}"
        : $"{Host}:{Number}";

    /// <summary>解析 <c>DISPLAY</c>。</summary>
    /// <param name="value">形如 <c>:0</c>、<c>:10.2</c>、<c>unix:0</c>、<c>host:0</c>、<c>[::1]:0</c>。</param>
    /// <returns>解析成功返回对象，否则 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>解析失败返回 <see langword="null"/> 而不是抛异常</b> ——
    /// <c>DISPLAY</c> 没设或者设成了奇怪的值是很常见的状态，
    /// 调用方需要的是「能不能用」，不是一个异常。
    /// </para>
    /// <para>
    /// macOS 的 launchd 会把 <c>DISPLAY</c> 设成一个套接字路径
    /// （<c>/private/tmp/com.apple.launchd.XXX/org.xquartz:0</c>），
    /// 所以带 <c>/</c> 的值要按路径处理，不能按 <c>host:N</c> 切。
    /// </para>
    /// </remarks>
    public static X11Display? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();

        // 最后一个冒号才是分隔符 —— IPv6 的 host 里有一堆冒号。
        int colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        string host = text[..colon];
        string tail = text[(colon + 1)..];

        if (tail.Length == 0)
        {
            return null;
        }

        int dot = tail.IndexOf('.', StringComparison.Ordinal);
        string numberPart = dot >= 0 ? tail[..dot] : tail;
        string screenPart = dot >= 0 ? tail[(dot + 1)..] : "0";

        // 只认纯十进制数字：不带符号、不带空白、与区域设置无关。
        // 显示号还要让 6000+N 是个合法端口 —— 否则 GetCandidateEndPoints 造 IPEndPoint 时会抛。
        if (!TryParseNumber(numberPart, out int number) || number > IPEndPoint.MaxPort - TcpPortBase)
        {
            return null;
        }

        if (!TryParseNumber(screenPart, out int screen))
        {
            return null;
        }

        // macOS launchd：DISPLAY 是一个套接字路径。
        if (host.Contains('/', StringComparison.Ordinal))
        {
            return new X11Display("", number, screen, UnixSocketPath: $"{host}:{number}");
        }

        // IPv6 写成 [::1]:0。
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        return new X11Display(host, number, screen);
    }

    private static bool TryParseNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>从环境变量读。</summary>
    public static X11Display? FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>这个显示可以从哪些端点连上去，按优先顺序。</summary>
    /// <remarks>
    /// <para>
    /// 返回的是**一串候选**而不是一个 —— 本机显示在 Linux 上既可能是
    /// 抽象套接字（<c>@/tmp/.X11-unix/X0</c>），也可能是文件系统里的
    /// <c>/tmp/.X11-unix/X0</c>，还可能是监听 TCP 的 X server。
    /// 挨个试比猜一个准。
    /// </para>
    /// <para>
    /// Windows 上没有 X server 的 Unix 套接字约定，VcXsrv / Xming 之类
    /// 都听 TCP <c>6000+N</c>，所以直接走 TCP。
    /// </para>
    /// </remarks>
    public IReadOnlyList<EndPoint> GetCandidateEndPoints()
    {
        List<EndPoint> candidates = [];

        if (UnixSocketPath is { } explicitPath)
        {
            candidates.Add(new UnixDomainSocketEndPoint(explicitPath));
            return candidates;
        }

        // localhost:N 是 TCP 6000+N，不试任何本机套接字（见 UsesLocalSocket）。
        if (!UsesLocalSocket && string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(new IPEndPoint(IPAddress.Loopback, TcpPortBase + Number));
            return candidates;
        }

        if (UsesLocalSocket)
        {
            if (!OperatingSystem.IsWindows() && Socket.OSSupportsUnixDomainSockets)
            {
                // Linux 的抽象命名空间：首字节是 \0。.NET 用前导 \0 表示它。
                if (OperatingSystem.IsLinux())
                {
                    candidates.Add(new UnixDomainSocketEndPoint($"\0/tmp/.X11-unix/X{Number}"));
                }

                candidates.Add(new UnixDomainSocketEndPoint($"/tmp/.X11-unix/X{Number}"));
            }

            candidates.Add(new IPEndPoint(IPAddress.Loopback, TcpPortBase + Number));
            return candidates;
        }

        // 远程显示：只能走 TCP。
        if (IPAddress.TryParse(Host, out IPAddress? address))
        {
            candidates.Add(new IPEndPoint(address, TcpPortBase + Number));
        }
        else
        {
            candidates.Add(new DnsEndPoint(Host, TcpPortBase + Number));
        }

        return candidates;
    }
}
