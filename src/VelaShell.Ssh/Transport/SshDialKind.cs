// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1

namespace VelaShell.Ssh.Transport;

/// <summary>一跳拨号的种类。</summary>
public enum SshDialKind
{
    /// <summary>直接 TCP 连接。</summary>
    Tcp,

    /// <summary>经 SOCKS5 代理。</summary>
    Socks5,

    /// <summary>经 HTTP CONNECT 代理。</summary>
    HttpConnect,

    /// <summary>经另一台 SSH 主机（跳板 / ProxyJump）。</summary>
    SshJump,

    /// <summary>内存传输（测试用，不经过网络）。</summary>
    InMemory,

    /// <summary>使用者自定义的拨号方式。</summary>
    Custom,

    /// <summary>经一个外部程序的标准输入输出（<c>ProxyCommand</c>）。</summary>
    ProxyCommand,
}
