// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §4    want_reply 为真的 GLOBAL_REQUEST 必有应答(成功或失败都算)
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §6.3

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Session;

/// <summary>保活循环：链路闲够一个间隔就发一次探测，连着几次没有应答就把连接判死。</summary>
/// <remarks>
/// 从 <see cref="SshConnection"/> 拆出来的协作者。探测本身是一个要应答的全局请求（<see cref="GlobalRequestLedger.ProbeAsync"/>）；
/// 这里只管什么时候探、几次没回算死。
/// </remarks>
internal sealed class KeepAliveMonitor(SshConnection connection, SshPacketTransport transport, GlobalRequestLedger globalRequests)
{
    /// <summary>保活循环一次至少睡这么久：闲够之前不会每毫秒醒一次。</summary>
    private static readonly TimeSpan MinKeepAliveSleep = TimeSpan.FromMilliseconds(50);

    /// <summary>保活循环。</summary>
    /// <remarks>
    /// <para>
    /// <b>计时基准是「上次收到任何报文」，不是固定周期。</b>
    /// 连接正忙的时候根本不需要发保活 —— 数据本身就证明了链路活着，
    /// 按固定周期发只是在给一条已经证明活着的链路添噪音。
    /// </para>
    /// <para>
    /// <b>任何入站报文都重置计数</b>，不只是保活应答。
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int missed = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan idle = connection.Time.GetElapsedTime(connection.LastInboundAt);
                TimeSpan interval = connection.KeepAlive.Interval;

                if (idle < interval)
                {
                    // 还没闲够。睡到「刚好闲够」的那一刻再看。
                    TimeSpan rest = interval - idle;
                    await Task.Delay(rest > MinKeepAliveSleep ? rest : MinKeepAliveSleep, connection.Time, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (connection.IsFaulted)
                {
                    return;   // 会话已经判死了，没什么可探的
                }

                // 「期间收到了别的报文」看报文计数，不看时间戳：时钟没走（粒度粗、或测试里手动拨的）时，
                // 新报文写下的时间戳与原来的一样，会被当成什么都没收到。
                long before = transport.PacketsReceived;

                // ⚠️ **每一次探测都要有自己的期限。**
                //    保活要对付的正是半开连接：报文写得进本机的发送缓冲，
                //    应答却永远不会来。不设期限的话这一次 await 永远不返回，
                //    missed 永远不会加一 —— 判死的逻辑就成了摆设。
                //
                //    超时的那一项**仍然留在应答账本里**：应答只是迟到，
                //    它来的时候必须落在这一项上，后面的应答才对得上号。
                bool alive = await globalRequests.ProbeAsync(interval, cancellationToken)
                    .ConfigureAwait(false);

                // 有应答，或者期间收到了别的报文 —— 都算活着。
                if (alive || transport.PacketsReceived != before)
                {
                    missed = 0;
                    continue;
                }

                if (++missed >= connection.KeepAlive.MaxMissed)
                {
                    // 判死时用 KeepAliveTimeout 而不是笼统的 Timeout ——
                    // 上层的自动重连策略只应该对这一类生效。
                    connection.Fault(new SshConnectionClosedException(
                        SshFailureReason.KeepAliveTimeout, SshPhase.Open,
                        $"连续 {missed} 次保活探测没有得到应答，判定连接已死" +
                        (connection.Description.Length > 0 ? $"（{connection.Description}）" : "") + "。"));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 我们自己要收工。
        }
        catch (Exception)
        {
            // 会话已经出问题了，接收循环会自己发现。
        }
    }
}
