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

    /// <summary>
    /// 各显示器的编号(0 … <see cref="MaxMonitors" /> − 1,与 <see cref="_monitors" /> 一一对应):RANDR 的 CRTC / 输出 ID 是基数加它。
    /// 按名字沿用 —— 原先按下标分配,拔掉一台之后它后面的 ID 全部前移,客户端手里的输出 ID 指向了另一台显示器。
    /// </summary>
    private int[] _monitorSlots = [0];

    /// <summary>名字 → 它上次用的编号(拔掉的显示器插回来还是原来的 ID,除非那个编号已经给了别的)。</summary>
    private readonly Dictionary<string, int> _slotByName = [];

    private void InitMonitors()
    {
        _monitors = NormalizeMonitors(_options.Monitors, Root.Width, Root.Height, "options");
        AssignMonitorSlots();
    }

    /// <summary>给 <see cref="_monitors" /> 分编号:名字认识且编号没被占的沿用;其余取空着的编号,先取没有名字记着的。</summary>
    private void AssignMonitorSlots()
    {
        int[] slots = new int[_monitors.Count];
        bool[] used = new bool[MaxMonitors];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = _slotByName.TryGetValue(_monitors[i].Name, out int s) && !used[s] ? s : -1;
            if (slots[i] >= 0)
            {
                used[slots[i]] = true;
            }
        }
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] >= 0)
            {
                continue;
            }
            int free = -1;
            for (int s = 0; s < MaxMonitors && free < 0; s++)
            {
                if (!used[s] && !_slotByName.ContainsValue(s))
                {
                    free = s;
                }
            }
            for (int s = 0; s < MaxMonitors && free < 0; s++)
            {
                if (!used[s])
                {
                    free = s;
                }
            }
            slots[i] = free;
            used[free] = true;
            foreach (string stale in _slotByName.Where(p => p.Value == free).Select(p => p.Key).ToArray())
            {
                _slotByName.Remove(stale);
            }
            _slotByName[_monitors[i].Name] = free;
        }
        _monitorSlots = slots;
    }

    /// <summary>ID(<paramref name="idBase" /> + 编号)对应的显示器下标;不是现有显示器的为 −1。</summary>
    private int MonitorIndexOf(uint id, uint idBase)
    {
        long slot = (long)id - idBase;
        return slot is >= 0 and < MaxMonitors ? Array.IndexOf(_monitorSlots, (int)slot) : -1;
    }

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
            if (m is null || m.Width <= 0 || m.Height <= 0)
            {
                throw new ArgumentException("显示器不能为 null,宽高必须为正。", paramName);
            }
            // 显示器要落在根窗口(虚拟桌面)里:RANDR 的 CRTC 与 XINERAMA 都按根窗口里的位置报,伸出去的显示器上最大化、
            // 菜单定位都会摆到根窗口外面;位置在 X 的协议里也只有 16 位。原先不查。
            if (m.X < 0 || m.Y < 0 || (long)m.X + m.Width > width || (long)m.Y + m.Height > height)
            {
                throw new ArgumentException($"显示器 {m.Name} 的矩形 ({m.X}, {m.Y}, {m.Width}×{m.Height}) 伸出了 {width}×{height} 的根窗口。", paramName);
            }
            if (m.Name is null || m.WidthMillimeters < 0 || m.HeightMillimeters < 0 || m.RefreshRate < 0)
            {
                throw new ArgumentException("显示器的名字不能为 null,物理尺寸与刷新率不能为负。", paramName);
            }
            if (m.WorkArea is { } area && (area.IsEmpty || area.Intersect(new XRect(m.X, m.Y, m.Width, m.Height)) != area))
            {
                throw new ArgumentException("显示器的工作区必须是落在显示器里的非空矩形。", paramName);
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
        AssignMonitorSlots();
        _layoutTime = Now;
        RebuildRandRModes();
        UpdateDesktopGeometry();

        if (resized)
        {
            DeliverToSelectors(Root, XEventMask.StructureNotify, c => c.Event(XEventCode.ConfigureNotify, 0, w => w
                .U32(Root.Id).U32(Root.Id).U32(0).I16(0).I16(0).U16((ushort)width).U16((ushort)height).U16(0).Bool(false)));
            SendXiDeviceChanged();   // 指针的轴范围跟着根窗口变了
            OnRootfulScreenResized();
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
