// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateGC」(function 的 16 种布尔运算、plane-mask、
//   fill-style、tile/stipple 原点、clip-mask 与 clip 原点、line-style 与 dashes、cap-style、fill-rule、arc-mode)、
//   「PolyPoint」「PolyLine」「PolySegment」「PolyRectangle」「PolyArc」「FillPoly」「PolyFillRectangle」
//   「PolyFillArc」(像素的取舍:细线含两端点、CapNotLast 不画末点;填充按像素中心是否落在形状内)

using VelaShell.XServer.Fonts;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Drawing;

/// <summary>
/// 在一个可绘对象上按 GC 画东西。坐标全部是<b>可绘对象坐标</b>;换算到缓冲坐标、裁剪、
/// 填充样式、光栅操作与平面掩码都在这里做。
/// </summary>
/// <remarks>
/// 所有图元最终落到两种操作上:<see cref="FillSpan" />(水平一段)与 <see cref="PlotPixel" />(单个像素)。
/// 宽线、弧、多边形先算出每行的覆盖区间、合并后再画 —— 同一个像素绝不会被画两次,
/// 否则 GXxor(橡皮筋框)一类的操作会把自己抵消掉。
/// </remarks>
internal sealed class Rasterizer
{
    private readonly PixelBuffer _buffer;
    private readonly int _ox;
    private readonly int _oy;
    private readonly List<XRect> _clip;
    private readonly XGc _gc;
    private readonly uint _depthMask;

    /// <param name="buffer">目标缓冲。</param>
    /// <param name="originX">可绘对象原点在缓冲里的 x。</param>
    /// <param name="originY">可绘对象原点在缓冲里的 y。</param>
    /// <param name="clip">可画的区域(缓冲坐标,已含窗口可见区域);GC 的裁剪矩形在这里再叠加。</param>
    /// <param name="gc">图形上下文。</param>
    public Rasterizer(PixelBuffer buffer, int originX, int originY, Region clip, XGc gc)
    {
        _buffer = buffer;
        _ox = originX;
        _oy = originY;
        _gc = gc;
        _depthMask = buffer.DepthMask;

        Region effective = clip.Clone().Intersect(buffer.Bounds);
        if (gc.ClipRects is { } rects)
        {
            Region gcClip = new();
            foreach (XRect r in rects)
            {
                gcClip.Union(r.Offset(gc.ClipXOrigin + originX, gc.ClipYOrigin + originY));
            }
            effective.Intersect(gcClip);
        }
        _clip = [.. effective.Rects];
    }

    private int _dirtyX1 = int.MaxValue, _dirtyY1 = int.MaxValue, _dirtyX2 = int.MinValue, _dirtyY2 = int.MinValue;

    /// <summary>这次实际写过的像素的外接矩形(缓冲坐标);一个像素都没写时为空。</summary>
    public XRect DirtyBounds => _dirtyX2 < _dirtyX1 ? default : new(_dirtyX1, _dirtyY1, _dirtyX2 - _dirtyX1, _dirtyY2 - _dirtyY1);

    private void Touch(int bx, int by, int width)
    {
        _dirtyX1 = Math.Min(_dirtyX1, bx);
        _dirtyY1 = Math.Min(_dirtyY1, by);
        _dirtyX2 = Math.Max(_dirtyX2, bx + width);
        _dirtyY2 = Math.Max(_dirtyY2, by + 1);
    }

    /// <summary>裁剪后是否什么都画不了。</summary>
    public bool IsClippedOut => _clip.Count == 0;

    // ------------------------------------------------------------------ 像素

    /// <summary>布尔光栅操作(function 0–15)。</summary>
    internal static uint Rop(byte function, uint src, uint dst) => function switch
    {
        0 => 0,
        1 => src & dst,
        2 => src & ~dst,
        3 => src,
        4 => ~src & dst,
        5 => dst,
        6 => src ^ dst,
        7 => src | dst,
        8 => ~(src | dst),
        9 => ~src ^ dst,
        10 => ~dst,
        11 => src | ~dst,
        12 => ~src,
        13 => ~src | dst,
        14 => ~(src & dst),
        _ => 0xFFFFFFFF,
    };

