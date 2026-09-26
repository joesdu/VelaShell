// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 1 节「Protocol Formats」(请求按序执行)、
//   「GrabServer」(独占期间不处理其他连接的请求)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型)

using System.Diagnostics;
using System.Threading.Channels;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>执行线程一次持锁最多跑这么久,然后放锁让宿主读像素(宿主的 UI 线程在 ReadPixels 里等这把锁)。</summary>
    private static readonly long LockBudgetTicks = Stopwatch.Frequency / 250;   // 4 毫秒

    /// <summary>GrabServer 期间暂存的别的客户端的工作项。</summary>
    private readonly List<WorkItem> _deferred = [];

    private XClient? _serverGrabber;

    /// <summary>一项工作:客户端的一条请求(<see cref="Request" />),或者一段要在执行线程上跑的代码。</summary>
    private readonly record struct WorkItem(XClient? Client, Action? Action, byte[]? Request = null);

    /// <summary>把一件事排进执行线程。可以在任意线程上调。</summary>
    internal void Post(XClient? client, Action action) => _work.Writer.TryWrite(new WorkItem(client, action));

    /// <summary>把客户端的一条请求排进执行线程(不为每条请求分配闭包)。</summary>
    private void PostRequest(XClient client, byte[] request) => _work.Writer.TryWrite(new WorkItem(client, null, request));

    /// <summary>排进执行线程并等它做完(连接建立等少数需要结果的地方用)。</summary>
    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _work.Writer.TryWrite(new WorkItem(null, () =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        if (!queued)
        {
            tcs.TrySetCanceled();   // 执行循环已经收工
        }
        return tcs.Task;
    }

    /// <summary>诊断日志的唯一出口(<see cref="X11ServerOptions.Log" />)。</summary>
    private void Log(string message) => _options.Log?.Invoke(message);

    private async Task RunLoopAsync()
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                // lock 不公平:刚放锁就再拿,等着读像素的宿主线程可能一直抢不到。宿主在等就先让它读完。
                _pixelGate.YieldToHost();
                lock (_pixelGate.Lock)
                {
                    long deadline = Stopwatch.GetTimestamp() + LockBudgetTicks;
                    while (reader.TryRead(out WorkItem item))
                    {
                        RunItem(item);
                        if (Stopwatch.GetTimestamp() >= deadline || _pixelGate.HostWaiting)
                        {
                            break;
                        }
                    }
                }
                // 宿主回调一律在放锁之后调:回调里同步等 UI 线程、而 UI 线程正在 ReadPixels 里等这把锁,就是死锁。
                FlushDamage();
                _host.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
    }

    private void RunItem(WorkItem item)
    {
        // SYNC 的 Await 期间,这个客户端之后的请求暂存,条件成立时放回。
        if (_syncWaits.Count != 0 && DeferIfWaiting(item))
        {
            return;
        }
        // GrabServer 期间,别人的请求原样暂存,Ungrab 后按原顺序放回(协议「GrabServer」)。
        if (_serverGrabber is { } grabber && item.Client is { } client && !ReferenceEquals(client, grabber) && !client.Closed)
        {
            _deferred.Add(item);
            return;
        }
        try
        {
            if (item.Request is { } request)
            {
                ExecuteRequest(item.Client!, request);
            }
            else
            {
                item.Action!();
            }
        }
        catch (Exception ex)
        {
            Log($"work item failed: {ex}");
        }
    }

    /// <summary>GrabServer 结束(或持有者断开):把暂存的请求按原顺序重新排进去。</summary>
    private void ReleaseServerGrab()
    {
        _serverGrabber = null;
        List<WorkItem> pending = [.. _deferred];
        _deferred.Clear();
        foreach (WorkItem item in pending)
        {
            RunItem(item);
        }
    }
}
