// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

using System.Diagnostics.Metrics;
using System.Net;

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发的形态。</summary>
public enum ForwardKind
{
    /// <summary>本地转发（<c>-L</c>）：我们监听，出站是 <c>direct-tcpip</c>。</summary>
    Local,

    /// <summary>动态转发（<c>-D</c>）：我们监听并跑 SOCKS5，目标由客户端给出。</summary>
    Dynamic,

    /// <summary>远程转发（<c>-R</c>）：服务端监听，回连由服务端发起。</summary>
    Remote,
}

/// <summary>一条转发连接的事件。</summary>
/// <remarks>
/// 它继承 <see cref="EventArgs"/> 而不是写成 record —— C# 不允许 record
/// 继承普通类，而 <c>*EventArgs</c> 这个名字按约定就该是一个 <see cref="EventArgs"/>。
/// </remarks>
public sealed class ForwardConnectionEventArgs : EventArgs
{
    /// <summary>创建一条连接事件。</summary>
    public ForwardConnectionEventArgs(
        long connectionId,
        EndPoint? source,
        string target,
        long bytesUp = 0,
        long bytesDown = 0,
        TimeSpan duration = default)
    {
        ConnectionId = connectionId;
        Source = source;
        Target = target;
        BytesUp = bytesUp;
        BytesDown = bytesDown;
        Duration = duration;
    }

    /// <summary>这条连接在本转发器内的序号。</summary>
    public long ConnectionId { get; }

    /// <summary>来源端点。</summary>
    public EndPoint? Source { get; }

    /// <summary>目标描述（<c>host:port</c> 或套接字路径）。</summary>
    public string Target { get; }

    /// <summary>本机 → 远端的应用字节数。</summary>
    public long BytesUp { get; }

    /// <summary>远端 → 本机的应用字节数。</summary>
    public long BytesDown { get; }

    /// <summary>持续时间。</summary>
    public TimeSpan Duration { get; }
}

/// <summary>一条转发连接失败了 —— <b>转发器本身仍在跑</b>。</summary>
public sealed class ForwardErrorEventArgs : EventArgs
{
    /// <summary>创建一条错误事件。</summary>
    public ForwardErrorEventArgs(string reason, string message, Exception? exception = null)
    {
        Reason = reason;
        Message = message;
        Exception = exception;
    }

    /// <summary>简短的原因标签，也用作 metrics 的标签值。</summary>
    public string Reason { get; }

    /// <summary>给人看的说明。</summary>
    public string Message { get; }

    /// <summary>底层异常（可能为空）。</summary>
    public Exception? Exception { get; }
}

/// <summary>转发的计量仪表。</summary>
/// <remarks>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §五〕<b>事件与 Metrics 两条路都给。</b>
/// 事件给桌面界面（要实时刷一个面板），Metrics 给服务端场景（接 OpenTelemetry）。
/// 只给一条会逼使用者自己把另一条重写一遍 —— 而那正是这一层要消除的东西。
/// </para>
/// <para>
/// 字节数在<b>搬运循环里</b>累加，不在通道层：通道层的数字含协议开销，
/// 而面板上要显示的是应用数据量。
/// </para>
/// </remarks>
public static class ForwardMetrics
{
    /// <summary>仪表源的名字。</summary>
    public const string MeterName = "VelaShell.Ssh.Forwarding";

    private static readonly Meter SharedMeter = new(MeterName);

    /// <summary>当前活跃的转发连接数。</summary>
    public static UpDownCounter<long> ActiveConnections { get; } =
        SharedMeter.CreateUpDownCounter<long>(
            "velashell.ssh.forward.connections.active", "{connection}", "当前活跃的转发连接数");

    /// <summary>累计的转发连接数。</summary>
    public static Counter<long> TotalConnections { get; } =
        SharedMeter.CreateCounter<long>(
            "velashell.ssh.forward.connections.total", "{connection}", "累计的转发连接数");

    /// <summary>转发的应用字节数。</summary>
    public static Counter<long> Bytes { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.forward.bytes", "By", "转发的应用数据字节数");

    /// <summary>转发中的错误数。</summary>
    public static Counter<long> Errors { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.forward.errors", "{error}", "转发中的错误数");
}

/// <summary>转发器触发事件的地方。</summary>
internal static class ForwardEvents
{
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