    private bool ClipMaskAllows(int dx, int dy)
    {
        if (_gc.ClipPixmap is not { } mask)
        {
            return true;
        }
        return mask.Buffer.Get(dx - _gc.ClipXOrigin, dy - _gc.ClipYOrigin) != 0;
    }

    private void Store(int bx, int by, uint src, byte function, uint planeMask)
    {
        Touch(bx, by, 1);
        int index = (by * _buffer.Width) + bx;
        uint dst = _buffer.Pixels[index];
        uint result = Rop(function, src, dst);
        _buffer.Pixels[index] = ((result & planeMask) | (dst & ~planeMask)) & _depthMask;
    }

    /// <summary>按填充样式取某个可绘坐标上的源像素;点画「不画」的位置返回 null。</summary>
    private uint? FillSource(int dx, int dy, bool useBackground = false)
    {
        uint fg = useBackground ? _gc.Background : _gc.Foreground;
        switch (_gc.FillStyle)
        {
            case 1 when _gc.Tile is { } tile:
                return tile.Buffer.Get(
                    Mod(dx - _gc.TileStipXOrigin, tile.Width), Mod(dy - _gc.TileStipYOrigin, tile.Height));
            case 2 when _gc.Stipple is { } stipple:
                return StippleBit(stipple, dx, dy) ? fg : null;
            case 3 when _gc.Stipple is { } stipple:
                return StippleBit(stipple, dx, dy) ? fg : _gc.Background;
            default:
                return fg;
        }
    }

    private bool StippleBit(XPixmap stipple, int dx, int dy) =>
        stipple.Buffer.Get(Mod(dx - _gc.TileStipXOrigin, stipple.Width), Mod(dy - _gc.TileStipYOrigin, stipple.Height)) != 0;

    private static int Mod(int a, int m) => m <= 0 ? 0 : ((a % m) + m) % m;

    /// <summary>画一个像素(按填充样式)。</summary>
    public void PlotPixel(int dx, int dy, bool useBackground = false)
    {
        int bx = dx + _ox, by = dy + _oy;
        if (!InClip(bx, by) || !ClipMaskAllows(dx, dy))
        {
            return;
        }
        if (FillSource(dx, dy, useBackground) is { } src)
        {
            Store(bx, by, src, _gc.Function, _gc.PlaneMask);
        }
    }

    /// <summary>写一个给定的源像素(CopyArea / PutImage 用:不走填充样式)。</summary>
    public void PutPixel(int dx, int dy, uint src)
    {
        int bx = dx + _ox, by = dy + _oy;
        if (InClip(bx, by) && ClipMaskAllows(dx, dy))
        {
            Store(bx, by, src, _gc.Function, _gc.PlaneMask);
        }
    }

    /// <summary>
    /// 写一个像素,光栅操作固定为 GXcopy、只用平面掩码与裁剪(ImageText 的语义:
    /// 「function 与 fill-style 被忽略」)。
    /// </summary>
    public void PutPixelCopy(int dx, int dy, uint src)
    {
        int bx = dx + _ox, by = dy + _oy;
        if (InClip(bx, by) && ClipMaskAllows(dx, dy))
        {
            Store(bx, by, src, 3, _gc.PlaneMask);
        }
    }

