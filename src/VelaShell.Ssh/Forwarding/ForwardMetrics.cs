// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

using System.Diagnostics.Metrics;

namespace VelaShell.Ssh.Forwarding;

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
/// <para>
/// 对外只交出仪表源的名字（用 <see cref="MeterListener"/> 或 OpenTelemetry 按名订阅）；
/// 仪表本身是 <c>internal</c> —— 交出可写的计数器等于让任何人都能往里记账。
/// 标签只有低基数的 <c>kind</c>、<c>reason</c>、<c>direction</c>，不带监听地址。
/// </para>
/// </remarks>
public static class ForwardMetrics
{
    /// <summary>仪表源的名字。</summary>
    public const string MeterName = "VelaShell.Ssh.Forwarding";

    private static readonly Meter SharedMeter = new(MeterName);

    /// <summary>当前活跃的转发连接数。</summary>
    internal static UpDownCounter<long> ActiveConnections { get; } =
        SharedMeter.CreateUpDownCounter<long>(
            "velashell.ssh.forward.connections.active", "{connection}", "当前活跃的转发连接数");

    /// <summary>累计的转发连接数。</summary>
    internal static Counter<long> TotalConnections { get; } =
        SharedMeter.CreateCounter<long>(
            "velashell.ssh.forward.connections.total", "{connection}", "累计的转发连接数");

    /// <summary>转发的应用字节数。</summary>
    internal static Counter<long> Bytes { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.forward.bytes", "By", "转发的应用数据字节数");

    /// <summary>转发中的错误数。</summary>
    internal static Counter<long> Errors { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.forward.errors", "{error}", "转发中的错误数");
}
