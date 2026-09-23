// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §8(接收循环与发送泵上不执行使用者的代码)

namespace VelaShell.Ssh.Session;

/// <summary>收尾时用到的小工具。</summary>
internal static class Lifecycle
{
    /// <summary>
    /// 取消一个令牌源，<b>回调放到线程池上执行</b>；已经释放过就什么都不做。
    /// </summary>
    /// <returns>回调执行完时完成的任务；源已释放时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <see cref="CancellationTokenSource.Cancel()"/> 在<b>调用它的线程上同步</b>执行所有回调。
    /// 本库的收尾动作常常发生在接收循环或发送泵上，而令牌上挂着的回调会唤醒
    /// 一串同步完成的续体，最后一直走到使用者的 <c>await</c> 之后 ——
    /// 使用者在那里同步阻塞一下，接收循环就被挂住了。
    /// </para>
    /// <para>
    /// <see cref="CancellationTokenSource.CancelAsync"/> 当场把令牌置为已取消
    /// （<see cref="CancellationToken.IsCancellationRequested"/> 立刻为真），
    /// 回调则在线程池上执行 —— 这正是这里要的。
    /// </para>
    /// </remarks>
    public static Task? CancelInBackground(CancellationTokenSource source)
    {
        try
        {
            Task pending = source.CancelAsync();
            _ = ObserveAsync(pending);
            return pending;
        }
        catch (ObjectDisposedException)
        {
            return null;   // 已经释放过了
        }
    }

    /// <summary>回调里抛出的异常不该变成未观察的任务异常 —— 那不是收尾路径的问题。</summary>
    private static async Task ObserveAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 某个登记的回调抛的。
        }
    }
}
