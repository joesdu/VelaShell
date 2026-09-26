// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>密钥交换失败。</summary>
/// <remarks>
/// 是 <see cref="SshException"/>：曾经直接继承 <see cref="Exception"/>，按 <c>catch (SshException)</c>
/// 兜库的错误时漏掉它，建连时它原样漏给调用方。原因记成 <see cref="SshFailureReason.ProtocolError"/>：
/// 它最常见于对端给的公开值不合法（长度不对、不在曲线上、弱值）；「算法名没实现」那一类在连接前的
/// <c>SshAlgorithmSet.Validate()</c> 就挡住了。
/// </remarks>
public sealed class SshKeyExchangeException : SshException
{
    /// <summary>用给定消息创建异常。</summary>
    public SshKeyExchangeException(string message)
        : base(SshFailureReason.ProtocolError, SshPhase.KeyExchange, message)
    {
    }

    /// <summary>用给定消息与内部异常创建异常。</summary>
    public SshKeyExchangeException(string message, Exception innerException)
        : base(SshFailureReason.ProtocolError, SshPhase.KeyExchange, message, innerException)
    {
    }
}
