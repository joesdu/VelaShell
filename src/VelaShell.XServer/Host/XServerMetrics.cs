// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Diagnostics.Metrics;

namespace VelaShell.XServer;

/// <summary>
/// 服务端的计量仪表(<see cref="System.Diagnostics.Metrics" />):用户反馈「卡了一下」「程序连不上」时有数据可看。
/// 对外只交出仪表源的名字,用 <see cref="MeterListener" /> 或 OpenTelemetry 按名订阅;仪表本身是 <c>internal</c> ——
/// 交出可写的计数器等于让任何人都能往里记账(与 SSH 库的 ForwardMetrics 同一个做法)。同一个进程里的所有服务端共用这一组仪表。
/// 标签只用低基数的 <c>reason</c>、<c>code</c>,不带客户端编号或地址。
/// </summary>
public static class XServerMetrics
{
    /// <summary>仪表源的名字。</summary>
    public const string MeterName = "VelaShell.XServer";

    private static readonly Meter SharedMeter = new(MeterName);

    /// <summary>此刻连着的客户端数。</summary>
    internal static UpDownCounter<long> ActiveClients { get; } =
        SharedMeter.CreateUpDownCounter<long>("velashell.xserver.clients.active", "{client}", "此刻连着的 X 客户端数");

    /// <summary>
    /// 连接建立阶段被拒的连接;<c>reason</c>:<c>authorization</c>(授权不过)、<c>too_many_clients</c>、<c>too_many_setups</c>
    /// (同时握手的太多)、<c>bad_setup</c>(版本不对、授权数据过长)。
    /// </summary>
    internal static Counter<long> RefusedConnections { get; } =
        SharedMeter.CreateCounter<long>("velashell.xserver.connections.refused", "{connection}", "连接建立阶段被拒的连接数");

    /// <summary>服务端主动断开的客户端;<c>reason</c>:<c>killed</c>(KillClient、宿主强制结束)、<c>output_backlog</c>(客户端不读了)。</summary>
    internal static Counter<long> Disconnects { get; } =
        SharedMeter.CreateCounter<long>("velashell.xserver.clients.disconnected", "{client}", "服务端主动断开的客户端数");

    /// <summary>发给客户端的协议错误;<c>code</c> 是错误名(Window、Match、Alloc……)。</summary>
    internal static Counter<long> ProtocolErrors { get; } =
        SharedMeter.CreateCounter<long>("velashell.xserver.protocol.errors", "{error}", "发给客户端的协议错误数");

    /// <summary>执行线程上每项工作(一条请求、一次宿主调用)持像素锁的时长。</summary>
    internal static Histogram<double> WorkItemDuration { get; } =
        SharedMeter.CreateHistogram<double>("velashell.xserver.work.duration", "ms", "执行线程上每项工作的耗时");

    /// <summary>超过看门狗时限还没做完的工作项(日志里点名了客户端)。</summary>
    internal static Counter<long> StalledWorkItems { get; } =
        SharedMeter.CreateCounter<long>("velashell.xserver.work.stalled", "{item}", "超过看门狗时限还没做完的工作项数");
}
