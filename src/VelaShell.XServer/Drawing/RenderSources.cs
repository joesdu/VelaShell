// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §7「Picture」的 repeat(None / Normal / Pad / Reflect)、
//   transform(从目标坐标映射到源坐标的 3×3 定点矩阵,取样点是像素中心)、filter(nearest / bilinear
//   及其别名 fast / good / best);§11「Gradients」(CreateSolidFill、CreateLinearGradient、
//   CreateRadialGradient —— 两个圆之间的锥形插值、CreateConicalGradient;色标颜色非预乘,插值后再预乘)

using System.Buffers;

namespace VelaShell.XServer.Drawing;

/// <summary>合成时的一个取样源(源图或遮罩)。坐标是这个 picture 自己的坐标。</summary>
internal abstract class RenderSource
{
    public const byte RepeatNone = 0, RepeatNormal = 1, RepeatPad = 2, RepeatReflect = 3;

    /// <summary>3×3 行优先矩阵,把目标坐标映到源坐标;null = 单位矩阵。</summary>
    public double[]? Transform { get; set; }

    public byte Repeat { get; set; }

    /// <summary>双线性过滤;否则最近邻。只在有变换时起作用(整数平移下两者一样)。</summary>
    public bool Bilinear { get; set; }

    /// <summary>取一行:从 (x, y) 起连续 <paramref name="row" />.Length 个像素。</summary>
    public void FetchRow(int x, int y, Span<Argb> row)
    {
        if (Transform is not { } t)
        {
            FetchIntegerRow(x, y, row);
            return;
        }
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Map(t, x + i, y, out double sx, out double sy) ? Sample(sx, sy) : default;
        }
    }

    /// <summary>
    /// 取一行,直接给 8 位预乘的 0xAARRGGBB —— 合成器的整数路径用。默认逐像素取浮点再量化;
    /// 常用的源(纯色、像素缓冲、单字节遮罩、渐变)各自覆写成整数取样。
    /// </summary>
    public void FetchRow8888(int x, int y, Span<uint> row)
    {
        if (Transform is not { } t)
        {
            FetchIntegerRow8888(x, y, row);
            return;
        }
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Map(t, x + i, y, out double sx, out double sy) ? Sample8888(sx, sy) : 0;
        }
    }

    /// <summary>目标像素 (x, y) 的中心经变换落到源的哪一点;齐次坐标 w 为 0 时没有定义(取透明)。</summary>
    private static bool Map(double[] t, int x, int y, out double sx, out double sy)
    {
        double px = x + 0.5, py = y + 0.5;
        double w = (t[6] * px) + (t[7] * py) + t[8];
        if (w == 0)
        {
            (sx, sy) = (0, 0);
            return false;
        }
        sx = ((t[0] * px) + (t[1] * py) + t[2]) / w;
        sy = ((t[3] * px) + (t[4] * py) + t[5]) / w;
        return true;
    }

    /// <summary>没有变换时的取样:像素 (x + i, y) 的中心。</summary>
    protected virtual void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Sample(x + i + 0.5, y + 0.5);
        }
    }

    /// <summary>没有变换时的 8 位取样。</summary>
    protected virtual void FetchIntegerRow8888(int x, int y, Span<uint> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Sample8888(x + i + 0.5, y + 0.5);
        }
    }

    /// <summary>在连续坐标上取样(像素 (i, j) 的中心是 (i + 0.5, j + 0.5))。</summary>
    protected abstract Argb Sample(double x, double y);

    /// <summary>同 <see cref="Sample" />,给 8 位预乘的 0xAARRGGBB。</summary>
    protected virtual uint Sample8888(double x, double y) => Argb8.Pack(Sample(x, y));

    /// <summary>按 repeat 把整数坐标折回 [0, size);None 时越界返回 false。</summary>
    protected bool Wrap(ref int v, int size)
    {
        if ((uint)v < (uint)size)
        {
            return true;
        }
        switch (Repeat)
        {
            case RepeatNormal:
                v = ((v % size) + size) % size;
                return true;
            case RepeatPad:
                v = Math.Clamp(v, 0, size - 1);
                return true;
            case RepeatReflect:
                int period = size * 2;
                int m = ((v % period) + period) % period;
                v = m < size ? m : period - 1 - m;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>纯色(CreateSolidFill、FillRectangles)。</summary>
internal sealed class SolidSource(Argb color) : RenderSource
{
    private readonly uint _packed = Argb8.Pack(color);

    public Argb Color { get; } = color;

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row) => row.Fill(Color);

    protected override void FetchIntegerRow8888(int x, int y, Span<uint> row) => row.Fill(_packed);

    protected override Argb Sample(double x, double y) => Color;

    protected override uint Sample8888(double x, double y) => _packed;
}

/// <summary>以像素缓冲为内容的源:像素图,或窗口在其顶层缓冲里的那一块。</summary>
internal sealed class ImageSource(PixelBuffer buffer, int originX, int originY, int width, int height, PictFormat format) : RenderSource
{
    public PixelBuffer Buffer { get; } = buffer;

    public int OriginX { get; } = originX;

    public int OriginY { get; } = originY;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public PictFormat Format { get; } = format;

    /// <summary>
    /// 拷出一份不再与原缓冲共享的源,只拷取样 <paramref name="needed" />(变换前的 picture 坐标)时会读到的那一块。
    /// 源与目标是同一块缓冲时用 —— 合成要像「先读完源再写」。
    /// </summary>
    public ImageSource Detach(XRect needed)
    {
        XRect area = SampledArea(needed);
        PixelBuffer copy = new(Math.Max(1, area.Width), Math.Max(1, area.Height), Buffer.Depth);
        PixelBuffer.CopyRect(Buffer, OriginX + area.X, OriginY + area.Y, copy, 0, 0, area.Width, area.Height);
        return new ImageSource(copy, -area.X, -area.Y, Width, Height, Format) { Transform = Transform, Repeat = Repeat, Bilinear = Bilinear };
    }

    /// <summary>
    /// 取样 <paramref name="needed" /> 里的像素时会读到图像的哪一块(图像坐标,已与图像求交)。有变换时取四个角的像素中心变换后的外接矩形
    /// (投影变换把矩形映成四边形,只要齐次坐标 w 在矩形里不变号);双线性再往外扩一格;折回之后可能落到任何地方的方向取整条边。
    /// 原先有变换或重复就整张拷 —— 窗口的 picture 至多 256 MB,每条请求拷一遍。
    /// </summary>
    internal XRect SampledArea(XRect needed)
    {
        if (needed.IsEmpty || Width <= 0 || Height <= 0)
        {
            return default;
        }
        long x0, y0, x1, y1;   // 读到的像素坐标(含两端)
        if (Transform is not { } t)
        {
            (x0, y0, x1, y1) = (needed.X, needed.Y, needed.Right - 1L, needed.Bottom - 1L);
        }
        else
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            int sign = 0;
            foreach ((double px, double py) in (ReadOnlySpan<(double, double)>)[(needed.X + 0.5, needed.Y + 0.5), (needed.Right - 0.5, needed.Y + 0.5),
                (needed.X + 0.5, needed.Bottom - 0.5), (needed.Right - 0.5, needed.Bottom - 0.5)])
            {
                double w = (t[6] * px) + (t[7] * py) + t[8];
                int s = Math.Sign(w);
                if (s == 0 || (sign != 0 && s != sign) || double.IsNaN(w))
                {
                    return new XRect(0, 0, Width, Height);   // 穿过无穷远:读哪里算不准
                }
                sign = s;
                double sx = ((t[0] * px) + (t[1] * py) + t[2]) / w, sy = ((t[3] * px) + (t[4] * py) + t[5]) / w;
                if (!double.IsFinite(sx) || !double.IsFinite(sy))
                {
                    return new XRect(0, 0, Width, Height);
                }
                (minX, minY, maxX, maxY) = (Math.Min(minX, sx), Math.Min(minY, sy), Math.Max(maxX, sx), Math.Max(maxY, sy));
            }
            // 最近邻读 floor(s);双线性读 floor(s − 0.5) 与它右(下)边一个。
            double shift = Bilinear ? 0.5 : 0;
            x0 = Floor(minX - shift);
            y0 = Floor(minY - shift);
            x1 = Floor(maxX - shift) + (Bilinear ? 1 : 0);
            y1 = Floor(maxY - shift) + (Bilinear ? 1 : 0);
        }
        (int ax, int aw) = AxisRange(x0, x1, Width);
        (int ay, int ah) = AxisRange(y0, y1, Height);
        return aw <= 0 || ah <= 0 ? default : new XRect(ax, ay, aw, ah);

        static long Floor(double v) => (long)Math.Floor(Math.Clamp(v, int.MinValue, int.MaxValue));
    }

    /// <summary>一个方向上读到 [lo, hi] 时,按 repeat 折回之后落在图像的哪一段。</summary>
    private (int Start, int Length) AxisRange(long lo, long hi, int size)
    {
        if (lo >= 0 && hi < size)
        {
            return ((int)lo, (int)(hi - lo + 1));
        }
        switch (Repeat)
        {
            case RepeatNone:
                {
                    long s = Math.Max(lo, 0), e = Math.Min(hi, size - 1L);
                    return e < s ? (0, 0) : ((int)s, (int)(e - s + 1));
                }
            case RepeatPad:
                {
                    long s = Math.Clamp(lo, 0, size - 1L), e = Math.Clamp(hi, 0, size - 1L);
                    return ((int)s, (int)(e - s + 1));
                }
            default:
                return (0, size);   // Normal / Reflect:越过边界就可能折到任何地方
        }
    }

    private Argb Texel(int x, int y) =>
        Wrap(ref x, Width) && Wrap(ref y, Height) ? Format.Decode(Buffer.Get(OriginX + x, OriginY + y)) : default;

    private uint Texel8888(int x, int y) =>
        Wrap(ref x, Width) && Wrap(ref y, Height) ? To8888(Buffer.Get(OriginX + x, OriginY + y)) : 0;

    /// <summary>存储的像素值换成 8 位预乘的 0xAARRGGBB:8888 与 a8(最常见的遮罩)直接换位,其余格式经浮点解码。</summary>
    private uint To8888(uint raw) =>
        ReferenceEquals(Format, PictFormat.A8R8G8B8) ? raw
        : ReferenceEquals(Format, PictFormat.X8R8G8B8) ? raw | 0xFF000000u
        : ReferenceEquals(Format, PictFormat.A8) ? (raw & 0xFF) << 24
        : Argb8.Pack(Format.Decode(raw));

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Texel(x + i, y);
        }
    }

    protected override void FetchIntegerRow8888(int x, int y, Span<uint> row)
    {
        // 整行都在图像与缓冲之内:按行读,不逐像素折回;a8r8g8b8 直接整行拷。
        int bx = OriginX + x, by = OriginY + y;
        if (x >= 0 && x + row.Length <= Width && (uint)y < (uint)Height
            && bx >= 0 && bx + row.Length <= Buffer.Width && (uint)by < (uint)Buffer.Height)
        {
            ReadOnlySpan<uint> from = Buffer.Pixels.AsSpan((by * Buffer.Width) + bx, row.Length);
            if (ReferenceEquals(Format, PictFormat.A8R8G8B8))
            {
                from.CopyTo(row);
                return;
            }
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = To8888(from[i]);
            }
            return;
        }
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Texel8888(x + i, y);
        }
    }

    protected override uint Sample8888(double x, double y)
    {
        if (!Bilinear)
        {
            return Texel8888((int)Math.Floor(x), (int)Math.Floor(y));
        }
        x -= 0.5;
        y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        // 权重取 0–256 的定点数:四个角按 (256 − fx)(256 − fy)… 加权,和是 65536。
        uint fx = (uint)(((x - x0) * 256) + 0.5), fy = (uint)(((y - y0) * 256) + 0.5);
        uint a = Texel8888(x0, y0), b = Texel8888(x0 + 1, y0), c = Texel8888(x0, y0 + 1), d = Texel8888(x0 + 1, y0 + 1);
        return (Lerp(a, b, c, d, fx, fy, 24) << 24) | (Lerp(a, b, c, d, fx, fy, 16) << 16)
            | (Lerp(a, b, c, d, fx, fy, 8) << 8) | Lerp(a, b, c, d, fx, fy, 0);

        static uint Lerp(uint a, uint b, uint c, uint d, uint fx, uint fy, int shift)
        {
            uint top = (((a >> shift) & 0xFF) * (256 - fx)) + (((b >> shift) & 0xFF) * fx);
            uint bottom = (((c >> shift) & 0xFF) * (256 - fx)) + (((d >> shift) & 0xFF) * fx);
            return ((top * (256 - fy)) + (bottom * fy) + 32768) >> 16;
        }
    }

    protected override Argb Sample(double x, double y)
    {
        if (!Bilinear)
        {
            return Texel((int)Math.Floor(x), (int)Math.Floor(y));
        }
        x -= 0.5;
        y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        Argb a = Texel(x0, y0), b = Texel(x0 + 1, y0), c = Texel(x0, y0 + 1), d = Texel(x0 + 1, y0 + 1);
        float wa = (1 - fx) * (1 - fy), wb = fx * (1 - fy), wc = (1 - fx) * fy, wd = fx * fy;
        return new Argb(
            (a.A * wa) + (b.A * wb) + (c.A * wc) + (d.A * wd),
            (a.R * wa) + (b.R * wb) + (c.R * wc) + (d.R * wd),
            (a.G * wa) + (b.G * wb) + (c.G * wc) + (d.G * wd),
            (a.B * wa) + (b.B * wb) + (c.B * wc) + (d.B * wd));
    }
}

