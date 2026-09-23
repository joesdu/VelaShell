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
/// </remarks>
public sealed class SshWindowPolicy
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
    public static SshWindowPolicy Default { get; } = Adaptive();
}

/// <summary>一个方向上的窗口账。</summary>
/// <remarks>
/// <para>
/// 发送侧与接收侧共用这一个结构，但用法相反：
/// </para>
/// <list type="bullet">
///   <item><b>发送侧</b>：<see cref="TryConsume"/> 扣减，窗口为 0 时必须停下等回补；
///   <see cref="Add"/> 在收到 <c>WINDOW_ADJUST</c> 时补回。</item>
///   <item><b>接收侧</b>：<see cref="TryConsume"/> 在数据到达时扣减（超了就是对端违规），
///   <see cref="Add"/> 在数据被**消费**后补回。</item>
/// </list>
/// </remarks>
internal sealed class SshWindow(int initialSize)
{
    private readonly Lock _lock = new();
    private uint _remaining = (uint)initialSize;

    /// <summary>当前剩余字节数。</summary>
    public uint Remaining
    {
        get
        {
            lock (_lock)
            {
                return _remaining;
            }
        }
    }

    /// <summary>窗口当前的额定大小（自适应时会变）。</summary>
    public int Size { get; private set; } = initialSize;

    /// <summary>扣减。</summary>
    /// <returns>窗口够不够。<see langword="false"/> 时**一个字节都没扣**。</returns>
    public bool TryConsume(int bytes)
    {
        lock (_lock)
        {
            if (_remaining < (uint)bytes)
            {
                return false;
            }
            _remaining -= (uint)bytes;
            return true;
        }
    }

    /// <summary>回补。</summary>
    /// <returns>
    /// 补完是否溢出了 <c>uint32</c>。<b>溢出是明确的协议违规</b>（velashell-docs/zh/ssh/spec/05 §8），
    /// 对端在用它来撑爆我们的计数。
    /// </returns>
    public bool Add(uint bytes)
    {
        lock (_lock)
        {
            if (_remaining > uint.MaxValue - bytes)
            {
                return false;
            }
            _remaining += bytes;
            return true;
        }
    }

    /// <summary>把窗口的额定大小改掉（自适应用）。</summary>
    public void Resize(int newSize) => Size = newSize;

    /// <summary>
    /// 该不该现在发一个 <c>WINDOW_ADJUST</c>。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §3.2〕剩余不足一半时补满。
    /// 太频繁是在浪费报文，太稀疏会让发送方空等。
    /// </remarks>
    public bool ShouldAdjust()
    {
        lock (_lock)
        {
            return _remaining <= (uint)(Size / 2);
        }
    }

    /// <summary>算出「补满」需要补多少，并直接记账。</summary>
    public uint TakeRefill()
    {
        lock (_lock)
        {
            uint target = (uint)Size;
            if (_remaining >= target)
            {
                return 0;
            }
            uint delta = target - _remaining;
            _remaining = target;
            return delta;
        }
    }
}
