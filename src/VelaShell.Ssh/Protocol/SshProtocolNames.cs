// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5              认证方法名
//   RFC 4253 §10             服务名
//   RFC 4254 §4、§5、§6、§7  全局请求、通道类型、通道请求
//   RFC 8308 §3.1            server-sig-algs
//   OpenSSH PROTOCOL         *@openssh.com 的通道类型与请求
//   行为规格:                velashell-docs/zh/ssh/spec/00-overview.md §6
//
// 说明:这些字符串是协议规定的事实,任何正确的实现都必然相同 ——
// 它们落在 NOTICE.md 所说的「表达方式唯一、不受版权保护」那一类里。

namespace VelaShell.Ssh.Protocol;

/// <summary>
/// SSH 协议里算法以外的注册名：服务、认证方法、扩展、通道类型、全局请求与通道请求、子系统。
/// </summary>
/// <remarks>
/// 与 <see cref="SshAlgorithmNames"/> 分开放：那边的名字进 <c>KEXINIT</c> 的算法清单参与协商，
/// 这边的名字出现在各自的报文字段里 —— 混在一起，「算法名」这三个字就说谎了。
/// 名字同样<b>区分大小写</b>、按字节比对。
/// </remarks>
internal static class SshProtocolNames
{
    // ------------------------------------------------------------------ 服务

    /// <summary>用户认证服务。</summary>
    public const string ServiceUserAuth = "ssh-userauth";

    /// <summary>连接协议服务。</summary>
    public const string ServiceConnection = "ssh-connection";

    // ------------------------------------------------------------------ 认证

    /// <summary>探测用的空认证。</summary>
    public const string AuthNone = "none";

    /// <summary>密码认证。</summary>
    public const string AuthPassword = "password";

    /// <summary>公钥认证（含证书）。</summary>
    public const string AuthPublicKey = "publickey";

    /// <summary>键盘交互认证（2FA / OTP 走这条）。</summary>
    public const string AuthKeyboardInteractive = "keyboard-interactive";

    /// <summary>GSS-API 认证。</summary>
    public const string AuthGssApiWithMic = "gssapi-with-mic";

    /// <summary>基于主机的认证。</summary>
    public const string AuthHostBased = "hostbased";

    // ------------------------------------------------------------------ 扩展

    /// <summary>RFC 8308 §3.1：服务端接受的公钥签名算法。</summary>
    public const string ExtServerSigAlgs = "server-sig-algs";

    // ------------------------------------------------------------------ 通道

    /// <summary>会话通道（exec / shell / subsystem）。</summary>
    public const string ChannelSession = "session";

    /// <summary>到远端 TCP 端点的直连通道。</summary>
    public const string ChannelDirectTcpIp = "direct-tcpip";

    /// <summary>到远端 Unix 套接字的直连通道。</summary>
    public const string ChannelDirectStreamLocal = "direct-streamlocal@openssh.com";

    /// <summary>服务端因远程转发而发起的通道。</summary>
    public const string ChannelForwardedTcpIp = "forwarded-tcpip";

    /// <summary>服务端因远程 Unix 套接字转发而发起的通道。</summary>
    public const string ChannelForwardedStreamLocal = "forwarded-streamlocal@openssh.com";

    /// <summary>远程 Unix 套接字转发的全局请求（OpenSSH PROTOCOL §2.2）。</summary>
    public const string RequestStreamLocalForward = "streamlocal-forward@openssh.com";

    /// <summary>取消远程 Unix 套接字转发。</summary>
    public const string RequestCancelStreamLocalForward = "cancel-streamlocal-forward@openssh.com";

    /// <summary>服务端为 X11 转发发起的通道（RFC 4254 §6.3.2）。</summary>
    public const string ChannelX11 = "x11";

    /// <summary>请求 X11 转发（RFC 4254 §6.3.1）。</summary>
    public const string RequestX11 = "x11-req";

    /// <summary>服务端为 agent 转发发起的通道。</summary>
    public const string ChannelAuthAgent = "auth-agent@openssh.com";

    // ------------------------------------------------------------------ 请求

    /// <summary>一次性命令。</summary>
    public const string RequestExec = "exec";

    /// <summary>交互式 shell。</summary>
    public const string RequestShell = "shell";

    /// <summary>子系统（如 <c>sftp</c>）。</summary>
    public const string RequestSubsystem = "subsystem";

    /// <summary>申请伪终端。</summary>
    public const string RequestPty = "pty-req";

    /// <summary>终端尺寸变化。<b>RFC 要求 want_reply 为假。</b></summary>
    public const string RequestWindowChange = "window-change";

    /// <summary>设置环境变量。</summary>
    public const string RequestEnvironment = "env";

    /// <summary>给远端进程发信号。<b>RFC 要求 want_reply 为假。</b></summary>
    public const string RequestSignal = "signal";

    /// <summary>远端进程的退出码。</summary>
    public const string RequestExitStatus = "exit-status";

    /// <summary>远端进程被信号杀死。</summary>
    public const string RequestExitSignal = "exit-signal";

    /// <summary>请求 agent 转发。</summary>
    public const string RequestAuthAgent = "auth-agent-req@openssh.com";

    /// <summary>保活探测。<b>只关心有没有应答，不关心应答内容。</b></summary>
    public const string KeepAliveOpenSsh = "keepalive@openssh.com";

    /// <summary>请求服务端开始远程 TCP 转发（RFC 4254 §7.1）。</summary>
    public const string RequestTcpIpForward = "tcpip-forward";

    /// <summary>取消远程 TCP 转发（RFC 4254 §7.1）。</summary>
    public const string RequestCancelTcpIpForward = "cancel-tcpip-forward";

    // ------------------------------------------------------------------ 子系统

    /// <summary>SFTP 子系统。</summary>
    public const string SubsystemSftp = "sftp";
}
