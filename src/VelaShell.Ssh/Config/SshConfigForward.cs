// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  LocalForward / RemoteForward / DynamicForward / GatewayPorts(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.3

using System.Globalization;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Config;

/// <summary><c>ssh_config</c> 里的一条转发是哪一种。</summary>
public enum SshConfigForwardKind
{
    /// <summary><c>LocalForward</c>（<c>-L</c>）。</summary>
    Local,

    /// <summary><c>RemoteForward</c>（<c>-R</c>）；不给目标时是远程动态转发。</summary>
    Remote,

    /// <summary><c>DynamicForward</c>（<c>-D</c>，本机 SOCKS）。</summary>
    Dynamic,
}

/// <summary><c>ssh_config</c> 里的一条转发，拆成结构化的两头。</summary>
/// <param name="Kind">哪一种。</param>
/// <param name="ListenAddress">监听地址（本地转发是本机地址，远程转发原样交给服务端）；没写为 <see langword="null"/>（本地默认环回、<c>GatewayPorts yes</c> 时全部网卡；远程交给服务端定）。</param>
/// <param name="ListenPort">监听端口；监听在套接字文件上时为 0。</param>
/// <param name="ListenSocketPath">监听在套接字文件上时的路径。</param>
/// <param name="TargetHost">目标主机；目标是套接字或动态转发时为 <see langword="null"/>。</param>
/// <param name="TargetPort">目标端口。</param>
/// <param name="TargetSocketPath">目标是套接字时的路径。</param>
/// <param name="Line">配置里的原文（进日志与报错）。</param>
public sealed record SshConfigForward(
    SshConfigForwardKind Kind,
    string? ListenAddress,
    int ListenPort,
    string? ListenSocketPath,
    string? TargetHost,
    int TargetPort,
    string? TargetSocketPath,
    string Line)
{
    /// <summary>远程转发没给目标：远程动态转发（服务端那头当 SOCKS 代理，由本机按放行名单去连）。</summary>
    public bool IsRemoteDynamic => Kind == SshConfigForwardKind.Remote && TargetHost is null && TargetSocketPath is null;

    /// <summary>
    /// 解析一条转发的值（<c>ssh_config</c> 里关键字之后的部分，写法照原文）。
    /// 调用方用 <see cref="SshHostConfig.GetForwards"/>；〔历史〕曾经公开，宿主导入时逐行调它，2026-10-31 改由 <c>GetForwards(onInvalid)</c> 逐行报错。
    /// </summary>
    /// <param name="kind">哪一种。</param>
    /// <param name="value">值，如 <c>8080 db.internal:5432</c>、<c>127.0.0.1:1080</c>、<c>/tmp/d.sock /var/run/docker.sock</c>。</param>
    /// <exception cref="SshConnectException">写法不对（<see cref="SshFailureReason.InvalidConfiguration"/>）。</exception>
    /// <remarks>
    /// 两头照 velashell-docs/zh/ssh/spec/09 §7.4 分词（带空格的套接字路径加引号写，是一头）；
    /// 含 <c>/</c> 的一头是 Unix 套接字路径（与 ssh 的判断一致；整个放进方括号的 <c>[C:/x.sock]</c> 是方括号里的那个路径）；IPv6 地址写在方括号里（<c>[::1]:8080</c>）；
    /// 监听地址写 <c>*</c> 或空（<c>:8080</c>）表示全部网卡。
    /// </remarks>
    internal static SshConfigForward Parse(SshConfigForwardKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SshConfigSyntax.TrySplit(value, out List<string>? parts)
            ? Parse(kind, parts, value)
            : throw Invalid(kind, value, "引号没配对");
    }

    /// <summary>解析一条已经分好词的转发（<see cref="SshHostConfig.GetForwards"/> 用）。</summary>
    /// <param name="kind">哪一种。</param>
    /// <param name="arguments">分好词的两头（引号与转义已解开）。</param>
    /// <param name="value">配置里的原文（进 <see cref="Line"/> 与报错）。</param>
    internal static SshConfigForward Parse(SshConfigForwardKind kind, IReadOnlyList<string> arguments, string value)
    {
        string[] parts = [.. arguments.Where(static part => part.Length > 0).Select(Unbracket)];
        int expected = kind == SshConfigForwardKind.Dynamic ? 1 : 2;
        if (parts.Length == 0 || parts.Length > expected || (kind == SshConfigForwardKind.Local && parts.Length != 2))
        {
            throw Invalid(kind, value, kind switch
            {
                SshConfigForwardKind.Dynamic => "要写成 [监听地址:]端口",
                SshConfigForwardKind.Local => "要写成「监听 目标」两段",
                _ => "要写成「监听 [目标]」",
            });
        }

        (string? listenAddress, int listenPort, string? listenSocket) = ParseListen(kind, value, parts[0]);
        if (parts.Length == 1)
        {
            return new SshConfigForward(kind, listenAddress, listenPort, listenSocket, null, 0, null, value);
        }

        if (parts[1].Contains('/', StringComparison.Ordinal))
        {
            return new SshConfigForward(kind, listenAddress, listenPort, listenSocket, null, 0, parts[1], value);
        }

        (string? host, int? port) = SplitHostPort(parts[1]);
        if (host is null or "" || port is null or 0)
        {
            throw Invalid(kind, value, $"目标「{parts[1]}」要写成 主机:端口 或套接字路径");
        }
        return new SshConfigForward(kind, listenAddress, listenPort, listenSocket, host, port.Value, null, value);
    }

    private static (string? Address, int Port, string? Socket) ParseListen(SshConfigForwardKind kind, string value, string listen)
    {
        if (listen.Contains('/', StringComparison.Ordinal))
        {
            return kind == SshConfigForwardKind.Dynamic
                ? throw Invalid(kind, value, "动态转发只能监听端口")
                : (null, 0, listen);
        }

        // 只有端口：8080。
        if (int.TryParse(listen, NumberStyles.None, CultureInfo.InvariantCulture, out int bare))
        {
            return IsPort(bare)
                ? (null, bare, null)
                : throw Invalid(kind, value, $"监听端口 {bare} 不在范围内");
        }

        (string? address, int? port) = SplitHostPort(listen);
        if (port is not { } listenPort || !IsPort(listenPort))
        {
            throw Invalid(kind, value, $"监听「{listen}」要写成 [地址:]端口");
        }
        return (address is "" ? "*" : address, listenPort, null);
    }

    /// <summary>监听端口：0 由系统（本地）或服务端（远程）分配，实际端口看转发器。</summary>
    private static bool IsPort(int port) => port is >= 0 and <= 65535;

    /// <summary>拆 <c>主机:端口</c>（IPv6 在方括号里）；拆不出端口时端口为 <see langword="null"/>。</summary>
    private static (string? Host, int? Port) SplitHostPort(string text)
    {
        string host;
        string port;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':')
            {
                return (null, null);
            }
            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon < 0)
            {
                return (text, null);
            }
            host = text[..colon];
            port = text[(colon + 1)..];
        }
        return int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is >= 0 and <= 65535
            ? (host, number)
            : (host, null);
    }

    /// <summary>
    /// 套接字那一头可以整个放进方括号（<c>[C:/x.sock]</c>、<c>[/run/a:b.sock]</c>），里面的原样是路径 —— 与 ssh 一样（spec/09 §7.4 的观察）：
    /// 路径里有 <c>:</c> 时 ssh 只认这种写法（不加的话把 <c>:</c> 当主机与端口的分隔，报 Bad forwarding specification、整份配置不读），
    /// 宿主导出 Windows 上的本机套接字就这样写。方括号里没有 <c>/</c> 的不动（<c>[::1]:8080</c> 这类 IPv6 写法后面还跟着端口，也不动）。
    /// </summary>
    private static string Unbracket(string part) =>
        part is ['[', .. var inner, ']'] && inner.Contains('/', StringComparison.Ordinal) ? inner : part;

    private static SshConnectException Invalid(SshConfigForwardKind kind, string value, string why) =>
        new(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
            $"{kind}Forward「{PeerText.Sanitize(value, 200)}」写得不对：{why}。");
}
