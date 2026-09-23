// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §4

namespace VelaShell.Ssh.Diagnostics;

/// <summary>
/// 会话所处的阶段。每个失败都带上它。
/// </summary>
/// <remarks>
/// <para>
/// 失败必须带阶段，因为同一个原因在不同阶段是完全不同的两件事：
/// <c>Timeout</c> 出现在 <see cref="Dialing"/> 是网络问题，
/// 出现在 <see cref="Authenticating"/> 则很可能是用户在找手机上的动态码。
/// </para>
/// <para>迁移图见 velashell-docs/zh/ssh/spec/03-key-exchange.md §8.3 与 velashell-docs/zh/ssh/spec/08-failures.md §4。</para>
/// </remarks>
public enum SshPhase
{
    /// <summary>尚未开始。</summary>
    None = 0,

    /// <summary>DNS 解析、TCP 建连、代理握手、跳板链。</summary>
    Dialing,

    /// <summary>协议版本标识串交换。</summary>
    VersionExchange,

    /// <summary>首次密钥交换，含主机密钥验证。</summary>
    KeyExchange,

    /// <summary>用户认证。</summary>
    Authenticating,

    /// <summary>连接已就绪，可以开通道。</summary>
    Open,

    /// <summary>密钥重协商进行中（连接仍可用）。</summary>
    Rekeying,

    /// <summary>正在关闭。</summary>
    Closing,

    /// <summary>已关闭。</summary>
    Closed,
}
