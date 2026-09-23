// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— 附录「Protocol Encoding」(次操作码 0–36、错误 PictFormat /
//   Picture / PictOp / GlyphSet / Glyph)、§6「QueryVersion / QueryPictFormats / QueryPictIndexValues」、
//   §7「CreatePicture / ChangePicture / SetPictureClipRectangles / SetPictureTransform / SetPictureFilter /
//   QueryFilters / FreePicture」、§9「Composite / FillRectangles」、
//   §10「Trapezoids / Triangles / TriStrip / TriFan / AddTraps」(源与第一个顶点对齐)、
//   §11「CreateSolidFill / CreateLinearGradient / CreateRadialGradient / CreateConicalGradient」、
//   §12「CreateGlyphSet / ReferenceGlyphSet / FreeGlyphSet / AddGlyphs / FreeGlyphs /
//   CompositeGlyphs8/16/32」(GLYPHITEM、len = 255 时切换字形集;源与第一个元素的 delta 对齐)、
//   §13「CreateCursor / CreateAnimCursor」
//
//   不做的:alpha-map(接受但忽略)、源 picture 的裁剪(只裁目标)、索引色格式(没有)、
//   poly-edge / poly-mode / dither(接受但忽略,多边形一律平滑边)。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private const byte RenderMajor = 133;
    private const byte RenderErrorBase = 133;   // PictFormat +0、Picture +1、PictOp +2、GlyphSet +3、Glyph +4

    private static readonly string[] RenderFilters = ["nearest", "bilinear", "convolution", "fast", "good", "best"];

    private static XProtocolError RenderError(int offset, uint value = 0) => new((XErrorCode)(RenderErrorBase + offset), value);

    private XPicture Picture(uint id) => Lookup<XPicture>(id) ?? throw RenderError(1, id);

    private static PictFormat Format(uint id) => PictFormat.ById(id) ?? throw RenderError(0, id);

    private XGlyphSet GlyphSet(uint id) => Lookup<XGlyphSet>(id) ?? throw RenderError(3, id);

    private static void CheckOp(byte op)
    {
        if (!RenderOps.IsValid(op))
        {
            throw RenderError(2, op);
        }
    }

    private void Render(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0: RenderQueryVersion(c, r); break;
            case 1: QueryPictFormats(c); break;
            case 2: throw RenderError(0, r.U32());   // QueryPictIndexValues:没有索引色格式
            case 4: CreatePicture(c, r); break;
            case 5: ApplyPictureValues(Picture(r.U32()), r.U32(), r); break;
            case 6: SetPictureClipRectangles(r); break;
            case 7: FreePicture(r); break;
            case 8: CompositeRequest(r); break;
            case 10: Trapezoids(r); break;
            case 11: case 12: case 13: Triangles(r); break;
            case 17: CreateGlyphSet(c, r); break;
            case 18: ReferenceGlyphSet(c, r); break;
            case 19: FreeGlyphSet(r); break;
            case 20: AddGlyphs(r); break;
            case 22: FreeGlyphs(r); break;
            case 23: case 24: case 25: CompositeGlyphs(r); break;
            case 26: FillRectangles(r); break;
            case 27: RenderCreateCursor(c, r); break;
            case 28: SetPictureTransform(r); break;
            case 29: QueryFilters(c, r); break;
            case 30: SetPictureFilter(r); break;
            case 31: CreateAnimCursor(c, r); break;
            case 32: AddTraps(r); break;
            case 33: case 34: case 35: case 36: CreateFillPicture(c, r); break;
            default: throw new XProtocolError(XErrorCode.Request);   // 3、9、14–16、21:早已废弃
        }
    }

    // ------------------------------------------------------------------ 版本与格式

    private static void RenderQueryVersion(XClient c, XRequestReader r)
    {
        uint major = r.U32(), minor = r.U32();
        (uint maj, uint min) = major > 0 || minor >= 11 ? (0u, 11u) : (major, minor);
        c.Reply(0, w => w.U32(maj).U32(min).Zero(16));
    }

    private static void QueryPictFormats(XClient c)
    {
        IReadOnlyList<PictFormat> formats = PictFormat.All;
        byte[] depths = [24, 1, 4, 8, 15, 16, 32];   // 与连接建立回复的 DEPTH 列表一致
        c.Reply(0, w =>
        {
            w.U32((uint)formats.Count).U32(1).U32((uint)depths.Length).U32(2).U32(1).Zero(4);
            foreach (PictFormat f in formats)
            {
                w.U32(f.Id).U8(1).U8(f.Depth).Zero(2)   // type = Direct
                    .U16(f.RedShift).U16(f.RedMask).U16(f.GreenShift).U16(f.GreenMask)
                    .U16(f.BlueShift).U16(f.BlueMask).U16(f.AlphaShift).U16(f.AlphaMask)
                    .U32(0);                            // colormap:Direct 格式没有
            }
            w.U32((uint)depths.Length).U32(PictFormat.X8R8G8B8.Id);   // PICTSCREEN:深度数、fallback
            foreach (byte depth in depths)
            {
                (uint Visual, PictFormat Format)? visual = depth switch
                {
                    24 => (RootVisualId, PictFormat.X8R8G8B8),
                    32 => (ArgbVisualId, PictFormat.A8R8G8B8),
                    _ => null,
                };
                w.U8(depth).Zero(1).U16((ushort)(visual is null ? 0 : 1)).Zero(4);
                if (visual is { } v)
                {
                    w.U32(v.Visual).U32(v.Format.Id);
                }
            }
            w.U32(0);   // subpixel:Unknown
        });
    }

    // ------------------------------------------------------------------ picture

    private void CreatePicture(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        uint drawableId = r.U32();
        PictFormat format = Format(r.U32());
        XResource drawable = Lookup<XResource>(drawableId) switch
        {
            XPixmap p => p,
            XWindow { IsInputOnly: false } w => w,
            XWindow => throw new XProtocolError(XErrorCode.Match),
            _ => throw new XProtocolError(XErrorCode.Drawable, drawableId),
        };
        byte depth = drawable switch
        {
            XPixmap p => p.Depth,
            XWindow { IsRoot: true } => 24,
            XWindow w => w.Depth,
            _ => 0,
        };
        if (format.Depth != depth)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        XPicture picture = new(id, c) { Drawable = drawable, Format = format };
        ApplyPictureValues(picture, r.U32(), r);
        AddResource(c, picture);
    }

    private void ApplyPictureValues(XPicture p, uint mask, XRequestReader r)
    {
        for (int bit = 0; bit < 13; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }
            uint v = r.U32();
            switch (bit)
            {
                case 0:
                    if (v > 3)
                    {
                        throw new XProtocolError(XErrorCode.Value, v);
                    }
                    p.Repeat = (byte)v;
                    break;
                case 1:   // alpha-map:接受但不用
                    if (v != 0)
                    {
                        _ = Picture(v);
                    }
                    break;
                case 4: p.ClipX = (int)v; break;
                case 5: p.ClipY = (int)v; break;
                case 6:
                    if (v == 0)
                    {
                        p.Clip = null;
                        break;
                    }
                    XPixmap clip = Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    if (clip.Depth != 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    p.Clip = RegionFromBitmap(clip.Buffer);
                    break;
                case 8: p.SubwindowMode = (byte)v; break;
                case 12: p.ComponentAlpha = v != 0; break;
                default:
                    break;   // alpha 原点、graphics-exposures、poly-edge、poly-mode、dither:不影响我们的输出
            }
        }
    }

    private void SetPictureClipRectangles(XRequestReader r)
    {
        XPicture p = Picture(r.U32());
        p.ClipX = r.I16();
        p.ClipY = r.I16();
        Region clip = new();
        while (r.Remaining >= 8)
        {
            clip.Union(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
        }
        p.Clip = clip;
    }

    private void FreePicture(XRequestReader r)
    {
        uint id = r.U32();
        _ = Picture(id);
        RemoveResource(id);
    }

    private void SetPictureTransform(XRequestReader r)
    {
        XPicture p = Picture(r.U32());
        double[] m = new double[9];
        for (int i = 0; i < 9; i++)
        {
            m[i] = r.I32() / 65536.0;
        }
        bool identity = m[0] == 1 && m[1] == 0 && m[2] == 0 && m[3] == 0 && m[4] == 1 && m[5] == 0 && m[6] == 0 && m[7] == 0 && m[8] == 1;
        p.Transform = identity ? null : m;
    }

    private static void QueryFilters(XClient c, XRequestReader r)
    {
        _ = r.U32();
        // 别名:fast → nearest、good / best → bilinear;其余不是别名(0xFFFF)。
        ushort[] aliases = [0xFFFF, 0xFFFF, 0xFFFF, 0, 1, 1];
        c.Reply(0, w =>
        {
            w.U32((uint)aliases.Length).U32((uint)RenderFilters.Length).Zero(16);
            foreach (ushort alias in aliases)
            {
                w.U16(alias);
            }
            foreach (string name in RenderFilters)
            {
                byte[] bytes = XWire.Latin1.GetBytes(name);
                w.U8((byte)bytes.Length).Bytes(bytes);
            }
            w.Pad4();
        });
    }

    private void SetPictureFilter(XRequestReader r)
    {
        XPicture p = Picture(r.U32());
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        p.Bilinear = name switch
        {
            "nearest" or "fast" => false,
            "bilinear" or "good" or "best" or "convolution" => true,   // 卷积核按双线性近似
            _ => throw new XProtocolError(XErrorCode.Name),
        };
    }

    private void CreateFillPicture(XClient c, XRequestReader r)
    {
        byte kind = r.Data;
        uint id = r.U32();
        RenderSource fill;
        if (kind == 33)   // CreateSolidFill:颜色是预乘的
        {
            fill = new SolidSource(ReadColor(r));
        }
        else
        {
            double Fixed() => r.I32() / 65536.0;
            (double, double) Point() => (Fixed(), Fixed());
            switch (kind)
            {
                case 34:
                    {
                        (double x1, double y1) = Point();
                        (double x2, double y2) = Point();
                        (double[] stops, Argb[] colors) = ReadStops(r);
                        fill = new LinearGradientSource(x1, y1, x2, y2, stops, colors);
                        break;
                    }
                case 35:
                    {
                        (double x1, double y1) = Point();
                        (double x2, double y2) = Point();
                        double r1 = Fixed(), r2 = Fixed();
                        (double[] stops, Argb[] colors) = ReadStops(r);
                        fill = new RadialGradientSource(x1, y1, r1, x2, y2, r2, stops, colors);
                        break;
                    }
                default:
                    {
                        (double cx, double cy) = Point();
                        double angle = Fixed();
                        (double[] stops, Argb[] colors) = ReadStops(r);
                        fill = new ConicalGradientSource(cx, cy, angle, stops, colors);
                        break;
                    }
            }
        }
        AddResource(c, new XPicture(id, c) { Fill = fill });
    }

    private static Argb ReadColor(XRequestReader r)
    {
        float red = r.U16() / 65535f, green = r.U16() / 65535f, blue = r.U16() / 65535f, alpha = r.U16() / 65535f;
        return new Argb(alpha, red, green, blue);
    }

    /// <summary>色标:n 个位置(FIXED),再 n 个颜色(非预乘)。</summary>
    private static (double[] Stops, Argb[] Colors) ReadStops(XRequestReader r)
    {
        int count = (int)r.U32();
        if ((long)count * 12 > r.Remaining)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        double[] stops = new double[count];
        for (int i = 0; i < count; i++)
        {
            stops[i] = r.I32() / 65536.0;
        }
        Argb[] colors = new Argb[count];
        for (int i = 0; i < count; i++)
        {
            colors[i] = ReadColor(r);
        }
        return (stops, colors);
    }

    // ------------------------------------------------------------------ 取样源与目标

    private static RenderSource SourceOf(XPicture p)
    {
        RenderSource source = p.Fill ?? p.Drawable switch
        {
            XPixmap px => new ImageSource(px.Buffer, 0, 0, px.Width, px.Height, p.Format!),
            XWindow { IsRoot: false, IsViewable: true, TopLevel.Buffer: { } buffer } w => WindowSource(w, buffer, p.Format!),
            _ => new SolidSource(default),   // 根窗口与不可见窗口:没有可读的内容
        };
        source.Repeat = p.Repeat;
        source.Transform = p.Transform;
        source.Bilinear = p.Bilinear;
        return source;
    }

    private static ImageSource WindowSource(XWindow w, PixelBuffer buffer, PictFormat format)
    {
        (int ox, int oy) = w.OffsetInTopLevel();
        return new ImageSource(buffer, ox, oy, w.Width, w.Height, format);
    }

    /// <summary>目标:可写区域 = 可绘对象范围 ∩ 窗口可见部分 ∩ picture 的裁剪。画不了(未映射等)时为 null。</summary>
    private static (RenderTarget Target, XWindow? TopLevel)? TargetOf(XPicture p)
    {
        if (p.Drawable is null || p.Format is null)
        {
            throw new XProtocolError(XErrorCode.Match);   // 纯色 / 渐变只能当源
        }
        PixelBuffer buffer;
        int ox = 0, oy = 0;
        Region region;
        XWindow? top = null;
        switch (p.Drawable)
        {
            case XPixmap px:
                buffer = px.Buffer;
                region = new Region(buffer.Bounds);
                break;
            case XWindow w when !w.IsRoot && w.IsViewable && w.TopLevel is { Buffer: { } b } t:
                buffer = b;
                (ox, oy) = w.OffsetInTopLevel();
                region = (p.SubwindowMode == 1 ? VisibleInner(w) : ClipByChildren(w)).Clone();
                top = t;
                break;
            default:
                return null;
        }
        if (p.Clip is { } clip)
        {
            region.Intersect(clip.Clone().Translate(p.ClipX + ox, p.ClipY + oy));
        }
        return region.IsEmpty ? null : (new RenderTarget(buffer, ox, oy, p.Format, [.. region.Rects]), top);
    }

    private void CompositeTo(XPicture dst, byte op, RenderSource src, RenderSource? mask, bool componentAlpha,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, int width, int height)
    {
        if (width <= 0 || height <= 0 || TargetOf(dst) is not { } target)
        {
            return;
        }
        XRect dirty = RenderCompositor.Composite(op, src, mask, componentAlpha, target.Target,
            srcX, srcY, maskX, maskY, dstX, dstY, width, height);
        if (target.TopLevel is { } top && !dirty.IsEmpty)
        {
            MarkDamage(top, dirty);
        }
    }

    // ------------------------------------------------------------------ Composite / FillRectangles

    private void CompositeRequest(XRequestReader r)
    {
        byte op = r.U8();
        r.Skip(3);
        XPicture src = Picture(r.U32());
        uint maskId = r.U32();
        XPicture? mask = maskId == 0 ? null : Picture(maskId);
        XPicture dst = Picture(r.U32());
        short srcX = r.I16(), srcY = r.I16(), maskX = r.I16(), maskY = r.I16(), dstX = r.I16(), dstY = r.I16();
        ushort width = r.U16(), height = r.U16();
        CheckOp(op);
        // 分量 alpha 只对有颜色通道的遮罩有意义(纯色 / 渐变也算有)。
        bool componentAlpha = mask is { ComponentAlpha: true } && (mask.Format?.HasColor ?? true);
        CompositeTo(dst, op, SourceOf(src), mask is null ? null : SourceOf(mask), componentAlpha,
            srcX, srcY, maskX, maskY, dstX, dstY, width, height);
    }

    private void FillRectangles(XRequestReader r)
    {
        byte op = r.U8();
        r.Skip(3);
        XPicture dst = Picture(r.U32());
        Argb color = ReadColor(r);
        CheckOp(op);
        SolidSource source = new(color);
        while (r.Remaining >= 8)
        {
            short x = r.I16(), y = r.I16();
            ushort w = r.U16(), h = r.U16();
            CompositeTo(dst, op, source, null, false, 0, 0, 0, 0, x, y, w, h);
        }
    }

    // ------------------------------------------------------------------ 梯形与三角形

    private static double ReadFixed(XRequestReader r) => r.I32() / 65536.0;

    /// <summary>
    /// 几何光栅化成覆盖率后合成。有遮罩格式时所有图元累加进一张遮罩、合成一次;
    /// 没有时每个图元单独合成(规范 §10)。源与第一个顶点(取整)对齐。
    /// </summary>
    private void CompositeShapes(byte op, XPicture src, XPicture dst, PictFormat? maskFormat, int srcX, int srcY,
        (int X, int Y) anchor, List<(XRect Bounds, Action<CoverageMask> Draw)> shapes)
    {
        CheckOp(op);
        if (shapes.Count == 0 || dst.Drawable is null)
        {
            return;
        }
        RenderSource source = SourceOf(src);
        XRect limit = DrawableBounds(dst);
        void Emit(XRect bounds, IEnumerable<Action<CoverageMask>> draws)
        {
            bounds = bounds.Intersect(limit);
            if (bounds.IsEmpty)
            {
                return;
            }
            CoverageMask coverage = new(bounds);
            foreach (Action<CoverageMask> draw in draws)
            {
                draw(coverage);
            }
            ArraySource mask = coverage.ToSource(maskFormat?.Depth == 1);
            CompositeTo(dst, op, source, mask, false,
                srcX + bounds.X - anchor.X, srcY + bounds.Y - anchor.Y, bounds.X, bounds.Y, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        if (maskFormat is not null)
        {
            XRect all = shapes[0].Bounds;
            foreach ((XRect b, _) in shapes)
            {
                all = Union(all, b);
            }
            Emit(all, shapes.Select(s => s.Draw));
        }
        else
        {
            foreach ((XRect b, Action<CoverageMask> draw) in shapes)
            {
                Emit(b, [draw]);
            }
        }
    }

    private static XRect Union(XRect a, XRect b)
    {
        int x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
        return new XRect(x1, y1, Math.Max(a.Right, b.Right) - x1, Math.Max(a.Bottom, b.Bottom) - y1);
    }

    private static XRect BoundsOf(params ReadOnlySpan<(double X, double Y)> points)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        foreach ((double x, double y) in points)
        {
            x1 = Math.Min(x1, x);
            y1 = Math.Min(y1, y);
            x2 = Math.Max(x2, x);
            y2 = Math.Max(y2, y);
        }
        int ix = (int)Math.Floor(x1), iy = (int)Math.Floor(y1);
        return new XRect(ix, iy, (int)Math.Ceiling(x2) - ix, (int)Math.Ceiling(y2) - iy);
    }

    private static XRect DrawableBounds(XPicture p) => p.Drawable switch
    {
        XPixmap px => px.Buffer.Bounds,
        XWindow w => new XRect(0, 0, w.Width, w.Height),
        _ => default,
    };

    private void Trapezoids(XRequestReader r)
    {
        byte op = r.U8();
        r.Skip(3);
        XPicture src = Picture(r.U32());
        XPicture dst = Picture(r.U32());
        uint maskFormatId = r.U32();
        PictFormat? maskFormat = maskFormatId == 0 ? null : Format(maskFormatId);
        short srcX = r.I16(), srcY = r.I16();
        List<(XRect, Action<CoverageMask>)> shapes = [];
        (int X, int Y) anchor = (0, 0);
        while (r.Remaining >= 40)
        {
            double top = ReadFixed(r), bottom = ReadFixed(r);
            CoverageMask.Line left = new(ReadFixed(r), ReadFixed(r), ReadFixed(r), ReadFixed(r));
            CoverageMask.Line right = new(ReadFixed(r), ReadFixed(r), ReadFixed(r), ReadFixed(r));
            if (shapes.Count == 0)
            {
                anchor = ((int)Math.Floor(left.X1), (int)Math.Floor(left.Y1));
            }
            if (bottom <= top)
            {
                continue;
            }
            XRect bounds = BoundsOf((left.XAt(top), top), (left.XAt(bottom), bottom), (right.XAt(top), top), (right.XAt(bottom), bottom));
            shapes.Add((bounds, m => m.AddTrapezoid(top, bottom, left, right)));
        }
        CompositeShapes(op, src, dst, maskFormat, srcX, srcY, anchor, shapes);
    }

    /// <summary>Triangles(11)、TriStrip(12)、TriFan(13):都拆成三角形。</summary>
    private void Triangles(XRequestReader r)
    {
        byte kind = r.Data;
        byte op = r.U8();
        r.Skip(3);
        XPicture src = Picture(r.U32());
        XPicture dst = Picture(r.U32());
        uint maskFormatId = r.U32();
        PictFormat? maskFormat = maskFormatId == 0 ? null : Format(maskFormatId);
        short srcX = r.I16(), srcY = r.I16();
        List<(double X, double Y)> points = [];
        while (r.Remaining >= 8)
        {
            points.Add((ReadFixed(r), ReadFixed(r)));
        }
        List<((double, double) A, (double, double) B, (double, double) C)> triangles = [];
        switch (kind)
        {
            case 11:
                for (int i = 0; i + 2 < points.Count; i += 3)
                {
                    triangles.Add((points[i], points[i + 1], points[i + 2]));
                }
                break;
            case 12:
                for (int i = 0; i + 2 < points.Count; i++)
                {
                    triangles.Add((points[i], points[i + 1], points[i + 2]));
                }
                break;
            default:
                for (int i = 1; i + 1 < points.Count; i++)
                {
                    triangles.Add((points[0], points[i], points[i + 1]));
                }
                break;
        }
        (int, int) anchor = points.Count > 0 ? ((int)Math.Floor(points[0].X), (int)Math.Floor(points[0].Y)) : (0, 0);
        List<(XRect, Action<CoverageMask>)> shapes =
            [.. triangles.Select(t => (BoundsOf(t.A, t.B, t.C), (Action<CoverageMask>)(m => m.AddTriangle(t.A, t.B, t.C))))];
        CompositeShapes(op, src, dst, maskFormat, srcX, srcY, anchor, shapes);
    }

    /// <summary>AddTraps:把梯形的覆盖率直接 Add 进一个只有 alpha 的 picture。</summary>
    private void AddTraps(XRequestReader r)
    {
        XPicture dst = Picture(r.U32());
        short xOff = r.I16(), yOff = r.I16();
        if (dst.Format is not { HasColor: false })
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        List<(XRect, Action<CoverageMask>)> shapes = [];
        while (r.Remaining >= 24)
        {
            double topL = ReadFixed(r) + xOff, topR = ReadFixed(r) + xOff, topY = ReadFixed(r) + yOff;
            double botL = ReadFixed(r) + xOff, botR = ReadFixed(r) + xOff, botY = ReadFixed(r) + yOff;
            if (botY <= topY)
            {
                continue;
            }
            CoverageMask.Line left = new(topL, topY, botL, botY), right = new(topR, topY, botR, botY);
            shapes.Add((BoundsOf((topL, topY), (topR, topY), (botL, botY), (botR, botY)), m => m.AddTrapezoid(topY, botY, left, right)));
        }
        if (shapes.Count == 0)
        {
            return;
        }
        XPicture white = new(0, null) { Fill = new SolidSource(Argb.Gray(1)) };
        CompositeShapes(RenderOps.Add, white, dst, dst.Format, 0, 0, (0, 0), shapes);
    }

    // ------------------------------------------------------------------ 字形

    private void CreateGlyphSet(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        PictFormat format = Format(r.U32());
        AddResource(c, new XGlyphSet(id, c, new GlyphTable(format)));
    }

    private void ReferenceGlyphSet(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        XGlyphSet existing = GlyphSet(r.U32());
        AddResource(c, new XGlyphSet(id, c, existing.Table));
    }

    private void FreeGlyphSet(XRequestReader r)
    {
        uint id = r.U32();
        _ = GlyphSet(id);
        RemoveResource(id);
    }

    private void AddGlyphs(XRequestReader r)
    {
        GlyphTable table = GlyphSet(r.U32()).Table;
        int count = (int)r.U32();
        if ((long)count * 16 > r.Remaining)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        uint[] ids = new uint[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = r.U32();
        }
        (ushort W, ushort H, short X, short Y, short XOff, short YOff)[] infos = new (ushort, ushort, short, short, short, short)[count];
        for (int i = 0; i < count; i++)
        {
            infos[i] = (r.U16(), r.U16(), r.I16(), r.I16(), r.I16(), r.I16());
        }
        PictFormat format = table.Format;
        int bpp = BitsPerPixel(format.Depth);
        for (int i = 0; i < count; i++)
        {
            (ushort w, ushort h, short x, short y, short xOff, short yOff) = infos[i];
            int size = BitmapStride(w * bpp) * h;
            if (size > r.Remaining)
            {
                throw new XProtocolError(XErrorCode.Length);
            }
            byte[] data = r.Bytes(size);
            uint[] raw = new uint[w * h];
            if (w > 0 && h > 0)
            {
                if (bpp == 1)
                {
                    DecodeBitmap(data, w, h, 0, raw, 1, 0);
                }
                else
                {
                    DecodeZPixmap(data, w, h, bpp, raw);
                }
            }
            Argb[] pixels = new Argb[raw.Length];
            for (int k = 0; k < raw.Length; k++)
            {
                pixels[k] = format.Decode(raw[k] & PixelBuffer.DepthMaskOf(format.Depth));
            }
            table.Glyphs[ids[i]] = new XRenderGlyph(w, h, x, y, xOff, yOff, pixels);
        }
    }

    private void FreeGlyphs(XRequestReader r)
    {
        GlyphTable table = GlyphSet(r.U32()).Table;
        while (r.Remaining >= 4)
        {
            uint id = r.U32();
            if (!table.Glyphs.Remove(id))
            {
                throw RenderError(4, id);
            }
        }
    }

    /// <summary>CompositeGlyphs8 / 16 / 32:按 GLYPHITEM 列表排字,逐个(或累加进遮罩后一次)合成。</summary>
    private void CompositeGlyphs(XRequestReader r)
    {
        int idSize = r.Data switch { 23 => 1, 24 => 2, _ => 4 };
        byte op = r.U8();
        r.Skip(3);
        XPicture src = Picture(r.U32());
        XPicture dst = Picture(r.U32());
        uint maskFormatId = r.U32();
        PictFormat? maskFormat = maskFormatId == 0 ? null : Format(maskFormatId);
        XGlyphSet set = GlyphSet(r.U32());
        short srcX = r.I16(), srcY = r.I16();
        CheckOp(op);

        // 先排版:每个字形落在目标上的位置。
        List<(int X, int Y, XRenderGlyph Glyph, bool Color)> placed = [];
        int penX = 0, penY = 0;
        bool first = true;
        (int X, int Y) anchor = (0, 0);
        while (r.Remaining >= 8)
        {
            byte count = r.U8();
            r.Skip(3);
            penX += r.I16();
            penY += r.I16();
            if (count == 255)
            {
                set = GlyphSet(r.U32());   // 切换字形集
                continue;
            }
            if (first)
            {
                anchor = (penX, penY);
                first = false;
            }
            int bytes = count * idSize;
            if (bytes > r.Remaining)
            {
                throw new XProtocolError(XErrorCode.Length);
            }
            for (int i = 0; i < count; i++)
            {
                uint id = idSize switch { 1 => r.U8(), 2 => r.U16(), _ => r.U32() };
                if (!set.Table.Glyphs.TryGetValue(id, out XRenderGlyph? glyph))
                {
                    continue;   // 没加过的字形:跳过(不画、不前进)
                }
                placed.Add((penX - glyph.X, penY - glyph.Y, glyph, set.Table.Format.HasColor));
                penX += glyph.XOff;
                penY += glyph.YOff;
            }
            r.Skip(XWire.Pad(bytes) - bytes);
        }
        if (placed.Count == 0 || dst.Drawable is null)
        {
            return;
        }

        RenderSource source = SourceOf(src);
        if (maskFormat is null)
        {
            foreach ((int x, int y, XRenderGlyph glyph, bool color) in placed)
            {
                if (glyph.Width == 0 || glyph.Height == 0)
                {
                    continue;
                }
                ArraySource mask = new(glyph.Pixels, x, y, glyph.Width, glyph.Height);
                CompositeTo(dst, op, source, mask, color, srcX + x - anchor.X, srcY + y - anchor.Y, x, y, x, y, glyph.Width, glyph.Height);
            }
            return;
        }

        // 有遮罩格式:所有字形 Add 进一张遮罩,再合成一次。
        XRect bounds = default;
        bool any = false;
        foreach ((int x, int y, XRenderGlyph glyph, _) in placed)
        {
            if (glyph.Width == 0 || glyph.Height == 0)
            {
                continue;
            }
            XRect g = new(x, y, glyph.Width, glyph.Height);
            bounds = any ? Union(bounds, g) : g;
            any = true;
        }
        bounds = bounds.Intersect(DrawableBounds(dst));
        if (!any || bounds.IsEmpty)
        {
            return;
        }
        Argb[] accum = new Argb[bounds.Width * bounds.Height];
        foreach ((int x, int y, XRenderGlyph glyph, _) in placed)
        {
            for (int gy = 0; gy < glyph.Height; gy++)
            {
                int my = y + gy - bounds.Y;
                if ((uint)my >= (uint)bounds.Height)
                {
                    continue;
                }
                for (int gx = 0; gx < glyph.Width; gx++)
                {
                    int mx = x + gx - bounds.X;
                    if ((uint)mx >= (uint)bounds.Width)
                    {
                        continue;
                    }
                    Argb p = glyph.Pixels[(gy * glyph.Width) + gx];
                    ref Argb m = ref accum[(my * bounds.Width) + mx];
                    m.A = Math.Min(1, m.A + p.A);
                    if (maskFormat.HasColor)
                    {
                        m.R = Math.Min(1, m.R + p.R);
                        m.G = Math.Min(1, m.G + p.G);
                        m.B = Math.Min(1, m.B + p.B);
                    }
                }
            }
        }
        // 按遮罩格式量化一遍(a1 二值、a4 16 级……),与真的画进那种格式的像素图一致。
        for (int i = 0; i < accum.Length; i++)
        {
            accum[i] = maskFormat.Decode(maskFormat.Encode(accum[i]));
        }
        ArraySource combined = new(accum, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        CompositeTo(dst, op, source, combined, maskFormat.HasColor,
            srcX + bounds.X - anchor.X, srcY + bounds.Y - anchor.Y, bounds.X, bounds.Y, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    // ------------------------------------------------------------------ 光标

    /// <summary>CreateCursor:ARGB 光标宿主暂时显示成默认箭头(与位图光标一样,字形号 −1)。</summary>
    private void RenderCreateCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        XPicture src = Picture(r.U32());
        if (src.Drawable is null)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        AddResource(c, new XCursor(id, c) { HotX = r.U16(), HotY = r.U16() });
    }

    private void CreateAnimCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        XCursor? first = null;
        while (r.Remaining >= 8)
        {
            uint cursor = r.U32();
            _ = r.U32();   // 帧间隔
            first ??= Lookup<XCursor>(cursor) ?? throw new XProtocolError(XErrorCode.Cursor, cursor);
        }
        AddResource(c, new XCursor(id, c) { Glyph = first?.Glyph ?? -1, HotX = first?.HotX ?? 0, HotY = first?.HotY ?? 0 });
    }

    /// <summary>XFIXES CreateRegionFromPicture:picture 的裁剪区域(没设裁剪时为空)。</summary>
    private Region PictureClipRegion(uint id)
    {
        XPicture p = Picture(id);
        return p.Clip is { } clip ? clip.Clone().Translate(p.ClipX, p.ClipY) : new Region();
    }

    /// <summary>XFIXES SetPictureClipRegion。</summary>
    private void SetPictureClipRegion(uint id, Region? region, int x, int y)
    {
        XPicture p = Picture(id);
        p.Clip = region;
        p.ClipX = x;
        p.ClipY = y;
    }
}
