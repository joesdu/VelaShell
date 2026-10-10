// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   freedesktop 的 System Tray Protocol Specification 0.3 —— 「Locating the system tray」(管理器选区 _NET_SYSTEM_TRAY_Sn)、
//   「Opcode messages」(_NET_SYSTEM_TRAY_OPCODE、SYSTEM_TRAY_REQUEST_DOCK)、「Docking a tray icon」、「Tray manager hints」
//   (_NET_SYSTEM_TRAY_ORIENTATION、_NET_SYSTEM_TRAY_VISUAL)
//   freedesktop 的 XEmbed Protocol Specification 0.5 —— 「Embedding life cycle」(_XEMBED_INFO 的 version 与 XEMBED_MAPPED、
//   协议的三种结束方式)、「Message Specifications」(_XEMBED 消息、XEMBED_EMBEDDED_NOTIFY 的 data1 / data2)
//   ICCCM §2.8(管理器选区与 MANAGER 消息)

using System.Buffers.Binary;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

/// <summary>
/// 系统托盘(F12):开着 <see cref="X11ServerOptions.SystemTray" /> 时服务端占住 <c>_NET_SYSTEM_TRAY_S0</c>,当托盘管理器。
/// X 程序请求停靠的图标窗口按 XEmbed 嵌进服务端自己的一个嵌入窗口(一个不交给宿主当普通窗口的顶层,尺寸见
/// <see cref="X11ServerOptions.SystemTrayIconSize" />),宿主经 <see cref="IX11ServerHost.SystemTrayIconAdded" /> 拿到它的句柄:
/// 照常读像素(画成宿主的托盘图标)、收损伤、往里注入指针。图标窗口销毁、被程序挪出去、程序断开时嵌入窗口随之收掉
/// (<see cref="IX11ServerHost.SystemTrayIconRemoved" />)。没占托盘时 GtkStatusIcon、Java 的 SystemTray、Qt 的托盘都说没有托盘。
/// </summary>
public sealed partial class X11Server
{
    /// <summary>嵌入窗口的编号从这里起(服务端自己的资源段,在根窗口之后、客户端的资源基址之前)。</summary>
    private const uint FirstTrayEmbedderId = 0x1000;

    /// <summary>同一时刻最多停靠这么多个图标(多的不接,程序照样运行,只是没有托盘图标)。</summary>
    internal const int MaxTrayIcons = 64;

    /// <summary>停靠着的一个图标:X 程序的图标窗口与服务端的嵌入窗口。</summary>
    private sealed record TrayDock(XWindow Icon, XWindow Embedder);

    private readonly Dictionary<XWindow, TrayDock> _trayByIcon = [];
    private readonly Dictionary<XWindow, TrayDock> _trayByEmbedder = [];
    private uint _nextTrayEmbedderId = FirstTrayEmbedderId;

    /// <summary>开着托盘时占住管理器选区、写托盘管理器的属性(在 <see cref="InitXSettings" /> 之后调)。</summary>
    private void InitSystemTray()
    {
        if (!_options.SystemTray || Rootful)   // 单窗口模式:托盘归远端桌面的面板
        {
            return;
        }
        uint selection = Intern("_NET_SYSTEM_TRAY_S0");
        _selections[new SelectionSlot(selection, null)] = (SelectionWindow, null, 0);
        byte[] horizontal = new byte[4];   // _NET_SYSTEM_TRAY_ORIENTATION_HORZ
        StoreServerProperty(SelectionWindow, Intern("_NET_SYSTEM_TRAY_ORIENTATION"), new XProperty(XAtom.Cardinal, 32, horizontal));
        byte[] visual = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(visual, RootVisualId);   // 屏幕的默认视觉(规范:要么是默认视觉,要么是 TrueColor)
        StoreServerProperty(SelectionWindow, Intern("_NET_SYSTEM_TRAY_VISUAL"), new XProperty(XAtom.VisualId, 32, visual));
    }

    /// <summary>这是托盘图标的嵌入窗口吗(映射时不当普通顶层交给宿主)。</summary>
    private bool IsTrayEmbedder(XWindow window) => _trayByEmbedder.ContainsKey(window);

