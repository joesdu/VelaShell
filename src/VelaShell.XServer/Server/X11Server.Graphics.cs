// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreatePixmap」「FreePixmap」「CreateGC」「ChangeGC」「CopyGC」
//   「SetDashes」「SetClipRectangles」「FreeGC」「ClearArea」「CopyArea」(GraphicsExposure / NoExposure)
//   「CopyPlane」「PolyPoint」「PolyLine」「PolySegment」「PolyRectangle」「PolyArc」「FillPoly」
//   「PolyFillRectangle」「PolyFillArc」「PutImage」「GetImage」;
//   第 8 节「Connection Setup」里的 image-byte-order / bitmap-format-bit-order / scanline-pad(本服务端声明 LSBFirst、32 位对齐)

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

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
        if ((long)width * height > PixelBuffer.MaxPixels)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 65535² 一块就是 16 GB
        }
        RequireMemory(c, ResourceOverheadBytes + PixelBytes(width, height));   // 先核账再分配(xs_plan X-2)
        AddResource(c, new XPixmap(id, c, width, height, depth));
    }

    private void FreePixmap(XRequestReader r)
    {
        uint id = r.U32();
        _ = Lookup<XPixmap>(id) ?? throw new XProtocolError(XErrorCode.Pixmap, id);
        // 像素图被窗口背景或 GC 引用时仍然可用(协议:释放 ID,数据活到最后一个引用消失)—— 引用持有对象本身,这里只删 ID。
        // 建在它上面的 Damage 对象也不跟着销毁:客户端释放像素图之后照样会 DamageDestroy(xeyes 用 Present 换帧时就是这个顺序),
        // 提前销毁会让那一条回 BadDamage、客户端直接退出。Damage 随 DamageDestroy 或客户端断开而释放。
        RemoveResource(id);
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

    /// <summary>
    /// CreateGC / ChangeGC 的值表。先全部读出来、逐项核对(枚举越界回 BadValue,像素图 / 字体不存在或深度不对回相应的错误),
    /// 都没问题才一起写进 GC —— 核心协议:出错的请求不产生效果(原先边读边改,出错时前面的值已经生效;function 还被 &amp; 0xF 截断)。
    /// </summary>
    private void ApplyGcValues(XGc gc, uint mask, XRequestReader r)
    {
        uint[] values = new uint[23];
        for (int bit = 0; bit < 23; bit++)
        {
            if ((mask & (1u << bit)) != 0)
            {
                values[bit] = r.U32();
            }
        }
        bool Has(XGcMask m) => (mask & (uint)m) != 0;
        uint Value(XGcMask m) => values[System.Numerics.BitOperations.TrailingZeroCount((uint)m)];
        void Range(XGcMask m, uint max)
        {
            if (Has(m) && Value(m) > max)
            {
                throw new XProtocolError(XErrorCode.Value, Value(m));
            }
        }
        Range(XGcMask.Function, 15);
        Range(XGcMask.LineStyle, 2);            // Solid / OnOffDash / DoubleDash
        Range(XGcMask.CapStyle, 3);             // NotLast / Butt / Round / Projecting
        Range(XGcMask.JoinStyle, 2);            // Miter / Round / Bevel
        Range(XGcMask.FillStyle, 3);            // Solid / Tiled / Stippled / OpaqueStippled
        Range(XGcMask.FillRule, 1);             // EvenOdd / Winding
        Range(XGcMask.SubwindowMode, 1);        // ClipByChildren / IncludeInferiors
        Range(XGcMask.GraphicsExposures, 1);    // BOOL
        Range(XGcMask.ArcMode, 1);              // Chord / PieSlice
        if (Has(XGcMask.Dashes) && (byte)Value(XGcMask.Dashes) == 0)
        {
            throw new XProtocolError(XErrorCode.Value, Value(XGcMask.Dashes));
        }
        XPixmap? tile = Has(XGcMask.Tile) ? PixmapOfDepth(Value(XGcMask.Tile), gc.Depth) : null;
        XPixmap? stipple = Has(XGcMask.Stipple) ? PixmapOfDepth(Value(XGcMask.Stipple), 1) : null;
        XFontResource? font = Has(XGcMask.Font)
            ? Lookup<XFontResource>(Value(XGcMask.Font)) ?? throw new XProtocolError(XErrorCode.Font, Value(XGcMask.Font))
            : null;
        XPixmap? clip = Has(XGcMask.ClipMask) && Value(XGcMask.ClipMask) != 0 ? PixmapOfDepth(Value(XGcMask.ClipMask), 1) : null;

        for (int bit = 0; bit < 23; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }
            uint v = values[bit];
            switch ((XGcMask)(1u << bit))
            {
                case XGcMask.Function: gc.Function = (byte)v; break;
                case XGcMask.PlaneMask: gc.PlaneMask = v; break;
                case XGcMask.Foreground: gc.Foreground = v; break;
                case XGcMask.Background: gc.Background = v; break;
                case XGcMask.LineWidth: gc.LineWidth = (ushort)v; break;
                case XGcMask.LineStyle: gc.LineStyle = (byte)v; break;
                case XGcMask.CapStyle: gc.CapStyle = (byte)v; break;
                case XGcMask.JoinStyle: gc.JoinStyle = (byte)v; break;
                case XGcMask.FillStyle: gc.FillStyle = (byte)v; break;
                case XGcMask.FillRule: gc.FillRule = (byte)v; break;
                case XGcMask.Tile: gc.Tile = tile; break;
                case XGcMask.Stipple: gc.Stipple = stipple; break;
                case XGcMask.TileStipXOrigin: gc.TileStipXOrigin = (short)v; break;
                case XGcMask.TileStipYOrigin: gc.TileStipYOrigin = (short)v; break;
                case XGcMask.Font: gc.Font = font; break;
                case XGcMask.SubwindowMode: gc.SubwindowMode = (byte)v; break;
                case XGcMask.GraphicsExposures: gc.GraphicsExposures = v != 0; break;
                case XGcMask.ClipXOrigin: gc.ClipXOrigin = (short)v; break;
                case XGcMask.ClipYOrigin: gc.ClipYOrigin = (short)v; break;
                case XGcMask.ClipMask:
                    gc.ClipRects = null;
                    gc.ClipPixmap = clip;
                    break;
                case XGcMask.DashOffset: gc.DashOffset = (ushort)v; break;
                case XGcMask.Dashes: gc.Dashes = [(byte)v, (byte)v]; break;
                case XGcMask.ArcMode: gc.ArcMode = (byte)v; break;
            }
        }
    }

    /// <summary>GC 引用的像素图:不存在回 BadPixmap,深度不对回 BadMatch。</summary>
    private XPixmap PixmapOfDepth(uint id, byte depth)
    {
        XPixmap pixmap = Lookup<XPixmap>(id) ?? throw new XProtocolError(XErrorCode.Pixmap, id);
        return pixmap.Depth == depth ? pixmap : throw new XProtocolError(XErrorCode.Match);
    }

    private void SetDashes(XRequestReader r)
    {
        XGc gc = Gc(r.U32());
        ushort offset = r.U16();
        int n = r.U16();
        byte[] dashes = r.BytesPadded(n);
        if (n == 0 || dashes.Any(d => d == 0))
        {
            throw new XProtocolError(XErrorCode.Value, 0);   // 先核对:出错时 dash-offset 也不改
        }
        gc.DashOffset = offset;
        // 奇数个元素时图案重复一遍(协议规定),保证开 / 关交替。
        gc.Dashes = n % 2 == 1 ? [.. dashes, .. dashes] : dashes;
    }

    private void SetClipRectangles(XRequestReader r)
    {
        XGc gc = Gc(r.U32());
        gc.ClipXOrigin = r.I16();
        gc.ClipYOrigin = r.I16();
        List<XRect> rects = [];
        if (r.Remaining / 8 > Region.MaxRects)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 每次画都要按它建一次区域(Rasterizer),块数与区域同一个上限
        }
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
        NoteDrawn(drawable, target.TopLevel, raster);
    }

    /// <summary>画完一次:画到窗口上时把实际写过的范围记为损伤,画到像素图上时告诉盯着它的 Damage 对象。</summary>
    private void NoteDrawn(uint drawable, XWindow? topLevel, Rasterizer raster)
    {
        if (topLevel is { } top)
        {
            MarkDamage(top, raster.DirtyBounds);
        }
        else if ((_damageObjects.Count != 0 || _namedWindowBuffers.Count != 0) && Lookup<XPixmap>(drawable) is { } pixmap)
        {
            NotePixmapDrawn(pixmap, raster.DirtyBounds);
        }
    }

    /// <summary>CoordModePrevious 累加出来的坐标饱和在这个范围里(见 <see cref="ReadPoints" />)。</summary>
    private const long MaxAccumulatedCoordinate = 1L << 30;

    private static List<(int X, int Y)> ReadPoints(XRequestReader r, bool relative)
    {
        List<(int X, int Y)> points = [];
        int px = 0, py = 0;
        while (r.Remaining >= 4)
        {
            int x = r.I16(), y = r.I16();
            if (relative && points.Count > 0)
            {
                // CoordModePrevious 一路累加:几百万个点能加出 int 范围之外,饱和在 ±2³⁰(早已远在任何可绘对象之外)。
                x = (int)Math.Clamp((long)x + px, -MaxAccumulatedCoordinate, MaxAccumulatedCoordinate);
                y = (int)Math.Clamp((long)y + py, -MaxAccumulatedCoordinate, MaxAccumulatedCoordinate);
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

    private void PolyLine(XRequestReader r)
    {
        bool relative = r.Data == 1;
        uint drawable = r.U32(), gc = r.U32();
        List<(int X, int Y)> points = ReadPoints(r, relative);
        // 协议:首尾两点重合时,第一段与最后一段也要「join correctly」—— 当闭合路径画:宽线在那里加接头而不是两个端帽,
        // 细线不再把起点画第二遍(GXxor 下会抵消)。只有两个点时是端点重合的一条线,按端帽的规则画。
        bool closed = points.Count > 2 && points[0] == points[^1];
        Draw(drawable, gc, raster => raster.PolyLine(points, closed));
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
                raster.Segment(x1, y1, x2, y2);
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
                raster.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
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
        List<(int X, int Y, int W, int H, int A1, int A2)> arcs = ReadArcs(r);
        // 首尾相接的弧要一起画(接头、虚线接着走、整串只画一次),整个列表交给光栅化器分串。
        Draw(drawable, gc, raster => raster.PolyArc(arcs));
    }

    private void PolyFillArc(XRequestReader r)
    {
        uint drawable = r.U32(), gc = r.U32();
        List<(int X, int Y, int W, int H, int A1, int A2)> arcs = ReadArcs(r);
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

    /// <summary>
    /// 读一块源像素:只含与源可绘对象相交的部分(<c>Available</c>,可绘对象坐标),按它的宽度行优先排列。
    /// 数组来自 <see cref="ArrayPool{T}" />,调用方用完要还。拿不到的部分由调用方发 GraphicsExposure。
    /// </summary>
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
                if (w.IsRoot)
                {
                    return ReadRoot(x, y, width, height);
                }
                if (!w.IsViewable || w.TopLevel is not { Buffer: { } b })
                {
                    return null;
                }
                buffer = b;
                (ox, oy) = w.OffsetInTopLevel();
                // 窗口里只有落在顶层缓冲之内的部分拿得到:伸出祖先之外的子窗口、被 PixelBuffer.MaxPixels 削掉的行都不在缓冲里
                // (原先只按窗口尺寸裁,下标越界回 BadImplementation,伸出右边时读到折到下一行开头的像素)。
                bounds = new XRect(0, 0, w.Width, w.Height).Intersect(new XRect(-ox, -oy, b.Width, b.Height));
                break;
            default:
                throw new XProtocolError(XErrorCode.Drawable, drawable);
        }

        // 只拷与源相交的那一块(按请求尺寸分配的话,65535×65535 的请求会溢出或耗尽内存),整行 Array.Copy。
        XRect available = new XRect(x, y, width, height).Intersect(bounds);
        uint[] pixels = ArrayPool<uint>.Shared.Rent(Math.Max(1, available.Width * available.Height));
        for (int row = 0; row < available.Height; row++)
        {
            Array.Copy(buffer.Pixels, ((available.Y + row + oy) * buffer.Width) + available.X + ox,
                pixels, row * available.Width, available.Width);
        }
        return (pixels, available);
    }

    /// <summary>
    /// 根窗口的内容:rootless 下根窗口本身不画,屏幕上看得到的就是各顶层窗口 —— 按堆叠次序从下往上把映射着的
    /// 顶层拼起来,其余地方是黑的(xwd -root、截图工具、xmag 读根窗口时拿到的就是这个)。
    /// </summary>
    private (uint[] Pixels, XRect Available) ReadRoot(int x, int y, int width, int height)
    {
        XRect available = new XRect(x, y, width, height).Intersect(new XRect(0, 0, Root.Width, Root.Height));
        if (Root.Buffer is { } screen)
        {
            // 单窗口模式:根窗口有拼好的屏幕(连背景),先把这一批到此为止画的拼进去再读。
            ComposeScreenBatch();
            available = available.Intersect(screen.Bounds);
            uint[] composed = ArrayPool<uint>.Shared.Rent(Math.Max(1, available.Width * available.Height));
            for (int row = 0; row < available.Height; row++)
            {
                Array.Copy(screen.Pixels, ((available.Y + row) * screen.Width) + available.X, composed, row * available.Width, available.Width);
            }
            return (composed, available);
        }
        uint[] pixels = ArrayPool<uint>.Shared.Rent(Math.Max(1, available.Width * available.Height));
        Array.Clear(pixels, 0, Math.Max(1, available.Width * available.Height));
        foreach (XWindow top in Root.Children)
        {
            if (!top.Mapped || top.Buffer is not { } buffer)
            {
                continue;
            }
            int left = top.X + top.BorderWidth, upper = top.Y + top.BorderWidth;
            XRect part = new XRect(left, upper, buffer.Width, buffer.Height).Intersect(available);
            for (int row = 0; row < part.Height; row++)
            {
                Array.Copy(buffer.Pixels, ((part.Y - upper + row) * buffer.Width) + part.X - left,
                    pixels, ((part.Y - available.Y + row) * available.Width) + part.X - available.X, part.Width);
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
        // 先核对深度再取像素:ReadSource 给的是租来的池化数组,拿到之后再抛 BadMatch / BadDrawable,它就还不回池里了。
        if (DrawableDepth(src) != DrawableDepth(dst))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        (uint[]? pixels, XRect block, Region copyable) = ReadCopySource(src, gc, sx, sy, width, height);
        try
        {
            // 只贴源里拿得到的那几块;拿不到的部分由 GraphicsExposure 请客户端自己补画。
            if (pixels is not null && !copyable.IsEmpty)
            {
                Draw(dst, gcId, raster => BlitCopyable(raster, pixels, block, copyable, dx - sx, dy - sy, preMasked: true));
            }
        }
        finally
        {
            if (pixels is not null)
            {
                ArrayPool<uint>.Shared.Return(pixels);
            }
        }
        FinishCopy(c, gc, dst, new XRect(dx, dy, width, height), copyable.Translate(dx - sx, dy - sy), XOpcode.CopyArea);
    }

    /// <summary>
    /// CopyArea / CopyPlane 的源:读出 (x, y, w, h) 里拿得到的像素(<c>Block</c> 这一块,行优先),以及其中真正可以拷的区域
    /// (可绘对象坐标)。窗口只有看得见的部分可拷:被兄弟或祖先挡住的、伸出缓冲的、窗口不可见的都拷不到;gc 的 subwindow-mode 为
    /// ClipByChildren 时映射着的子窗口也挡着,IncludeInferiors 时连子窗口的内容一起拷(核心协议「CopyArea」与 CreateGC 的 subwindow-mode)。
    /// 原先直接拷缓冲里的像素 —— 被挡住的地方拷到的是别的窗口。像素数组是租来的,调用方用完要还。
    /// </summary>
    private (uint[]? Pixels, XRect Block, Region Copyable) ReadCopySource(uint drawable, XGc gc, int x, int y, int width, int height)
    {
        if (ReadSource(drawable, x, y, width, height, out _) is not { } source)
        {
            return (null, default, new Region());
        }
        Region copyable = new(source.Available);
        if (Lookup<XResource>(drawable) is XWindow { IsRoot: false } window && window.TopLevel is { Buffer: not null })
        {
            (int ox, int oy) = window.OffsetInTopLevel();
            copyable.Intersect(CachedClip(window, includeInferiors: gc.SubwindowMode == 1).Clone().Translate(-ox, -oy));
        }
        return (source.Pixels, source.Available, copyable);
    }

    /// <summary>把 <paramref name="block" /> 这块源像素里 <paramref name="copyable" /> 的部分平移 (<paramref name="dx" />, <paramref name="dy" />) 贴上去。</summary>
    private static void BlitCopyable(Rasterizer raster, uint[] pixels, XRect block, Region copyable, int dx, int dy, bool preMasked)
    {
        foreach (XRect r in copyable.Rects)
        {
            int offset = ((r.Y - block.Y) * block.Width) + (r.X - block.X);
            raster.Blit(pixels.AsSpan(offset), block.Width, r.Width, r.Height, r.X + dx, r.Y + dy, preMasked);
        }
    }

    /// <summary>
    /// CopyArea / CopyPlane 之后:目标矩形里对应源拿不到的部分(<paramref name="copied" /> 之外,只算目标可绘对象范围内的)——
    /// 目标是背景不为 None 的窗口时先用背景铺上(按 GXcopy、全平面),再逐块发 GraphicsExposure 请客户端自己补画;
    /// 都拿到了发一个 NoExposure(gc 的 graphics-exposures 关着时都不发)。核心协议「CopyArea」。
    /// </summary>
    private void FinishCopy(XClient c, XGc gc, uint dst, XRect destination, Region copied, byte major)
    {
        Region missing = new Region(destination).Subtract(copied).Intersect(DrawableRect(dst));
        if (!missing.IsEmpty && Lookup<XResource>(dst) is XWindow { IsRoot: false } window && DrawTarget(dst, null) is { TopLevel: { } top } target)
        {
            Region area = missing.Clone().Translate(target.OriginX, target.OriginY).Intersect(target.Clip);
            if (!area.IsEmpty)
            {
                PaintBackground(window, area);
                MarkDamage(top, area);
            }
        }
        if (!gc.GraphicsExposures)
        {
            return;
        }
        if (missing.IsEmpty)
        {
            SendNoExposure(c, gc, dst, major);
            return;
        }
        List<XRect> rects = [.. missing.Rects];
        for (int i = 0; i < rects.Count; i++)
        {
            XRect m = rects[i];
            int count = rects.Count - 1 - i;
            c.Event(XEventCode.GraphicsExposure, 0, w => w
                .U32(dst).U16((ushort)m.X).U16((ushort)m.Y).U16((ushort)m.Width).U16((ushort)m.Height)
                .U16(0).U16((ushort)count).U8(major));
        }
    }

    private static void SendNoExposure(XClient c, XGc gc, uint drawable, byte major)
    {
        if (gc.GraphicsExposures)
        {
            c.Event(XEventCode.NoExposure, 0, w => w.U32(drawable).U16(0).U8(major));
        }
    }

    /// <summary>可绘对象自己的矩形(原点、宽、高)。</summary>
    private XRect DrawableRect(uint drawable) => Lookup<XResource>(drawable) switch
    {
        XPixmap p => new XRect(0, 0, p.Width, p.Height),
        XWindow w => new XRect(0, 0, w.Width, w.Height),
        _ => default,
    };

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
        XGc gc = Gc(gcId);
        byte srcDepth = DrawableDepth(src);
        _ = DrawableDepth(dst);
        // 协议:bit-plane 恰好一位、且小于 2^源深度(深度 8 的源没有第 8 位以上的平面)。
        if (plane == 0 || (plane & (plane - 1)) != 0 || (srcDepth < 32 && plane >= 1u << srcDepth))
        {
            throw new XProtocolError(XErrorCode.Value, plane);
        }
        (uint[]? pixels, XRect block, Region copyable) = ReadCopySource(src, gc, sx, sy, width, height);
        try
        {
            // 等于拿源的这一位平面当点画、按 OpaqueStippled 填:位为 1 处是前景、0 处是背景,再按 CopyArea 贴(走光栅操作与平面掩码,
            // GXcopy + 全平面时整行拷)。原先逐像素 PutPixel。
            if (pixels is not null && !copyable.IsEmpty)
            {
                uint foreground = gc.Foreground, background = gc.Background;
                Span<uint> span = pixels.AsSpan(0, block.Width * block.Height);
                for (int i = 0; i < span.Length; i++)
                {
                    span[i] = (span[i] & plane) != 0 ? foreground : background;
                }
                Draw(dst, gcId, raster => BlitCopyable(raster, pixels, block, copyable, dx - sx, dy - sy, preMasked: false));
            }
        }
        finally
        {
            if (pixels is not null)
            {
                ArrayPool<uint>.Shared.Return(pixels);
            }
        }
        // 与 CopyArea 同样的曝光语义:源拿不到的部分发 GraphicsExposure。
        FinishCopy(c, gc, dst, new XRect(dx, dy, width, height), copyable.Translate(dx - sx, dy - sy), XOpcode.CopyPlane);
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

        // 先按格式核对数据够不够 —— 客户端报的宽高可以是 65535×65535,数据却只有几个字节。
        long need = ImageDataLength(format, depth, width, height, leftPad);
        if (need > data.Length)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        PutImageRegion(drawable, gc, targetDepth, format, depth, leftPad, data, width, height, new XRect(0, 0, width, height), dx, dy);
    }

    /// <summary>
    /// 把一幅图像里 <paramref name="part" />(图像坐标)那一块贴到可绘对象的 (<paramref name="dx" />, <paramref name="dy" />)
    /// —— PutImage 与 MIT-SHM 的 PutImage 共用。
    /// </summary>
    /// <remarks>
    /// 只解码、只贴目标上画得到的部分(按可画区域的外接矩形裁):位图格式一个字节是八个像素,
    /// 按整幅图分配像素会把几 MB 的请求放大成几百 MB。32 位 ZPixmap(Qt、GTK4 / llvmpipe、浏览器整窗送像素走的就是它)
    /// 直接从请求数据按行贴进缓冲,不经中转数组。<c>data</c> 是整幅图像的数据,长度已按 <see cref="ImageDataLength" /> 核对过。
    /// </remarks>
    private void PutImageRegion(uint drawable, XGc gc, byte targetDepth, byte format, byte depth, byte leftPad,
        ReadOnlySpan<byte> data, int imageWidth, int imageHeight, XRect part, int dx, int dy)
    {
        ValidateImageFormat(format, depth, targetDepth, leftPad);
        if (part.IsEmpty || DrawTarget(drawable, gc) is not { } target)
        {
            return;
        }
        Rasterizer raster = new(target.Buffer, target.OriginX, target.OriginY, target.Clip, gc);
        XRect visible = new XRect(dx, dy, part.Width, part.Height).Intersect(raster.ClipBounds);
        if (visible.IsEmpty)
        {
            return;
        }
        // 画得到的那一块在图像里的位置。
        XRect source = new(part.X + (visible.X - dx), part.Y + (visible.Y - dy), visible.Width, visible.Height);
        if (format == 2 && depth != 1 && BitsPerPixel(depth) == 32 && BitConverter.IsLittleEndian)
        {
            // 32 位 ZPixmap:每行恰好 宽 × 4 字节、LSBFirst(连接建立时声明的 image-byte-order),整幅就是本机的 uint 数组。
            ReadOnlySpan<uint> all = MemoryMarshal.Cast<byte, uint>(data);
            raster.Blit(all[((source.Y * imageWidth) + source.X)..], imageWidth, source.Width, source.Height, visible.X, visible.Y);
        }
        else
        {
            uint[] pixels = ArrayPool<uint>.Shared.Rent(source.Width * source.Height);
            try
            {
                DecodeImage(format, depth, leftPad, data, imageWidth, imageHeight, source, gc, pixels);
                raster.Blit(pixels, source.Width, source.Height, visible.X, visible.Y);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(pixels);
            }
        }
        NoteDrawn(drawable, target.TopLevel, raster);
    }

    /// <summary>一幅图像按格式要多少字节(PutImage 与 MIT-SHM 的 PutImage 共用)。</summary>
    private static long ImageDataLength(byte format, byte depth, int width, int height, int leftPad) => format switch
    {
        0 => (long)BitmapStride(width + leftPad) * height,
        1 => (long)BitmapStride(width + leftPad) * height * depth,
        2 => depth == 1 ? (long)BitmapStride(width) * height : (long)BitmapStride(width * BitsPerPixel(depth)) * height,
        _ => throw new XProtocolError(XErrorCode.Value, format),
    };

    /// <summary>格式与深度的搭配(协议「PutImage」):Bitmap 必须深度 1;XYPixmap / ZPixmap 必须与可绘对象同深度,ZPixmap 不许左补。</summary>
    /// <summary>
    /// PutImage 的格式与深度:Bitmap 深度为 1、其余与目标同深度;ZPixmap 的 left-pad 必须为 0,Bitmap / XYPixmap 的 left-pad 必须小于
    /// 连接建立时声明的 bitmap-scanline-pad(32)—— 否则 BadMatch(核心协议「PutImage」)。
    /// </summary>
    private static void ValidateImageFormat(byte format, byte depth, byte targetDepth, byte leftPad)
    {
        bool ok = format switch
        {
            0 => depth == 1 && leftPad < 32,
            1 => depth == targetDepth && leftPad < 32,
            2 => depth == targetDepth && leftPad == 0,
            _ => throw new XProtocolError(XErrorCode.Value, format),
        };
        if (!ok)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
    }

    /// <summary>把图像里 <paramref name="part" />(图像坐标)那一块解成像素值,行优先、宽 = part.Width 写进 <paramref name="pixels" />。</summary>
    private static void DecodeImage(byte format, byte depth, byte leftPad, ReadOnlySpan<byte> data, int imageWidth, int imageHeight,
        XRect part, XGc gc, uint[] pixels)
    {
        switch (format)
        {
            case 0:   // Bitmap:1 → 前景,0 → 背景
                DecodeBitmap(data, imageWidth, leftPad, part, pixels, gc.Foreground, gc.Background);
                break;
            case 1:   // XYPixmap:逐平面的位图,高位平面在前
                int stride = BitmapStride(imageWidth + leftPad);
                int planeBytes = stride * imageHeight;
                Array.Clear(pixels, 0, part.Width * part.Height);
                for (int plane = 0; plane < depth; plane++)
                {
                    uint bit = 1u << (depth - 1 - plane);
                    ReadOnlySpan<byte> planeData = data.Slice(plane * planeBytes, planeBytes);
                    for (int yy = 0; yy < part.Height; yy++)
                    {
                        ReadOnlySpan<byte> row = planeData.Slice((part.Y + yy) * stride, stride);
                        int o = yy * part.Width;
                        for (int xx = 0; xx < part.Width; xx++)
                        {
                            int b = part.X + xx + leftPad;
                            if ((row[b >> 3] & (1 << (b & 7))) != 0)
                            {
                                pixels[o + xx] |= bit;
                            }
                        }
                    }
                }
                break;
            default:  // ZPixmap
                if (depth == 1)
                {
                    DecodeBitmap(data, imageWidth, 0, part, pixels, 1, 0);
                    break;
                }
                DecodeZPixmap(data, imageWidth, BitsPerPixel(depth), part, pixels);
                break;
        }
    }

    /// <summary>ZPixmap(8 / 16 / 32 位每像素,LSBFirst,每行补齐到 32 位)整幅解码。</summary>
    private static void DecodeZPixmap(ReadOnlySpan<byte> data, int width, int height, int bpp, uint[] pixels)
    {
        if (data.Length < (long)BitmapStride(width * bpp) * height)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        DecodeZPixmap(data, width, bpp, new XRect(0, 0, width, height), pixels);
    }

    /// <summary>ZPixmap 里 <paramref name="part" /> 那一块(8 / 16 / 32 位每像素,LSBFirst,每行补齐到 32 位)。</summary>
    private static void DecodeZPixmap(ReadOnlySpan<byte> data, int imageWidth, int bpp, XRect part, uint[] pixels)
    {
        int bytesPer = bpp / 8;
        int stride = BitmapStride(imageWidth * bpp);
        for (int y = 0; y < part.Height; y++)
        {
            ReadOnlySpan<byte> row = data.Slice(((part.Y + y) * stride) + (part.X * bytesPer), part.Width * bytesPer);
            Span<uint> to = pixels.AsSpan(y * part.Width, part.Width);
            switch (bytesPer)
            {
                case 1:
                    for (int x = 0; x < to.Length; x++)
                    {
                        to[x] = row[x];
                    }
                    break;
                case 2:
                    for (int x = 0; x < to.Length; x++)
                    {
                        to[x] = (uint)(row[2 * x] | (row[(2 * x) + 1] << 8));
                    }
                    break;
                default:
                    if (BitConverter.IsLittleEndian)
                    {
                        MemoryMarshal.Cast<byte, uint>(row).CopyTo(to);
                        break;
                    }
                    for (int x = 0; x < to.Length; x++)
                    {
                        to[x] = BinaryPrimitives.ReadUInt32LittleEndian(row[(4 * x)..]);
                    }
                    break;
            }
        }
    }

    private static byte[] EncodeZPixmap(uint[] pixels, int width, int height, int bpp, uint planeMask)
    {
        int bytesPer = bpp / 8;
        int stride = BitmapStride(width * bpp);
        if (bpp == 32 && planeMask == uint.MaxValue && BitConverter.IsLittleEndian)
        {
            return MemoryMarshal.AsBytes(pixels.AsSpan(0, width * height)).ToArray();
        }
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

    /// <summary>解一张 LSBFirst 位序、32 位对齐的位图里 <paramref name="part" />(图像坐标)那一块。</summary>
    private static void DecodeBitmap(ReadOnlySpan<byte> data, int imageWidth, int leftPad, XRect part, uint[] pixels, uint one, uint zero)
    {
        int stride = BitmapStride(imageWidth + leftPad);
        for (int y = 0; y < part.Height; y++)
        {
            ReadOnlySpan<byte> row = data.Slice((part.Y + y) * stride, stride);
            int o = y * part.Width;
            for (int x = 0; x < part.Width; x++)
            {
                int bit = part.X + x + leftPad;
                pixels[o + x] = (row[bit >> 3] & (1 << (bit & 7))) != 0 ? one : zero;
            }
        }
    }

    /// <summary>
    /// GetImage 回复里的像素数据最多这么大,再大回 BadAlloc —— 先按尺寸估、再取像素,超了不白算。与最大的像素图(2^26 像素 × 4 字节)一样;
    /// 三块 4K 横排时 <c>xwd -root</c> 的回复约 100 MB,在上限之内(超过输出队列上限的单条回复照样发得出去,见 <see cref="XClient.Send" />)。
    /// </summary>
    internal const long MaxImageReplyBytes = 256L * 1024 * 1024;

    private void GetImage(XClient c, XRequestReader r)
    {
        byte format = r.Data;
        uint drawable = r.U32();
        short x = r.I16(), y = r.I16();
        ushort width = r.U16(), height = r.U16();
        uint planeMask = r.U32();
        (byte depth, uint visual, byte[] data) = CaptureImage(format, drawable, x, y, width, height, planeMask, MaxImageReplyBytes);
        c.Reply(depth, w => w.U32(visual).Zero(20).Bytes(data).Pad4());
    }

    /// <summary>GetImage 与 MIT-SHM 的 GetImage 共用:核对矩形,按格式编好像素;编出来会超过 <paramref name="maxBytes" /> 时先回 BadAlloc。</summary>
    private (byte Depth, uint Visual, byte[] Data) CaptureImage(byte format, uint drawable, short x, short y, ushort width, ushort height, uint planeMask,
        long maxBytes = long.MaxValue)
    {
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
            case XWindow w when w.IsViewable:
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
        byte drawableDepth = resource is XPixmap pixmap ? pixmap.Depth : ((XWindow)resource).Depth;
        if (ImageDataLength(format, drawableDepth, width, height, 0) > maxBytes)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }

        // 矩形已核对在可绘对象之内,按请求宽度排列(池化,编完码就还)。窗口伸出顶层之外的部分拿不到(核心协议:
        // 被遮住的区域内容未定义),补 0 —— 回 BadMatch 的话,Xlib 默认的错误处理会让程序直接退出。
        (uint[] Pixels, XRect Available)? source = ReadSource(drawable, x, y, width, height, out byte depth);
        uint[]? pooled = source?.Pixels;
        if (source is { } s && s.Available != new XRect(x, y, width, height))
        {
            pooled = ArrayPool<uint>.Shared.Rent(Math.Max(1, width * height));
            Array.Clear(pooled, 0, Math.Max(1, width * height));
            XRect a = s.Available;
            for (int row = 0; row < a.Height; row++)
            {
                Array.Copy(s.Pixels, row * a.Width, pooled, ((a.Y - y + row) * width) + a.X - x, a.Width);
            }
            ArrayPool<uint>.Shared.Return(s.Pixels);
        }
        uint[] pixels = pooled ?? new uint[Math.Max(1, width * height)];
        uint depthMask = PixelBuffer.DepthMaskOf(depth);
        if ((planeMask & depthMask) == depthMask)
        {
            planeMask = uint.MaxValue;   // 缓冲里的像素本来就在深度掩码之内:平面掩码盖住全部有效位时等于不掩
        }
        byte[] data;
        if (format == 2 && depth != 1)
        {
            data = EncodeZPixmap(pixels, width, height, BitsPerPixel(depth), planeMask);
        }
        else
        {
            // 深度 1 的 ZPixmap 与 XYPixmap 都是位图(XYPixmap 按平面掩码里的平面逐张给出,高位在前)。
            // 先数出要几个平面,一次分配整块回复数据,逐平面、逐字节直接写进去(原先每个平面一个数组,再 AddRange 进 List<byte>,
            // 最后再拷一遍成数组)。
            int stride = BitmapStride(width);
            int planeBytes = stride * height;
            int count = 0;
            for (int plane = depth - 1; plane >= 0; plane--)
            {
                if (format == 2 || (planeMask & (1u << plane)) != 0)
                {
                    count++;
                }
                if (format == 2)
                {
                    break;
                }
            }
            data = new byte[(long)count * planeBytes];
            int offset = 0;
            for (int plane = depth - 1; plane >= 0 && offset < data.Length; plane--)
            {
                uint bit = 1u << plane;
                if (format == 1 && (planeMask & bit) == 0)
                {
                    continue;
                }
                for (int yy = 0; yy < height; yy++)
                {
                    ReadOnlySpan<uint> row = pixels.AsSpan(yy * width, width);
                    Span<byte> to = data.AsSpan(offset + (yy * stride), stride);
                    for (int xx = 0; xx < width; xx += 8)
                    {
                        int end = Math.Min(8, width - xx), v = 0;
                        for (int k = 0; k < end; k++)
                        {
                            if ((row[xx + k] & bit) != 0)
                            {
                                v |= 1 << k;   // LSBFirst(连接建立时声明的 bitmap-format-bit-order)
                            }
                        }
                        to[xx >> 3] = (byte)v;
                    }
                }
                offset += planeBytes;
            }
        }
        if (pooled is not null)
        {
            ArrayPool<uint>.Shared.Return(pooled);
        }
        return (depth, visual, data);
    }
}
