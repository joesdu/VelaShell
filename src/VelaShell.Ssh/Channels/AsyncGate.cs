// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.Ssh.Channels;

/// <summary>「有变化了」的异步通知。</summary>
/// <remarks>
/// <para>
/// 用法**必须**是「先取票，再检查条件，最后等票」：
/// </para>
/// <code>
/// while (true)
/// {
///     Task ticket = gate.NextChange();   // ← 先取
///     if (Condition()) break;            // ← 再查
///     await ticket;                      // ← 后等
/// }
/// </code>
/// <para>
/// 顺序反过来就会丢通知：检查与等待之间发生的 <see cref="Signal"/>
/// 不会唤醒任何人，而下一次通知可能永远不来。
/// 症状是**挂死**，而挂死是一个协议库最该防的失败模式 ——
/// 它没有堆栈、没有日志，只有一个再也不返回的 <c>await</c>。
/// </para>
/// <para>
/// 用 <see cref="SemaphoreSlim"/> 做同样的事需要在「放多少票」上做精确记账，
/// 放多了会让等待方空转，放少了会挂死。这里不需要记账：
/// 每次 <see cref="Signal"/> 换一张新票，旧票全部兑现。
/// </para>
/// </remarks>
internal sealed class AsyncGate
{
    private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>取一张票。它会在下一次 <see cref="Signal"/> 时兑现。</summary>
    public Task NextChange() => Volatile.Read(ref _current).Task;

    /// <summary>通知所有持票人，并换发新票。</summary>
    public void Signal()
    {
        TaskCompletionSource previous = Interlocked.Exchange(
            ref _current, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }
}
