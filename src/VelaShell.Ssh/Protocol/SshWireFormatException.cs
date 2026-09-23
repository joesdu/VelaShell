// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.Ssh.Protocol;

/// <summary>
/// wire 格式解析失败。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是一个内部异常，不会漏给使用者。</b>它在会话边界上被包成公开的
/// <c>SshProtocolException</c>（带 <c>Reason</c> 与 <c>Phase</c>，
/// 见 velashell-docs/zh/ssh/spec/08-failures.md）。
/// </para>
/// <para>
/// 分成两层是因为它们服务于不同的读者：这一层要说清「第 3 个字段的长度是 9999，
/// 但只剩 12 字节」，给排查协议问题的人看；公开那一层要说清「在密钥交换阶段
/// 对端违反了协议」，给写 <c>catch</c> 与 UI 文案的人看。
/// </para>
/// </remarks>
internal sealed class SshWireFormatException : Exception
{
    /// <summary>用给定消息创建异常。</summary>
    public SshWireFormatException(string message) : base(message)
    {
    }

    /// <summary>用给定消息与内部异常创建异常。</summary>
    public SshWireFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>数据不足以读出一个完整字段。</summary>
    public static SshWireFormatException Truncated(string field, long needed, long available) =>
        new($"读取 {field} 需要 {needed} 字节，只剩 {available} 字节。");
}
