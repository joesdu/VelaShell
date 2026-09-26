// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §1、§2.2

namespace VelaShell.Ssh.Session;

/// <summary>会话级的通道限额。</summary>
public sealed record SshConnectionLimits
{
    /// <summary>同时存在的通道数上限。</summary>
    public int MaxChannels { get; init; } = 512;

    /// <summary>
    /// 所有通道的接收窗口之和不得超过这个数。
    /// </summary>
    /// <remarks>
    /// 没有它的话，开 100 条自适应窗口的通道就能把进程撑爆 ——
    /// 每条最坏 64 MiB，100 条就是 6.4 GiB。
    /// </remarks>
    public long SessionWindowBudgetBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// 通道号回收后延迟多久才允许复用。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §1 规则 3〕通道号回收过早会串话：
    /// 对端可能还在路上发这个号的数据，号一旦被新通道复用，
    /// 那些数据就会被投递到错误的通道上。这是对端实现不规范时的兜底。
    /// </remarks>
    public TimeSpan ChannelIdReuseDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 接收循环要回给对端、还排在发送队列里的应答最多攒多少字节。超过就判对端违规、断开。
    /// </summary>
    /// <remarks>
    /// 对端可以不读我们发的东西、同时不停地发要应答的报文（全局请求、开通道、未知报文号）——
    /// 接收循环不能在背压上等（那会让它等它自己），所以这些应答必须有一个硬上限，
    /// 否则就是一条不花对端任何代价的内存放大。正常的应答只有几个字节，默认值远到碰不上。
    /// </remarks>
    public long MaxQueuedReplyBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>默认限额。</summary>
    public static SshConnectionLimits Default { get; } = new();
}
