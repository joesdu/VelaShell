// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「ConfigureNotify」(根窗口尺寸变化同样经它通知)
//   The X Resize and Rotate Extension (RandR), Version 1.5 —— §6「Events」(RRScreenChangeNotify、
//   RRNotify 的 CrtcChange / OutputChange 子类型)
//
//   显示器布局:虚拟桌面(根窗口)的尺寸与其中每台显示器的矩形。RANDR 与 XINERAMA 都从这里取数;
//   宿主在运行中换布局(接了一台显示器、改了分辨率)时调 SetScreenLayout,服务端据此改根窗口并通知客户端。

using VelaShell.XServer.Protocol;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private IReadOnlyList<XMonitor> _monitors = [];

    /// <summary>最近一次布局变化的服务端时间(RANDR 的 timestamp 与 config-timestamp)。</summary>
    private uint _layoutTime;

    private void InitMonitors() => _monitors = NormalizeMonitors(_options.Monitors, Root.Width, Root.Height, "options");

    /// <summary>空列表 → 一台覆盖整个根窗口的显示器;没有标主显示器的 → 第一台是主显示器。</summary>
    private static IReadOnlyList<XMonitor> NormalizeMonitors(IReadOnlyList<XMonitor>? monitors, int width, int height, string paramName)
    {
        if (monitors is null || monitors.Count == 0)
        {
            return [new XMonitor(0, 0, width, height) { Primary = true }];
        }
        if (monitors.Count > MaxMonitors)
        {
            throw new ArgumentException($"最多 {MaxMonitors} 台显示器。", paramName);
        }
        foreach (XMonitor m in monitors)
        {
            if (m.Width <= 0 || m.Height <= 0)
            {
                throw new ArgumentException("显示器的宽高必须为正。", paramName);
            }
        }
        return monitors.Any(m => m.Primary) ? [.. monitors] : [monitors[0] with { Primary = true }, .. monitors.Skip(1)];
    }

    private void ApplyScreenLayout(int width, int height, IReadOnlyList<XMonitor> monitors)
    {
        bool resized = Root.Width != width || Root.Height != height;
        Root.Width = width;
        Root.Height = height;
        _monitors = monitors;
        _layoutTime = Now;
        RebuildRandRModes();
        UpdateDesktopGeometry();

        if (resized)
        {
            DeliverToSelectors(Root, XEventMask.StructureNotify, c => c.Event(XEventCode.ConfigureNotify, 0, w => w
                .U32(Root.Id).U32(Root.Id).U32(0).I16(0).I16(0).U16((ushort)width).U16((ushort)height).U16(0).Bool(false)));
        }
        NotifyRandRChange();
    }

    /// <summary>根窗口的物理尺寸(毫米):按 DPI 换算。</summary>
    private (int Width, int Height) ScreenMillimeters() => (ToMillimeters(Root.Width), ToMillimeters(Root.Height));

    private int ToMillimeters(int pixels) => (int)Math.Round(pixels * 25.4 / (_dpi > 0 ? _dpi : _options.Dpi));

    private (int Width, int Height) MonitorMillimeters(XMonitor m) =>
        (m.WidthMillimeters > 0 ? m.WidthMillimeters : ToMillimeters(m.Width), m.HeightMillimeters > 0 ? m.HeightMillimeters : ToMillimeters(m.Height));

    private int PrimaryMonitorIndex()
    {
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].Primary)
            {
                return i;
            }
        }
        return 0;
    }
}
