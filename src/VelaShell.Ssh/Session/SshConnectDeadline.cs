// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3(裁决独立计时);velashell-docs/zh/ssh/spec/08-failures.md §4

namespace VelaShell.Ssh.Session;

/// <summary>
/// 一把可以<b>停表</b>的连接计时器。
/// </summary>
/// <remarks>
/// <para>
/// 连接超时是为网络往返设计的：拨号、版本交换、密钥交换都应该在它之内完成。
/// 但密钥交换中间夹着一段<b>等人</b>的时间 —— 首次连接要弹窗问用户认不认这把主机密钥。
/// 那段时间算进连接超时的话，用户看了十秒指纹再点「信任」，这一轮已经被判超时，
/// 而「永久信任」的那次持久化也拿到一个已取消的令牌，根本没存下来。
/// </para>
/// <para>
/// 所以裁决期间停表（<see cref="Pause"/> / <see cref="Resume"/>），
/// 裁决自己另有 <c>HostKeyDecisionTimeout</c>。
/// </para>
/// <para>
/// <b>停表要一路往外传</b>：经跳板连接时，里面那一跳的整个建连都跑在外面那一跳的
/// 拨号计时之内；里面在等人时外面也得停，不然外层会在用户还在看指纹时判超时。
/// </para>
/// <para>线程安全：停表与续表可能来自不同线程（策略回调在哪个线程返回不由我们决定）。</para>
/// </remarks>
internal sealed class SshConnectDeadline : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly SshConnectDeadline? _outer;
    private readonly Lock _lock = new();
    private readonly bool _infinite;

    private TimeSpan _remaining;
    private long _runningSinceTicks;
    private int _pauseDepth;

    /// <summary>建一把计时器并立刻开始走。</summary>
    /// <param name="budget">总时长；<see cref="Timeout.InfiniteTimeSpan"/> 表示不限时。</param>
    /// <param name="cancellationToken">调用方的取消（不停表，始终生效）。</param>
    /// <param name="outer">外层计时器（经跳板时）；停表会一并传给它。</param>
    public SshConnectDeadline(TimeSpan budget, CancellationToken cancellationToken, SshConnectDeadline? outer = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _outer = outer;
        _infinite = budget == Timeout.InfiniteTimeSpan;
        _remaining = budget;
        _runningSinceTicks = Environment.TickCount64;

        if (!_infinite)
        {
            _cts.CancelAfter(budget);
        }
    }

    /// <summary>计时器到点或调用方取消时被取消的令牌。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>停表。可以嵌套；每次 <see cref="Pause"/> 都要配一次 <see cref="Resume"/>。</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_pauseDepth++ == 0 && !_infinite && !_cts.IsCancellationRequested)
            {
                long elapsed = Environment.TickCount64 - _runningSinceTicks;
                _remaining = TimeSpan.FromMilliseconds(Math.Max(0, _remaining.TotalMilliseconds - elapsed));
                TryCancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        _outer?.Pause();
    }

    /// <summary>续表：从停表时剩下的时间接着走。</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_pauseDepth > 0 && --_pauseDepth == 0 && !_infinite && !_cts.IsCancellationRequested)
            {
                _runningSinceTicks = Environment.TickCount64;
                TryCancelAfter(_remaining);
            }
        }

        _outer?.Resume();
    }

    private void TryCancelAfter(TimeSpan delay)
    {
        try
        {
            _cts.CancelAfter(delay);
        }
        catch (ObjectDisposedException)
        {
            // 建连已经结束了 —— 没有表可停了。
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();
}