/// <summary>服务端自己算出来的遮罩(梯形覆盖率、字形累加),放在目标坐标系里的一块矩形上;之外全透明。</summary>
internal sealed class ArraySource(Argb[] pixels, int x0, int y0, int width, int height) : RenderSource
{
    private Argb Texel(int x, int y)
    {
        x -= x0;
        y -= y0;
        return (uint)x < (uint)width && (uint)y < (uint)height ? pixels[(y * width) + x] : default;
    }

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Texel(x + i, y);
        }
    }

    protected override Argb Sample(double x, double y) => Texel((int)Math.Floor(x), (int)Math.Floor(y));
}

/// <summary>
/// 只有 alpha 的遮罩(字形、梯形覆盖率),每像素一个字节,放在遮罩坐标系里的一块矩形上;之外全透明。
/// 合成器认得它,走整数快路径;通用路径经 <see cref="RenderSource.FetchRow" /> 取。
/// </summary>
internal sealed class ByteMaskSource(byte[] alpha, int x0, int y0, int width, int height) : RenderSource
{
    private static readonly float[] ToFloat = BuildTable();

    public byte[] Alpha { get; } = alpha;

    public int X0 { get; } = x0;

    public int Y0 { get; } = y0;

    public int Width { get; } = width;

    public int Height { get; } = height;

