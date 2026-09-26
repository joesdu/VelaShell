// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>协商失败发生在哪一类算法上。</summary>
public enum SshNegotiationCategory
{
    /// <summary>密钥交换算法。</summary>
    KeyExchange,

    /// <summary>主机密钥算法。</summary>
    HostKey,

    /// <summary>加密算法（客户端 → 服务端）。</summary>
    EncryptionClientToServer,

    /// <summary>加密算法（服务端 → 客户端）。</summary>
    EncryptionServerToClient,

    /// <summary>完整性算法（客户端 → 服务端）。</summary>
    MacClientToServer,

    /// <summary>完整性算法（服务端 → 客户端）。</summary>
    MacServerToClient,

    /// <summary>压缩算法（客户端 → 服务端）。</summary>
    CompressionClientToServer,

    /// <summary>压缩算法（服务端 → 客户端）。</summary>
    CompressionServerToClient,
}
