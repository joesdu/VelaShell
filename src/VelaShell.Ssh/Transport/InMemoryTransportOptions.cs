// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1

namespace VelaShell.Ssh.Transport;

/// <summary>内存传输的参数。</summary>
public sealed record InMemoryTransportOptions
{
    /// <summary>
    /// 单向缓冲写入方被暂停的水位（字节）。默认 64 KiB。
    /// </summary>
    /// <remarks>
    /// 它模拟的是「对端还没读，我方发不动了」。把它调小可以在测试里**制造背压**，
    /// 验证上层在写不动时的行为 —— 那条路径在真实网络上很难稳定复现。
    /// </remarks>
    public long PauseWriterThreshold { get; init; } = 64 * 1024;

    /// <summary>写入方恢复的水位（字节）。默认 32 KiB。</summary>
    public long ResumeWriterThreshold { get; init; } = 32 * 1024;
}