    private static float[] BuildTable()
    {
        float[] table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = i / 255f;
        }
        return table;
    }

    public byte At(int x, int y)
    {
        x -= X0;
        y -= Y0;
        return (uint)x < (uint)Width && (uint)y < (uint)Height ? Alpha[(y * Width) + x] : (byte)0;
    }

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Argb.Gray(ToFloat[At(x + i, y)]);
        }
    }

    protected override void FetchIntegerRow8888(int x, int y, Span<uint> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = At(x + i, y) * 0x01010101u;
        }
    }

    protected override Argb Sample(double x, double y) => Argb.Gray(ToFloat[At((int)Math.Floor(x), (int)Math.Floor(y))]);

    protected override uint Sample8888(double x, double y) => At((int)Math.Floor(x), (int)Math.Floor(y)) * 0x01010101u;
}

/// <summary>把 alpha-map 应用到源 picture。alpha-map 的原点相对源 drawable 原点。</summary>
internal sealed class AlphaMapSource(RenderSource source, RenderSource alphaMap, int alphaX, int alphaY) : RenderSource
{
    protected override void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        Argb[] sourceRow = ArrayPool<Argb>.Shared.Rent(row.Length);
        Argb[] alphaRow = ArrayPool<Argb>.Shared.Rent(row.Length);
        try
        {
            source.FetchRow(x, y, sourceRow.AsSpan(0, row.Length));
            alphaMap.FetchRow(x - alphaX, y - alphaY, alphaRow.AsSpan(0, row.Length));
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = ReplaceAlpha(sourceRow[i], alphaRow[i].A);
            }
        }
        finally
        {
            ArrayPool<Argb>.Shared.Return(sourceRow);
            ArrayPool<Argb>.Shared.Return(alphaRow);
        }
    }

    protected override void FetchIntegerRow8888(int x, int y, Span<uint> row)
    {
        uint[] sourceRow = ArrayPool<uint>.Shared.Rent(row.Length);
        uint[] alphaRow = ArrayPool<uint>.Shared.Rent(row.Length);
        try
        {
            source.FetchRow8888(x, y, sourceRow.AsSpan(0, row.Length));
            alphaMap.FetchRow8888(x - alphaX, y - alphaY, alphaRow.AsSpan(0, row.Length));
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = ReplaceAlpha(sourceRow[i], alphaRow[i] >> 24);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(sourceRow);
            ArrayPool<uint>.Shared.Return(alphaRow);
        }
    }

    protected override Argb Sample(double x, double y)
    {
        Span<Argb> sourcePixel = stackalloc Argb[1];
        Span<Argb> alphaPixel = stackalloc Argb[1];
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        source.FetchRow(ix, iy, sourcePixel);
        alphaMap.FetchRow(ix - alphaX, iy - alphaY, alphaPixel);
        return ReplaceAlpha(sourcePixel[0], alphaPixel[0].A);
    }

    protected override uint Sample8888(double x, double y)
    {
        Span<uint> sourcePixel = stackalloc uint[1];
        Span<uint> alphaPixel = stackalloc uint[1];
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        source.FetchRow8888(ix, iy, sourcePixel);
        alphaMap.FetchRow8888(ix - alphaX, iy - alphaY, alphaPixel);
        return ReplaceAlpha(sourcePixel[0], alphaPixel[0] >> 24);
    }

    private static Argb ReplaceAlpha(Argb source, float alpha)
    {
        if (source.A <= 0 || alpha <= 0)
        {
            return default;
        }
        float scale = alpha / source.A;
        return new(alpha, source.R * scale, source.G * scale, source.B * scale);
    }

    private static uint ReplaceAlpha(uint source, uint alpha)
    {
        uint sourceAlpha = source >> 24;
        if (sourceAlpha == 0 || alpha == 0)
        {
            return 0;
        }
        static uint Rescale(uint channel, uint oldAlpha, uint newAlpha) =>
            Math.Min((channel * newAlpha) + (oldAlpha / 2), oldAlpha * 255u) / oldAlpha;
        return (alpha << 24)
            | (Rescale((source >> 16) & 0xFF, sourceAlpha, alpha) << 16)
            | (Rescale((source >> 8) & 0xFF, sourceAlpha, alpha) << 8)
            | Rescale(source & 0xFF, sourceAlpha, alpha);
    }
}

