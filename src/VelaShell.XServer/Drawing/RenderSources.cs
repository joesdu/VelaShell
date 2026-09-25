// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §7「Picture」的 repeat(None / Normal / Pad / Reflect)、
//   transform(从目标坐标映射到源坐标的 3×3 定点矩阵,取样点是像素中心)、filter(nearest / bilinear
//   及其别名 fast / good / best);§11「Gradients」(CreateSolidFill、CreateLinearGradient、
//   CreateRadialGradient —— 两个圆之间的锥形插值、CreateConicalGradient;色标颜色非预乘,插值后再预乘)

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
            double px = x + i + 0.5, py = y + 0.5;
            double w = (t[6] * px) + (t[7] * py) + t[8];
            if (w == 0)
            {
                row[i] = default;
                continue;
            }
            row[i] = Sample(((t[0] * px) + (t[1] * py) + t[2]) / w, ((t[3] * px) + (t[4] * py) + t[5]) / w);
        }
    }

    /// <summary>没有变换时的取样:像素 (x + i, y) 的中心。</summary>
    protected virtual void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Sample(x + i + 0.5, y + 0.5);
        }
    }

    /// <summary>在连续坐标上取样(像素 (i, j) 的中心是 (i + 0.5, j + 0.5))。</summary>
    protected abstract Argb Sample(double x, double y);

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
    public Argb Color { get; } = color;

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row) => row.Fill(Color);

    protected override Argb Sample(double x, double y) => Color;
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

    private Argb Texel(int x, int y) =>
        Wrap(ref x, Width) && Wrap(ref y, Height) ? Format.Decode(Buffer.Get(OriginX + x, OriginY + y)) : default;

    protected override void FetchIntegerRow(int x, int y, Span<Argb> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = Texel(x + i, y);
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

    protected override Argb Sample(double x, double y) => Argb.Gray(ToFloat[At((int)Math.Floor(x), (int)Math.Floor(y))]);
}

/// <summary>带颜色的遮罩(次像素字形,分量 alpha):每像素一个预乘的 0xAARRGGBB。</summary>
internal sealed class ColorMaskSource(uint[] pixels, int x0, int y0, int width, int height) : RenderSource
{
    private Argb Texel(int x, int y)
    {
        x -= x0;
        y -= y0;
        return (uint)x < (uint)width && (uint)y < (uint)height ? PictFormat.A8R8G8B8.Decode(pixels[(y * width) + x]) : default;
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

/// <summary>渐变:色标位置 0–1,颜色非预乘;取样时算出参数 t,按 repeat 折回后插值。</summary>
internal abstract class GradientSource(double[] stops, Argb[] colors) : RenderSource
{
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
            int i = 1;
            while (stops[i] < t)
            {
                i++;
            }
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
