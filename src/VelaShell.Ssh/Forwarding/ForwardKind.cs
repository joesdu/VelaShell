// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发的形态。</summary>
public enum ForwardKind
{
    /// <summary>本地转发（<c>-L</c>）：我们监听，出站是 <c>direct-tcpip</c>。</summary>
    Local,

    /// <summary>动态转发（<c>-D</c>）：我们监听并跑 SOCKS5，目标由客户端给出。</summary>
    Dynamic,

    /// <summary>远程转发（<c>-R</c>）：服务端监听，回连由服务端发起。</summary>
    Remote,

    /// <summary>X11 转发（<c>-X</c> / <c>-Y</c>）：服务端为远端的图形程序开回 <c>x11</c> 通道。</summary>
    X11,

    /// <summary>agent 转发（<c>-A</c>）：服务端为远端的 <c>ssh-add</c> / <c>ssh</c> 开回 agent 通道。</summary>
    Agent,
}
