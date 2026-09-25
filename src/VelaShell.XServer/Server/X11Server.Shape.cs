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
                    Region source = new();
                    while (r.Remaining >= 8)
                    {
                        source.Union(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
                    }
                    ApplyShape(window, op, kind, source.Translate(dx, dy));
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
                    XPixmap pixmap = Lookup<XPixmap>(pixmapId) ?? throw new XProtocolError(XErrorCode.Pixmap, pixmapId);
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
                    // 源窗口的形状换到目标窗口的坐标系:两者内区原点之差。
                    (int sx, int sy) = source.AbsoluteInner();
                    (int tx, int ty) = window.AbsoluteInner();
                    Region region = EffectiveShape(source, sourceKind).Translate(sx - tx + dx, sy - ty + dy);
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
        SetShape(window, kind, result);
    }

    /// <summary>设形状,重画受影响的区域,通知宿主与选了 ShapeNotify 的客户端。</summary>
    private void SetShape(XWindow window, byte kind, Region? shape)
    {
        Region before = window.IsViewable && !window.IsTopLevel ? VisibleOuter(window) : new Region();
        switch (kind)
        {
            case ShapeBounding: window.BoundingShape = shape; break;
            case ShapeClip: window.ClipShape = shape; break;
            case ShapeInput: window.InputShape = shape; break;
            default: throw new XProtocolError(XErrorCode.Value, kind);
        }
        InvalidateVisibility();

        if (window.IsViewable && window.TopLevel is { Buffer: { } buffer } top)
        {
            Region redraw = window.IsTopLevel ? new Region(buffer.Bounds) : before.Union(VisibleOuter(window));
            ExposeWindowTree(top, redraw);
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

    /// <summary>深度 1 位图里为 1 的像素组成的区域(逐行合并连续的一段)。</summary>
    private static Region RegionFromBitmap(PixelBuffer bitmap)
    {
        Region region = new();
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
                    region.Union(new XRect(start, y, x - start, 1));
                }
            }
        }
        return region;
    }
}
