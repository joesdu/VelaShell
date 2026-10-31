// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST 的语义
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §3

namespace VelaShell.Ssh.Channels;

/// <summary>接收窗口怎么定大小。</summary>
/// <remarks>
/// <para>
/// 固定窗口的问题在于**它同时决定了吞吐上限**：
/// </para>
/// <code>
/// 吞吐上限 ≈ 窗口 / RTT
/// 2 MiB / 200 ms ≈ 10 MB/s      ← 跨洋链路上怎么也跑不过这个数
/// </code>
/// <para>
/// 所以默认是自适应的：链路快就把窗口放大，让带宽时延积填得满。
/// 需要确定性内存占用的场景（嵌入式、成百上千条并发通道）用
/// <see cref="Fixed(int)"/>。
/// </para>
/// <para>
/// 〔AGENTS 4.2 / 4.3〕规则型策略是 <c>readonly record struct</c>：相等比较按值，
/// 所在的 <see cref="SshChannelOptions"/> 比较起来才有意义。曾经是 class，两个同样的策略比较结果是「不等」。
/// 只能经 <see cref="Fixed(int)"/> / <see cref="Adaptive(int, int)"/> 构造；<c>default</c> 不是合法的策略，
/// 设到 <see cref="SshChannelOptions.WindowPolicy"/> 上会当场抛。
/// </para>
/// </remarks>
public readonly record struct SshWindowPolicy
{
    /// <summary>默认的初始窗口。</summary>
    public const int DefaultInitialBytes = 256 * 1024;

    /// <summary>默认的自适应上限。</summary>
    public const int DefaultMaximumBytes = 64 * 1024 * 1024;

    /// <summary>窗口允许的最小值。</summary>
    /// <remarks>比一个最大报文还小的窗口会让发送方几乎每包都要等回补。</remarks>
    public const int AbsoluteMinimumBytes = 32 * 1024;

    private SshWindowPolicy(int initial, int minimum, int maximum, bool adaptive)
    {
        InitialBytes = initial;
        MinimumBytes = minimum;
        MaximumBytes = maximum;
        IsAdaptive = adaptive;
    }

    /// <summary>初始窗口大小。</summary>
    public int InitialBytes { get; }

    /// <summary>自适应时窗口不会缩到这个值以下。</summary>
    public int MinimumBytes { get; }

    /// <summary>自适应时窗口不会涨过这个值。</summary>
    public int MaximumBytes { get; }

    /// <summary>是否随链路情况调整。</summary>
    public bool IsAdaptive { get; }

    /// <summary>固定大小的窗口。</summary>
    /// <param name="bytes">窗口字节数。</param>
    public static SshWindowPolicy Fixed(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, AbsoluteMinimumBytes);
        return new SshWindowPolicy(bytes, bytes, bytes, adaptive: false);
    }

    /// <summary>自适应窗口。</summary>
    /// <param name="minimumBytes">下限。</param>
    /// <param name="maximumBytes">上限 —— <b>这就是单条通道最坏的接收缓冲占用</b>。</param>
    public static SshWindowPolicy Adaptive(
        int minimumBytes = DefaultInitialBytes, int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumBytes, AbsoluteMinimumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, minimumBytes);
        return new SshWindowPolicy(minimumBytes, minimumBytes, maximumBytes, adaptive: true);
    }

    /// <summary>默认策略：256 KiB 起步，最大 64 MiB。</summary>
    public static SshWindowPolicy Default => Adaptive();

    /// <summary>是不是经 <see cref="Fixed(int)"/> / <see cref="Adaptive(int, int)"/> 构造的（<c>default</c> 不是）。</summary>
    internal bool IsValid => InitialBytes >= AbsoluteMinimumBytes;
}