/// <summary>带颜色的遮罩(次像素字形,分量 alpha):每像素一个预乘的 0xAARRGGBB。</summary>
internal sealed class ColorMaskSource(uint[] pixels, int x0, int y0, int width, int height) : RenderSource
{
    private Argb Texel(int x, int y) => PictFormat.A8R8G8B8.Decode(Texel8888(x, y));

    /// <summary>存的就是 0xAARRGGBB:整数路径直接取(原先经浮点解码再量化回来,结果相同)。</summary>
    private uint Texel8888(int x, int y)
    {
        x -= x0;
        y -= y0;
        return (uint)x < (uint)width && (uint)y < (uint)height ? pixels[(y * width) + x] : 0;
    }

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Texel(x + i, y);
        }
    }

    protected override void FetchIntegerRow8888(int x, int y, Span<uint> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Texel8888(x + i, y);
        }
    }

    protected override Argb Sample(double x, double y) => Texel((int)Math.Floor(x), (int)Math.Floor(y));

    protected override uint Sample8888(double x, double y) => Texel8888((int)Math.Floor(x), (int)Math.Floor(y));
}

/// <summary>渐变:色标位置 0–1,颜色非预乘;取样时算出参数 t,按 repeat 折回后插值。</summary>
internal abstract class GradientSource(double[] stops, Argb[] colors) : RenderSource
{
    /// <summary>色标个数(内存账按它算)。</summary>
    public int StopCount => stops.Length;

