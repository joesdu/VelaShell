// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Nonrectangular Window Shape Extension Protocol, Version 1.1 —— §2「Types」(Bounding / Clip / Input 三种形状、
//   默认形状)、§4「Requests」(QueryVersion 0、Rectangles 1、Mask 2、Combine 3、Offset 4、QueryExtents 5、
//   SelectInput 6、InputSelected 7、GetRectangles 8;操作 Set / Union / Intersect / Subtract / Invert)、
//   §5「Events」(ShapeNotify)、附录「Protocol Encoding」

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private const byte ShapeBounding = 0, ShapeClip = 1, ShapeInput = 2;
    private const byte ShapeSet = 0, ShapeUnion = 1, ShapeIntersect = 2, ShapeSubtract = 3, ShapeInvert = 4;

    private void Shape(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U16(1).U16(1).Zero(20));
                break;
            case 1:   // Rectangles
                {
                    byte op = r.U8(), kind = r.U8();
                    r.U8();                    // ordering:只是提示
                    r.U8();
                    XWindow window = Window(r.U32());
                    short dx = r.I16(), dy = r.I16();
                    List<XRect> rects = [];
                    while (r.Remaining >= 8)
                    {
                        rects.Add(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
                    }
                    ApplyShape(window, op, kind, Region.FromRects(rects).Translate(dx, dy));
                    break;
                }
            case 2:   // Mask
                {
                    byte op = r.U8(), kind = r.U8();
                    r.Skip(2);
                    XWindow window = Window(r.U32());
                    short dx = r.I16(), dy = r.I16();
                    uint pixmapId = r.U32();
                    if (pixmapId == 0)
                    {
                        // 源为 None:形状回到默认(协议规定,与 op 无关)。
                        SetShape(window, kind, null);
                        break;
                    }
                    XPixmap pixmap = Use<XPixmap>(pixmapId) ?? throw new XProtocolError(XErrorCode.Pixmap, pixmapId);
                    if (pixmap.Depth != 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    ApplyShape(window, op, kind, RegionFromBitmap(pixmap.Buffer).Translate(dx, dy));
                    break;
                }
            case 3:   // Combine
                {
                    byte op = r.U8(), kind = r.U8(), sourceKind = r.U8();
                    r.U8();
                    XWindow window = Window(r.U32());
                    short dx = r.I16(), dy = r.I16();
                    XWindow source = Window(r.U32());
                    // SHAPE 规范「ShapeCombine」:源窗口的形状(相对源窗口的原点)「offset from the window origin by xOff and yOff」——
                    // 只按客户端给的偏移放进目标窗口的坐标系。原先另外加上两个窗口内区原点之差,常见的用法(把子窗口的形状并进父窗口、
                    // 偏移给子窗口的位置)偏了两倍。
                    Region region = EffectiveShape(source, sourceKind).Translate(dx, dy);
                    ApplyShape(window, op, kind, region);
                    break;
                }
            case 4:   // Offset
                {
                    byte kind = r.U8();
                    r.Skip(3);
                    XWindow window = Window(r.U32());
                    short dx = r.I16(), dy = r.I16();
                    if (GetShape(window, kind) is { } existing)
                    {
                        SetShape(window, kind, existing.Clone().Translate(dx, dy));
                    }
                    break;
                }
            case 5:   // QueryExtents
                {
                    XWindow window = Window(r.U32());
                    XRect b = EffectiveShape(window, ShapeBounding).Bounds;
                    XRect k = EffectiveShape(window, ShapeClip).Bounds;
                    c.Reply(0, w => w
                        .Bool(window.BoundingShape is not null).Bool(window.ClipShape is not null).Zero(2)
                        .I16(b.X).I16(b.Y).U16((ushort)b.Width).U16((ushort)b.Height)
                        .I16(k.X).I16(k.Y).U16((ushort)k.Width).U16((ushort)k.Height).Zero(4));
                    break;
                }
            case 6:   // SelectInput
                {
                    XWindow window = Window(r.U32());
                    if (r.U8() != 0)
                    {
                        window.ShapeSelections.Add(c);
                    }
                    else
                    {
                        window.ShapeSelections.Remove(c);
                    }
                    break;
                }
            case 7:   // InputSelected
                {
                    XWindow window = Window(r.U32());
                    c.Reply(window.ShapeSelections.Contains(c) ? (byte)1 : (byte)0, w => w.Zero(24));
                    break;
                }
            case 8:   // GetRectangles
                {
                    XWindow window = Window(r.U32());
                    byte kind = r.U8();
                    List<XRect> rects = [.. EffectiveShape(window, kind).Rects];
                    c.Reply(0, w =>   // ordering:UnSorted
                    {
                        w.U32((uint)rects.Count).Zero(20);
                        foreach (XRect rect in rects)
                        {
                            w.I16(rect.X).I16(rect.Y).U16((ushort)rect.Width).U16((ushort)rect.Height);
                        }
                    });
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private static Region? GetShape(XWindow window, byte kind) => kind switch
    {
        ShapeBounding => window.BoundingShape,
        ShapeClip => window.ClipShape,
        ShapeInput => window.InputShape,
        _ => throw new XProtocolError(XErrorCode.Value, kind),
    };

    /// <summary>生效的形状:设过就是它,否则是默认值(输入形状的默认值是边界形状)。</summary>
    private static Region EffectiveShape(XWindow window, byte kind) => kind switch
    {
        ShapeBounding => window.BoundingShape?.Clone() ?? new Region(window.DefaultBounding),
        ShapeClip => window.ClipShape?.Clone() ?? new Region(window.DefaultClip),
        ShapeInput => window.InputShape?.Clone() ?? EffectiveShape(window, ShapeBounding),
        _ => throw new XProtocolError(XErrorCode.Value, kind),
    };

    private void ApplyShape(XWindow window, byte op, byte kind, Region source)
    {
        Region dest = EffectiveShape(window, kind);
        Region result = op switch
        {
            ShapeSet => source,
            ShapeUnion => dest.Union(source),
            ShapeIntersect => dest.Intersect(source),
            ShapeSubtract => dest.Subtract(source),
            ShapeInvert => source.Subtract(dest),   // dest = source − dest
            _ => throw new XProtocolError(XErrorCode.Value, op),
        };
        SetShape(window, kind, Exact(result));
    }

    /// <summary>设形状,重画受影响的区域,通知宿主与选了 ShapeNotify 的客户端。</summary>
    private void SetShape(XWindow window, byte kind, Region? shape)
    {
        // 顶层看的是它自己画得到的部分(边界与裁剪形状都算);子窗口看它在顶层里露出来的外框。
        Region before = !window.IsViewable ? new Region() : window.IsTopLevel ? VisibleInner(window) : VisibleOuter(window);
        switch (kind)
        {
            case ShapeBounding: window.BoundingShape = shape; break;
            case ShapeClip: window.ClipShape = shape; break;
            case ShapeInput: window.InputShape = shape; break;
            default: throw new XProtocolError(XErrorCode.Value, kind);
        }
        InvalidateVisibility();

        if (window.IsViewable && window.TopLevel is { Buffer: not null } top)
        {
            if (kind != ShapeInput)
            {
                // 输入形状不改看得见的东西:不重画。顶层只重画新露出来的部分(缩小的部分宿主按形状透明掉);子窗口重画新旧并集
                // (缩小时让出来的父窗口与兄弟也要画)。原先任何形状变化都让顶层整窗重画、整窗 Expose —— 客户端随之整窗重画,
                // 经 SSH 再传一遍整窗像素(GTK 改尺寸时每次都设输入形状)。
                Region redraw = window.IsTopLevel ? VisibleInner(window).Subtract(before) : before.Union(VisibleOuter(window));
                ExposeWindowTree(top, redraw);
            }
            if (window.IsTopLevel)
            {
                RefreshTopLevel(window);
            }
        }
        if (kind == ShapeInput)
        {
            UpdatePointerWindow();
        }

        XRect extents = EffectiveShape(window, kind).Bounds;
        bool shaped = shape is not null;
        uint time = Now;
        foreach (XClient client in window.ShapeSelections)
        {
            if (!client.Closed)
            {
                client.Event(ShapeEventBase, kind, w => w   // ShapeNotify = 事件基数 + 0
                    .U32(window.Id).I16(extents.X).I16(extents.Y).U16((ushort)extents.Width).U16((ushort)extents.Height)
                    .U32(time).Bool(shaped));
            }
        }
    }

    /// <summary>
    /// 客户端要的区域(形状、XFIXES 区域、RENDER / GC 裁剪)超了 <see cref="Region.MaxRects" /> 或归并预算:回 BadAlloc ——
    /// 不拿一个近似的外接矩形冒充它要的区域。服务端内部算出来的可见区域、损伤超限时照常用近似值(多不少)。
    /// </summary>
    private static Region Exact(Region region) => region.Saturated ? throw new XProtocolError(XErrorCode.Alloc) : region;

    /// <summary>
    /// 深度 1 位图里为 1 的像素组成的区域(逐行扫出连续的一段;扫出来的本身就是分好带的,一趟建成)。
    /// 段数超过 <see cref="Region.MaxRects" />(棋盘格之类)就不再扫,返回超限的区域。
    /// </summary>
    private static Region RegionFromBitmap(PixelBuffer bitmap)
    {
        List<XRect> spans = [];
        for (int y = 0; y < bitmap.Height; y++)
        {
            int x = 0;
            while (x < bitmap.Width)
            {
                while (x < bitmap.Width && bitmap.Get(x, y) == 0)
                {
                    x++;
                }
                int start = x;
                while (x < bitmap.Width && bitmap.Get(x, y) != 0)
                {
                    x++;
                }
                if (x > start)
                {
                    spans.Add(new XRect(start, y, x - start, 1));
                    if (spans.Count > Region.MaxRects)
                    {
                        return Region.OverLimit(bitmap.Bounds);
                    }
                }
            }
        }
        return Region.FromRects(spans);
    }
}
