// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §6.3

namespace VelaShell.Ssh.Session;

/// <summary>保活策略。</summary>
/// <remarks>
/// <para>
/// <b>计时基准是「上次收到任何报文」，不是「上次发保活」。</b>
/// 连接正忙的时候根本不需要发保活 —— 数据本身就证明了链路活着。
/// </para>
/// <para>
/// 判死时抛的是 <c>KeepAliveTimeout</c> 而不是笼统的 <c>Timeout</c>，
/// 因为上层的自动重连策略只应该对这一类生效。
/// </para>
/// </remarks>
public readonly record struct SshKeepAlivePolicy
{
    /// <summary>建一个保活策略。</summary>
    /// <param name="interval">距<b>上次收到任何报文</b>的间隔。<see cref="TimeSpan.Zero"/> 表示不保活。</param>
    /// <param name="maxMissed">连续这么多次没等到应答就判定连接已死。</param>
    /// <exception cref="ArgumentOutOfRangeException">间隔为负，或 <paramref name="maxMissed"/> 小于 1。</exception>
    public SshKeepAlivePolicy(TimeSpan interval, int maxMissed = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMissed, 1);
        Interval = interval;
        MaxMissed = maxMissed;
    }

    /// <summary>距上次收到任何报文的间隔。<see cref="TimeSpan.Zero"/> 表示不保活。</summary>
    public TimeSpan Interval { get; }

    /// <summary>连续这么多次没等到应答就判定连接已死。</summary>
    public int MaxMissed { get; }

    /// <summary>不保活。</summary>
    public static SshKeepAlivePolicy Disabled => default;

    /// <summary>保活是否启用。</summary>
    public bool IsEnabled => Interval > TimeSpan.Zero;
}
