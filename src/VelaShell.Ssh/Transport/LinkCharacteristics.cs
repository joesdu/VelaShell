// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §10.2、§11.2.1
//
// 这一层存在的**唯一理由**是：自适应窗口与 SFTP 管线深度
// 在一条零延迟、无限带宽的内存链路上没有任何东西可验证 ——
// 无论窗口是 32 KiB 还是 64 MiB,跑出来都一样快。
// 没有链路特征模拟,那两处「自适应」的代码就是在空转。

namespace VelaShell.Ssh.Transport;

/// <summary>一条链路长什么样。</summary>
/// <param name="OneWayLatency">单向时延。往返时延（RTT）是它的两倍。</param>
/// <param name="BytesPerSecond">带宽上限；<c>0</c> 表示不限。</param>
/// <remarks>
/// <para>
/// <b>带宽时延积（BDP）= 带宽 × RTT</b> 就是「一条链路在任一瞬间能装多少在途数据」。
/// 窗口小于 BDP 时，发送方会在等确认上空转，吞吐被死死压在 <c>窗口 / RTT</c>：
/// </para>
/// <code>
/// 2 MiB / 200 ms ≈ 10 MB/s      ← 跨洋链路上怎么也跑不过这个数
/// </code>
/// </remarks>
internal readonly record struct LinkCharacteristics(TimeSpan OneWayLatency, long BytesPerSecond = 0)
{
    /// <summary>理想链路：零时延、无限带宽。</summary>
    public static LinkCharacteristics Ideal => new(TimeSpan.Zero);

    /// <summary>局域网：0.25 ms 单向，1 Gbps。</summary>
    public static LinkCharacteristics LocalNetwork =>
        new(TimeSpan.FromMilliseconds(0.25), 125_000_000);

    /// <summary>跨洋：100 ms 单向（RTT 200 ms），100 Mbps。</summary>
    /// <remarks>BDP = 12.5 MB/s × 0.2 s = 2.5 MiB —— 正好卡在「固定 2 MiB 窗口」上面。</remarks>
    public static LinkCharacteristics Intercontinental =>
        new(TimeSpan.FromMilliseconds(100), 12_500_000);

    /// <summary>往返时延。</summary>
    public TimeSpan RoundTrip => OneWayLatency * 2;

    /// <summary>带宽时延积（字节）。<c>0</c> 表示带宽不限。</summary>
    public long BandwidthDelayProduct =>
        BytesPerSecond == 0 ? 0 : (long)(BytesPerSecond * RoundTrip.TotalSeconds);

    /// <summary>有没有需要模拟的东西。</summary>
    public bool IsIdeal => OneWayLatency <= TimeSpan.Zero && BytesPerSecond == 0;
}

