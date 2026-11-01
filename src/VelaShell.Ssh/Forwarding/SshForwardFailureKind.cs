// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.1  tcpip-forward 与它的应答(端口给 0 时应答里带实际端口)
//   RFC 4254 §6.3  x11-req;OpenSSH PROTOCOL 的 streamlocal-forward@openssh.com 与 auth-agent-req@openssh.com
//   行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §八;velashell-docs/zh/ssh/spec/08-failures.md §一

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发器起不来的具体情形（<see cref="SshForwardException.FailureKind"/>）—— 比原因码更细，界面据此说清是哪一步、该去查什么。</summary>
/// <remarks>
/// 异常消息是写给开发者的中文；界面按这个种类与异常上的参数（监听的地址、端口、上限、X 显示、xauth 的退出码与输出……）自己说。
/// 零值 <see cref="Unknown"/> 是「没归类」：库自己抛的转发异常都会归类。
/// </remarks>
public enum SshForwardFailureKind
{
    /// <summary>没归类。</summary>
    Unknown = 0,

    // ---- 本机监听（ForwardBindFailed）----

    /// <summary>本机在 <see cref="SshForwardException.Endpoint"/>（地址:端口，或 Unix 套接字路径）上起不了监听；套接字错误在内层。</summary>
    ListenFailed,

    /// <summary>
    /// 听 <c>localhost</c> 时 IPv4 起来了，同一端口的 IPv6 环回（<see cref="SshForwardException.Endpoint"/>）却起不来；
    /// 被占时（内层是 <c>AddressAlreadyInUse</c>）先试 <c>::1</c> 的客户端会连到别的进程那里，所以整个转发不起。
    /// </summary>
    Ipv6LoopbackBindFailed,

    /// <summary>要监听的 Unix 套接字路径 <see cref="SshForwardException.Endpoint"/> 已经存在，而没有要求替换。</summary>
    SocketPathExists,

    /// <summary>
    /// Unix 套接字路径 <see cref="SshForwardException.Endpoint"/> 太长（Linux 上限 107 字节、macOS 103）：本地转发要监听的那个（<see cref="Diagnostics.SshFailureReason.ForwardBindFailed"/>），
    /// 或者远程转发回连要连的本机那个（<see cref="Diagnostics.SshFailureReason.ForwardSetupFailed"/>，起转发时就报、什么都没发）。
    /// </summary>
    SocketPathTooLong,

    // ---- 服务端拒绝或答非所问 ----

    /// <summary>
    /// 服务端拒绝在 <see cref="SshForwardException.Endpoint"/> 上开监听（远程转发 / 远程动态转发）；请求的端口在 <see cref="SshForwardException.Port"/>，
    /// 1–1023 是特权端口。
    /// </summary>
    RemoteListenRejected,

    /// <summary>服务端拒绝在套接字路径 <see cref="SshForwardException.Endpoint"/> 上开监听（<c>streamlocal-forward@openssh.com</c>）。</summary>
    RemoteSocketListenRejected,

    /// <summary>请求了动态端口（0），服务端的应答里却没有带回实际端口号。</summary>
    NoBoundPortInReply,

    /// <summary>服务端拒绝了 agent 转发请求（常见原因是 sshd_config 里 <c>AllowAgentForwarding no</c>）。</summary>
    AgentForwardRejected,

    /// <summary>本机 ssh-agent 不可用（没在跑、连不上、端点不可信），agent 转发没有请求或这条通道被拒；agent 那边的异常在内层。</summary>
    LocalAgentUnavailable,

    /// <summary>服务端拒绝了 X11 转发请求（常见原因是 sshd_config 里 <c>X11Forwarding no</c>，或者服务端没装 xauth）。</summary>
    X11ForwardRejected,

    // ---- 本机准备 X11 转发（ForwardSetupFailed）----

    /// <summary>拿不到本机的 X 显示：<c>DISPLAY</c> 没设或者格式不认识，也没有显式指定。</summary>
    NoX11Display,

    /// <summary>建不了给 xauth 用的临时目录；IO 错误在内层。</summary>
    XAuthTempDirectoryFailed,

    /// <summary>xauth 跑不起来（没装、不在 PATH 里；Windows 上通常没有）；原因在内层。</summary>
    XAuthStartFailed,

    /// <summary>xauth 在 <see cref="SshForwardException.Timeout"/> 内没有返回（卡在一个没响应的 X server 上），已被杀掉。</summary>
    XAuthTimedOut,

    /// <summary>
    /// xauth 失败：退出码在 <see cref="SshForwardException.ExitCode"/>，stderr 的末尾（已清洗）在 <see cref="SshForwardException.ErrorOutput"/>。
    /// 非受信 X11 转发要本机有 xauth、且 X server 支持 SECURITY 扩展。
    /// </summary>
    XAuthFailed,

    /// <summary>xauth 跑完了，却没有生成显示 <see cref="SshForwardException.DisplayName"/> 的 cookie。</summary>
    XAuthNoCookie,

    /// <summary>
    /// ssh_config 里的一条转发（原文在 <see cref="SshForwardException.ConfigLine"/>）没起来，而配了 <c>ExitOnForwardFailure yes</c>：
    /// 已起的转发全部撤掉；那一条自己的失败在内层。
    /// </summary>
    ConfigForwardFailed,

    // ---- 对端发来的转发通道被拒（这几种的消息是回给服务端的拒绝理由）----

    /// <summary>服务端发来的转发通道对不上任何转发（<see cref="SshForwardException.Endpoint"/> 是它报的监听地址或套接字路径）。</summary>
    UnmatchedForwardedChannel,

    /// <summary>这条连接上的 X11 转发都已过期，新的 <c>x11</c> 通道被拒。</summary>
    X11ForwardingExpired,

    /// <summary>同时打开的转发通道（agent 通道、远程转发的连接）已达上限 <see cref="SshForwardException.Limit"/>。</summary>
    ChannelLimitReached,

    /// <summary>远程转发连不上本机目标；套接字错误在内层。</summary>
    LocalTargetUnreachable,

    // ---- 收工 ----

    /// <summary>转发器已由本端停止（<see cref="Diagnostics.SshFailureReason.Aborted"/>，<c>PortForwarder.Completion</c> 的结果）。</summary>
    Stopped,
}
