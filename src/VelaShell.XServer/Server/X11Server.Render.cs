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
//   §13「CreateCursor / CreateAnimCursor」(光标的处理与交给宿主见 X11Server.Cursors.cs)
//
//   源 / 遮罩 picture 的裁剪(§7 clip-mask「restricts reads and writes … including sources」):Composite 里源与遮罩
//   没有变换、不重复时,裁剪之外的部分不合成;有变换或重复时、以及梯形 / 字形的源,仍只裁目标。
//   源窗口的 subwindow-mode 按规范忽略(被挡住的像素内容未定义)。
//   不做的:索引色格式(没有)、poly-edge / poly-mode / dither(接受但忽略,多边形一律平滑边)。

using System.Buffers;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private static readonly string[] RenderFilters = ["nearest", "bilinear", "convolution", "fast", "good", "best"];

    private static XProtocolError RenderError(int offset, uint value = 0) => new((XErrorCode)(RenderErrorBase + offset), value);

    private XPicture Picture(uint id) => Use<XPicture>(id) ?? throw RenderError(1, id);

    private static PictFormat Format(uint id) => PictFormat.ById(id) ?? throw RenderError(0, id);

    private XGlyphSet GlyphSet(uint id) => Use<XGlyphSet>(id) ?? throw RenderError(3, id);

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
            case 8: RenderComposite(r); break;
            case 10: Trapezoids(r); break;
            case 11: case 12: case 13: Triangles(r); break;
            case 17: CreateGlyphSet(c, r); break;
            case 18: ReferenceGlyphSet(c, r); break;
            case 19: FreeGlyphSet(r); break;
            case 20: AddGlyphs(c, r); break;
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
        XResource drawable = Use<XResource>(drawableId) switch
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
                case 1:   // alpha-map
                    if (v == 0)
                    {
                        p.AlphaMap = null;
                        break;
                    }
                    XPicture alphaMap = Picture(v);
                    if (ReferenceEquals(alphaMap, p) || alphaMap.Drawable is not XPixmap || alphaMap.AlphaMap is not null)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    p.AlphaMap = alphaMap;
                    break;
                case 2: p.AlphaX = (short)v; break;
                case 3: p.AlphaY = (short)v; break;
                case 4: p.ClipX = (int)v; break;
                case 5: p.ClipY = (int)v; break;
                case 6:
                    if (v == 0)
                    {
                        p.Clip = null;
                        break;
                    }
                    XPixmap clip = Use<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    if (clip.Depth != 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    p.Clip = Exact(RegionFromBitmap(clip.Buffer));
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
        List<XRect> rects = [];
        while (r.Remaining >= 8)
        {
            rects.Add(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
        }
        p.Clip = Exact(Region.FromRects(rects));
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
        uint count = r.U32();   // 按无符号读:当成 int 时 2³¹ 以上是负数,检查放行、随后分配抛异常(BadImplementation)
        if (count * 12L > r.Remaining)
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        double[] stops = new double[count];
        for (int i = 0; i < count; i++)
        {
            int raw = r.I32();
            stops[i] = raw / 65536.0;
            // RENDER「CreateLinearGradient」等:色标要在 0–1 之间、按大小排好,否则 Value 错误。相等的色标照收(硬过渡,cairo 会这么发)。
            if (stops[i] is < 0 or > 1 || (i > 0 && stops[i] < stops[i - 1]))
            {
                throw new XProtocolError(XErrorCode.Value, unchecked((uint)raw));
            }
        }
        var colors = new Argb[count];
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
        // alpha-map 只作用一层:作 alpha-map 用的 picture 只取它的像素图(它自己的 alpha-map、变换、repeat 都不算)。ChangePicture 只核
        // 新挂上的那张有没有 alpha-map,拦不住「先 P1 → P2、再 P2 → P3 ……」一张张接下去的长链;顺着链往下解,每一环一层递归,
        // 栈溢出在 .NET 里接不住,整个进程会崩。变换与过滤作用在 drawable 与 alpha-map 拼好的那张上(见 AlphaMapSource)。
        if (p.AlphaMap is { Drawable: XPixmap alphaPixmap } alphaMap)
        {
            source.Transform = null;
            source.Bilinear = false;
            return new AlphaMapSource(source, AlphaMapImage(alphaMap, alphaPixmap), p.AlphaX, p.AlphaY)
            {
                Transform = p.Transform,
                Bilinear = p.Bilinear,
            };
        }
        source.Transform = p.Transform;
        source.Bilinear = p.Bilinear;
        return source;
    }

    /// <summary>作 alpha-map 用的 picture 的像素:原样,不变换、不重复(规范:「Rendering is additionally clipped by the geometry ... of alpha-map」)。</summary>
    private static ImageSource AlphaMapImage(XPicture alphaMap, XPixmap pixmap) =>
        new(pixmap.Buffer, 0, 0, pixmap.Width, pixmap.Height, alphaMap.Format!);

    /// <summary>alpha-map 限制读写的范围(alpha-map 自己的坐标):像素图的范围 ∩ 它的 clip-mask。</summary>
    private static Region AlphaMapArea(XPicture alphaMap, XPixmap pixmap)
    {
        Region area = new(new XRect(0, 0, pixmap.Width, pixmap.Height));
        return alphaMap.Clip is { } clip ? area.Intersect(clip.Clone().Translate(alphaMap.ClipX, alphaMap.ClipY)) : area;
    }

    private static ImageSource WindowSource(XWindow w, PixelBuffer buffer, PictFormat format)
    {
        (int ox, int oy) = w.OffsetInTopLevel();
        return new ImageSource(buffer, ox, oy, w.Width, w.Height, format);
    }

    /// <summary>目标:可写区域 = 可绘对象范围 ∩ 窗口可见部分 ∩ picture 的裁剪(∩ <paramref name="readable" />,目标坐标)。画不了(未映射等)时为 null。</summary>
    private (RenderTarget Target, XWindow? TopLevel)? TargetOf(XPicture p, Region? readable = null)
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
                region = CachedClip(w, includeInferiors: p.SubwindowMode == 1).Clone();
                top = t;
                break;
            default:
                return null;
        }
        if (p.Clip is { } clip)
        {
            region.Intersect(clip.Clone().Translate(p.ClipX + ox, p.ClipY + oy));
        }
        if (p.AlphaMap is { Drawable: XPixmap alphaPixmap } alphaMap)
        {
            // 写进挂了 alpha-map 的目标也受 alpha-map 的范围与裁剪限制(合成见 CompositeOnto)。
            region.Intersect(AlphaMapArea(alphaMap, alphaPixmap).Translate(p.AlphaX + ox, p.AlphaY + oy));
        }
        if (readable is not null)
        {
            region.Intersect(readable.Clone().Translate(ox, oy));
        }
        return region.IsEmpty ? null : (new RenderTarget(buffer, ox, oy, p.Format, [.. region.Rects]), top);
    }

    /// <summary>
    /// 源 / 遮罩 picture 的裁剪换到目标坐标(源的 (0, 0) 对着目标的 (<paramref name="dx" />, <paramref name="dy" />)):RENDER 规范说 clip-mask
    /// 限制对这个 picture 的读写,裁剪之外的源像素读不到,对应的目标像素就不合成。只在源没有变换、不重复时这样做 ——
    /// 有变换或重复时读到的源像素与目标不是一一平移的关系,仍只裁目标。没有裁剪时为 null。
    /// 挂了 alpha-map 的,alpha-map 的范围与裁剪也限制读(规范「CreatePicture」:「Rendering is additionally clipped by the geometry and clip mask
    /// of alpha-map」;alpha-map 不重复,所以源有 repeat 也照样裁);同样只在没有变换时 —— 有变换时 alpha-map 之外取到的 alpha 为 0。
    /// 同 <see cref="SourceOf" /> 只看一层。
    /// </summary>
    private static Region? ReadableIn(XPicture? p, int dx, int dy)
    {
        if (p is null)
        {
            return null;
        }

        Region? readable = p is { Clip: { } clip, Transform: null, Repeat: 0, Drawable: not null }
            ? clip.Clone().Translate(p.ClipX + dx, p.ClipY + dy)
            : null;
        if (p is { AlphaMap: { Drawable: XPixmap alphaPixmap } alphaMap, Transform: null })
        {
            Region alphaReadable = AlphaMapArea(alphaMap, alphaPixmap).Translate(dx + p.AlphaX, dy + p.AlphaY);
            readable = readable?.Intersect(alphaReadable) ?? alphaReadable;
        }
        return readable;
    }

    private void CompositeTo(XPicture dst, byte op, RenderSource src, RenderSource? mask, bool componentAlpha,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, int width, int height, Region? readable = null)
    {
        if (width <= 0 || height <= 0 || TargetOf(dst, readable) is not { } target)
        {
            return;
        }
        XRect dirty = CompositeOnto(dst, target.Target, op, src, mask, componentAlpha, srcX, srcY, maskX, maskY, dstX, dstY, width, height);
        NoteRendered(dst, target.TopLevel, dirty);
    }

    /// <summary>
    /// 合成到 <paramref name="dst" />(<paramref name="target" /> 是 <see cref="TargetOf" /> 给的目标),返回写过的范围(目标缓冲坐标)。
    /// 目标挂了 alpha-map 时(规范「CreatePicture」:alpha-map 的 alpha 通道「is used in place of any alpha channel contained within the drawable
    /// for all rendering operations」),把要写的这一块拼成一张临时的 a8r8g8b8 —— 颜色取自 drawable、alpha 取自 alpha-map ——
    /// 在它上面合成,再把颜色写回 drawable(drawable 自己的 alpha 通道不动)、alpha 写回 alpha-map。原先目标的 alpha-map 接受但不生效。
    /// 只拼请求的这一块与可写区域的交,工作量按两遍拷贝扣。
    /// </summary>
    private XRect CompositeOnto(XPicture dst, RenderTarget target, byte op, RenderSource src, RenderSource? mask, bool componentAlpha,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, int width, int height)
    {
        if (dst.AlphaMap is not { Drawable: XPixmap alphaPixmap } alphaMap)
        {
            return RenderCompositor.Composite(op, src, mask, componentAlpha, target, srcX, srcY, maskX, maskY, dstX, dstY, width, height);
        }
        XRect requested = new(dstX + target.OriginX, dstY + target.OriginY, width, height);
        List<XRect> clip = [];
        XRect box = default;
        foreach (XRect c in target.Clip)
        {
            XRect r = c.Intersect(requested);
            if (!r.IsEmpty)
            {
                clip.Add(r);
                box = box.IsEmpty ? r : Union(box, r);
            }
        }
        if (box.IsEmpty)
        {
            return default;
        }
        WorkBudget.Charge(2L * box.Width * box.Height);

        // 目标缓冲上的 (x, y) 对着 alpha-map 的 (x − ax, y − ay)。
        PixelBuffer buffer = target.Buffer, alphaBuffer = alphaPixmap.Buffer;
        PictFormat format = target.Format, alphaFormat = alphaMap.Format!;
        int ax = target.OriginX + dst.AlphaX, ay = target.OriginY + dst.AlphaY;
        PixelBuffer staged = new(box.Width, box.Height, 32);
        for (int y = box.Y; y < box.Bottom; y++)
        {
            for (int x = box.X; x < box.Right; x++)
            {
                Argb color = format.Decode(buffer.Get(x, y));
                color.A = alphaFormat.Decode(alphaBuffer.Get(x - ax, y - ay)).A;
                staged.Pixels[((y - box.Y) * box.Width) + (x - box.X)] = Argb8.Pack(color);
            }
        }
        RenderTarget stagedTarget = new(staged, target.OriginX - box.X, target.OriginY - box.Y, PictFormat.A8R8G8B8,
            [.. clip.Select(r => r.Offset(-box.X, -box.Y))]);
        XRect written = RenderCompositor.Composite(op, src, mask, componentAlpha, stagedTarget, srcX, srcY, maskX, maskY, dstX, dstY, width, height);
        if (written.IsEmpty)
        {
            return default;
        }
        written = written.Offset(box.X, box.Y);
        uint depthMask = buffer.DepthMask, alphaDepthMask = alphaBuffer.DepthMask;
        foreach (XRect c in clip)
        {
            XRect r = c.Intersect(written);
            for (int y = r.Y; y < r.Bottom; y++)
            {
                for (int x = r.X; x < r.Right; x++)
                {
                    Argb result = PictFormat.A8R8G8B8.Decode(staged.Pixels[((y - box.Y) * box.Width) + (x - box.X)]);
                    ref uint pixel = ref buffer.Pixels[(y * buffer.Width) + x];
                    Argb color = result with { A = format.Decode(pixel).A };
                    pixel = format.Encode(color) & depthMask;
                    if ((uint)(x - ax) < (uint)alphaBuffer.Width && (uint)(y - ay) < (uint)alphaBuffer.Height)
                    {
                        ref uint alpha = ref alphaBuffer.Pixels[((y - ay) * alphaBuffer.Width) + (x - ax)];
                        Argb kept = alphaFormat.Decode(alpha);
                        alpha = alphaFormat.Encode(kept with { A = result.A }) & alphaDepthMask;
                    }
                }
            }
        }
        NotePixmapDrawn(alphaPixmap, written.Offset(-ax, -ay).Intersect(new XRect(0, 0, alphaPixmap.Width, alphaPixmap.Height)));
        return written;
    }

    /// <summary>合成写过的范围(缓冲坐标)记成损伤:窗口记到顶层上,像素图交给 DAMAGE。</summary>
    private void NoteRendered(XPicture dst, XWindow? top, XRect dirty)
    {
        if (dirty.IsEmpty)
        {
            return;
        }
        if (top is not null)
        {
            MarkDamage(top, dirty);
        }
        else if (dst.Drawable is XPixmap pixmap)
        {
            NotePixmapDrawn(pixmap, dirty);
        }
    }

    // ------------------------------------------------------------------ Composite / FillRectangles

    private void RenderComposite(XRequestReader r)
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
        // 源与遮罩的裁剪也限制读:裁剪之外的像素读不到,目标上对应的地方不合成。
        Region? readable = ReadableIn(src, dstX - srcX, dstY - srcY);
        if (ReadableIn(mask, dstX - maskX, dstY - maskY) is { } maskReadable)
        {
            readable = readable?.Intersect(maskReadable) ?? maskReadable;
        }
        CompositeTo(dst, op, SourceOf(src), mask is null ? null : SourceOf(mask), componentAlpha,
            srcX, srcY, maskX, maskY, dstX, dstY, width, height, readable);
    }

    private void FillRectangles(XRequestReader r)
    {
        byte op = r.U8();
        r.Skip(3);
        XPicture dst = Picture(r.U32());
        Argb color = ReadColor(r);
        CheckOp(op);
        // 目标(可见区域 ∩ picture 裁剪)整个请求只算一次:cairo / Qt 清背景时一个请求里常有几十上百个矩形。
        if (r.Remaining < 8 || TargetOf(dst) is not { } target)
        {
            return;
        }
        SolidSource source = new(color);
        while (r.Remaining >= 8)
        {
            short x = r.I16(), y = r.I16();
            ushort w = r.U16(), h = r.U16();
            if (w == 0 || h == 0)
            {
                continue;
            }
            XRect dirty = CompositeOnto(dst, target.Target, op, source, null, false, 0, 0, 0, 0, x, y, w, h);
            NoteRendered(dst, target.TopLevel, dirty);
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
        if (TargetOf(dst) is not { } target)
        {
            return;
        }
        RenderSource source = SourceOf(src);
        // 覆盖率只算目标上真正可写的那一块:一个铺满大窗口的图形不会分配整窗大小的遮罩再大半丢掉。
        XRect limit = BoundsOf(target.Target.Clip).Offset(-target.Target.OriginX, -target.Target.OriginY).Intersect(DrawableBounds(dst));
        XRect dirty = default;
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
            ByteMaskSource mask = coverage.ToByteSource(maskFormat?.Depth ?? 8);
            XRect written = CompositeOnto(dst, target.Target, op, source, mask, false,
                srcX + bounds.X - anchor.X, srcY + bounds.Y - anchor.Y, bounds.X, bounds.Y, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            dirty = dirty.IsEmpty ? written : written.IsEmpty ? dirty : Union(dirty, written);
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
        NoteRendered(dst, target.TopLevel, dirty);
    }

    private static XRect BoundsOf(IReadOnlyList<XRect> rects)
    {
        if (rects.Count == 0)
        {
            return default;
        }
        XRect all = rects[0];
        for (int i = 1; i < rects.Count; i++)
        {
            all = Union(all, rects[i]);
        }
        return all;
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
        bool first = true;
        while (r.Remaining >= 40)
        {
            double top = ReadFixed(r), bottom = ReadFixed(r);
            CoverageMask.Line left = new(ReadFixed(r), ReadFixed(r), ReadFixed(r), ReadFixed(r));
            CoverageMask.Line right = new(ReadFixed(r), ReadFixed(r), ReadFixed(r), ReadFixed(r));
            if (first)
            {
                // 源与「第一个」梯形的左上顶点对齐 —— 哪怕它是退化的、不画(规范 §10)。
                anchor = ((int)Math.Floor(left.X1), (int)Math.Floor(left.Y1));
                first = false;
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

    private void AddGlyphs(XClient c, XRequestReader r)
    {
        GlyphTable table = GlyphSet(r.U32()).Table;
        uint count = r.U32();
        if (count * 16L > r.Remaining)
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
            // 按 long 算:32768² 的 a8r8g8b8 字形是 4 GB,按 int 算回绕成 0,长度检查放行、随后按宽 × 高分配 4 GB。
            long size = (long)BitmapStride(w * bpp) * h;
            if (size > r.Remaining)
            {
                throw new XProtocolError(XErrorCode.Length);
            }
            byte[] data = r.Bytes((int)size);
            // 字形位图记在加它的客户端名下(xs_plan X-2),在解码分配之前核账;同一个 ID 的旧字形退还。
            ChargeMemory(c, (long)w * h * (format.HasColor ? 4 : 1));
            if (table.Glyphs.TryGetValue(ids[i], out XRenderGlyph? previous))
            {
                ReleaseGlyphs([previous]);
            }
            table.Glyphs[ids[i]] = format.HasColor
                ? new XRenderGlyph(w, h, x, y, xOff, yOff, null, DecodeColorGlyph(data, w, h, bpp, format)) { ChargedTo = c }
                : new XRenderGlyph(w, h, x, y, xOff, yOff, DecodeAlphaGlyph(data, w, h, bpp, format), null) { ChargedTo = c };
        }
    }

    /// <summary>只有 alpha 的字形:每像素一个字节(a8 直接拷,a4 放大到 0–255,a1 是 0 / 255)。</summary>
    private static byte[] DecodeAlphaGlyph(ReadOnlySpan<byte> data, int w, int h, int bpp, PictFormat format)
    {
        byte[] alpha = new byte[w * h];
        int stride = BitmapStride(w * bpp);
        for (int yy = 0; yy < h; yy++)
        {
            ReadOnlySpan<byte> row = data.Slice(yy * stride, stride);
            Span<byte> to = alpha.AsSpan(yy * w, w);
            if (bpp == 1)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    to[xx] = (row[xx >> 3] & (1 << (xx & 7))) != 0 ? (byte)255 : (byte)0;
                }
            }
            else if (format.Depth == 8)
            {
                row[..w].CopyTo(to);
            }
            else
            {
                for (int xx = 0; xx < w; xx++)
                {
                    to[xx] = (byte)((row[xx] & 0x0F) * 17);   // a4 存在 8 位像素的低 4 位
                }
            }
        }
        return alpha;
    }

    /// <summary>带颜色的字形:转成预乘的 0xAARRGGBB(a8r8g8b8 本来就是,其它格式经浮点换一次)。</summary>
    private static uint[] DecodeColorGlyph(ReadOnlySpan<byte> data, int w, int h, int bpp, PictFormat format)
    {
        uint[] pixels = new uint[w * h];
        if (w == 0 || h == 0)
        {
            return pixels;
        }
        DecodeZPixmap(data, w, h, bpp, pixels);
        if (!ReferenceEquals(format, PictFormat.A8R8G8B8))
        {
            uint mask = PixelBuffer.DepthMaskOf(format.Depth);
            for (int k = 0; k < pixels.Length; k++)
            {
                pixels[k] = PictFormat.A8R8G8B8.Encode(format.Decode(pixels[k] & mask));
            }
        }
        return pixels;
    }

    private void FreeGlyphs(XRequestReader r)
    {
        GlyphTable table = GlyphSet(r.U32()).Table;
        // 先全部核对(不存在的、或者列了两次的 —— 第二次时它已经释放了 —— 回 BadGlyph),再一起释放:出错的请求不产生效果。
        HashSet<uint> ids = [];
        while (r.Remaining >= 4)
        {
            uint id = r.U32();
            if (!table.Glyphs.ContainsKey(id) || !ids.Add(id))
            {
                throw RenderError(4, id);
            }
        }
        foreach (uint id in ids)
        {
            table.Glyphs.Remove(id, out XRenderGlyph? removed);
            ReleaseGlyphs([removed!]);
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
        if (placed.Count == 0 || dst.Drawable is null || TargetOf(dst) is not { } target)
        {
            return;
        }

        RenderSource source = SourceOf(src);
        XRect dirty = default;
        void Accumulate(XRect written) => dirty = dirty.IsEmpty ? written : written.IsEmpty ? dirty : Union(dirty, written);
        if (maskFormat is null)
        {
            // 每个字形单独当遮罩合成(规范 §12);目标只算一次、损伤最后记一次。
            foreach ((int x, int y, XRenderGlyph glyph, bool color) in placed)
            {
                if (glyph.Width == 0 || glyph.Height == 0)
                {
                    continue;
                }
                RenderSource mask = glyph.Alpha is { } alpha
                    ? new ByteMaskSource(alpha, x, y, glyph.Width, glyph.Height)
                    : new ColorMaskSource(glyph.Color!, x, y, glyph.Width, glyph.Height);
                Accumulate(CompositeOnto(dst, target.Target, op, source, mask, color,
                    srcX + x - anchor.X, srcY + y - anchor.Y, x, y, x, y, glyph.Width, glyph.Height));
            }
            NoteRendered(dst, target.TopLevel, dirty);
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
        // 遮罩只覆盖目标上真正可写的那一块(同 CompositeShapes):两个相距很远的字形原先按外接矩形分配,32000² 的窗口上就是 1 GB。
        XRect writable = BoundsOf(target.Target.Clip).Offset(-target.Target.OriginX, -target.Target.OriginY);
        bounds = bounds.Intersect(DrawableBounds(dst)).Intersect(writable);
        if (!any || bounds.IsEmpty)
        {
            return;
        }
        // 先按遮罩的大小扣工作量(带颜色的遮罩每像素 16 字节,按 4 倍算),再分配。
        WorkBudget.Charge((long)bounds.Width * bounds.Height * (maskFormat.HasColor || placed.Any(p => p.Glyph.Alpha is null) ? 4 : 1));
        if (!maskFormat.HasColor && placed.All(p => p.Glyph.Alpha is not null))
        {
            // 常态(Xft):只有 alpha 的字形累加进只有 alpha 的遮罩 —— 字节饱和加,池化,整数快路径合成。
            int size = bounds.Width * bounds.Height;
            byte[] alphaMask = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                Array.Clear(alphaMask, 0, size);
                foreach ((int x, int y, XRenderGlyph glyph, _) in placed)
                {
                    AddGlyphAlpha(alphaMask, bounds, x, y, glyph);
                }
                QuantizeMask(alphaMask.AsSpan(0, size), maskFormat.Depth);
                ByteMaskSource combined = new(alphaMask, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                Accumulate(CompositeOnto(dst, target.Target, op, source, combined, false,
                    srcX + bounds.X - anchor.X, srcY + bounds.Y - anchor.Y, bounds.X, bounds.Y, bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(alphaMask);
            }
            NoteRendered(dst, target.TopLevel, dirty);
            return;
        }
        var accum = new Argb[bounds.Width * bounds.Height];
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
                    int gi = (gy * glyph.Width) + gx;
                    // 只有 alpha 的字形进带颜色的遮罩:alpha 复制到三个颜色通道,否则分量 alpha 全是 0、字形画不出来。
                    Argb p = glyph.Alpha is { } alphaBytes
                        ? Argb.Gray(alphaBytes[gi] / 255f)
                        : PictFormat.A8R8G8B8.Decode(glyph.Color![gi]);
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
        ArraySource colorMask = new(accum, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Accumulate(CompositeOnto(dst, target.Target, op, source, colorMask, maskFormat.HasColor,
            srcX + bounds.X - anchor.X, srcY + bounds.Y - anchor.Y, bounds.X, bounds.Y, bounds.X, bounds.Y, bounds.Width, bounds.Height));
        NoteRendered(dst, target.TopLevel, dirty);
    }

    /// <summary>把一个 alpha 字形饱和加进遮罩(遮罩覆盖 <paramref name="bounds" />,目标坐标)。</summary>
    private static void AddGlyphAlpha(byte[] mask, XRect bounds, int x, int y, XRenderGlyph glyph)
    {
        XRect g = new XRect(x, y, glyph.Width, glyph.Height).Intersect(bounds);
        byte[] alpha = glyph.Alpha!;
        for (int yy = g.Y; yy < g.Bottom; yy++)
        {
            int gRow = ((yy - y) * glyph.Width) + (g.X - x);
            int mRow = ((yy - bounds.Y) * bounds.Width) + (g.X - bounds.X);
            for (int i = 0; i < g.Width; i++)
            {
                int sum = mask[mRow + i] + alpha[gRow + i];
                mask[mRow + i] = (byte)(sum > 255 ? 255 : sum);
            }
        }
    }

    /// <summary>按遮罩格式的位数量化(a1 二值、a4 16 级),与真的画进那种格式的像素图一致;a8 不动。</summary>
    private static void QuantizeMask(Span<byte> mask, byte depth)
    {
        if (depth >= 8)
        {
            return;
        }
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = depth == 1
                ? (mask[i] >= 128 ? (byte)255 : (byte)0)
                : (byte)(((mask[i] * 15) + 127) / 255 * 17);
        }
    }

    // ------------------------------------------------------------------ 供 XFIXES 用

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
