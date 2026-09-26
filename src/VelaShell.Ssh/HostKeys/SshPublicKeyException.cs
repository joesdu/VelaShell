// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.HostKeys;

/// <summary>公钥解析失败。</summary>
/// <remarks>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.KeyFormatInvalid"/>
/// 或 <see cref="SshFailureReason.Unsupported"/>。解析本身不知道这把钥来自哪里（对端的主机密钥、
/// 本地的 <c>.pub</c>），所以阶段是 <see cref="SshPhase.None"/>；握手里解析主机密钥失败时，
/// 密钥交换会把它包成一个带阶段的 <see cref="SshConnectException"/>。
/// </remarks>
public sealed class SshPublicKeyException : SshException
{
    /// <summary>创建一个公钥解析异常。</summary>
    /// <param name="reason">失败的分类。</param>
    /// <param name="message">诊断消息。</param>
    /// <param name="innerException">内部异常。</param>
    public SshPublicKeyException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.None, message, innerException)
    {
    }
}
