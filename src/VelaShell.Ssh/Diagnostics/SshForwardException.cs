// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §八

namespace VelaShell.Ssh.Diagnostics;

/// <summary>转发器起不来。</summary>
/// <remarks>
/// <b>只有「转发器本身」的失败才抛这个。</b>
/// 单条连接的失败走 <c>PortForwarder.Error</c> 事件 ——
/// 一条隧道要能跑几天，期间必然有连不上的目标、被重置的连接；
/// 把那些当成致命错误，隧道就没法用了。
/// </remarks>
public sealed class SshForwardException : SshException
{
    /// <summary>创建一个转发异常。</summary>
    public SshForwardException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Open, message, innerException)
    {
    }
}
