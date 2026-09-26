// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §1(原则)、§2(层级)、§5(带上下文的三个异常)

namespace VelaShell.Ssh.Diagnostics;

/// <summary>
/// 本库全部异常的基类。
/// </summary>
/// <remarks>
/// <para>
/// <b>异常里装数据，不装拼好的句子。</b><c>Message</c> 是给人看的，<b>不是 API</b>。
/// 要程序化判断就用 <see cref="Reason"/> 与 <see cref="Phase"/>，以及各派生类型的结构化字段。
/// </para>
/// <para>
/// 这一条不是洁癖：让调用方为了区分「认证失败 / 超时 / 协商失败」去切消息字符串，
/// 会在库改动一个字的措辞时静默失效 —— 而那种代码没有任何办法察觉自己已经坏了。
/// </para>
/// </remarks>
public abstract class SshException : Exception
{
    /// <summary>用给定原因、阶段与消息创建异常。</summary>
    protected SshException(SshFailureReason reason, SshPhase phase, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        Phase = phase;
    }

    /// <summary>失败的分类。</summary>
    public SshFailureReason Reason { get; }

    /// <summary>在哪一步失败的。</summary>
    public SshPhase Phase { get; }

    /// <summary>
    /// 这个失败是否**可能**因为重试而消失。
    /// </summary>
    /// <remarks>
    /// 这是库给的**建议，不是承诺**：它表达「重试有没有意义」，
    /// 不表达「应该重试」—— 重试策略是使用者的事，只有他们知道用户在等还是在跑批。
    /// </remarks>
    public virtual bool IsRetryable => Reason is
        SshFailureReason.DnsFailure or
        SshFailureReason.TcpRefused or
        SshFailureReason.TcpTimeout or
        SshFailureReason.TcpUnreachable or
        SshFailureReason.ProxyRefused or
        SshFailureReason.Timeout or
        SshFailureReason.KeepAliveTimeout or
        SshFailureReason.ClosedByPeer or
        SshFailureReason.AuthenticationFailed;
}

