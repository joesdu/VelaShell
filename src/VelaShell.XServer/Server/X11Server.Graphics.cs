// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreatePixmap」「FreePixmap」「CreateGC」「ChangeGC」「CopyGC」
//   「SetDashes」「SetClipRectangles」「FreeGC」「ClearArea」「CopyArea」(GraphicsExposure / NoExposure)
//   「CopyPlane」「PolyPoint」「PolyLine」「PolySegment」「PolyRectangle」「PolyArc」「FillPoly」
//   「PolyFillRectangle」「PolyFillArc」「PutImage」「GetImage」;
//   第 8 节「Connection Setup」里的 image-byte-order / bitmap-format-bit-order / scanline-pad(本服务端声明 LSBFirst、32 位对齐)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private XGc Gc(uint id) => Lookup<XGc>(id) ?? throw new XProtocolError(XErrorCode.GContext, id);

    /// <summary>可建像素图的深度:与连接建立回复里的 FORMAT / DEPTH 列表一致(X.Org 的惯例:1、4、8、15、16、24、32)。</summary>
    private static bool IsSupportedDepth(byte depth) => depth is 1 or 4 or 8 or 15 or 16 or 24 or 32;

    /// <summary>ZPixmap 里每像素占几位(与 FORMAT 列表一致)。</summary>
    internal static int BitsPerPixel(byte depth) => depth switch
    {
        1 => 1,
        4 or 8 => 8,
        15 or 16 => 16,
        _ => 32,
    };

    // ------------------------------------------------------------------ 像素图

    private void CreatePixmap(XClient c, XRequestReader r)
    {
        byte depth = r.Data;
        uint id = r.U32();
        uint drawable = r.U32();
        ushort width = r.U16(), height = r.U16();
        if (Lookup<XResource>(drawable) is not (XWindow or XPixmap))
        {
            throw new XProtocolError(XErrorCode.Drawable, drawable);
        }
        if (width == 0 || height == 0)
        {
            throw new XProtocolError(XErrorCode.Value, 0);
        }
        if (!IsSupportedDepth(depth))
        {
            throw new XProtocolError(XErrorCode.Value, depth);
        }
        AddResource(c, new XPixmap(id, c, width, height, depth));
    }

    private void FreePixmap(XRequestReader r)
    {
        uint id = r.U32();
        XPixmap pixmap = Lookup<XPixmap>(id) ?? throw new XProtocolError(XErrorCode.Pixmap, id);
        // 像素图被窗口背景或 GC 引用时仍然可用(协议:释放 ID,数据活到最后一个引用消失)—— 引用持有对象本身,这里只删 ID。
        RemoveResource(id);
        CleanupDamage(null, pixmap);
    }

    // ------------------------------------------------------------------ GC

    private void CreateGC(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        uint drawable = r.U32();
        byte depth = Lookup<XResource>(drawable) switch
        {
            XWindow w => w.IsRoot ? (byte)24 : w.Depth,
            XPixmap p => p.Depth,
            _ => throw new XProtocolError(XErrorCode.Drawable, drawable),
        };
        XGc gc = new(id, c, depth);
        ApplyGcValues(gc, r.U32(), r);
        AddResource(c, gc);
    }

    private void ChangeGC(XRequestReader r)
    {
        XGc gc = Gc(r.U32());
        ApplyGcValues(gc, r.U32(), r);
    }

    private void CopyGC(XRequestReader r)
    {
        XGc src = Gc(r.U32());
        XGc dst = Gc(r.U32());
        uint mask = r.U32();
        if (src.Depth != dst.Depth)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        void Copy(XGcMask bit, Action action)
        {
            if ((mask & (uint)bit) != 0)
            {
                action();
            }
        }
        Copy(XGcMask.Function, () => dst.Function = src.Function);
        Copy(XGcMask.PlaneMask, () => dst.PlaneMask = src.PlaneMask);
        Copy(XGcMask.Foreground, () => dst.Foreground = src.Foreground);
        Copy(XGcMask.Background, () => dst.Background = src.Background);
        Copy(XGcMask.LineWidth, () => dst.LineWidth = src.LineWidth);
        Copy(XGcMask.LineStyle, () => dst.LineStyle = src.LineStyle);
        Copy(XGcMask.CapStyle, () => dst.CapStyle = src.CapStyle);
        Copy(XGcMask.JoinStyle, () => dst.JoinStyle = src.JoinStyle);
        Copy(XGcMask.FillStyle, () => dst.FillStyle = src.FillStyle);
        Copy(XGcMask.FillRule, () => dst.FillRule = src.FillRule);
        Copy(XGcMask.Tile, () => dst.Tile = src.Tile);
        Copy(XGcMask.Stipple, () => dst.Stipple = src.Stipple);
        Copy(XGcMask.TileStipXOrigin, () => dst.TileStipXOrigin = src.TileStipXOrigin);
        Copy(XGcMask.TileStipYOrigin, () => dst.TileStipYOrigin = src.TileStipYOrigin);
        Copy(XGcMask.Font, () => dst.Font = src.Font);
        Copy(XGcMask.SubwindowMode, () => dst.SubwindowMode = src.SubwindowMode);
        Copy(XGcMask.GraphicsExposures, () => dst.GraphicsExposures = src.GraphicsExposures);
        Copy(XGcMask.ClipXOrigin, () => dst.ClipXOrigin = src.ClipXOrigin);
        Copy(XGcMask.ClipYOrigin, () => dst.ClipYOrigin = src.ClipYOrigin);
        Copy(XGcMask.ClipMask, () =>
        {
            dst.ClipPixmap = src.ClipPixmap;
            dst.ClipRects = src.ClipRects is null ? null : [.. src.ClipRects];
        });
        Copy(XGcMask.DashOffset, () => dst.DashOffset = src.DashOffset);
        Copy(XGcMask.Dashes, () => dst.Dashes = [.. src.Dashes]);
        Copy(XGcMask.ArcMode, () => dst.ArcMode = src.ArcMode);
    }

    private void ApplyGcValues(XGc gc, uint mask, XRequestReader r)
    {
        for (int bit = 0; bit < 23; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }
            uint v = r.U32();
            switch ((XGcMask)(1u << bit))
            {
                case XGcMask.Function: gc.Function = (byte)(v & 0xF); break;
                case XGcMask.PlaneMask: gc.PlaneMask = v; break;
                case XGcMask.Foreground: gc.Foreground = v; break;
                case XGcMask.Background: gc.Background = v; break;
                case XGcMask.LineWidth: gc.LineWidth = (ushort)v; break;
                case XGcMask.LineStyle: gc.LineStyle = (byte)v; break;
                case XGcMask.CapStyle: gc.CapStyle = (byte)v; break;
                case XGcMask.JoinStyle: gc.JoinStyle = (byte)v; break;
                case XGcMask.FillStyle: gc.FillStyle = (byte)v; break;
                case XGcMask.FillRule: gc.FillRule = (byte)v; break;
                case XGcMask.Tile:
                    XPixmap tile = Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    if (tile.Depth != gc.Depth)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    gc.Tile = tile;
                    break;
                case XGcMask.Stipple:
                    XPixmap stipple = Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    if (stipple.Depth != 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    gc.Stipple = stipple;
                    break;
                case XGcMask.TileStipXOrigin: gc.TileStipXOrigin = (short)v; break;
                case XGcMask.TileStipYOrigin: gc.TileStipYOrigin = (short)v; break;
                case XGcMask.Font: gc.Font = Lookup<XFontResource>(v) ?? throw new XProtocolError(XErrorCode.Font, v); break;
                case XGcMask.SubwindowMode: gc.SubwindowMode = (byte)v; break;
                case XGcMask.GraphicsExposures: gc.GraphicsExposures = v != 0; break;
                case XGcMask.ClipXOrigin: gc.ClipXOrigin = (short)v; break;
                case XGcMask.ClipYOrigin: gc.ClipYOrigin = (short)v; break;
                case XGcMask.ClipMask:
                    gc.ClipRects = null;
                    if (v == 0)
                    {
                        gc.ClipPixmap = null;
                        break;
                    }
                    XPixmap clip = Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    if (clip.Depth != 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    gc.ClipPixmap = clip;
                    break;
                case XGcMask.DashOffset: gc.DashOffset = (ushort)v; break;
                case XGcMask.Dashes:
                    if ((byte)v == 0)
                    {
                        throw new XProtocolError(XErrorCode.Value, v);
                    }
                    gc.Dashes = [(byte)v, (byte)v];
                    break;
                case XGcMask.ArcMode: gc.ArcMode = (byte)v; break;
            }
        }
    }

    private void SetDashes(XRequestReader r)
    {
        XGc gc = Gc(r.U32());
        gc.DashOffset = r.U16();
        int n = r.U16();
        byte[] dashes = r.BytesPadded(n);
        if (n == 0 || dashes.Any(d => d == 0))
        {
            throw new XProtocolError(XErrorCode.Value, 0);
        }
        // 奇数个元素时图案重复一遍(协议规定),保证开 / 关交替。
        gc.Dashes = n % 2 == 1 ? [.. dashes, .. dashes] : dashes;
    }

    private void SetClipRectangles(XRequestReader r)
    {
        XGc gc = Gc(r.U32());
        gc.ClipXOrigin = r.I16();
        gc.ClipYOrigin = r.I16();
        List<XRect> rects = [];
        while (r.Remaining >= 8)
        {
            rects.Add(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
        }
        gc.ClipRects = rects;
        gc.ClipPixmap = null;
    }

    private void FreeGC(XRequestReader r)
    {
        uint id = r.U32();
        _ = Gc(id);
        RemoveResource(id);
    }

    // ------------------------------------------------------------------ 绘图公共路径

    /// <summary>在可绘对象上按 GC 画一次;画到窗口上时把实际写过的范围记为损伤。</summary>
    private void Draw(uint drawable, uint gcId, Action<Rasterizer> draw)
    {
        XGc gc = Gc(gcId);
        if (DrawTarget(drawable, gc) is not { } target)
        {
            return;
        }
        Rasterizer raster = new(target.Buffer, target.OriginX, target.OriginY, target.Clip, gc);
        if (raster.IsClippedOut)
        {
            return;
        }
        draw(raster);
        if (target.TopLevel is { } top)
        {
            MarkDamage(top, raster.DirtyBounds);
        }
        else if (_damageObjects.Count != 0 && Lookup<XPixmap>(drawable) is { } pixmap)
        {
            NotePixmapDrawn(pixmap, raster.DirtyBounds);
        }
    }

    private static List<(int X, int Y)> ReadPoints(XRequestReader r, bool relative)
    {
        List<(int X, int Y)> points = [];
        int px = 0, py = 0;
        while (r.Remaining >= 4)
        {
            int x = r.I16(), y = r.I16();
            if (relative && points.Count > 0)
            {
                x += px;
                y += py;
            }
            points.Add((x, y));
            (px, py) = (x, y);
        }
        return points;
    }

    private void PolyPoint(XRequestReader r)
    {
        bool relative = r.Data == 1;
        uint drawable = r.U32(), gc = r.U32();
        List<(int X, int Y)> points = ReadPoints(r, relative);
        Draw(drawable, gc, raster =>
        {
            foreach ((int x, int y) in points)
            {
                raster.PlotPixel(x, y);
            }
        });
    }

    private void PolyLineRequest(XRequestReader r)
    {
        bool relative = r.Data == 1;
        uint drawable = r.U32(), gc = r.U32();
        List<(int X, int Y)> points = ReadPoints(r, relative);
        Draw(drawable, gc, raster => raster.PolyLine(points));
    }

    private void PolySegment(XRequestReader r)
    {
        uint drawable = r.U32(), gc = r.U32();
        List<(int, int, int, int)> segments = [];
        while (r.Remaining >= 8)
        {
            segments.Add((r.I16(), r.I16(), r.I16(), r.I16()));
        }
        Draw(drawable, gc, raster =>
        {
            foreach ((int x1, int y1, int x2, int y2) in segments)
            {
                raster.PolyLine([(x1, y1), (x2, y2)]);
            }
        });
    }

    private void PolyRectangle(XRequestReader r)
    {
        uint drawable = r.U32(), gc = r.U32();
        List<XRect> rects = ReadRects(r);
        Draw(drawable, gc, raster =>
        {
            foreach (XRect rect in rects)
            {
                int x2 = rect.X + rect.Width, y2 = rect.Y + rect.Height;
                raster.PolyLine([(rect.X, rect.Y), (x2, rect.Y), (x2, y2), (rect.X, y2), (rect.X, rect.Y)], closed: true);
            }
        });
    }

    private static List<XRect> ReadRects(XRequestReader r)
    {
        List<XRect> rects = [];
        while (r.Remaining >= 8)
        {
            rects.Add(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
        }
        return rects;
    }

    private static List<(int X, int Y, int W, int H, int A1, int A2)> ReadArcs(XRequestReader r)
    {
        List<(int, int, int, int, int, int)> arcs = [];
        while (r.Remaining >= 12)
        {
            arcs.Add((r.I16(), r.I16(), r.U16(), r.U16(), r.I16(), r.I16()));
        }
        return arcs;
    }

    private void PolyArc(XRequestReader r)
    {
        uint drawable = r.U32(), gc = r.U32();
        var arcs = ReadArcs(r);
        Draw(drawable, gc, raster =>
        {
            foreach ((int x, int y, int w, int h, int a1, int a2) in arcs)
            {
                raster.Arc(x, y, w, h, a1, a2);
            }
        });
    }

    private void PolyFillArc(XRequestReader r)
    {
        uint drawable = r.U32(), gc = r.U32();
        var arcs = ReadArcs(r);
        Draw(drawable, gc, raster =>
        {
            foreach ((int x, int y, int w, int h, int a1, int a2) in arcs)
            {
                raster.FillArc(x, y, w, h, a1, a2);
            }
        });
    }

    private void FillPoly(XRequestReader r)
    {
        uint drawable = r.U32(), gcId = r.U32();
        r.U8();                       // shape:只是性能提示
        bool relative = r.U8() == 1;
        r.Skip(2);
        List<(int X, int Y)> points = ReadPoints(r, relative);
        XGc gc = Gc(gcId);
        Draw(drawable, gcId, raster =>
            raster.FillPolygons([[.. points.Select(p => ((double)p.X, (double)p.Y))]], winding: gc.FillRule == 1));
    }

    private void PolyFillRectangle(XRequestReader r)
    {
        uint drawable = r.U32(), gc = r.U32();
        List<XRect> rects = ReadRects(r);
        Draw(drawable, gc, raster =>
        {
            foreach (XRect rect in rects)
            {
                raster.FillRect(rect.X, rect.Y, rect.Width, rect.Height);
            }
        });
    }

    // ------------------------------------------------------------------ ClearArea / CopyArea / CopyPlane

    private void ClearArea(XRequestReader r)
    {
        bool exposures = r.Data != 0;
        XWindow window = Window(r.U32());
        short x = r.I16(), y = r.I16();
        int width = r.U16(), height = r.U16();
        if (window.IsInputOnly)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (width == 0)
        {
            width = window.Width - x;
        }
        if (height == 0)
        {
            height = window.Height - y;
        }
        if (width <= 0 || height <= 0 || !window.IsViewable || window.TopLevel is not { Buffer: not null } top)
        {
            return;
        }
        (int ox, int oy) = window.OffsetInTopLevel();
        Region area = ClipByChildren(window).Intersect(new XRect(ox + x, oy + y, width, height));
        if (area.IsEmpty)
        {
            return;
        }
        PaintBackground(window, area);
        if (exposures)
        {
            SendExpose(window, area);
        }
        MarkDamage(top, area);
    }

    /// <summary>读一块源像素;超出源可绘对象范围的部分返回为「不可得」(调用方据此发 GraphicsExposure)。</summary>
    private (uint[] Pixels, XRect Available)? ReadSource(uint drawable, int x, int y, int width, int height, out byte depth)
    {
        PixelBuffer? buffer;
        int ox = 0, oy = 0;
        XRect bounds;
        switch (Lookup<XResource>(drawable))
        {
            case XPixmap p:
                buffer = p.Buffer;
                depth = p.Depth;
                bounds = p.Buffer.Bounds;
                break;
            case XWindow w:
                if (w.IsInputOnly)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                depth = w.IsRoot ? (byte)24 : w.Depth;
                if (w.IsRoot || !w.IsViewable || w.TopLevel is not { Buffer: { } b })
                {
                    return null;
                }
                buffer = b;
                (ox, oy) = w.OffsetInTopLevel();
                bounds = new XRect(0, 0, w.Width, w.Height);
                break;
            default:
                throw new XProtocolError(XErrorCode.Drawable, drawable);
        }

        XRect available = new XRect(x, y, width, height).Intersect(bounds);
        uint[] pixels = new uint[Math.Max(0, width * height)];
        for (int row = available.Y; row < available.Bottom; row++)
        {
            for (int col = available.X; col < available.Right; col++)
            {
                pixels[((row - y) * width) + (col - x)] = buffer.Get(col + ox, row + oy);
            }
        }
        return (pixels, available);
    }

    private void CopyArea(XClient c, XRequestReader r)
    {
        uint src = r.U32(), dst = r.U32(), gcId = r.U32();
        short sx = r.I16(), sy = r.I16(), dx = r.I16(), dy = r.I16();
        ushort width = r.U16(), height = r.U16();
        XGc gc = Gc(gcId);
        if (ReadSource(src, sx, sy, width, height, out byte srcDepth) is not { } source)
        {
            SendNoExposure(c, gc, dst, XOpcode.CopyArea);
            return;
        }
        if (srcDepth != DrawableDepth(dst))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        XRect avail = source.Available;
        Draw(dst, gcId, raster =>
        {
            // 只贴源里拿得到的那一块;拿不到的部分由 GraphicsExposure 请客户端自己补画。
            if (avail.IsEmpty)
            {
                return;
            }
            uint[] block = new uint[avail.Width * avail.Height];
            for (int row = 0; row < avail.Height; row++)
            {
                Array.Copy(source.Pixels, ((avail.Y - sy + row) * width) + (avail.X - sx), block, row * avail.Width, avail.Width);
            }
            raster.Blit(block, avail.Width, avail.Height, dx + (avail.X - sx), dy + (avail.Y - sy));
        });

        if (!gc.GraphicsExposures)
        {
            return;
        }
        Region missing = new Region(new XRect(dx, dy, width, height)).Subtract(avail.Offset(dx - sx, dy - sy));
        if (missing.IsEmpty)
        {
            SendNoExposure(c, gc, dst, XOpcode.CopyArea);
            return;
        }
        List<XRect> rects = [.. missing.Rects];
        for (int i = 0; i < rects.Count; i++)
        {
            XRect m = rects[i];
            int count = rects.Count - 1 - i;
            c.Event(XEventCode.GraphicsExposure, 0, w => w
                .U32(dst).U16((ushort)m.X).U16((ushort)m.Y).U16((ushort)m.Width).U16((ushort)m.Height)
                .U16(0).U16((ushort)count).U8(XOpcode.CopyArea));
        }
    }

    private static void SendNoExposure(XClient c, XGc gc, uint drawable, byte major)
    {
        if (gc.GraphicsExposures)
        {
            c.Event(XEventCode.NoExposure, 0, w => w.U32(drawable).U16(0).U8(major));
        }
    }

    private byte DrawableDepth(uint drawable) => Lookup<XResource>(drawable) switch
    {
        XPixmap p => p.Depth,
        XWindow w => w.IsRoot ? (byte)24 : w.Depth,
        _ => throw new XProtocolError(XErrorCode.Drawable, drawable),
    };

    private void CopyPlane(XClient c, XRequestReader r)
    {
        uint src = r.U32(), dst = r.U32(), gcId = r.U32();
        short sx = r.I16(), sy = r.I16(), dx = r.I16(), dy = r.I16();
        ushort width = r.U16(), height = r.U16();
        uint plane = r.U32();
        if (plane == 0 || (plane & (plane - 1)) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, plane);
        }
        XGc gc = Gc(gcId);
        if (ReadSource(src, sx, sy, width, height, out _) is not { } source)
        {
            SendNoExposure(c, gc, dst, XOpcode.CopyPlane);
            return;
        }
        XRect avail = source.Available;
        Draw(dst, gcId, raster =>
        {
            for (int row = avail.Y; row < avail.Bottom; row++)
            {
                for (int col = avail.X; col < avail.Right; col++)
                {
                    uint bit = source.Pixels[((row - sy) * width) + (col - sx)] & plane;
                    raster.PutPixel(dx + (col - sx), dy + (row - sy), bit != 0 ? gc.Foreground : gc.Background);
                }
            }
        });
        SendNoExposure(c, gc, dst, XOpcode.CopyPlane);
    }

    // ------------------------------------------------------------------ PutImage / GetImage

    private void PutImage(XRequestReader r)
    {
        byte format = r.Data;
        uint drawable = r.U32(), gcId = r.U32();
        ushort width = r.U16(), height = r.U16();
        short dx = r.I16(), dy = r.I16();
        byte leftPad = r.U8();
        byte depth = r.U8();
        r.Skip(2);
        ReadOnlySpan<byte> data = r.Rest();
        XGc gc = Gc(gcId);
        byte targetDepth = DrawableDepth(drawable);

        uint[] pixels = new uint[width * height];
        switch (format)
        {
            case 0:   // Bitmap:深度必须为 1,1 → 前景,0 → 背景
                if (depth != 1)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                DecodeBitmap(data, width, height, leftPad, pixels, gc.Foreground, gc.Background);
                break;
            case 1:   // XYPixmap:逐平面的位图,高位平面在前
                if (depth != targetDepth)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                int planeBytes = BitmapStride(width + leftPad) * height;
                for (int plane = 0; plane < depth; plane++)
                {
                    uint bit = 1u << (depth - 1 - plane);
                    if (data.Length < (plane + 1) * planeBytes)
                    {
                        throw new XProtocolError(XErrorCode.Length);
                    }
                    ReadOnlySpan<byte> planeData = data.Slice(plane * planeBytes, planeBytes);
                    uint[] one = new uint[width * height];
                    DecodeBitmap(planeData, width, height, leftPad, one, 1, 0);
                    for (int i = 0; i < one.Length; i++)
                    {
                        if (one[i] != 0)
                        {
                            pixels[i] |= bit;
                        }
                    }
                }
                break;
            case 2:   // ZPixmap
                if (depth != targetDepth || leftPad != 0)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                if (depth == 1)
                {
                    DecodeBitmap(data, width, height, 0, pixels, 1, 0);
                    break;
                }
                DecodeZPixmap(data, width, height, BitsPerPixel(depth), pixels);
                break;
            default:
                throw new XProtocolError(XErrorCode.Value, format);
        }
        Draw(drawable, gcId, raster => raster.Blit(pixels, width, height, dx, dy));
    }

    /// <summary>ZPixmap(8 / 16 / 32 位每像素,LSBFirst,每行补齐到 32 位)。</summary>
    private static void DecodeZPixmap(ReadOnlySpan<byte> data, int width, int height, int bpp, uint[] pixels)
    {
        int bytesPer = bpp / 8;
        int stride = BitmapStride(width * bpp);
        if (data.Length < stride * height)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * stride) + (x * bytesPer);
                uint v = data[o];
                if (bytesPer > 1)
                {
                    v |= (uint)data[o + 1] << 8;
                }
                if (bytesPer > 2)
                {
                    v |= ((uint)data[o + 2] << 16) | ((uint)data[o + 3] << 24);
                }
                pixels[(y * width) + x] = v;
            }
        }
    }

    private static byte[] EncodeZPixmap(uint[] pixels, int width, int height, int bpp, uint planeMask)
    {
        int bytesPer = bpp / 8;
        int stride = BitmapStride(width * bpp);
        byte[] data = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint v = pixels[(y * width) + x] & planeMask;
                int o = (y * stride) + (x * bytesPer);
                for (int b = 0; b < bytesPer; b++)
                {
                    data[o + b] = (byte)(v >> (8 * b));
                }
            }
        }
        return data;
    }

    /// <summary>位图行宽(字节):scanline-pad 32 位。</summary>
    private static int BitmapStride(int widthInBits) => ((widthInBits + 31) / 32) * 4;

    /// <summary>解一张 LSBFirst 位序、32 位对齐的位图。</summary>
    private static void DecodeBitmap(ReadOnlySpan<byte> data, int width, int height, int leftPad, uint[] pixels, uint one, uint zero)
    {
        int stride = BitmapStride(width + leftPad);
        if (data.Length < stride * height)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bit = x + leftPad;
                bool set = (data[(y * stride) + (bit >> 3)] & (1 << (bit & 7))) != 0;
                pixels[(y * width) + x] = set ? one : zero;
            }
        }
    }

    private void GetImage(XClient c, XRequestReader r)
    {
        byte format = r.Data;
        uint drawable = r.U32();
        short x = r.I16(), y = r.I16();
        ushort width = r.U16(), height = r.U16();
        uint planeMask = r.U32();
        if (format is not (1 or 2))
        {
            throw new XProtocolError(XErrorCode.Value, format);
        }

        XResource? resource = Lookup<XResource>(drawable);
        int boundsW, boundsH;
        uint visual = 0;
        switch (resource)
        {
            case XPixmap p:
                (boundsW, boundsH) = (p.Width, p.Height);
                break;
            case XWindow w when w.IsViewable && !w.IsRoot:
                (boundsW, boundsH) = (w.Width, w.Height);
                visual = w.Visual;
                break;
            case XWindow:
                throw new XProtocolError(XErrorCode.Match);
            default:
                throw new XProtocolError(XErrorCode.Drawable, drawable);
        }
        if (x < 0 || y < 0 || x + width > boundsW || y + height > boundsH)
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        uint[] pixels = ReadSource(drawable, x, y, width, height, out byte depth)?.Pixels ?? new uint[width * height];
        byte[] data;
        if (format == 2 && depth != 1)
        {
            data = EncodeZPixmap(pixels, width, height, BitsPerPixel(depth), planeMask);
        }
        else
        {
            // 深度 1 的 ZPixmap 与 XYPixmap 都是位图(XYPixmap 按平面掩码里的平面逐张给出,高位在前)。
            int stride = BitmapStride(width);
            List<byte> planes = [];
            for (int plane = depth - 1; plane >= 0; plane--)
            {
                uint bit = 1u << plane;
                if (format == 1 && (planeMask & bit) == 0)
                {
                    continue;
                }
                byte[] one = new byte[stride * height];
                for (int yy = 0; yy < height; yy++)
                {
                    for (int xx = 0; xx < width; xx++)
                    {
                        if ((pixels[(yy * width) + xx] & bit) != 0)
                        {
                            one[(yy * stride) + (xx >> 3)] |= (byte)(1 << (xx & 7));
                        }
                    }
                }
                planes.AddRange(one);
                if (format == 2)
                {
                    break;
                }
            }
            data = [.. planes];
        }
        c.Reply(depth, w => w.U32(visual).Zero(20).Bytes(data).Pad4());
    }
}
