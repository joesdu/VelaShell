// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发器触发事件、记 metrics 的地方。</summary>
internal static class ForwardEvents
{
    /// <summary>metrics 的 <c>direction</c> 标签：本机送进隧道。</summary>
    public static readonly KeyValuePair<string, object?> DirectionSent = new("direction", "sent");

    /// <summary>metrics 的 <c>direction</c> 标签：从隧道收回本机。</summary>
    public static readonly KeyValuePair<string, object?> DirectionReceived = new("direction", "received");

    /// <summary>metrics 的 <c>kind</c> 标签。</summary>
    public static KeyValuePair<string, object?> KindTag(ForwardKind kind) => new("kind", kind.ToString());

    /// <summary>记一笔转发错误（只计数，不发事件）。</summary>
    public static void RecordError(ForwardKind kind, ForwardErrorReason reason) =>
        ForwardMetrics.Errors.Add(1, KindTag(kind), new KeyValuePair<string, object?>("reason", reason.ToString()));

    /// <summary>触发一个事件；订阅者抛的异常吞掉。</summary>
    /// <remarks>
    /// 事件是给面板刷新用的，订阅者的一个 bug 不该变成转发的故障：曾经 <c>ConnectionOpened</c>
    /// 的订阅者一抛，那条连接就不搬了，活跃连接数也只加不减。
    /// 逐个订阅者调用，一个抛了不影响后面的。
    /// </remarks>
    public static void Raise<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<TArgs> subscriber in handler.GetInvocationList().Cast<EventHandler<TArgs>>())
        {
            try
            {
                subscriber(sender, args);
            }
            catch (Exception)
            {
                // 见说明。
            }
        }
    }
}