    protected Argb ColorAt(double t)
    {
        if (stops.Length == 0 || double.IsNaN(t))
        {
            return default;
        }
        switch (Repeat)
        {
            case RepeatNone:
                if (t is < 0 or > 1)
                {
                    return default;
                }
                break;
            case RepeatNormal:
                t -= Math.Floor(t);
                break;
            case RepeatPad:
                t = Math.Clamp(t, 0, 1);
                break;
            default:
                t = Math.Abs(t) % 2;
                t = t > 1 ? 2 - t : t;
                break;
        }

        Argb c;
        if (t <= stops[0])
        {
            c = colors[0];
        }
        else if (t >= stops[^1])
        {
            c = colors[^1];
        }
        else
        {
            // 第一个不小于 t 的色标(二分;色标在创建时已核过不递减)。原先逐个往后找,色标可以有上百万个,每个像素都扫一遍。
            int lo = 1, hi = stops.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (stops[mid] < t)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            int i = lo;
            double span = stops[i] - stops[i - 1];
            float f = span <= 0 ? 1 : (float)((t - stops[i - 1]) / span);
            Argb a = colors[i - 1], b = colors[i];
            c = new Argb(a.A + ((b.A - a.A) * f), a.R + ((b.R - a.R) * f), a.G + ((b.G - a.G) * f), a.B + ((b.B - a.B) * f));
        }
        return new Argb(c.A, c.R * c.A, c.G * c.A, c.B * c.A);
    }
}