    /// <summary>
    /// 发给服务端选区窗口(托盘管理器选区的属主窗口)的 <c>_NET_SYSTEM_TRAY_OPCODE</c>:是就处理并返回 true。只认 SYSTEM_TRAY_REQUEST_DOCK;
    /// 气泡消息(BEGIN / CANCEL_MESSAGE)收下不显示。
    /// </summary>
    private bool OnSystemTrayClientMessage(byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.ClientMessage || raw[1] != 32 || !_options.SystemTray)
        {
            return false;
        }
        uint Read(int offset) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
        if (Read(8) != Intern("_NET_SYSTEM_TRAY_OPCODE"))
        {
            return false;
        }
        if (Read(16) == 0 && _resources.GetValueOrDefault(Read(20)) is XWindow icon)   // SYSTEM_TRAY_REQUEST_DOCK,l[2] 是图标窗口
        {
            DockTrayIcon(icon);
        }
        return true;
    }

    /// <summary>
    /// 停靠一个图标:建嵌入窗口、把图标窗口 reparent 进去并撑满,按 <c>_XEMBED_INFO</c> 的 XEMBED_MAPPED 映射它,发 XEMBED_EMBEDDED_NOTIFY,
    /// 再映射嵌入窗口(宿主收到 <see cref="IX11ServerHost.SystemTrayIconAdded" />)。
    /// </summary>
    private void DockTrayIcon(XWindow icon)
    {
        if (icon.IsRoot || icon.Owner is not { Closed: false } owner || IsServerWindow(icon) || _trayByIcon.ContainsKey(icon)
            || _trayByIcon.Count >= MaxTrayIcons || icon.IsInputOnly)
        {
            return;
        }
        int size = _options.SystemTrayIconSize;
        uint id = NextTrayEmbedderId();
        XWindow embedder = new(id, null, Root)
        {
            Class = 1,   // InputOutput
            Width = size,
            Height = size,
            X = Math.Max(0, Root.Width - size),   // 摆在屏幕右下角:程序按图标的根坐标弹菜单,弹在托盘那一带
            Y = Math.Max(0, Root.Height - size),
            Depth = icon.Depth,
            Visual = icon.Visual,
            OverrideRedirect = true,
        };
        _resources[id] = embedder;
        Root.Children.Add(embedder);
        TrayDock dock = new(icon, embedder);
        _trayByIcon[icon] = dock;
        _trayByEmbedder[embedder] = dock;

        Reparent(icon, embedder, 0, 0, requester: null);
        Configure(icon, 0, 0, size, size, 0, null, -1);
        if (TrayIconWantsMapping(icon))
        {
            Map(null, icon);
        }
        uint xembed = Intern("_XEMBED");
        uint now = Now;
        owner.Event(XEventCode.ClientMessage, 32, w => w.U32(icon.Id).U32(xembed)
            .U32(now).U32(0).U32(0).U32(embedder.Id).U32(0), sent: true);   // XEMBED_EMBEDDED_NOTIFY,data1 = 嵌入窗口,data2 = 版本 0
        Map(null, embedder);
    }

    private uint NextTrayEmbedderId()
    {
        while (_resources.ContainsKey(_nextTrayEmbedderId) || _nextTrayEmbedderId >= RootWindowId + 0x100000)
        {
            _nextTrayEmbedderId = _nextTrayEmbedderId >= RootWindowId + 0x100000 ? FirstTrayEmbedderId : _nextTrayEmbedderId + 1;
        }
        return _nextTrayEmbedderId++;
    }

    /// <summary>
    /// <c>_XEMBED_INFO</c> 的 flags 里有 XEMBED_MAPPED(规范:嵌入方跟着这一位映射 / 取消映射图标窗口)。没有这个属性的老程序当作要映射 ——
    /// 它们本来就在请求停靠前后自己映射窗口。
    /// </summary>
    private bool TrayIconWantsMapping(XWindow icon) =>
        !icon.Properties.TryGetValue(Intern("_XEMBED_INFO"), out XProperty? info) || info.Format != 32 || info.Length < 8
        || (BinaryPrimitives.ReadUInt32LittleEndian(info.Data[4..]) & 1) != 0;

    /// <summary>停靠着的图标改了 <c>_XEMBED_INFO</c>:按 XEMBED_MAPPED 映射或取消映射它(从改属性的路径上调)。</summary>
    private void OnTrayIconPropertyChanged(XWindow window, uint property)
    {
        if (_trayByIcon.Count == 0 || property != Intern("_XEMBED_INFO") || !_trayByIcon.ContainsKey(window))
        {
            return;
        }
        if (TrayIconWantsMapping(window))
        {
            Map(null, window);
        }
        else
        {
            Unmap(window);
        }
    }

    /// <summary>停靠着的图标被程序 reparent 到别处(规范:协议到此结束):收掉嵌入窗口。</summary>
    private void OnTrayIconReparented(XWindow window, XWindow parent)
    {
        if (_trayByIcon.TryGetValue(window, out TrayDock? dock) && !ReferenceEquals(parent, dock.Embedder))
        {
            Undock(dock);
        }
    }

    /// <summary>窗口销毁时:是停靠着的图标就收掉它的嵌入窗口(放到销毁做完之后,别在销毁一棵树的半中间改窗口树)。</summary>
    private void CleanupSystemTray(XWindow window)
    {
        if (_trayByIcon.TryGetValue(window, out TrayDock? dock))
        {
            _trayByIcon.Remove(window);
            Post(null, () => Undock(dock));
        }
    }

    /// <summary>收掉一个嵌入窗口:图标还在就挪回根窗口(规范的第一种结束方式),嵌入窗口取消映射(宿主收到 SystemTrayIconRemoved)并释放。</summary>
    private void Undock(TrayDock dock)
    {
        _trayByIcon.Remove(dock.Icon);   // 先摘掉:下面挪回根窗口时 reparent 的钩子不再当它是停靠着的
        if (!_trayByEmbedder.ContainsKey(dock.Embedder))
        {
            return;
        }
        XWindow embedder = dock.Embedder;
        if (ReferenceEquals(dock.Icon.Parent, embedder) && _resources.ContainsKey(dock.Icon.Id))
        {
            Unmap(dock.Icon);
            Reparent(dock.Icon, Root, 0, 0, requester: null);
        }
        Unmap(embedder);   // 还在表里:取消映射交给宿主的是 SystemTrayIconRemoved
        _trayByEmbedder.Remove(embedder);
        foreach (XWindow child in embedder.Children.ToArray())
        {
            Reparent(child, Root, 0, 0, requester: null);
        }
        RemoveResource(embedder.Id);
        Root.Children.Remove(embedder);
        if (_topLevelHandles.Remove(embedder, out XTopLevelWindow? handle))
        {
            RetireHandle(handle);
        }
        embedder.Buffer = null;
        InvalidateVisibility();
        UpdateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>托盘图标给宿主的名字:图标窗口的 <c>_NET_WM_NAME</c>(UTF-8),没有退到 WM_NAME,再退到 WM_CLASS 的类名。</summary>
    private string TrayIconTitle(XWindow embedder)
    {
        if (!_trayByEmbedder.TryGetValue(embedder, out TrayDock? dock))
        {
            return "";
        }
        XWindow icon = dock.Icon;
        if (icon.Properties.TryGetValue(Intern("_NET_WM_NAME"), out XProperty? netName) && netName.Format == 8 && netName.Length > 0)
        {
            return System.Text.Encoding.UTF8.GetString(netName.Data);
        }
        if (icon.Properties.TryGetValue(XAtom.WmName, out XProperty? name) && name.Format == 8 && name.Length > 0)
        {
            return System.Text.Encoding.Latin1.GetString(name.Data);
        }
        if (icon.Properties.TryGetValue(XAtom.WmClass, out XProperty? wmClass) && wmClass.Format == 8)
        {
            string[] parts = System.Text.Encoding.Latin1.GetString(wmClass.Data).Split('\0');
            return parts.Length > 1 ? parts[1] : parts[0];
        }
        return "";
    }
}
