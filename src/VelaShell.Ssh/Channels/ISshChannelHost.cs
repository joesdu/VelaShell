// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN / CONFIRMATION / FAILURE
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST / DATA / EXTENDED_DATA
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE
//   RFC 4254 §5.4  CHANNEL_REQUEST / SUCCESS / FAILURE
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§3、§4、§5

namespace VelaShell.Ssh.Channels;

/// <summary>通道往会话那边发包的出口。</summary>
internal interface ISshChannelHost
{
    /// <summary>把一个已经拼好的报文发出去。<b>实现必须是线程安全的</b>（多条通道并发发）。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);

    /// <summary>
    /// 发一个报文，并在它入队的<b>同一时刻</b>执行 <paramref name="onEnqueued"/>。
    /// </summary>
    /// <remarks>给「应答靠 FIFO 对齐」的请求登记账本用 —— 登记顺序必须等于上线顺序。</remarks>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, Action onEnqueued, CancellationToken cancellationToken);

    /// <summary>
    /// 发一个报文；入队的<b>同一时刻</b>先问 <paramref name="admit"/> 还发不发，它说不发就不发。
    /// </summary>
    /// <remarks>
    /// 通道上的每一帧都走这里，<paramref name="admit"/> 查的是「CLOSE 发过没有」——
    /// 先查后入队的话，中间插进来的 CLOSE 会让这一帧排到 CLOSE 后面（RFC 4254 §5.3 不许）。
    /// <paramref name="admit"/> 在入队锁里执行，不许在里面等任何东西。
    /// </remarks>
    ValueTask SendIfAsync(ReadOnlyMemory<byte> packet, Func<bool> admit, CancellationToken cancellationToken);

    /// <summary>同 <see cref="SendIfAsync"/>，但 <paramref name="packet"/> 是<b>借来的</b>：返回之后调用方就会回收它。</summary>
    /// <remarks>
    /// 给通道数据用 —— 那是按块从池里租的缓冲。返回时这一帧要么已经写进传输（加密时已复制），
    /// 要么被重协商的闸门暂存了 —— 暂存的那一刻发送泵会自己复制一份，不再引用这块内存。
    /// </remarks>
    ValueTask SendBorrowedIfAsync(ReadOnlyMemory<byte> packet, Func<bool> admit, CancellationToken cancellationToken);

    /// <summary>通道已经彻底关了（或者永远不会再有对端的报文），可以把号收回去。</summary>
    /// <param name="localId">通道号。</param>
    /// <param name="windowBytes">这条通道<b>此刻计在会话预算上的</b>字节数（开通道时计的加上扩窗时追加的）。</param>
    void OnChannelClosed(uint localId, int windowBytes);

    /// <summary>自适应扩窗之前先向会话的窗口总预算申请；预算不够就不扩。</summary>
    bool TryReserveWindowBudget(int bytes);

    /// <summary>缩窗时把多出来的预算还回去。</summary>
    void ReleaseWindowBudget(int bytes);
}