/// <summary>线性渐变:t 是点在 p1→p2 方向上的投影比例。</summary>
internal sealed class LinearGradientSource(double x1, double y1, double x2, double y2, double[] stops, Argb[] colors)
    : GradientSource(stops, colors)
{
    protected override Argb Sample(double x, double y)
    {
        double vx = x2 - x1, vy = y2 - y1;
        double len2 = (vx * vx) + (vy * vy);
        return len2 == 0 ? default : ColorAt((((x - x1) * vx) + ((y - y1) * vy)) / len2);
    }
}

/// <summary>
/// 径向渐变:两个圆 c(t) = c1 + t·(c2 − c1)、r(t) = r1 + t·(r2 − r1) 之间插值,
/// 取经过该点、半径非负的最大 t(repeat 为 None 时还要落在 [0, 1] 里)。
/// </summary>
internal sealed class RadialGradientSource(double cx1, double cy1, double r1, double cx2, double cy2, double r2, double[] stops, Argb[] colors)
    : GradientSource(stops, colors)
{
    protected override Argb Sample(double x, double y)
    {
        double cdx = cx2 - cx1, cdy = cy2 - cy1, dr = r2 - r1;
        double pdx = x - cx1, pdy = y - cy1;
        double a = (cdx * cdx) + (cdy * cdy) - (dr * dr);
        double b = (pdx * cdx) + (pdy * cdy) + (r1 * dr);
        double c = (pdx * pdx) + (pdy * pdy) - (r1 * r1);
        if (Math.Abs(a) < 1e-12)
        {
            if (b == 0)
            {
                return default;
            }
            double t = c / (2 * b);
            return Accept(t) ? ColorAt(t) : default;
        }
        double disc = (b * b) - (a * c);
        if (disc < 0)
        {
            return default;
        }
        double sq = Math.Sqrt(disc);
        double t1 = (b + sq) / a, t2 = (b - sq) / a;
        (double hi, double lo) = t1 >= t2 ? (t1, t2) : (t2, t1);
        return Accept(hi) ? ColorAt(hi) : Accept(lo) ? ColorAt(lo) : default;
    }

    private bool Accept(double t) => r1 + (t * (r2 - r1)) >= 0 && (Repeat != RepeatNone || (t >= 0 && t <= 1));
}

/// <summary>锥形渐变:t 是点绕中心的角度(从 <paramref name="angleDegrees" /> 起算)占一圈的比例。</summary>
internal sealed class ConicalGradientSource(double cx, double cy, double angleDegrees, double[] stops, Argb[] colors)
    : GradientSource(stops, colors)
{
    protected override Argb Sample(double x, double y)
    {
        double t = (Math.Atan2(y - cy, x - cx) + (angleDegrees * Math.PI / 180)) / (2 * Math.PI);
        return ColorAt(t - Math.Floor(t));
    }
}
