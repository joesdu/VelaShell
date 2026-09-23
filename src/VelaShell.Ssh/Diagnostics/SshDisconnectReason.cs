// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4250 §4.2.2  Disconnection Messages Reason Codes —— 本表全部取值
//
// 说明:这张表是协议规定的事实,任何正确的实现都必然给出同样的名字与数值 ——
// 它落在 NOTICE.md 所说的「表达方式唯一、不受版权保护」那一类里。
//
// 它从 SshExceptions.cs 里拆出来:那里是真正有表达性的异常层级,这里只是协议码表。

namespace VelaShell.Ssh.Diagnostics;
/// <summary><c>SSH_MSG_DISCONNECT</c> 的原因码（RFC 4250 §4.2.2）。</summary>
public enum SshDisconnectReason
{
    /// <summary>主机不允许连接。</summary>
    HostNotAllowedToConnect = 1,

    /// <summary>协议错误。</summary>
    ProtocolError = 2,

    /// <summary>密钥交换失败。</summary>
    KeyExchangeFailed = 3,

    /// <summary>保留。</summary>
    Reserved = 4,

    /// <summary>MAC 校验失败。</summary>
    MacError = 5,

    /// <summary>压缩错误。</summary>
    CompressionError = 6,

    /// <summary>服务不可用。</summary>
    ServiceNotAvailable = 7,

    /// <summary>协议版本不支持。</summary>
    ProtocolVersionNotSupported = 8,

    /// <summary>主机密钥无法验证。</summary>
    HostKeyNotVerifiable = 9,

    /// <summary>连接丢失。</summary>
    ConnectionLost = 10,

    /// <summary>由应用主动断开。</summary>
    ByApplication = 11,

    /// <summary>连接数过多。</summary>
    TooManyConnections = 12,

    /// <summary>用户取消了认证。</summary>
    AuthCancelledByUser = 13,

    /// <summary>没有更多可用的认证方法。</summary>
    NoMoreAuthMethodsAvailable = 14,

    /// <summary>用户名非法。</summary>
    IllegalUserName = 15,
}
