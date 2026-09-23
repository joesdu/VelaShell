// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 3 节「Window Hierarchy」、「CreateWindow」一节
//   (属性默认值、InputOnly 的限制)、「GetWindowAttributes」(map-state)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Windowing;

/// <summary>窗口上的一个属性(ChangeProperty 存进来的东西)。</summary>
/// <param name="Type">类型原子。</param>
/// <param name="Format">8 / 16 / 32。</param>
/// <param name="Data">原始字节 —— 按<b>存进来的那个客户端的字节序</b>规整成本机序存放,取出时再按取的人的字节序写出。</param>
internal sealed record XProperty(uint Type, byte Format, byte[] Data);

/// <summary>一个窗口。</summary>
/// <remarks>
/// 坐标约定:<see cref="X" />/<see cref="Y" /> 是相对父窗口内区(不含边框)的位置,指的是本窗口<b>外框</b>
/// (含边框)的左上角 —— 与协议一致。内区原点 = 外框左上角 + 边框宽。
/// </remarks>
internal sealed class XWindow : XResource
{
    public XWindow(uint id, XClient? owner, XWindow? parent) : base(id, owner) => Parent = parent;

    public XWindow? Parent { get; set; }

    /// <summary>子窗口,<b>从下到上</b>的堆叠顺序(最后一个在最上面)。</summary>
    public List<XWindow> Children { get; } = [];

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int BorderWidth { get; set; }

    /// <summary>1 InputOutput,2 InputOnly。</summary>
    public ushort Class { get; set; } = 1;

    public bool IsInputOnly => Class == 2;

    public byte Depth { get; set; }

    public uint Visual { get; set; }

    /// <summary>背景:None(0,不画)、ParentRelative(1)或像素图 ID;为 <see cref="BackgroundPixmapNone" /> 时看 <see cref="BackgroundPixel" />。</summary>
    public uint BackgroundPixmap { get; set; }

    public const uint BackgroundPixmapNone = 0;
    public const uint BackgroundParentRelative = 1;

    /// <summary>实色背景;null 且 BackgroundPixmap = None 时背景为 None(不画)。</summary>
    public uint? BackgroundPixel { get; set; }

    public XPixmap? BackgroundTile { get; set; }

    public uint BorderPixel { get; set; }

    public XPixmap? BorderTile { get; set; }

    public byte BitGravity { get; set; }

    public byte WinGravity { get; set; } = 1;

    public byte BackingStore { get; set; }

    public uint BackingPlanes { get; set; } = 0xFFFFFFFF;

    public uint BackingPixel { get; set; }

    public bool OverrideRedirect { get; set; }

    public bool SaveUnder { get; set; }

    public ushort DoNotPropagateMask { get; set; }

    public uint Colormap { get; set; }

    public XCursor? Cursor { get; set; }

    public bool Mapped { get; set; }

    /// <summary>各客户端在这个窗口上选择的事件。</summary>
    public Dictionary<XClient, uint> EventSelections { get; } = [];

    public Dictionary<uint, XProperty> Properties { get; } = [];

    /// <summary>顶层窗口(根的直接子窗口)的像素缓冲;子窗口画在所属顶层的缓冲里(架构 §6)。</summary>
    public PixelBuffer? Buffer { get; set; }

    /// <summary>被动按钮抓取(GrabButton)。</summary>
    public List<Input.PassiveGrab> ButtonGrabs { get; } = [];

    /// <summary>被动按键抓取(GrabKey)。</summary>
    public List<Input.PassiveGrab> KeyGrabs { get; } = [];

    public bool IsRoot => Parent is null;

    public bool IsTopLevel => Parent is { IsRoot: true };

    /// <summary>所有客户端在这个窗口上选择的事件的并集(GetWindowAttributes 的 all-event-masks)。</summary>
    public uint AllEventMasks
    {
        get
        {
            uint all = 0;
            foreach (uint mask in EventSelections.Values)
            {
                all |= mask;
            }
            return all;
        }
    }

    /// <summary>可见 = 自己与全部祖先都已映射(根总是映射的)。</summary>
    public bool IsViewable
    {
        get
        {
            for (XWindow? w = this; w is not null; w = w.Parent)
            {
                if (!w.Mapped && !w.IsRoot)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>GetWindowAttributes 的 map-state:0 Unmapped,1 Unviewable,2 Viewable。</summary>
    public byte MapState => !Mapped ? (byte)0 : IsViewable ? (byte)2 : (byte)1;

    /// <summary>所属顶层窗口;根自己返回 null。</summary>
    public XWindow? TopLevel
    {
        get
        {
            XWindow? w = this;
            while (w is { IsRoot: false, IsTopLevel: false })
            {
                w = w.Parent;
            }
            return w is { IsTopLevel: true } ? w : null;
        }
    }

    /// <summary>内区原点在根坐标系里的位置。</summary>
    public (int X, int Y) AbsoluteInner()
    {
        int x = 0, y = 0;
        for (XWindow w = this; w.Parent is not null; w = w.Parent)
        {
            x += w.X + w.BorderWidth;
            y += w.Y + w.BorderWidth;
        }
        return (x, y);
    }

    /// <summary>
    /// 内区原点在所属顶层缓冲里的位置。顶层缓冲只含顶层的内区(边框由宿主的原生窗口替代),所以顶层自己是 (0, 0)。
    /// </summary>
    public (int X, int Y) OffsetInTopLevel()
    {
        int x = 0, y = 0;
        for (XWindow w = this; w is { IsRoot: false, IsTopLevel: false }; w = w.Parent!)
        {
            x += w.X + w.BorderWidth;
            y += w.Y + w.BorderWidth;
        }
        return (x, y);
    }

    /// <summary>是不是 <paramref name="ancestor" /> 的后代(不含自己)。</summary>
    public bool IsDescendantOf(XWindow ancestor)
    {
        for (XWindow? w = Parent; w is not null; w = w.Parent)
        {
            if (ReferenceEquals(w, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>这个客户端有没有选这些事件。</summary>
    public bool Selects(XClient client, XEventMask mask) =>
        EventSelections.TryGetValue(client, out uint m) && (m & (uint)mask) != 0;

    /// <summary>有没有任何客户端选了这些事件。</summary>
    public bool AnySelects(XEventMask mask) => (AllEventMasks & (uint)mask) != 0;
}
