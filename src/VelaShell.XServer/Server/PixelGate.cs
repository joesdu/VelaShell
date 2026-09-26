// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Diagnostics;

namespace VelaShell.XServer.Server;

/// <summary>
/// 顶层像素的锁(执行线程执行一批工作项期间持有它),外加「宿主正在等它」的计数。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要计数:<c>lock</c> 不公平。执行线程放锁之后几微秒内就会为下一批再拿,而被唤醒的宿主线程要先从内核里醒过来 ——
/// 负载重的时候(整窗 PutImage 一帧接一帧)宿主的 UI 线程会在读像素时被饿上几十毫秒,整个宿主界面跟着卡。
/// 有了计数,执行线程每执行完一项就看一眼:有人在等就提前放锁,并且等它读完再拿。
/// </para>
/// <para>
/// 宿主读像素只经 <see cref="XTopLevelWindow" /> 走这里,锁本身不对外公开。
/// </para>
/// </remarks>
internal sealed class PixelGate
{
    /// <summary>执行线程为让行最多等这么久:宿主一直在读(或者读的时候卡住了)也不能把执行线程拖死。</summary>
    private static readonly long MaxYieldTicks = Stopwatch.Frequency / 50;   // 20 毫秒

    private int _waiting;

    public object Lock { get; } = new();

    /// <summary>有宿主线程在等锁或正持有锁读像素。</summary>
    public bool HostWaiting => Volatile.Read(ref _waiting) != 0;

    /// <summary>宿主线程拿锁(先登记「在等」,执行线程据此让行)。与 <see cref="ExitHost" /> 成对调用。</summary>
    public void EnterHost()
    {
        Interlocked.Increment(ref _waiting);
        bool taken = false;
        try
        {
            Monitor.Enter(Lock, ref taken);
        }
        finally
        {
            if (!taken)
            {
                Interlocked.Decrement(ref _waiting);
            }
        }
    }

    public void ExitHost()
    {
        Monitor.Exit(Lock);
        Interlocked.Decrement(ref _waiting);
    }

    /// <summary>执行线程拿锁之前调:有宿主在等,就先等它读完(有上限)。</summary>
    public void YieldToHost()
    {
        if (!HostWaiting)
        {
            return;
        }
        long giveUp = Stopwatch.GetTimestamp() + MaxYieldTicks;
        SpinWait spin = default;
        while (HostWaiting && Stopwatch.GetTimestamp() < giveUp)
        {
            spin.SpinOnce();
        }
    }
}
