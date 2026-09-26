// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §八

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发器起不来。</summary>
/// <remarks>
/// <para>
/// <b>只有「转发器本身」的失败才抛这个。</b>
/// 单条连接的失败走转发器的 <c>Error</c> 事件 ——
/// 一条隧道要能跑几天，期间必然有连不上的目标、被重置的连接；
/// 把那些当成致命错误，隧道就没法用了。
/// </para>
/// <para>
/// 具体是哪一种看 <see cref="SshException.Reason"/>：<see cref="SshFailureReason.ForwardRejected"/>、
/// <see cref="SshFailureReason.ForwardBindFailed"/>、<see cref="SshFailureReason.ForwardSetupFailed"/>、
/// <see cref="SshFailureReason.LimitExceeded"/>、<see cref="SshFailureReason.ProtocolError"/>。
/// </para>
/// </remarks>
public sealed class SshForwardException : SshException
{
    /// <summary>创建一个转发异常。</summary>
    /// <param name="reason">失败的分类。</param>
    /// <param name="message">诊断消息。</param>
    /// <param name="innerException">内部异常。</param>
    public SshForwardException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.Open, message, innerException)
    {
    }
}
