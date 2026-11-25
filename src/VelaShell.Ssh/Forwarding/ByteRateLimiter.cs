// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

using System.Diagnostics.CodeAnalysis;

namespace VelaShell.Ssh.Forwarding;

/// <summary>令牌桶限速：一秒的额度可以一次用掉（突发），之后按速率补，放行时才扣额度。</summary>
/// <remarks>
/// 一个转发器的全部连接共用一个（每个方向一个）：「这条隧道最多占多少带宽」是转发器的事，不是单条连接的 ——
/// 否则开十条连接就是十倍。在搬运循环里、写到另一头之前等：等的时候不再读，背压自然传回发送方。
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "闸门只用 WaitAsync，没有访问 AvailableWaitHandle，不持有系统句柄。")]
internal sealed class ByteRateLimiter
{
    private readonly long _bytesPerSecond;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private double _available;
    private long _last;

    public ByteRateLimiter(long bytesPerSecond, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerSecond, 1);
        _bytesPerSecond = bytesPerSecond;
        _time = time;
        _available = bytesPerSecond;
        _last = time.GetTimestamp();
        MaxBurst = (int)Math.Min(bytesPerSecond, int.MaxValue);
    }

    /// <summary>一次最多放行多少字节：一秒的额度。</summary>
    public int MaxBurst { get; }

    /// <summary>
    /// 想搬 <paramref name="wanted"/> 字节：至多放行一秒的额度，额度不够就等到够为止；返回这一次放行了多少（1 到 <paramref name="wanted"/>）。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/07 §5.1〕一次读到的块比一秒额度大时，调用方只写出放行的那一段、剩下的下一轮再要 ——
    /// 曾经整块记账、整块等完再整块写出：64 KB/s 的隧道一次读到 256 KB（机器忙时通道里攒下的），先什么都不写、等满三秒，
    /// 再一口气写出 256 KB —— 平均速率对，单次突发却是额度的四倍，面板上的计数也跟着三秒一跳。
    /// </remarks>
    public async ValueTask<int> AcquireAsync(int wanted, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wanted, 1);
        int granted = Math.Min(wanted, MaxBurst);
        // 只有队首等时钟；取消时让下一条连接直接按当前余额算，无需撤销预扣或改排后续定时器。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long now = _time.GetTimestamp();
                double elapsed = _time.GetElapsedTime(_last, now).TotalSeconds;
                _last = now;
                _available = Math.Min(_bytesPerSecond, _available + (elapsed * _bytesPerSecond));
                if (_available >= granted)
                {
                    _available -= granted;
                    return granted;
                }

                // Task.Delay 按整毫秒计时，向上取整，避免不足一毫秒时空转。
                double milliseconds = Math.Ceiling((granted - _available) * 1000 / _bytesPerSecond);
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
