// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §4    GLOBAL_REQUEST 的应答没有 id,按发送顺序对齐
//   RFC 4254 §5.4  CHANNEL_REQUEST 的应答同样没有 id,按发送顺序对齐
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.1、§6.4;velashell-docs/zh/ssh/design/architecture.md §5.6

namespace VelaShell.Ssh.Session;

/// <summary>
/// 应答没有 id、只能按顺序对齐的那一类请求的账本。
/// </summary>
/// <typeparam name="TResult">应答的形状。</typeparam>
/// <remarks>
/// <para>
/// 全局请求与通道请求在协议里是同一种东西：应答里没有任何能指回请求的字段，
/// 唯一的对应关系是<b>先发先答</b>。两处原先各写了一遍「队列 + TCS + 断线收尾」，
/// 取消与断线的语义就要证明两次；合成一个之后只需证明一次（architecture.md §5.6）。
/// </para>
/// <para>
/// <b>登记必须在发送之前</b>：先发后登记的话，一个快到的应答会发现账本是空的，
/// 那会被当成 FIFO 失步 —— 而那是一个协议错误。
/// </para>
/// <para>
/// 调用方取消等待<b>不会</b>把项从账本里摘掉：请求已经发出去了，应答迟早会来，
/// 它必须落在这一项上，后面的应答才对得上号。
/// </para>
/// </remarks>
internal sealed class FifoRequestLedger<TResult>
{
    private readonly Queue<TaskCompletionSource<TResult>> _pending = new();
    private readonly Lock _lock = new();
    private bool _closed;
    private TResult _closedResult = default!;

    /// <summary>在途的请求数。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>登记一个即将发出的请求。</summary>
    /// <returns>应答到达（或账本关闭）时完成的任务。</returns>
    public Task<TResult> Register()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return Task.FromResult(_closedResult);
            }

            TaskCompletionSource<TResult> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(completion);
            return completion.Task;
        }
    }

    /// <summary>把一个应答交给最早的那个请求。</summary>
    /// <returns>账本里没有人在等时返回 <see langword="false"/> —— 那是 FIFO 失步。</returns>
    public bool TryComplete(TResult result)
    {
        TaskCompletionSource<TResult>? completion;
        lock (_lock)
        {
            if (!_pending.TryDequeue(out completion))
            {
                return false;
            }
        }

        completion.TrySetResult(result);
        return true;
    }

    /// <summary>关账：在途的与之后登记的请求一律以 <paramref name="result"/> 收尾。</summary>
    /// <remarks>
    /// 用一个「失败」的结果而不是异常收尾：等应答的调用方问的是「成了没有」，
    /// 而连接没了的答案就是「没成」。真正的原因由发送路径抛出的异常带着。
    /// </remarks>
    public void Close(TResult result)
    {
        List<TaskCompletionSource<TResult>> pending;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            _closedResult = result;
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (TaskCompletionSource<TResult> completion in pending)
        {
            completion.TrySetResult(result);
        }
    }
}
