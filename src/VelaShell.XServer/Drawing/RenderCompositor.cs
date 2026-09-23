// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §9「Composite」(dst = (src IN mask) OP dst;遮罩有颜色通道且
//   component-alpha 为 True 时逐通道相乘;src / mask 的坐标按 (x − dst-x) 与目标对齐)、
//   §10「Trapezoids / Triangles / TriStrip / TriFan / AddTraps」(几何按像素覆盖率光栅化成 alpha 遮罩,
//   多个图元以 Add 累加进遮罩)

using System.Buffers;

namespace VelaShell.XServer.Drawing;

/// <summary>合成目标:一块缓冲、可绘对象原点在缓冲里的位置、像素格式、可写的区域(缓冲坐标)。</summary>
internal sealed record RenderTarget(PixelBuffer Buffer, int OriginX, int OriginY, PictFormat Format, IReadOnlyList<XRect> Clip);

internal static class RenderCompositor
{
    /// <summary>
    /// 把目标上 (dstX, dstY, width, height)(可绘对象坐标)这一块合成一遍。返回实际写过的范围(缓冲坐标)。
    /// </summary>
    public static XRect Composite(byte op, RenderSource src, RenderSource? mask, bool componentAlpha, RenderTarget dst,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, int width, int height)
    {
        XRect area = new(dstX + dst.OriginX, dstY + dst.OriginY, width, height);
        PixelBuffer buffer = dst.Buffer;
        uint depthMask = buffer.DepthMask;
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;

        Argb[] srcRow = ArrayPool<Argb>.Shared.Rent(Math.Max(1, width));
        Argb[] maskRow = ArrayPool<Argb>.Shared.Rent(Math.Max(1, width));
        try
        {
            foreach (XRect clip in dst.Clip)
            {
                XRect r = clip.Intersect(area);
                if (r.IsEmpty)
                {
                    continue;
                }
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.Right);
                y2 = Math.Max(y2, r.Bottom);

                // 纯色 + 无遮罩 + Src(或不透明的 Over):整块填同一个像素值。
                if (mask is null && src is SolidSource solid && src.Transform is null
                    && (op == RenderOps.Src || (op == RenderOps.Over && solid.Color.A >= 1f)))
                {
                    uint value = dst.Format.Encode(solid.Color) & depthMask;
                    for (int by = r.Y; by < r.Bottom; by++)
                    {
                        buffer.Pixels.AsSpan((by * buffer.Width) + r.X, r.Width).Fill(value);
                    }
                    continue;
                }

                Span<Argb> s = srcRow.AsSpan(0, r.Width);
                Span<Argb> m = maskRow.AsSpan(0, r.Width);
                for (int by = r.Y; by < r.Bottom; by++)
                {
                    int dx = r.X - dst.OriginX - dstX;
                    int dy = by - dst.OriginY - dstY;
                    src.FetchRow(srcX + dx, srcY + dy, s);
                    mask?.FetchRow(maskX + dx, maskY + dy, m);
                    int row = by * buffer.Width;
                    for (int i = 0; i < s.Length; i++)
                    {
                        Argb sc = s[i];
                        Argb sa;
                        if (mask is not null)
                        {
                            Argb mc = m[i];
                            if (componentAlpha)
                            {
                                sa = new Argb(sc.A * mc.A, sc.A * mc.R, sc.A * mc.G, sc.A * mc.B);
                                sc = new Argb(sc.A * mc.A, sc.R * mc.R, sc.G * mc.G, sc.B * mc.B);
                            }
                            else
                            {
                                sc = new Argb(sc.A * mc.A, sc.R * mc.A, sc.G * mc.A, sc.B * mc.A);
                                sa = Argb.Gray(sc.A);
                            }
                        }
                        else
                        {
                            sa = Argb.Gray(sc.A);
                        }

                        // Over / Add 下完全透明的源不改变目标 —— 字形与覆盖率遮罩的大部分像素走这里。
                        if (op is RenderOps.Over or RenderOps.Add && sa.R <= 0 && sa.G <= 0 && sa.B <= 0 && sc.A <= 0)
                        {
                            continue;
                        }
                        int index = row + r.X + i;
                        Argb d = dst.Format.Decode(buffer.Pixels[index]);
                        buffer.Pixels[index] = dst.Format.Encode(RenderOps.Combine(op, sc, sa, d)) & depthMask;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Argb>.Shared.Return(srcRow);
            ArrayPool<Argb>.Shared.Return(maskRow);
        }
        return x2 < x1 ? default : new XRect(x1, y1, x2 - x1, y2 - y1);
    }
}

/// <summary>
/// 把梯形 / 三角形光栅化成覆盖率(0–1)。每个像素行分 <see cref="SubRows" /> 条子扫描线,
/// 每条子扫描线上按精确的水平重叠长度累加 —— 水平方向是解析的,垂直方向是 16 级采样。
/// </summary>
internal sealed class CoverageMask
{
    private const int SubRows = 16;

    public CoverageMask(XRect bounds)
    {
        Bounds = bounds;
        Alpha = new float[Math.Max(0, bounds.Width * bounds.Height)];
    }

    /// <summary>覆盖的范围(目标可绘对象坐标)。</summary>
    public XRect Bounds { get; }

    public float[] Alpha { get; }

    /// <summary>一条直线(两点式)在高度 y 处的 x。</summary>
    public readonly record struct Line(double X1, double Y1, double X2, double Y2)
    {
        public double XAt(double y) => Y2 == Y1 ? X1 : X1 + ((y - Y1) * (X2 - X1) / (Y2 - Y1));
    }

    /// <summary>梯形:top ≤ y &lt; bottom 之间、左边线与右边线之间的部分(左在右的右边时那一段不画)。</summary>
    public void AddTrapezoid(double top, double bottom, Line left, Line right) =>
        AddBand(top, bottom, y => (left.XAt(y), right.XAt(y)));

    /// <summary>三角形:按中间顶点拆成上下两段,每条子扫描线取与各边交点的最小 / 最大值。</summary>
    public void AddTriangle((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        (double X, double Y)[] v = [a, b, c];
        Array.Sort(v, (p, q) => p.Y.CompareTo(q.Y));
        Line longEdge = new(v[0].X, v[0].Y, v[2].X, v[2].Y);
        Line upper = new(v[0].X, v[0].Y, v[1].X, v[1].Y);
        Line lower = new(v[1].X, v[1].Y, v[2].X, v[2].Y);
        AddBand(v[0].Y, v[1].Y, y => MinMax(longEdge.XAt(y), upper.XAt(y)));
        AddBand(v[1].Y, v[2].Y, y => MinMax(longEdge.XAt(y), lower.XAt(y)));

        static (double, double) MinMax(double p, double q) => p <= q ? (p, q) : (q, p);
    }

    private void AddBand(double top, double bottom, Func<double, (double Left, double Right)> span)
    {
        double yStart = Math.Max(top, Bounds.Y), yEnd = Math.Min(bottom, Bounds.Bottom);
        if (yEnd <= yStart)
        {
            return;
        }
        const float weight = 1f / SubRows;
        for (int row = (int)Math.Floor(yStart); row < (int)Math.Ceiling(yEnd); row++)
        {
            for (int k = 0; k < SubRows; k++)
            {
                double y = row + ((k + 0.5) / SubRows);
                if (y < top || y >= bottom)
                {
                    continue;
                }
                (double left, double right) = span(y);
                AddSpan(row, left, right, weight);
            }
        }
    }

    private void AddSpan(int row, double left, double right, float weight)
    {
        left = Math.Max(left, Bounds.X);
        right = Math.Min(right, Bounds.Right);
        if (right <= left || row < Bounds.Y || row >= Bounds.Bottom)
        {
            return;
        }
        int offset = (row - Bounds.Y) * Bounds.Width;
        int first = (int)Math.Floor(left), last = (int)Math.Ceiling(right) - 1;
        for (int px = first; px <= last; px++)
        {
            double covered = Math.Min(right, px + 1) - Math.Max(left, px);
            if (covered > 0)
            {
                Alpha[offset + px - Bounds.X] += (float)covered * weight;
            }
        }
    }

    /// <summary>转成遮罩源(alpha 封顶 1;<paramref name="oneBit" /> 时按 0.5 二值化,对应 a1 遮罩格式)。</summary>
    public ArraySource ToSource(bool oneBit)
    {
        Argb[] pixels = new Argb[Alpha.Length];
        for (int i = 0; i < Alpha.Length; i++)
        {
            float a = Math.Min(1f, Alpha[i]);
            if (oneBit)
            {
                a = a >= 0.5f ? 1f : 0f;
            }
            pixels[i] = Argb.Gray(a);
        }
        return new ArraySource(pixels, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
    }
}