    private bool InClip(int bx, int by)
    {
        foreach (XRect r in _clip)
        {
            if (r.Contains(bx, by))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把一块源像素(宽 <paramref name="width" />,行优先)贴到可绘坐标 (<paramref name="dx" />, <paramref name="dy" />)。
    /// 走光栅操作与平面掩码,不走填充样式(CopyArea / PutImage 的语义)。GXcopy + 全平面时整行拷贝。
    /// </summary>
    public void Blit(uint[] pixels, int width, int height, int dx, int dy)
    {
        XRect dest = new(dx + _ox, dy + _oy, width, height);
        bool fast = _gc.Function == 3 && _gc.ClipPixmap is null && (_gc.PlaneMask & _depthMask) == _depthMask;
        foreach (XRect clip in _clip)
        {
            XRect r = dest.Intersect(clip);
            if (r.IsEmpty)
            {
                continue;
            }
            for (int by = r.Y; by < r.Bottom; by++)
            {
                int srcRow = (by - dest.Y) * width;
                if (fast)
                {
                    int srcIndex = srcRow + (r.X - dest.X);
                    int dstIndex = (by * _buffer.Width) + r.X;
                    for (int i = 0; i < r.Width; i++)
                    {
                        _buffer.Pixels[dstIndex + i] = pixels[srcIndex + i] & _depthMask;
                    }
                    Touch(r.X, by, r.Width);
                    continue;
                }
                for (int bx = r.X; bx < r.Right; bx++)
                {
                    if (ClipMaskAllows(bx - _ox, by - _oy))
                    {
                        Store(bx, by, pixels[srcRow + (bx - dest.X)], _gc.Function, _gc.PlaneMask);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ 区间

    /// <summary>画一行 [x1, x2)(可绘坐标)。</summary>
    public void FillSpan(int dy, int x1, int x2)
    {
        if (x2 <= x1)
        {
            return;
        }
        int by = dy + _oy;
        int bx1 = x1 + _ox, bx2 = x2 + _ox;
        bool fastSolid = _gc.FillStyle == 0 && _gc.Function == 3 && _gc.ClipPixmap is null
                         && (_gc.PlaneMask & _depthMask) == _depthMask;
        foreach (XRect r in _clip)
        {
            if (by < r.Y || by >= r.Bottom)
            {
                continue;
            }
            int s = Math.Max(bx1, r.X), e = Math.Min(bx2, r.Right);
            if (e <= s)
            {
                continue;
            }
            if (fastSolid)
            {
                Array.Fill(_buffer.Pixels, _gc.Foreground & _depthMask, (by * _buffer.Width) + s, e - s);
                Touch(s, by, e - s);
                continue;
            }
            for (int bx = s; bx < e; bx++)
            {
                int dx = bx - _ox;
                if (!ClipMaskAllows(dx, dy))
                {
                    continue;
                }
                if (FillSource(dx, dy) is { } src)
                {
                    Store(bx, by, src, _gc.Function, _gc.PlaneMask);
                }
            }
        }
    }

    public void FillRect(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }
        for (int row = y; row < y + height; row++)
        {
            FillSpan(row, x, x + width);
        }
    }

    // ------------------------------------------------------------------ 细线

    /// <summary>虚线的走位状态:一条 PolyLine 上的各段接着同一个图案往下走(协议如此规定)。</summary>
    public sealed class DashState
    {
        public int Index;
        public int Remaining;
    }

    public DashState NewDashState()
    {
        DashState state = new();
        byte[] dashes = _gc.Dashes;
        int offset = dashes.Length == 0 ? 0 : _gc.DashOffset % dashes.Sum(d => d);
        state.Index = 0;
        state.Remaining = dashes.Length == 0 ? int.MaxValue : dashes[0];
        while (offset > 0 && dashes.Length > 0)
        {
            int step = Math.Min(offset, state.Remaining);
            offset -= step;
            state.Remaining -= step;
            if (state.Remaining == 0)
            {
                state.Index = (state.Index + 1) % dashes.Length;
                state.Remaining = dashes[state.Index];
            }
        }
        return state;
    }

    /// <summary>
    /// 零宽线(Bresenham),含起点;<paramref name="drawLast" /> 为假时不画终点(CapNotLast、
    /// 或 PolyLine 中间的接缝 —— 接缝点由下一段的起点画,避免 GXxor 下画两次)。
    /// </summary>
    public void ThinLine(int x1, int y1, int x2, int y2, bool drawLast, DashState? dash = null)
    {
        int dx = Math.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
        int dy = -Math.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1;
        int err = dx + dy;
        int x = x1, y = y1;
        while (true)
        {
            bool last = x == x2 && y == y2;
            if (!last || drawLast)
            {
                PlotDashed(x, y, dash);
            }
            if (last)
            {
                break;
            }
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private void PlotDashed(int x, int y, DashState? dash)
    {
        if (dash is null || _gc.LineStyle == 0 || _gc.Dashes.Length == 0)
        {
            PlotPixel(x, y);
            return;
        }
        bool on = dash.Index % 2 == 0;
        if (on)
        {
            PlotPixel(x, y);
        }
        else if (_gc.LineStyle == 2)
        {
            PlotPixel(x, y, useBackground: true);
        }
        dash.Remaining--;
        if (dash.Remaining <= 0)
        {
            dash.Index = (dash.Index + 1) % _gc.Dashes.Length;
            dash.Remaining = _gc.Dashes[dash.Index];
        }
    }

    // ------------------------------------------------------------------ 多边形与宽线

    /// <summary>填一组多边形的并集(非零环绕或奇偶规则),每个像素最多画一次。</summary>
    /// <param name="polygons">多边形顶点(可绘坐标,允许小数)。</param>
    /// <param name="winding">真 = 非零环绕规则;假 = 奇偶规则。</param>
    public void FillPolygons(IReadOnlyList<IReadOnlyList<(double X, double Y)>> polygons, bool winding)
    {
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (IReadOnlyList<(double X, double Y)> poly in polygons)
        {
            foreach ((double _, double y) in poly)
            {
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }
        if (minY > maxY)
        {
            return;
        }

        List<(double X, int Dir)> crossings = [];
        List<(int Start, int End)> spans = [];
        for (int row = (int)Math.Floor(minY); row <= (int)Math.Ceiling(maxY); row++)
        {
            // 按像素中心采样:第 row 行的中心在 row + 0.5。
            double sampleY = row + 0.5;
            spans.Clear();
            foreach (IReadOnlyList<(double X, double Y)> poly in polygons)
            {
                crossings.Clear();
                for (int i = 0; i < poly.Count; i++)
                {
                    (double ax, double ay) = poly[i];
                    (double bx, double by) = poly[(i + 1) % poly.Count];
                    if (ay == by)
                    {
                        continue;
                    }
                    bool downward = ay < by;
                    double top = downward ? ay : by, bottom = downward ? by : ay;
                    if (sampleY < top || sampleY >= bottom)
                    {
                        continue;
                    }
                    double x = ax + ((sampleY - ay) * (bx - ax) / (by - ay));
                    crossings.Add((x, downward ? 1 : -1));
                }
                crossings.Sort((a, b) => a.X.CompareTo(b.X));
                int wind = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    wind += winding ? crossings[i].Dir : 1;
                    bool inside = winding ? wind != 0 : (wind & 1) == 1;
                    if (!inside)
                    {
                        continue;
                    }
                    // 像素中心 px + 0.5 落在 [xa, xb) 内的像素。
                    int start = (int)Math.Ceiling(crossings[i].X - 0.5);
                    int end = (int)Math.Ceiling(crossings[i + 1].X - 0.5);
                    if (end > start)
                    {
                        spans.Add((start, end));
                    }
                }
            }
            foreach ((int s, int e) in MergeSpans(spans))
            {
                FillSpan(row, s, e);
            }
        }
    }

    private static List<(int Start, int End)> MergeSpans(List<(int Start, int End)> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<(int, int)> merged = [];
        (int cs, int ce) = spans[0];
        for (int i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start <= ce)
            {
                ce = Math.Max(ce, spans[i].End);
            }
            else
            {
                merged.Add((cs, ce));
                (cs, ce) = spans[i];
            }
        }
        merged.Add((cs, ce));
        return merged;
    }

    /// <summary>
    /// 一条折线:线宽 0 走 Bresenham;线宽 &gt; 0 把每段(连同端帽与圆形接头)变成多边形后求并集一次填完。
    /// </summary>
    public void PolyLine(IReadOnlyList<(int X, int Y)> points, bool closed = false)
    {
        if (points.Count == 0)
        {
            return;
        }
        if (_gc.LineWidth == 0)
        {
            DashState dash = NewDashState();
            if (points.Count == 1)
            {
                PlotDashed(points[0].X, points[0].Y, dash);
                return;
            }
            for (int i = 0; i < points.Count - 1; i++)
            {
                bool lastSegment = i == points.Count - 2;
                bool drawLast = lastSegment && _gc.CapStyle != 0 && !closed;
                ThinLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, drawLast, dash);
            }
            return;
        }

        List<IReadOnlyList<(double, double)>> polys = [];
        double half = _gc.LineWidth / 2.0;
        for (int i = 0; i < points.Count - 1; i++)
        {
            bool first = i == 0 && !closed;
            bool last = i == points.Count - 2 && !closed;
            AddWideSegment(polys, points[i], points[i + 1], half, first ? _gc.CapStyle : (byte)1, last ? _gc.CapStyle : (byte)1);
            if (!last || closed)
            {
                // 接头:圆形接头补一个圆;斜接 / 斜切统一近似为圆 —— 视觉差别在宽线的尖角处,M1 可接受。
                polys.Add(Circle(points[i + 1].X, points[i + 1].Y, half));
            }
        }
        FillPolygons(polys, winding: true);
    }

    private static void AddWideSegment(
        List<IReadOnlyList<(double, double)>> polys, (int X, int Y) a, (int X, int Y) b, double half, byte capA, byte capB)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-9)
        {
            if (capA == 2 || capB == 2)
            {
                polys.Add(Circle(a.X, a.Y, half));
            }
            return;
        }
        double ux = dx / length, uy = dy / length;
        double nx = -uy * half, ny = ux * half;
        double ax = a.X, ay = a.Y, bx = b.X, by = b.Y;
        if (capA == 3)
        {
            ax -= ux * half;
            ay -= uy * half;
        }
        if (capB == 3)
        {
            bx += ux * half;
            by += uy * half;
        }
        polys.Add([(ax + nx, ay + ny), (bx + nx, by + ny), (bx - nx, by - ny), (ax - nx, ay - ny)]);
        if (capA == 2)
        {
            polys.Add(Circle(a.X, a.Y, half));
        }
        if (capB == 2)
        {
            polys.Add(Circle(b.X, b.Y, half));
        }
    }

    private static List<(double, double)> Circle(double cx, double cy, double r)
    {
        int n = Math.Max(8, (int)(r * 4));
        List<(double, double)> points = new(n);
        for (int i = 0; i < n; i++)
        {
            double t = 2 * Math.PI * i / n;
            points.Add((cx + (r * Math.Cos(t)), cy + (r * Math.Sin(t))));
        }
        return points;
    }

    // ------------------------------------------------------------------ 弧

    /// <summary>弧上的点:外接框 (x, y, w, h),起角与跨度以 1/64 度计,3 点钟方向为 0、逆时针为正。</summary>
    private static List<(double X, double Y)> ArcPoints(int x, int y, int w, int h, int angle1, int angle2)
    {
        double cx = x + (w / 2.0), cy = y + (h / 2.0), rx = w / 2.0, ry = h / 2.0;
        double start = angle1 / 64.0 * Math.PI / 180.0;
        double extent = Math.Clamp(angle2, -360 * 64, 360 * 64) / 64.0 * Math.PI / 180.0;
        int n = Math.Max(4, (int)(Math.Abs(extent) * Math.Max(rx, ry)));
        List<(double, double)> points = new(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = start + (extent * i / n);
            // y 轴朝下,所以逆时针对应 −sin。
            points.Add((cx + (rx * Math.Cos(t)), cy - (ry * Math.Sin(t))));
        }
        return points;
    }

    public void Arc(int x, int y, int w, int h, int angle1, int angle2)
    {
        List<(double X, double Y)> points = ArcPoints(x, y, w, h, angle1, angle2);
        if (_gc.LineWidth == 0)
        {
            // 细弧:相邻采样点之间用细线连起来。采样点先取整去重,免得同一像素被画两次。
            List<(int X, int Y)> pixels = [];
            foreach ((double px, double py) in points)
            {
                (int X, int Y) p = ((int)Math.Round(px), (int)Math.Round(py));
                if (pixels.Count == 0 || pixels[^1] != p)
                {
                    pixels.Add(p);
                }
            }
            bool full = Math.Abs(angle2) >= 360 * 64;
            for (int i = 0; i < pixels.Count - 1; i++)
            {
                bool drawLast = i == pixels.Count - 2 && !full;
                ThinLine(pixels[i].X, pixels[i].Y, pixels[i + 1].X, pixels[i + 1].Y, drawLast);
            }
            if (pixels.Count == 1)
            {
                PlotPixel(pixels[0].X, pixels[0].Y);
            }
            return;
        }

        // 宽弧:内外两条等距曲线围成的环带。
        double half = _gc.LineWidth / 2.0;
        List<(double X, double Y)> outer = ArcPoints(
            (int)Math.Round(x - half), (int)Math.Round(y - half), (int)Math.Round(w + (2 * half)), (int)Math.Round(h + (2 * half)), angle1, angle2);
        List<(double X, double Y)> inner = ArcPoints(
            (int)Math.Round(x + half), (int)Math.Round(y + half), Math.Max(0, (int)Math.Round(w - (2 * half))), Math.Max(0, (int)Math.Round(h - (2 * half))), angle1, angle2);
        inner.Reverse();
        FillPolygons([[.. outer, .. inner]], winding: false);
    }

    public void FillArc(int x, int y, int w, int h, int angle1, int angle2)
    {
        List<(double X, double Y)> points = ArcPoints(x, y, w, h, angle1, angle2);
        if (_gc.ArcMode == 1 && Math.Abs(angle2) < 360 * 64)
        {
            points.Add((x + (w / 2.0), y + (h / 2.0)));   // PieSlice:连回圆心
        }
        FillPolygons([points], winding: true);
    }

    // ------------------------------------------------------------------ 文字

    /// <summary>PolyText:字形当作点画,按填充样式画前景。返回前进宽度。</summary>
    public int DrawGlyph(XGlyph glyph, int originX, int baselineY)
    {
        int w = glyph.BitmapWidth, h = glyph.BitmapHeight;
        int left = originX + glyph.Info.LeftBearing, top = baselineY - glyph.Info.Ascent;
        for (int gy = 0; gy < h; gy++)
        {
            for (int gx = 0; gx < w; gx++)
            {
                if (glyph.IsSet(gx, gy))
                {
                    PlotPixel(left + gx, top + gy);
                }
            }
        }
        return glyph.Info.Width;
    }

    /// <summary>ImageText:先按 GXcopy 用背景色填字体的行框,再用前景色画字形。</summary>
    public void ImageText(XFont font, ReadOnlySpan<int> codes, int x, int baselineY)
    {
        (int width, _, _, _, _) = font.Measure(codes);
        int top = baselineY - font.Ascent, height = font.Ascent + font.Descent;
        for (int yy = top; yy < top + height; yy++)
        {
            for (int xx = x; xx < x + width; xx++)
            {
                PutPixelCopy(xx, yy, _gc.Background);
            }
        }
        int pen = x;
        foreach (int code in codes)
        {
            if (font.Lookup(code) is not { } glyph)
            {
                continue;
            }
            int left = pen + glyph.Info.LeftBearing, gTop = baselineY - glyph.Info.Ascent;
            for (int gy = 0; gy < glyph.BitmapHeight; gy++)
            {
                for (int gx = 0; gx < glyph.BitmapWidth; gx++)
                {
                    if (glyph.IsSet(gx, gy))
                    {
                        PutPixelCopy(left + gx, gTop + gy, _gc.Foreground);
                    }
                }
            }
            pen += glyph.Info.Width;
        }
    }
}
