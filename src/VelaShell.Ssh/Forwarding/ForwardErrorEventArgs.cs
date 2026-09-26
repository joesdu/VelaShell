// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

namespace VelaShell.Ssh.Forwarding;

/// <summary>一条转发连接失败了 —— <b>转发器本身仍在跑</b>。</summary>
public sealed class ForwardErrorEventArgs : EventArgs
{
    /// <summary>创建一条错误事件。</summary>
    /// <param name="reason">失败的分类。</param>
    /// <param name="message">诊断消息。</param>
    /// <param name="exception">底层异常。</param>
    public ForwardErrorEventArgs(ForwardErrorReason reason, string message, Exception? exception = null)
    {
        Reason = reason;
        Message = message;
        Exception = exception;
    }

    /// <summary>失败的分类。</summary>
    public ForwardErrorReason Reason { get; }

    /// <summary>诊断消息（写给开发者看的，界面文案请按 <see cref="Reason"/> 本地化）。</summary>
    public string Message { get; }

    /// <summary>底层异常（可能为空）。</summary>
    public Exception? Exception { get; }
}
