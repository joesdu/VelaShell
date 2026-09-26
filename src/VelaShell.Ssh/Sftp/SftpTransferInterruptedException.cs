// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §3.3、§6.2、§九

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>传输被中断，但我们知道**确切**已经落盘了多少。</summary>
/// <remarks>
/// <para>
/// 这是 <see cref="DurableLength"/> 存在的全部意义。
/// </para>
/// <para>
/// 流水线满载时有 N 个在途 <c>WRITE</c>，而<b>应答顺序不保证</b>。
/// 中途断开时，服务端报告的文件长度只代表「已确认的<b>最高</b>偏移」，
/// 它之前可能留着读作 0 的空洞。所以不能拿文件长度当续传起点。
/// </para>
/// <para>
/// 常见做法是从文件长度<b>盲退一个完整的在途窗口</b>（比如 2 MiB），
/// 代价是每次续传都要重传那么多，而且那个数字是猜的 ——
/// 换个实现或换个配置就不对了。
/// </para>
/// <para>
/// <see cref="DurableLength"/> 是「从 0 开始连续已确认」的最高偏移，
/// <b>精确，不用猜</b>。从这个数续传即可。
/// </para>
/// </remarks>
public sealed class SftpTransferInterruptedException : SshException
{
    /// <summary>创建一个传输中断异常。</summary>
    public SftpTransferInterruptedException(
        long durableLength, string message, Exception? innerException = null)
        : base(SshFailureReason.ClosedByPeer, SshPhase.Open, message, innerException) =>
        DurableLength = durableLength;

    /// <summary>
    /// 从 0 开始<b>连续</b>已确认的字节数。断点续传从这里续。
    /// </summary>
    public long DurableLength { get; }
}
