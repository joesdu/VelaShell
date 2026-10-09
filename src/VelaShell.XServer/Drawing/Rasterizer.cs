// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateGC」(function 的 16 种布尔运算、plane-mask、
//   fill-style、tile/stipple 原点、clip-mask 与 clip 原点、line-style 与 dashes、cap-style、join-style、fill-rule、arc-mode;
//   端点重合的线)、「SetDashes」(虚线沿线量、连接的各段接着走)、
//   「PolyPoint」「PolyLine」「PolySegment」「PolyRectangle」「PolyArc」「FillPoly」「PolyFillRectangle」
//   「PolyFillArc」(像素的取舍:细线含两端点、CapNotLast 不画末点;填充按像素中心是否落在形状内;宽弧的边界与端帽;
//   PolyArc 里首尾相接的弧按 join-style 接、虚线接着走、相交处的像素只画一次)

using VelaShell.XServer.Fonts;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using ArcSpec = (int X, int Y, int W, int H, int A1, int A2);
using Polygon = System.Collections.Generic.List<(double X, double Y)>;

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

        // 可画区域:可见区域 ∩ 缓冲 ∩ GC 的裁剪矩形。最常见的情形 —— 没有裁剪矩形、可见区域本来就在缓冲之内 —— 直接用可见区域的块,
        // 不拷(它在这个光栅化器活着的时候不会变:可见区域的缓存只在两项工作之间换);GC 的裁剪区域缓存在 GC 上,
        // 原先每个请求都 Clone 一遍可见区域、拷成新 List、再按裁剪矩形 FromRects 一次。
        Region? gcClip = gc.ClipRegionAt(gc.ClipXOrigin + originX, gc.ClipYOrigin + originY);
        XRect visible = clip.Bounds;
        Region effective;
        if (gcClip is null && visible.Intersect(buffer.Bounds) == visible)
        {
            effective = clip;
        }
        else
        {
            effective = clip.Clone().Intersect(buffer.Bounds);
            if (gcClip is not null)
            {
                effective.Intersect(gcClip);
            }
        }
        _clip = effective.RectList;
        XRect bounds = effective.Bounds;
        (_clipTop, _clipBottom) = bounds.IsEmpty ? (0, 0) : (bounds.Y - originY, bounds.Bottom - originY);
        ClipBounds = bounds.IsEmpty ? default : bounds.Offset(-originX, -originY);
    }

    /// <summary>可画区域的行范围(可绘对象坐标,[top, bottom)):扫描线填充只扫这几行。</summary>
    private readonly int _clipTop, _clipBottom;

    /// <summary>可画区域的外接矩形(可绘对象坐标;不含 clip-mask 像素图,它只会再缩小):画不到的部分不必准备源像素。</summary>
    public XRect ClipBounds { get; }

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

    /// <summary>
    /// 按填充样式取某个可绘坐标上的源像素;点画「不画」的位置返回 null。<paramref name="useBackground" /> 是 DoubleDash 的奇数段:
    /// Solid 用背景色、Stippled 用背景色按点画遮,Tiled 与 OpaqueStippled 与偶数段相同(协议「CreateGC」fill-style)。
    /// </summary>
    private uint? FillSource(int dx, int dy, bool useBackground = false)
    {
        uint fg = useBackground ? _gc.Background : _gc.Foreground;
        return _gc.FillStyle switch
        {
            1 when _gc.Tile is { } tile => tile.Buffer.Get(
                                Mod(dx - _gc.TileStipXOrigin, tile.Width), Mod(dy - _gc.TileStipYOrigin, tile.Height)),
            2 when _gc.Stipple is { } stipple => StippleBit(stipple, dx, dy) ? fg : null,
            3 when _gc.Stipple is { } stipple => StippleBit(stipple, dx, dy) ? _gc.Foreground : _gc.Background,
            _ => fg,
        };
    }

    private bool StippleBit(XPixmap stipple, int dx, int dy) =>
        stipple.Buffer.Get(Mod(dx - _gc.TileStipXOrigin, stipple.Width), Mod(dy - _gc.TileStipYOrigin, stipple.Height)) != 0;

    private static int Mod(int a, int m) => m <= 0 ? 0 : ((a % m) + m) % m;

    /// <summary>画一个像素(按填充样式)。</summary>
    public void PlotPixel(int dx, int dy, bool useBackground = false)
    {
        WorkBudget.Charge(1);
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
        WorkBudget.Charge(1);
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
        WorkBudget.Charge(1);
        int bx = dx + _ox, by = dy + _oy;
        if (InClip(bx, by) && ClipMaskAllows(dx, dy))
        {
            Store(bx, by, src, 3, _gc.PlaneMask);
        }
    }

    private bool InClip(int bx, int by)
    {
        int i = FirstClipIndex(by, bx);
        return i < _clip.Count && _clip[i].Y <= by && _clip[i].X <= bx;
    }

    /// <summary>
    /// 第一块不在 (<paramref name="bx" />, <paramref name="by" />) 「之前」的裁剪矩形(二分):之前 = 整带在这一行之上,或者同一带里右边界不超过 bx。
    /// 裁剪区域按 y 分带、先 y 后 x 存放(见 <see cref="Region" />),所以这个判断随下标单调 —— 盖住这一行、从 bx 往右的那几块就从这里开始,
    /// 不必每个像素、每一段都把上万块矩形扫一遍。
    /// </summary>
    private int FirstClipIndex(int by, int bx)
    {
        int lo = 0, hi = _clip.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            XRect r = _clip[mid];
            if (r.Bottom <= by || (r.Y <= by && r.Right <= bx))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>
    /// 把一块源像素(宽 <paramref name="width" />,行优先)贴到可绘坐标 (<paramref name="dx" />, <paramref name="dy" />)。
    /// 走光栅操作与平面掩码,不走填充样式(CopyArea / PutImage 的语义)。GXcopy + 全平面时整行拷贝。
    /// </summary>
    /// <param name="pixels">源像素,行优先(可以比 宽 × 高 长:池化的数组)。</param>
    /// <param name="width">源宽。</param>
    /// <param name="height">源高。</param>
    /// <param name="dx">贴到的可绘坐标 x。</param>
    /// <param name="dy">贴到的可绘坐标 y。</param>
    /// <param name="preMasked">源像素已经在深度掩码之内(从同深度的缓冲读来的,CopyArea):整行直接拷,不再逐个与掩码。</param>
    public void Blit(uint[] pixels, int width, int height, int dx, int dy, bool preMasked = false) =>
        Blit(pixels, width, width, height, dx, dy, preMasked);

    /// <summary>同上,源像素每行相隔 <paramref name="stride" /> 个 —— 可以直接是一幅更大图像里的一块,不必先拷出来。</summary>
    /// <param name="pixels">源像素:第 k 行从下标 k × <paramref name="stride" /> 开始。</param>
    /// <param name="stride">源的行距(像素数,不小于 <paramref name="width" />)。</param>
    /// <param name="width">源宽。</param>
    /// <param name="height">源高。</param>
    /// <param name="dx">贴到的可绘坐标 x。</param>
    /// <param name="dy">贴到的可绘坐标 y。</param>
    /// <param name="preMasked">源像素已经在深度掩码之内。</param>
    public void Blit(ReadOnlySpan<uint> pixels, int stride, int width, int height, int dx, int dy, bool preMasked = false)
    {
        XRect dest = new(dx + _ox, dy + _oy, width, height);
        XRect reachable = new XRect(dx, dy, width, height).Intersect(ClipBounds);
        WorkBudget.Charge(1 + _clip.Count + ((long)reachable.Width * reachable.Height));
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
                int srcRow = (by - dest.Y) * stride;
                if (fast)
                {
                    ReadOnlySpan<uint> from = pixels.Slice(srcRow + (r.X - dest.X), r.Width);
                    Span<uint> to = _buffer.Pixels.AsSpan((by * _buffer.Width) + r.X, r.Width);
                    if (preMasked || _depthMask == uint.MaxValue)
                    {
                        from.CopyTo(to);
                    }
                    else
                    {
                        PixelBuffer.CopyMasked(from, to, _depthMask);
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

    /// <summary>用给定颜色画一行 [x1, x2)(可绘坐标),光栅操作固定为 GXcopy(ImageText 的语义),走平面掩码与裁剪。</summary>
    private void FillSpanCopy(int dy, int x1, int x2, uint color)
    {
        if (x2 <= x1)
        {
            return;
        }
        ChargeSpan(x1, x2);
        int by = dy + _oy;
        int bx1 = x1 + _ox, bx2 = x2 + _ox;
        bool fast = _gc.ClipPixmap is null && (_gc.PlaneMask & _depthMask) == _depthMask;
        for (int i = FirstClipIndex(by, bx1); i < _clip.Count && _clip[i].Y <= by && _clip[i].X < bx2; i++)
        {
            XRect r = _clip[i];
            int s = Math.Max(bx1, r.X), e = Math.Min(bx2, r.Right);
            if (e <= s)
            {
                continue;
            }
            if (fast)
            {
                Array.Fill(_buffer.Pixels, color & _depthMask, (by * _buffer.Width) + s, e - s);
                Touch(s, by, e - s);
                continue;
            }
            for (int bx = s; bx < e; bx++)
            {
                if (ClipMaskAllows(bx - _ox, dy))
                {
                    Store(bx, by, color, 3, _gc.PlaneMask);
                }
            }
        }
    }

    /// <summary>
    /// 按段画一个字形:每行里连续置位的像素合成一段,<paramref name="copy" /> 时用 GXcopy 画 <paramref name="color" />(ImageText),
    /// 否则按 GC 的填充样式画(PolyText)—— 与逐点画的结果相同,但走整段的快路径。
    /// </summary>
    private void DrawGlyphRuns(XGlyph glyph, int left, int top, bool copy, uint color)
    {
        int w = glyph.BitmapWidth;
        for (int gy = 0; gy < glyph.BitmapHeight; gy++)
        {
            int gx = 0;
            while (gx < w)
            {
                while (gx < w && !glyph.IsSet(gx, gy))
                {
                    gx++;
                }
                int start = gx;
                while (gx < w && glyph.IsSet(gx, gy))
                {
                    gx++;
                }
                if (gx > start)
                {
                    if (copy)
                    {
                        FillSpanCopy(top + gy, left + start, left + gx, color);
                    }
                    else
                    {
                        FillSpan(top + gy, left + start, left + gx);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ 区间

    /// <summary>画一行 [x1, x2)(可绘坐标);<paramref name="useBackground" /> 时按 DoubleDash 奇数段的源(见 <see cref="FillSource" />)。</summary>
    public void FillSpan(int dy, int x1, int x2, bool useBackground = false)
    {
        if (x2 <= x1)
        {
            return;
        }
        ChargeSpan(x1, x2);
        int by = dy + _oy;
        int bx1 = x1 + _ox, bx2 = x2 + _ox;
        bool plain = _gc.Function == 3 && _gc.ClipPixmap is null && (_gc.PlaneMask & _depthMask) == _depthMask;
        bool fastSolid = plain && _gc.FillStyle == 0;
        // 平铺 + GXcopy + 全平面:按平铺图的行整段拷(原先逐像素对 x、y 各取一次模)。
        PixelBuffer? fastTile = plain && _gc.FillStyle == 1 && _gc.Tile is { } tile ? tile.Buffer : null;
        uint solid = (useBackground ? _gc.Background : _gc.Foreground) & _depthMask;
        for (int i = FirstClipIndex(by, bx1); i < _clip.Count && _clip[i].Y <= by && _clip[i].X < bx2; i++)
        {
            XRect r = _clip[i];
            int s = Math.Max(bx1, r.X), e = Math.Min(bx2, r.Right);
            if (e <= s)
            {
                continue;
            }
            if (fastSolid)
            {
                Array.Fill(_buffer.Pixels, solid, (by * _buffer.Width) + s, e - s);
                Touch(s, by, e - s);
                continue;
            }
            if (fastTile is not null)
            {
                _buffer.FillTiledRow(by, s, e, fastTile, _gc.TileStipXOrigin + _ox, _gc.TileStipYOrigin + _oy, _depthMask);
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
                if (FillSource(dx, dy, useBackground) is { } src)
                {
                    Store(bx, by, src, _gc.Function, _gc.PlaneMask);
                }
            }
        }
    }

    /// <summary>一行 [x1, x2) 的工作量:扫一遍裁剪矩形,再加上画得到的那几个像素(见 <see cref="WorkBudget" />)。</summary>
    private void ChargeSpan(int x1, int x2) =>
        WorkBudget.Charge(1 + Math.Max(0, (long)Math.Min(x2, ClipBounds.Right) - Math.Max(x1, ClipBounds.X)));

    public void FillRect(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }
        // 先与可画区域求交:(0, −32768, 1, 65535) 这样的矩形原先要逐行走六万多行,一个请求塞几万个就是几十秒。
        XRect visible = new XRect(x, y, width, height).Intersect(ClipBounds);
        for (int row = visible.Y; row < visible.Bottom; row++)
        {
            FillSpan(row, visible.X, visible.Right);
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
    /// <remarks>
    /// 只走与可画区域相交的那几步:这种 Bresenham 每一步主轴(|Δ| 较大的那根)必走一格,第 k 步的位置与误差项有闭式解
    /// (<see cref="MinorSteps" />),于是按主轴算出 k 的范围、再在其中二分出副轴也落在范围里的那一段,从那一段的起点接着走 ——
    /// 画出来的像素与从头走一遍完全一样,代价却只与看得见的长度成正比(一条 65535 长、几乎全在窗口外的线原先要走六万多步)。
    /// 虚线的走位照样按全程推进。
    /// </remarks>
    public void ThinLine(int x1, int y1, int x2, int y2, bool drawLast, DashState? dash = null)
    {
        long dx = Math.Abs((long)x2 - x1), dy = Math.Abs((long)y2 - y1);
        int sx = x1 < x2 ? 1 : -1, sy = y1 < y2 ? 1 : -1;
        bool xMajor = dx >= dy;
        long major = xMajor ? dx : dy, minor = xMajor ? dy : dx;
        long last = drawLast ? major : major - 1;   // 第 major 步是终点
        if (last < 0)
        {
            return;
        }
        bool dashed = dash is not null && _gc.LineStyle != 0 && _gc.Dashes.Length != 0;

        // 主轴落在可画区域里的 k:[majorLow, majorHigh]。
        XRect clip = ClipBounds;
        (long a0, int sa, long aLow, long aHigh) = xMajor ? (x1, sx, clip.X, clip.Right - 1L) : (y1, sy, clip.Y, clip.Bottom - 1L);
        (long b0, int sb, long bLow, long bHigh) = xMajor ? (y1, sy, clip.Y, clip.Bottom - 1L) : (x1, sx, clip.X, clip.Right - 1L);
        long k0 = 0, k1 = last;
        if (clip.IsEmpty)
        {
            k1 = -1;
        }
        else
        {
            (long kLow, long kHigh) = sa > 0 ? (aLow - a0, aHigh - a0) : (a0 - aHigh, a0 - aLow);
            k0 = Math.Max(k0, kLow);
            k1 = Math.Min(k1, kHigh);
            // 副轴随 k 单调:副轴步数 m(k) 要落在 [mLow, mHigh] 里。
            (long mLow, long mHigh) = sb > 0 ? (bLow - b0, bHigh - b0) : (b0 - bHigh, b0 - bLow);
            if (k0 <= k1)
            {
                k0 = FirstStepReaching(k0, k1, major, minor, mLow);
                k1 = FirstStepReaching(k0, k1 + 1, major, minor, mHigh + 1) - 1;   // 第一个 m(k) > mHigh 的前一步
            }
        }
        if (k0 > k1)
        {
            if (dashed)
            {
                AdvanceDash(dash!, last + 1);
            }
            return;
        }
        if (dashed)
        {
            AdvanceDash(dash!, k0);
        }

        // 第 k0 步的位置与误差项(误差项的含义同下面的循环:初值 dx − dy,每步主轴 / 副轴各自加减)。
        long m0 = MinorSteps(k0, major, minor);
        long err = xMajor ? dx - dy - (k0 * dy) + (m0 * dx) : dx - dy + (k0 * dx) - (m0 * dy);
        int x = (int)(xMajor ? x1 + (sx * k0) : x1 + (sx * m0));
        int y = (int)(xMajor ? y1 + (sy * m0) : y1 + (sy * k0));
        for (long k = k0; ; k++)
        {
            PlotDashed(x, y, dash);
            if (k == k1)
            {
                break;
            }
            long e2 = 2 * err;
            if (e2 >= -dy)
            {
                err -= dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
        if (dashed && k1 < last)
        {
            AdvanceDash(dash!, last - k1);
        }
    }

    /// <summary>
    /// 走完前 <paramref name="k" /> 步时副轴走了几格。误差项始终落在一个宽为主轴长度的半开区间里(主轴每步减副轴长、副轴走一格加主轴长),
    /// 由此得到 m(k) = ⌊(k·副轴 − ⌈主轴 / 2⌉) / 主轴⌋ + 1 —— 与逐步走的结果逐格相同(对拍用例见 RasterizerTests)。
    /// </summary>
    internal static long MinorSteps(long k, long major, long minor) =>
        major == 0 ? 0 : FloorDiv((k * minor) - ((major + 1) / 2), major) + 1;

    private static long FloorDiv(long a, long b) => (a / b) - (((a % b) != 0 && ((a < 0) != (b < 0))) ? 1 : 0);

    /// <summary>
    /// [<paramref name="low" />, <paramref name="high" />) 里第一个副轴步数 m(k) ≥ <paramref name="target" /> 的 k(m 随 k 单调);
    /// 都不满足时返回 <paramref name="high" />。不经委托:每条细线调两次,原先每次分配一个闭包。
    /// </summary>
    private static long FirstStepReaching(long low, long high, long major, long minor, long target)
    {
        while (low < high)
        {
            long mid = low + ((high - low) / 2);
            if (MinorSteps(mid, major, minor) >= target)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }
        return low;
    }

    /// <summary>虚线的走位往前推 <paramref name="steps" /> 个像素(画不到的那几步也要推,下一段才接得上图案)。</summary>
    private void AdvanceDash(DashState dash, long steps)
    {
        byte[] dashes = _gc.Dashes;
        long period = 0;
        foreach (byte d in dashes)
        {
            period += d;
        }
        if (period > 0)
        {
            steps %= period;
        }
        while (steps > 0)
        {
            int take = (int)Math.Min(steps, dash.Remaining);
            steps -= take;
            dash.Remaining -= take;
            if (dash.Remaining <= 0)
            {
                dash.Index = (dash.Index + 1) % dashes.Length;
                dash.Remaining = dashes[dash.Index];
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

    // ------------------------------------------------------------------ 多边形

    /// <summary>填一组多边形的并集(非零环绕或奇偶规则,各多边形各自判),每个像素最多画一次。</summary>
    /// <param name="polygons">多边形顶点(可绘坐标,允许小数)。</param>
    /// <param name="winding">真 = 非零环绕规则;假 = 奇偶规则。</param>
    public void FillPolygons(IReadOnlyList<IReadOnlyList<(double X, double Y)>> polygons, bool winding) =>
        FillScanned(new PolygonScanner(this, polygons, winding, union: false));

    private void FillScanned(PolygonScanner scanner, bool useBackground = false)
    {
        List<(int Start, int End)> spans = [];
        for (int row = scanner.FirstRow; row <= scanner.LastRow; row++)
        {
            spans.Clear();
            scanner.Row(row, spans);
            foreach ((int s, int e) in MergeSpans(spans))
            {
                FillSpan(row, s, e, useBackground);
            }
        }
    }

    /// <summary>
    /// 逐行求一组多边形覆盖的区间(像素中心落在形状内的像素,左闭右开、上闭下开)。行必须从小到大地要。
    /// </summary>
    /// <remarks>
    /// <c>union</c> 模式给宽线 / 宽弧用:各块都是我们自己拼的简单多边形(段的矩形、端帽、接头、环带),先按有向面积统一成同一个转向,
    /// 所有边放进一张活动边表、按非零环绕判 —— 同向的简单多边形叠在一起,环绕数在并集里处处是正的、外面是 0,结果就是并集;
    /// 每行的代价只与跨过这一行的边数有关,而不是与块数有关(一条几万段的虚线不会每行把几万块扫一遍)。
    /// </remarks>
    private sealed class PolygonScanner
    {
        private readonly List<Edge>[] _edges;
        private readonly List<Edge>[] _active;
        private readonly int[] _next;
        private readonly bool _winding;
        private readonly List<(double X, int Dir)> _crossings = [];

        public PolygonScanner(Rasterizer raster, IReadOnlyList<IReadOnlyList<(double X, double Y)>> polygons, bool winding, bool union)
        {
            _winding = winding || union;
            int lists = union ? 1 : polygons.Count;
            _edges = new List<Edge>[lists];
            _active = new List<Edge>[lists];
            _next = new int[lists];
            for (int i = 0; i < lists; i++)
            {
                _edges[i] = [];
                _active[i] = [];
            }
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int pi = 0; pi < polygons.Count; pi++)
            {
                IReadOnlyList<(double X, double Y)> poly = polygons[pi];
                WorkBudget.Charge(1 + poly.Count);
                List<Edge> edges = _edges[union ? 0 : pi];
                int orientation = union && SignedArea(poly) < 0 ? -1 : 1;
                for (int i = 0; i < poly.Count; i++)
                {
                    (double ax, double ay) = poly[i];
                    (double bx, double by) = poly[(i + 1) % poly.Count];
                    if (ay == by || double.IsNaN(ax) || double.IsNaN(bx) || double.IsNaN(ay) || double.IsNaN(by))
                    {
                        continue;
                    }
                    edges.Add(ay < by
                        ? new Edge(ay, by, ax, (bx - ax) / (by - ay), orientation)
                        : new Edge(by, ay, bx, (ax - bx) / (ay - by), -orientation));
                    minY = Math.Min(minY, Math.Min(ay, by));
                    maxY = Math.Max(maxY, Math.Max(ay, by));
                }
            }
            foreach (List<Edge> edges in _edges)
            {
                edges.Sort(static (a, b) => a.Top.CompareTo(b.Top));
            }
            if (minY > maxY || raster._clipBottom <= raster._clipTop)
            {
                (FirstRow, LastRow) = (0, -1);
                return;
            }
            FirstRow = (int)Math.Max(Math.Floor(Math.Max(minY, int.MinValue)), raster._clipTop);
            LastRow = (int)Math.Min(Math.Ceiling(Math.Min(maxY, int.MaxValue)), raster._clipBottom - 1);
        }

        /// <summary>要扫的第一行(已与可画区域求交)。</summary>
        public int FirstRow { get; }

        /// <summary>要扫的最后一行;小于 <see cref="FirstRow" /> 时什么都不用画。</summary>
        public int LastRow { get; }

        /// <summary>第 <paramref name="row" /> 行的覆盖区间追加到 <paramref name="spans" />(未合并)。</summary>
        public void Row(int row, List<(int Start, int End)> spans)
        {
            // 核心协议:整数坐标就是像素中心(「coordinates … coincide with pixel centers」),所以第 row 行在 y = row 处采样;
            // 边按「上闭下开」进出活动表,正好落在水平边上的像素中心只算下方是内部的那一侧。
            // (RENDER 的 CoverageMask 按 RENDER 规范以 +0.5 为中心,不走这里。)
            double sampleY = row;
            for (int pi = 0; pi < _edges.Length; pi++)
            {
                List<Edge> edges = _edges[pi], live = _active[pi];
                WorkBudget.Charge(1 + live.Count);
                while (_next[pi] < edges.Count && edges[_next[pi]].Top <= sampleY)
                {
                    live.Add(edges[_next[pi]++]);
                }
                // 下端已过这一行的边出表:就地压紧(原先 RemoveAll 每行分配一个闭包)。
                int kept = 0;
                for (int i = 0; i < live.Count; i++)
                {
                    if (live[i].Bottom > sampleY)
                    {
                        live[kept++] = live[i];
                    }
                }
                live.RemoveRange(kept, live.Count - kept);
                _crossings.Clear();
                foreach (Edge e in live)
                {
                    if (sampleY >= e.Top)
                    {
                        _crossings.Add((e.X + ((sampleY - e.Top) * e.Slope), e.Dir));
                    }
                }
                _crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
                int wind = 0;
                for (int i = 0; i < _crossings.Count - 1; i++)
                {
                    wind += _winding ? _crossings[i].Dir : 1;
                    bool inside = _winding ? wind != 0 : (wind & 1) == 1;
                    if (!inside)
                    {
                        continue;
                    }
                    // 像素中心 px 落在 [xa, xb) 内的像素:左闭右开,恰在边上的只算右侧是内部的那一侧。
                    double xa = Math.Clamp(_crossings[i].X, int.MinValue, int.MaxValue);
                    double xb = Math.Clamp(_crossings[i + 1].X, int.MinValue, int.MaxValue);
                    int start = (int)Math.Ceiling(xa);
                    int end = (int)Math.Ceiling(xb);
                    if (end > start)
                    {
                        spans.Add((start, end));
                    }
                }
            }
        }

        private static double SignedArea(IReadOnlyList<(double X, double Y)> poly)
        {
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                (double ax, double ay) = poly[i];
                (double bx, double by) = poly[(i + 1) % poly.Count];
                sum += (ax * by) - (bx * ay);
            }
            return sum;
        }
    }

    /// <summary>多边形的一条边,从上端(Top,X)到下端(Bottom);Dir = 1 表示原本朝下走。</summary>
    private readonly record struct Edge(double Top, double Bottom, double X, double Slope, int Dir);

    /// <summary>排序并合并相交或相接的区间,就地写回 <paramref name="spans" />(原先每行新建一个 List)。</summary>
    private static List<(int Start, int End)> MergeSpans(List<(int Start, int End)> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }
        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        int count = 0;
        (int cs, int ce) = spans[0];
        for (int i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start <= ce)
            {
                ce = Math.Max(ce, spans[i].End);
            }
            else
            {
                spans[count++] = (cs, ce);
                (cs, ce) = spans[i];
            }
        }
        spans[count++] = (cs, ce);
        spans.RemoveRange(count, spans.Count - count);
        return spans;
    }

    // ------------------------------------------------------------------ 宽线与宽弧的笔画

    /// <summary>
    /// 填一道宽笔画。<paramref name="even" /> 为 null 时按实线填 <paramref name="solid" />;否则按 line-style:
    /// OnOffDash 只填偶数段,DoubleDash 时偶数段(与实线求交)按前景、实线减去偶数段的部分按背景 ——
    /// 协议要求 DoubleDash 两种段合起来的像素与 Solid 完全相同,这样拆正好满足,也保证每个像素只画一次。
    /// </summary>
    private void FillStroke(List<Polygon> solid, List<Polygon>? even)
    {
        if (even is null)
        {
            FillScanned(new PolygonScanner(this, solid, winding: true, union: true));
            return;
        }
        if (_gc.LineStyle == 1)
        {
            FillScanned(new PolygonScanner(this, even, winding: true, union: true));
            return;
        }
        PolygonScanner all = new(this, solid, winding: true, union: true), on = new(this, even, winding: true, union: true);
        List<(int Start, int End)> solidSpans = [], evenSpans = [];
        for (int row = all.FirstRow; row <= all.LastRow; row++)
        {
            solidSpans.Clear();
            evenSpans.Clear();
            all.Row(row, solidSpans);
            on.Row(row, evenSpans);
            List<(int Start, int End)> s = MergeSpans(solidSpans), e = MergeSpans(evenSpans);
            int j = 0;
            foreach ((int ss, int se) in s)
            {
                int x = ss;
                while (j < e.Count && e[j].End <= ss)
                {
                    j++;
                }
                for (int k = j; k < e.Count && e[k].Start < se; k++)
                {
                    int a = Math.Max(ss, e[k].Start), b = Math.Min(se, e[k].End);
                    FillSpan(row, x, a, useBackground: true);   // 奇数段
                    FillSpan(row, a, b);                       // 偶数段
                    x = b;
                }
                FillSpan(row, x, se, useBackground: true);
            }
        }
    }

    /// <summary>当前 GC 下虚线是否生效(线宽 &gt; 0 时)。</summary>
    private bool Dashed => _gc.LineStyle != 0 && _gc.Dashes.Length != 0;

    /// <summary>虚线段内部的端点用什么端帽:OnOffDash 用 cap-style(NotLast 当 Butt),DoubleDash 一律 Butt(协议)。</summary>
    private byte InternalCap => _gc.LineStyle == 1 && _gc.CapStyle != 0 ? _gc.CapStyle : (byte)1;

    /// <summary>宽线(双精度)的虚线走位:当前是第几段、这一段还剩多长。</summary>
    private struct WideDash
    {
        public int Index;
        public double Remaining;

        /// <summary>这一段是刚刚开始的(还没走过任何长度):从这里开始的偶数段,起点要加端帽。</summary>
        public bool Fresh;
    }

    private WideDash StartWideDash()
    {
        WideDash dash = new() { Index = 0, Remaining = _gc.Dashes[0], Fresh = true };
        AdvanceWideDash(ref dash, _gc.DashOffset);
        return dash;
    }

    /// <summary>虚线的走位往前推 <paramref name="distance" />(整周期先取模)。</summary>
    private void AdvanceWideDash(ref WideDash dash, double distance)
    {
        if (distance <= 0)
        {
            return;
        }
        byte[] dashes = _gc.Dashes;
        if (distance < dash.Remaining)
        {
            dash.Remaining -= distance;
            dash.Fresh = false;
            return;
        }
        distance -= dash.Remaining;
        dash.Index = (dash.Index + 1) % dashes.Length;
        dash.Remaining = dashes[dash.Index];
        long period = 0;
        foreach (byte d in dashes)
        {
            period += d;
        }
        distance %= period;   // 正好落在段的边界上:整周期可以跳过
        while (distance >= dash.Remaining)
        {
            WorkBudget.Charge(1);
            distance -= dash.Remaining;
            dash.Index = (dash.Index + 1) % dashes.Length;
            dash.Remaining = dashes[dash.Index];
        }
        dash.Remaining -= distance;
        dash.Fresh = distance == 0;
    }

    /// <summary>路径上的一段:A → B,长度与单位方向。</summary>
    private readonly record struct PathSegment(double Ax, double Ay, double Bx, double By, double Length, double Ux, double Uy)
    {
        public static PathSegment Between((double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt((dx * dx) + (dy * dy));
            return new PathSegment(a.X, a.Y, b.X, b.Y, length, dx / length, dy / length);
        }

        public (double X, double Y) At(double s) => (Ax + (Ux * s), Ay + (Uy * s));
    }

    /// <summary>一个偶数虚线段(实线时是整条路径):依次经过的各段上的 [S0, S1],以及两头怎么收。</summary>
    private sealed class StrokeRun
    {
        public List<(int Segment, double S0, double S1)> Pieces { get; } = [];

        /// <summary>起点是路径的起点。</summary>
        public bool AtPathStart { get; set; }

        /// <summary>终点是路径的终点。</summary>
        public bool AtPathEnd { get; set; }

        /// <summary>起点 / 终点是因为离可画区域太远而截断的地方:不加端帽(反正看不见)。</summary>
        public bool StartHidden { get; set; }

        public bool EndHidden { get; set; }

        /// <summary>起点是一个虚线段开始的地方(而不是半路截进来的)。</summary>
        public bool StartsFresh { get; set; }
    }

    /// <summary>
    /// 一条宽折线:每段一块矩形,段与段之间按 join-style 加接头,两端按 cap-style 加端帽;虚线时按段长沿线量出各个偶数段,
    /// 内部的端点按 line-style 加端帽(协议「CreateGC」的 line-style / cap-style / join-style)。整条折线的各块一起填,像素只画一次。
    /// </summary>
    /// <summary><see cref="WidePolyLine" /> 在一个请求里重复用的顶点、段列表与 PolySegment / PolyRectangle 的点数组。</summary>
    private Polygon? _vertices;

    private List<PathSegment>? _segments;

    private (int X, int Y)[]? _points2, _points5;

    /// <summary>PolySegment 的一段:段与段之间不相连(不加接头,虚线每段从 dash-offset 重新开始)。不为每段分配点列表。</summary>
    public void Segment(int x1, int y1, int x2, int y2)
    {
        if (_gc.LineWidth == 0)
        {
            ThinLine(x1, y1, x2, y2, drawLast: _gc.CapStyle != 0, _gc.LineStyle != 0 ? NewDashState() : null);
            return;
        }
        (int X, int Y)[] points = _points2 ??= new (int X, int Y)[2];
        points[0] = (x1, y1);
        points[1] = (x2, y2);
        WidePolyLine(points, closed: false);
    }

    /// <summary>PolyRectangle 的一个矩形:五个点的闭合折线(协议)。不为每个矩形分配点列表。</summary>
    public void Rectangle(int x, int y, int width, int height)
    {
        (int X, int Y)[] points = _points5 ??= new (int X, int Y)[5];
        int x2 = x + width, y2 = y + height;
        points[0] = (x, y);
        points[1] = (x2, y);
        points[2] = (x2, y2);
        points[3] = (x, y2);
        points[4] = (x, y);
        PolyLine(points, closed: true);
    }

    private void WidePolyLine(IReadOnlyList<(int X, int Y)> points, bool closed)
    {
        double half = _gc.LineWidth / 2.0;
        WorkBudget.Charge(points.Count);
        // 端点重合的段:协议说「效果如同这条线从路径里拿掉了」。顶点与段的列表在一个请求里重复用(PolySegment / PolyRectangle 一条请求几千段)。
        Polygon vertices = _vertices ??= [];
        vertices.Clear();
        for (int i = 0; i < points.Count; i++)
        {
            (int x, int y) = points[i];
            if (vertices.Count == 0 || vertices[^1] != (x, y))
            {
                vertices.Add((x, y));
            }
        }
        if (closed && vertices.Count > 1 && vertices[^1] == vertices[0])
        {
            vertices.RemoveAt(vertices.Count - 1);
        }
        if (vertices.Count == 1)
        {
            // 整条路径缩成一个点:两端各按端帽处理 —— Round 是直径为线宽的圆,Projecting 是与坐标轴对齐、边长为线宽的方块,Butt 什么也不画。
            List<Polygon> dot = [];
            AddPointCap(dot, vertices[0], half);
            FillStroke(dot, null);
            return;
        }
        List<PathSegment> segments = _segments ??= [];
        segments.Clear();
        int count = closed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < count; i++)
        {
            segments.Add(PathSegment.Between(vertices[i], vertices[(i + 1) % vertices.Count]));
        }

        List<Polygon> solid = [];
        StrokeRun whole = new() { AtPathStart = true, AtPathEnd = true };
        for (int i = 0; i < segments.Count; i++)
        {
            whole.Pieces.Add((i, 0, segments[i].Length));
        }
        AddRun(solid, segments, whole, closed ? (byte)1 : _gc.CapStyle, closed ? (byte)1 : _gc.CapStyle, half);
        if (closed)
        {
            AddJoin(solid, segments[^1], segments[0], half);
        }
        if (!Dashed)
        {
            FillStroke(solid, null);
            return;
        }

        List<StrokeRun> runs = DashRuns(segments, half);
        List<Polygon> even = [];
        // 闭合路径的接缝(第一个点):首尾两个偶数段都碰到它时连起来 —— 加接头、两头不加端帽。
        bool seamJoined = closed && runs.Count > 0 && runs[0].AtPathStart && runs[^1].AtPathEnd;
        if (seamJoined)
        {
            AddJoin(even, segments[^1], segments[0], half);
        }
        for (int i = 0; i < runs.Count; i++)
        {
            StrokeRun run = runs[i];
            byte startCap = RunCap(run.AtPathStart, run.StartHidden, run.StartsFresh, closed, seamJoined);
            byte endCap = RunCap(run.AtPathEnd, run.EndHidden, fresh: true, closed, seamJoined);
            AddRun(even, segments, run, startCap, endCap, half);
        }
        FillStroke(solid, even);
    }

    /// <summary>一个偶数段的一端用什么端帽。</summary>
    private byte RunCap(bool atPathEnd, bool hidden, bool fresh, bool closed, bool seamJoined)
    {
        if (hidden)
        {
            return 1;
        }
        if (atPathEnd)
        {
            return closed ? (seamJoined ? (byte)1 : InternalCap) : _gc.CapStyle;
        }
        return fresh ? InternalCap : (byte)1;
    }

    /// <summary>
    /// 沿路径量出各个偶数段。离可画区域足够远(超过端帽与斜接尖角能伸到的距离)的部分不逐段走,只按长度推进图案 ——
    /// 一条几万像素长、虚线 [1, 1] 的线原先要拆成几万块。
    /// </summary>
    private List<StrokeRun> DashRuns(List<PathSegment> segments, double half)
    {
        List<StrokeRun> runs = [];
        WideDash dash = StartWideDash();
        StrokeRun? run = null;
        // 斜接的尖角最远伸出 half / sin(5.5°) ≈ 10.5 × half,端帽 √2 × half:截断处离可画区域再远一些就看不出差别。
        double margin = (12 * half) + 2;
        for (int k = 0; k < segments.Count; k++)
        {
            PathSegment seg = segments[k];
            (double lo, double hi) = VisibleRange(seg, margin);
            if (lo > hi)
            {
                Close(hidden: true);
                AdvanceWideDash(ref dash, seg.Length);
                continue;
            }
            if (lo > 0)
            {
                Close(hidden: true);
                AdvanceWideDash(ref dash, lo);
            }
            double s = lo;
            while (hi - s > 1e-9)
            {
                WorkBudget.Charge(1);
                double take = Math.Min(dash.Remaining, hi - s);
                if ((dash.Index & 1) == 0)
                {
                    if (run is null)
                    {
                        run = new StrokeRun
                        {
                            AtPathStart = k == 0 && s == 0,
                            StartsFresh = dash.Fresh,
                            StartHidden = !(k == 0 && s == 0) && !dash.Fresh,
                        };
                        runs.Add(run);
                    }
                    run.Pieces.Add((k, s, s + take));
                }
                else
                {
                    Close(hidden: false);
                }
                s += take;
                dash.Remaining -= take;
                dash.Fresh = false;
                if (dash.Remaining <= 1e-9)
                {
                    dash.Index = (dash.Index + 1) % _gc.Dashes.Length;
                    dash.Remaining = _gc.Dashes[dash.Index];
                    dash.Fresh = true;
                }
            }
            if (hi < seg.Length)
            {
                Close(hidden: true);
                AdvanceWideDash(ref dash, seg.Length - hi);
            }
        }
        run?.AtPathEnd = true;
        return runs;

        void Close(bool hidden)
        {
            run?.EndHidden = hidden;
            run = null;
        }
    }

    /// <summary>
    /// 段上离可画区域不超过 <paramref name="margin" /> 的参数范围 [lo, hi](Liang–Barsky 裁剪);碰不到时 lo &gt; hi。
    /// </summary>
    private (double Lo, double Hi) VisibleRange(PathSegment seg, double margin)
    {
        XRect clip = ClipBounds;
        if (clip.IsEmpty)
        {
            return (1, 0);
        }
        double lo = 0, hi = seg.Length;
        if (!Clip1(seg.Ax, seg.Ux, clip.X - margin, clip.Right + margin) || !Clip1(seg.Ay, seg.Uy, clip.Y - margin, clip.Bottom + margin))
        {
            return (1, 0);
        }
        return (lo, hi);

        bool Clip1(double p, double u, double min, double max)
        {
            if (Math.Abs(u) < 1e-12)
            {
                return p >= min && p <= max;
            }
            double t1 = (min - p) / u, t2 = (max - p) / u;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            lo = Math.Max(lo, t1);
            hi = Math.Min(hi, t2);
            return lo <= hi;
        }
    }

    /// <summary>把一个偶数段(或整条实线)变成多边形:各段的矩形、相邻两段之间的接头、两头的端帽。</summary>
    private void AddRun(List<Polygon> polys, List<PathSegment> segments, StrokeRun run, byte startCap, byte endCap, double half)
    {
        if (run.Pieces.Count == 0)
        {
            return;
        }
        for (int i = 0; i < run.Pieces.Count; i++)
        {
            (int k, double s0, double s1) = run.Pieces[i];
            PathSegment seg = segments[k];
            if (s1 > s0)
            {
                (double ax, double ay) = seg.At(s0);
                (double bx, double by) = seg.At(s1);
                double nx = -seg.Uy * half, ny = seg.Ux * half;
                AddIfReaches(polys, [(ax + nx, ay + ny), (bx + nx, by + ny), (bx - nx, by - ny), (ax - nx, ay - ny)]);
            }
            if (i + 1 < run.Pieces.Count)
            {
                AddJoin(polys, seg, segments[run.Pieces[i + 1].Segment], half);
            }
        }
        (int first, double start, _) = run.Pieces[0];
        (int last, _, double end) = run.Pieces[^1];
        PathSegment a = segments[first], b = segments[last];
        AddCap(polys, a.At(start), -a.Ux, -a.Uy, half, startCap);
        AddCap(polys, b.At(end), b.Ux, b.Uy, half, endCap);
    }

    /// <summary>
    /// 端帽:<paramref name="dx" />, <paramref name="dy" /> 是朝外(离开线)的单位方向。Round 是直径为线宽的圆,Projecting 是朝外伸出半个线宽的方块,
    /// Butt / NotLast 什么都不加。
    /// </summary>
    private void AddCap(List<Polygon> polys, (double X, double Y) p, double dx, double dy, double half, byte cap)
    {
        if (cap == 2)
        {
            AddCircle(polys, p.X, p.Y, half);
        }
        else if (cap == 3)
        {
            double nx = -dy * half, ny = dx * half, ex = dx * half, ey = dy * half;
            AddIfReaches(polys, [(p.X + nx, p.Y + ny), (p.X + nx + ex, p.Y + ny + ey), (p.X - nx + ex, p.Y - ny + ey), (p.X - nx, p.Y - ny)]);
        }
    }

    /// <summary>端点重合的路径:Round 是圆,Projecting 是与坐标轴对齐的方块,其余什么都不画(协议「CreateGC」)。</summary>
    private void AddPointCap(List<Polygon> polys, (double X, double Y) p, double half)
    {
        if (_gc.CapStyle == 2)
        {
            AddCircle(polys, p.X, p.Y, half);
        }
        else if (_gc.CapStyle == 3)
        {
            AddIfReaches(polys, [(p.X - half, p.Y - half), (p.X + half, p.Y - half), (p.X + half, p.Y + half), (p.X - half, p.Y + half)]);
        }
    }

    /// <summary>斜接的角度下限:两条线的夹角小于 11° 时改用斜切(协议 JoinMiter)。</summary>
    private static readonly double MiterLimitCos = Math.Cos(11 * Math.PI / 180);

    /// <summary>
    /// 两段在连接点上的接头(<paramref name="into" /> 的终点就是 <paramref name="outOf" /> 的起点):Round 补一个圆;
    /// Bevel 把外侧的三角缺口补上;Miter 把两条外沿延长到相交,夹角小于 11° 时退成 Bevel。
    /// </summary>
    private void AddJoin(List<Polygon> polys, PathSegment into, PathSegment outOf, double half)
    {
        double px = into.Bx, py = into.By;
        if (_gc.JoinStyle == 1)
        {
            AddCircle(polys, px, py, half);
            return;
        }
        double cross = (into.Ux * outOf.Uy) - (into.Uy * outOf.Ux);
        double dot = (into.Ux * outOf.Ux) + (into.Uy * outOf.Uy);
        if (Math.Abs(cross) < 1e-12)
        {
            return;   // 同向:两块矩形本来就接在一起;反向:缺口退化成一条线
        }
        // 法线取 (−uy, ux);转向朝哪边,外侧就在另一边。
        double side = cross < 0 ? half : -half;
        (double X, double Y) o1 = (px - (into.Uy * side), py + (into.Ux * side));
        (double X, double Y) o2 = (px - (outOf.Uy * side), py + (outOf.Ux * side));
        // 两条线的夹角 φ:cos φ = −(u1 · u2)。
        if (_gc.JoinStyle == 0 && -dot <= MiterLimitCos)
        {
            double scale = 1 / (1 + dot);
            (double X, double Y) tip = (px + ((o1.X - px + o2.X - px) * scale), py + ((o1.Y - py + o2.Y - py) * scale));
            AddIfReaches(polys, [(px, py), o1, tip, o2]);
            return;
        }
        AddIfReaches(polys, [(px, py), o1, o2]);
    }

    /// <summary>多边形的外接矩形碰得到可画区域才要(结果是并集,少了看不见的块,看得见的像素不变)。</summary>
    private void AddIfReaches(List<Polygon> polys, Polygon polygon)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((double x, double y) in polygon)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        if (BoxReaches(minX, minY, maxX, maxY))
        {
            polys.Add(polygon);
        }
    }

    private bool BoxReaches(double minX, double minY, double maxX, double maxY)
    {
        XRect clip = ClipBounds;
        return !clip.IsEmpty && maxX >= clip.X - 1 && minX <= clip.Right && maxY >= clip.Y - 1 && minY <= clip.Bottom;
    }

    private void AddCircle(List<Polygon> polys, double cx, double cy, double r)
    {
        if (BoxReaches(cx - r, cy - r, cx + r, cy + r))
        {
            polys.Add(Circle(cx, cy, r));
        }
    }

    /// <summary>
    /// 一条折线:线宽 0 走 Bresenham;线宽 &gt; 0 见 <see cref="WidePolyLine" />。
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
        WidePolyLine(points, closed);
    }

    /// <summary>圆帽 / 圆接头最多这么多个顶点:半径三万多时弦高也只有约 0.15 像素,再多只是白算(原先按半径 × 4,线宽 65535 时一个圆就是 13 万个顶点)。</summary>
    private const int MaxCircleVertices = 1024;

    private static Polygon Circle(double cx, double cy, double r)
    {
        int n = Math.Clamp((int)(r * 4), 8, MaxCircleVertices);
        Polygon points = [with(n)];
        for (int i = 0; i < n; i++)
        {
            double t = 2 * Math.PI * i / n;
            points.Add((cx + (r * Math.Cos(t)), cy + (r * Math.Sin(t))));
        }
        return points;
    }

    // ------------------------------------------------------------------ 弧

    /// <summary>外接框 (x, y, w, h) 的椭圆:圆心与两个半轴(都不取整 —— 协议:这些坐标「not necessarily integral」)。</summary>
    private static (double Cx, double Cy, double Rx, double Ry) Ellipse(int x, int y, int w, int h) =>
        (x + (w / 2.0), y + (h / 2.0), w / 2.0, h / 2.0);

    /// <summary>起角与跨度(1/64 度,3 点钟方向为 0、逆时针为正)换成弧度;跨度超过 360° 截成 360°。</summary>
    private static (double Start, double Extent) ArcAngles(int angle1, int angle2) =>
        (angle1 / 64.0 * Math.PI / 180.0, Math.Clamp(angle2, -360 * 64, 360 * 64) / 64.0 * Math.PI / 180.0);

    /// <summary>采样段数:约一像素一段;上限 4096 段 —— 半径三万多的整圆,4096 段的弦高也只有百分之一像素,再多只是白算。</summary>
    private static int ArcSteps(double extent, double radius) => Math.Clamp((int)(Math.Abs(extent) * radius), 4, 4096);

    /// <summary>椭圆上参数角 t 处的点(协议的角度在椭圆「拉伸过的」坐标里量,正好就是参数角);y 轴朝下,逆时针对应 −sin。</summary>
    private static (double X, double Y) EllipsePoint(double cx, double cy, double rx, double ry, double t) =>
        (cx + (rx * Math.Cos(t)), cy - (ry * Math.Sin(t)));

    /// <summary>弧上的点:外接框 (x, y, w, h),起角与跨度以 1/64 度计。</summary>
    private static Polygon ArcPoints(int x, int y, int w, int h, int angle1, int angle2)
    {
        (double cx, double cy, double rx, double ry) = Ellipse(x, y, w, h);
        (double start, double extent) = ArcAngles(angle1, angle2);
        int n = ArcSteps(extent, Math.Max(rx, ry));
        List<(double, double)> points = [with(n + 1)];
        for (int i = 0; i <= n; i++)
        {
            points.Add(EllipsePoint(cx, cy, rx, ry, start + (extent * i / n)));
        }
        return points;
    }

    /// <summary>弧的起点(实数坐标)。</summary>
    private static (double X, double Y) ArcStart(ArcSpec arc)
    {
        (double cx, double cy, double rx, double ry) = Ellipse(arc.X, arc.Y, arc.W, arc.H);
        (double start, _) = ArcAngles(arc.A1, arc.A2);
        return EllipsePoint(cx, cy, rx, ry, start);
    }

    /// <summary>弧的终点(实数坐标)。</summary>
    private static (double X, double Y) ArcEnd(ArcSpec arc)
    {
        (double cx, double cy, double rx, double ry) = Ellipse(arc.X, arc.Y, arc.W, arc.H);
        (double start, double extent) = ArcAngles(arc.A1, arc.A2);
        return EllipsePoint(cx, cy, rx, ry, start + extent);
    }

    /// <summary>两个端点只要各轴相差都不到这么多就算「重合」(见 <see cref="Coincide" />)。</summary>
    private const double CoincideTolerance = 0.5 - (1.0 / 256);

    /// <summary>
    /// PolyArc 里一条弧的终点与另一条的起点算不算重合:各轴相差都不到半个像素(留 1/256 像素的余量)。
    /// </summary>
    /// <remarks>
    /// 端点是实数(圆心与半轴是半像素的倍数,再乘上 cos / sin),逐位相等太苛刻:同一个椭圆的四段 90° 弧,前一段的终点角是起角加跨度、
    /// 后一段的起角是另一个数换算来的,末位常常差一点,cos(π/2) 也不是 0。按「取整到同一个像素」判又不稳:宽或高是奇数时圆心在半像素上,
    /// 0° / 180° 处的端点正好落在 .5 上,取整到哪一边全看浮点误差的正负。「各轴差不到半个像素」—— 在像素上看就是同一点 ——
    /// 不受浮点误差左右,也与平移无关。90° 倍数处的端点都在半像素的格点上,对它们这个判据就是「恰好相等」:正好差半个像素的
    /// (宽或高奇偶不同的两个框)不算,那 1/256 像素的余量保证这种情形不会因为浮点误差时而相接、时而不相接。
    /// </remarks>
    private static bool Coincide((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) < CoincideTolerance && Math.Abs(a.Y - b.Y) < CoincideTolerance;

    /// <summary>
    /// 一串首尾相接的弧:<see cref="Arcs" /> 里从 <see cref="Start" /> 起的 <see cref="Count" /> 条。下标绕回开头 ——
    /// 最后一条接回第一条时,最后一串接在第一串前面当一串画。
    /// </summary>
    private readonly record struct ArcChain(IReadOnlyList<ArcSpec> Arcs, int Start, int Count)
    {
        public ArcSpec this[int j] => Arcs[(Start + j) % Arcs.Count];
    }

    /// <summary>
    /// PolyArc:按列出的次序画一组弧。一条弧的终点与下一条的起点重合(<see cref="Coincide" />)时两条相接,最后一条的终点与第一条的起点重合时
    /// 也相接;相接的一串当一条路径画 —— 宽弧之间按 join-style 加接头、只在整串的两头加端帽(首尾也相接时哪里都不加),整串放进同一张
    /// 活动边表一次填,相交处的像素只画一次;虚线沿整串接着走,每一串从 dash-offset 重新开始;细弧在接点上只画一次
    /// (协议「PolyArc」「SetDashes」)。不相接的弧之间相交的像素照样画多次(协议如此)。
    /// </summary>
    public void PolyArc(IReadOnlyList<ArcSpec> arcs)
    {
        int count = arcs.Count;
        if (count == 0)
        {
            return;
        }
        WorkBudget.Charge(count);
        // 每一串的第一条:第 0 条,以及每条与前一条不相接的弧。
        List<int> starts = [0];
        for (int i = 1; i < count; i++)
        {
            if (!Coincide(ArcEnd(arcs[i - 1]), ArcStart(arcs[i])))
            {
                starts.Add(i);
            }
        }
        bool wrap = count > 1 && Coincide(ArcEnd(arcs[count - 1]), ArcStart(arcs[0]));
        if (starts.Count == 1)
        {
            DrawArcs(new ArcChain(arcs, 0, count), closed: wrap);
            return;
        }
        int from = 0, to = starts.Count;
        if (wrap)
        {
            // 最后一条接回第一条:最后一串接在第一串前面,当一串画(它含第一条弧,先画)。
            DrawArcs(new ArcChain(arcs, starts[^1], count - starts[^1] + starts[1]), closed: false);
            (from, to) = (1, starts.Count - 1);
        }
        for (int k = from; k < to; k++)
        {
            int next = k + 1 < starts.Count ? starts[k + 1] : count;
            DrawArcs(new ArcChain(arcs, starts[k], next - starts[k]), closed: false);
        }
    }

    /// <summary>单独画一条弧(自成一串)。</summary>
    public void Arc(int x, int y, int w, int h, int angle1, int angle2) =>
        DrawArcs(new ArcChain([(x, y, w, h, angle1, angle2)], 0, 1), closed: false);

    /// <summary>跨度截到 ±360°(协议:超过 360° 的按 360° 算)。</summary>
    private static int ClampExtent(int angle2) => Math.Clamp(angle2, -360 * 64, 360 * 64);

    /// <summary>画一串相接的弧;<paramref name="closed" /> 是首尾也相接(两条以上的串才有;一条弧自己首尾相接就是整圆)。</summary>
    private void DrawArcs(ArcChain chain, bool closed)
    {
        // 同一个椭圆上一段接着一段(起角正好是上一段的终角、方向相同、加起来不超过一整圈)的弧并成一条:与跨度是两者之和的一条弧画法完全相同,
        // 接点上没有接缝 —— 分开算的两段在接点上的端面差几个末位,cos(π/2) 也不是 0,正好压在端面上的一排像素中心会两边都不算。
        List<ArcSpec> arcs = [chain[0]];
        for (int j = 1; j < chain.Count; j++)
        {
            ArcSpec prev = arcs[^1], next = chain[j];
            int a = ClampExtent(prev.A2), b = ClampExtent(next.A2);
            if ((next.X, next.Y, next.W, next.H) == (prev.X, prev.Y, prev.W, prev.H) && a != 0 && Math.Sign(a) == Math.Sign(b)
                && Math.Abs(a + b) <= 360 * 64 && (prev.A1 + a - next.A1) % (360 * 64) == 0)
            {
                arcs[^1] = prev with { A2 = a + b };
            }
            else
            {
                arcs.Add(next);
            }
        }
        // 只剩一条整圆时,接缝是它自己的(与单独画一条整圆相同);否则首尾相接的串在接缝处要连起来。
        bool full = arcs.Count == 1 && Math.Abs(arcs[0].A2) >= 360 * 64;
        bool loop = full || closed;
        if (_gc.LineWidth == 0)
        {
            ThinArcs(arcs, loop, connect: loop && !full);
        }
        else
        {
            WideArcs(arcs, loop, seamJoin: loop && !full);
        }
    }

    /// <summary>
    /// 一串细弧:各条弧的采样点依次取整、去掉相邻的重复点,连成一条细折线(相邻两点之间画 Bresenham 细线,终点留给下一段)——
    /// 接点上的像素只画一次,虚线沿整串接着走。<paramref name="loop" /> 时路径回到起点,终点不再画一遍;<paramref name="connect" /> 时
    /// (几条弧首尾相接)终点与起点取整后不一定是同一个像素,最后再连回起点。一条整圆照旧:终点不画、不连。
    /// </summary>
    private void ThinArcs(List<ArcSpec> arcs, bool loop, bool connect)
    {
        DashState dash = NewDashState();
        (int X, int Y) first = default, last = default;
        bool any = false, moved = false;
        foreach ((int x, int y, int w, int h, int a1, int a2) in arcs)
        {
            foreach ((double px, double py) in ArcPoints(x, y, w, h, a1, a2))
            {
                (int X, int Y) p = ((int)Math.Round(px), (int)Math.Round(py));
                if (!any)
                {
                    (first, last, any) = (p, p, true);
                }
                else if (p != last)
                {
                    ThinLine(last.X, last.Y, p.X, p.Y, drawLast: false, dash);
                    (last, moved) = (p, true);
                }
            }
        }
        if (connect && last != first)
        {
            ThinLine(last.X, last.Y, first.X, first.Y, drawLast: false, dash);
        }
        else if (!loop || !moved)
        {
            PlotDashed(last.X, last.Y, dash);
        }
    }

    /// <summary>宽弧路径上一条弧一端的端面:中线上的点、沿路径前进的单位切向,以及环带在这一端的外角与内角(同一参数角上外 / 内边界的点)。</summary>
    private readonly record struct ArcFace(double X, double Y, double Ux, double Uy, (double X, double Y) Outer, (double X, double Y) Inner);

    /// <summary>
    /// 宽弧路径上的一条弧:中线、外边界、内边界在同一组参数角上采样,外加沿中线累计的弧长(边界的取法见 <see cref="WideArcs" />)。
    /// 采样只在用得着时做(碰得到可画区域,或者虚线要量它的长度);两端的端面直接按端点的参数角算,与采样数组的首尾逐位相同。
    /// </summary>
    private sealed class ArcStroke
    {
        private readonly double _half;
        private readonly int _a1, _a2;

        /// <summary><see cref="Segment" /> 时线段的法线方向上半个线宽(竖线朝右、横线朝下):边界是中线两侧各平移这么多。</summary>
        private readonly double _nx, _ny;

        private ArcFace? _begin, _end;

        public ArcStroke(ArcSpec arc, double half)
        {
            _half = half;
            (_a1, _a2) = (arc.A1, ClampExtent(arc.A2));
            (Cx, Cy, Rx, Ry) = Ellipse(arc.X, arc.Y, arc.W, arc.H);
            (Start, Extent) = ArcAngles(arc.A1, arc.A2);
            Full = Math.Abs(arc.A2) >= 360 * 64;
            N = ArcSteps(Extent, Math.Max(Rx, Ry) + half);
            Segment = (Rx == 0) != (Ry == 0);
            (_nx, _ny) = Rx == 0 ? (half, 0.0) : (0.0, half);
        }

        /// <summary>外接框的宽或高(只有一个)为 0:路径是一条线段,在上面来回走(见 <see cref="AddSegmentBand" />)。</summary>
        public bool Segment { get; }

        /// <summary>
        /// 两端在 90° 的倍数上时按精确的 cos / sin(0、±1)取点(在 <see cref="Sample" /> 之前设)。与别的弧相接的弧这样做:
        /// 端面正好与坐标轴平行、接点正好落在端面上,两条环带与接头在端面上严丝合缝 —— 按浮点算的 cos(π/2) 不是 0,
        /// 两边的端面会差几个末位,正好压在端面上的一排像素中心两边都不算。单独的一条弧不这样做(像素与原来逐个相同)。
        /// </summary>
        public bool ExactEnds { get; set; }

        public double Cx { get; }
        public double Cy { get; }
        public double Rx { get; }
        public double Ry { get; }
        public double Start { get; }
        public double Extent { get; }

        /// <summary>采样段数。</summary>
        public int N { get; }

        /// <summary>整圆(跨度 ≥ 360°)。</summary>
        public bool Full { get; }

        /// <summary>长度为 0(跨度为 0,或外接框缩成一点)。</summary>
        public bool IsPoint => Extent == 0 || (Rx == 0 && Ry == 0);

        public (double X, double Y)[] Center { get; private set; } = [];
        public (double X, double Y)[] Outer { get; private set; } = [];
        public (double X, double Y)[] Inner { get; private set; } = [];

        /// <summary>中线的累计弧长;<see cref="Sample" /> 之后才有。</summary>
        public double Total { get; private set; }

        private double[] _length = [];

        /// <summary>起点的端面。</summary>
        public ArcFace Begin => _begin ??= Face(0);

        /// <summary>终点的端面。</summary>
        public ArcFace End => _end ??= Face(N);

        /// <summary>第 i 个采样点的参数角。</summary>
        private double T(int i) => Start + (Extent * i / N);

        /// <summary>第 i 个采样点上参数角的 cos 与 sin(<see cref="ExactEnds" /> 时两端在 90° 的倍数上取精确值)。</summary>
        private (double Cos, double Sin) Angle(int i)
        {
            int degrees64 = i == 0 ? _a1 : _a1 + _a2;
            if (ExactEnds && (i == 0 || i == N) && degrees64 % (90 * 64) == 0)
            {
                return ((((degrees64 / (90 * 64)) % 4) + 4) % 4) switch
                {
                    0 => (1, 0),
                    1 => (0, 1),
                    2 => (-1, 0),
                    _ => (0, -1),
                };
            }
            double t = T(i);
            return (Math.Cos(t), Math.Sin(t));
        }

        /// <summary>半轴 (<paramref name="rx" />, <paramref name="ry" />) 的椭圆上参数角为 <paramref name="angle" /> 的点(同 <see cref="EllipsePoint" />)。</summary>
        private (double X, double Y) Point(double rx, double ry, (double Cos, double Sin) angle) =>
            (Cx + (rx * angle.Cos), Cy - (ry * angle.Sin));

        public void Sample()
        {
            if (_length.Length != 0)
            {
                return;
            }
            WorkBudget.Charge(N);
            Center = new (double X, double Y)[N + 1];
            Outer = new (double X, double Y)[N + 1];
            Inner = new (double X, double Y)[N + 1];
            _length = new double[N + 1];
            for (int i = 0; i <= N; i++)
            {
                (double Cos, double Sin) t = Angle(i);
                Center[i] = Point(Rx, Ry, t);
                (Outer[i], Inner[i]) = Bounds(Center[i], t);
                if (i > 0)
                {
                    double dx = Center[i].X - Center[i - 1].X, dy = Center[i].Y - Center[i - 1].Y;
                    _length[i] = _length[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
                }
            }
            Total = _length[N];
        }

        /// <summary>中线上弧长 <paramref name="at" /> 处所在的采样段下标与段内比例。</summary>
        public (int Index, double Fraction) Locate(double at)
        {
            if (at <= 0)
            {
                return (0, 0);
            }
            if (at >= Total)
            {
                return (N - 1, 1);
            }
            int lo = 0, hi = N;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >>> 1;
                if (_length[mid] <= at)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            double span = _length[lo + 1] - _length[lo];
            return (lo, span <= 0 ? 0 : Math.Clamp((at - _length[lo]) / span, 0, 1));
        }

        public static (double X, double Y) Lerp((double X, double Y)[] curve, int i, double f) =>
            (curve[i].X + ((curve[i + 1].X - curve[i].X) * f), curve[i].Y + ((curve[i + 1].Y - curve[i].Y) * f));

        /// <summary>
        /// 曲线上弧长 <paramref name="at" /> 处的点(<paramref name="i" />, <paramref name="f" /> 是 <see cref="Locate" /> 的结果)。
        /// <see cref="ExactEnds" /> 时终点直接取最后一个采样点 —— 插值 a + (b − a) × 1 不一定逐位等于 b,端面要与接头用同一个点。
        /// </summary>
        public (double X, double Y) At((double X, double Y)[] curve, double at, int i, double f) =>
            ExactEnds && at >= Total ? curve[N] : Lerp(curve, i, f);

        /// <summary>
        /// 第 <paramref name="i" /> 个采样点(0 或 N)处的端面。前进方向取参数角上的切向 (−rx·sin t, −ry·cos t)(逆时针的弧),顺时针的反过来;
        /// 一根轴长为 0 的椭圆(一条线段)在折返的那一头切向为 0,改用相邻采样点的弦。
        /// </summary>
        private ArcFace Face(int i)
        {
            (double Cos, double Sin) t = Angle(i);
            (double X, double Y) p = Point(Rx, Ry, t);
            double ux = -Rx * t.Sin, uy = -Ry * t.Cos;
            if (Extent < 0)
            {
                (ux, uy) = (-ux, -uy);
            }
            double d = Math.Sqrt((ux * ux) + (uy * uy));
            if (d <= 1e-9 * (Rx + Ry))
            {
                (double X, double Y) q = Point(Rx, Ry, Angle(i == 0 ? 1 : N - 1));
                (ux, uy) = i == 0 ? (q.X - p.X, q.Y - p.Y) : (p.X - q.X, p.Y - q.Y);
                d = Math.Sqrt((ux * ux) + (uy * uy));
            }
            d = d > 0 ? d : 1;
            ((double X, double Y) outer, (double X, double Y) inner) = Bounds(p, t);
            return new ArcFace(p.X, p.Y, ux / d, uy / d, outer, inner);
        }

        /// <summary>
        /// 中线上参数角为 <paramref name="t" /> 的点 <paramref name="center" /> 对应的外边界与内边界上的点:半轴各加 / 减半个线宽(不小于 0)的椭圆上
        /// 同一参数角的点;<see cref="Segment" /> 时是中线两侧各平移半个线宽(协议:宽或高为 0 时边界就是与路径相距 lw/2 的两条线,
        /// 不由实现决定 —— 原先内边界缩成中线自己,只画出一半线宽)。
        /// </summary>
        private ((double X, double Y) Outer, (double X, double Y) Inner) Bounds((double X, double Y) center, (double Cos, double Sin) t) =>
            Segment
                ? ((center.X + _nx, center.Y + _ny), (center.X - _nx, center.Y - _ny))
                : (Point(Rx + _half, Ry + _half, t), Point(Math.Max(0, Rx - _half), Math.Max(0, Ry - _half), t));
    }

    /// <summary>一串宽弧在一张活动边表里攒到这么多个顶点就先填掉(见 <see cref="WideArcs" />)。</summary>
    private const int MaxStrokeVertices = 1 << 20;

    /// <summary>
    /// 一串宽弧:每条弧是沿弧的内外两条边界围成的环带。外边界是半轴各加半个线宽的椭圆,内边界是各减半个线宽(不小于 0)的椭圆 ——
    /// 对圆来说正好是与弧相距线宽一半的两条曲线;椭圆的边界协议留给实现,只要求形状(相对圆心)只取决于宽、高与线宽,
    /// 所以圆心与半轴都不取整;外接框宽或高为 0 的弧是一条线段,边界是它两侧的两条线(<see cref="AddSegmentBand" />)。相邻两条弧之间按 join-style 加接头(<see cref="AddArcJoin" />);整串的两头按 cap-style 加端帽
    /// (端面是同一参数角上内外两点的连线),<paramref name="loop" />(首尾相接,或一条整圆)时哪里都不加,<paramref name="seamJoin" />
    /// 时接缝上也按 join-style 接。虚线沿中线量,跨过接点接着走。整串的各块放进同一张活动边表一次填,每个像素只画一次。
    /// </summary>
    /// <remarks>
    /// 一串弧的块要攒到一起才能填,而一条大弧的环带有上万个顶点:一个请求里成千上万条相接的大弧(比如同一个位置的整圆一条接一条)
    /// 攒在一起就是几个 GB。攒到 <see cref="MaxStrokeVertices" /> 个顶点就在两条弧之间先填掉一批 —— 只有这么长的串,批与批之间
    /// 重叠的像素才会画两次。
    /// </remarks>
    private void WideArcs(List<ArcSpec> chain, bool loop, bool seamJoin)
    {
        double half = _gc.LineWidth / 2.0;
        List<ArcStroke> arcs = [];
        if (chain.Count == 1)
        {
            ArcStroke only = new(chain[0], half);
            if (!Reaches(only))
            {
                return;
            }
            arcs.Add(only);
        }
        else
        {
            // 长度为 0 的弧从路径里拿掉(与 PolyLine 拿掉端点重合的线段一样);全是这种弧时路径缩成一点,按第一条弧单独画(两端的端帽)。
            foreach (ArcSpec spec in chain)
            {
                ArcStroke arc = new(spec, half);
                if (!arc.IsPoint)
                {
                    arcs.Add(arc);
                }
            }
            if (arcs.Count == 0)
            {
                bool full = Math.Abs(chain[0].A2) >= 360 * 64;
                WideArcs([chain[0]], loop: full, seamJoin: false);
                return;
            }
        }
        foreach (ArcStroke arc in arcs)
        {
            arc.ExactEnds = arcs.Count > 1 || seamJoin;
        }
        bool dashed = Dashed;
        List<Polygon> solid = [];
        List<Polygon>? even = dashed ? [] : null;
        WideDash dash = dashed ? StartWideDash() : default;
        // 虚线的走位:inRun = 正在一个偶数段里(可能是从上一条弧接过来的);路径起点上的偶数段要不要端帽,得看最后一个偶数段是否也碰到终点(接缝)。
        bool inRun = false, firstRunAtStart = false, lastRunAtEnd = false;
        double runStart = 0;
        List<Polygon>? startCap = null;
        int solidSeen = 0, evenSeen = 0;
        long vertices = 0;
        int last = arcs.Count - 1;
        for (int k = 0; k < arcs.Count; k++)
        {
            ArcStroke arc = arcs[k];
            bool reaches = Reaches(arc);
            if (reaches || dashed)
            {
                arc.Sample();
            }
            if (k > 0)
            {
                ArcFace into = arcs[k - 1].End, outOf = arc.Begin;
                AddArcJoin(solid, into, outOf, half);
                if (inRun)
                {
                    AddArcJoin(even!, into, outOf, half);
                }
            }
            if (reaches)
            {
                AddArcBand(solid, arc, 0, arc.Total);
                if (!loop && k == 0)
                {
                    AddArcCap(solid, arc, 0, forward: false, _gc.CapStyle);
                }
                if (!loop && k == last)
                {
                    AddArcCap(solid, arc, arc.Total, forward: true, _gc.CapStyle);
                }
            }
            if (dashed)
            {
                List<Polygon> on = even!;
                double total = arc.Total, s = 0;
                runStart = 0;   // 从上一条弧接过来的偶数段,在这条弧上从起点量
                while (total - s > 1e-9)
                {
                    WorkBudget.Charge(1);
                    double take = Math.Min(dash.Remaining, total - s);
                    if ((dash.Index & 1) == 0)
                    {
                        if (!inRun)
                        {
                            (inRun, runStart) = (true, s);
                            if (k == 0 && s == 0)
                            {
                                firstRunAtStart = true;
                                if (loop)
                                {
                                    startCap = [];
                                    if (reaches)
                                    {
                                        AddArcCap(startCap, arc, 0, forward: false, InternalCap);
                                    }
                                }
                                else if (reaches)
                                {
                                    AddArcCap(on, arc, 0, forward: false, _gc.CapStyle);
                                }
                            }
                            else if (dash.Fresh && reaches)
                            {
                                AddArcCap(on, arc, s, forward: false, InternalCap);
                            }
                        }
                    }
                    else if (inRun)
                    {
                        if (reaches)
                        {
                            AddArcBand(on, arc, runStart, s);
                            AddArcCap(on, arc, s, forward: true, InternalCap);
                        }
                        inRun = false;
                    }
                    s += take;
                    dash.Remaining -= take;
                    dash.Fresh = false;
                    if (dash.Remaining <= 1e-9)
                    {
                        dash.Index = (dash.Index + 1) % _gc.Dashes.Length;
                        dash.Remaining = _gc.Dashes[dash.Index];
                        dash.Fresh = true;
                    }
                }
                if (inRun)
                {
                    // 偶数段一直走到这条弧的终点。单独一条弧照原来的取法,量到一段段加出来的 s(浮点加法可能差终点一点点);
                    // 与别的弧相接的弧量到正好的终点,与接头、下一条弧在端面上严丝合缝。
                    double end = arc.ExactEnds ? total : s;
                    if (reaches)
                    {
                        AddArcBand(on, arc, runStart, end);
                    }
                    if (k == last)
                    {
                        // 路径的终点:闭合时与起点上的偶数段在接缝处连起来就不加端帽。
                        lastRunAtEnd = end >= total;
                        byte cap = !lastRunAtEnd ? InternalCap : loop ? (firstRunAtStart ? (byte)1 : InternalCap) : _gc.CapStyle;
                        if (reaches)
                        {
                            AddArcCap(on, arc, end, forward: true, cap);
                        }
                    }
                    else if ((dash.Index & 1) != 0)
                    {
                        // 偶数段正好在这条弧的终点结束:就在这里收尾,不与下一条弧接。
                        if (reaches)
                        {
                            AddArcCap(on, arc, end, forward: true, InternalCap);
                        }
                        inRun = false;
                    }
                }
            }
            vertices += CountVertices(solid, ref solidSeen) + (even is null ? 0 : CountVertices(even, ref evenSeen));
            if (vertices > MaxStrokeVertices && k < last)
            {
                FillStroke(solid, even);
                solid.Clear();
                even?.Clear();
                (solidSeen, evenSeen, vertices) = (0, 0, 0);
            }
        }
        bool seam = loop && firstRunAtStart && lastRunAtEnd;
        if (seamJoin)
        {
            AddArcJoin(solid, arcs[last].End, arcs[0].Begin, half);
            if (seam)
            {
                AddArcJoin(even!, arcs[last].End, arcs[0].Begin, half);
            }
        }
        if (!seam && startCap is not null)
        {
            even!.AddRange(startCap);
        }
        FillStroke(solid, even);

        bool Reaches(ArcStroke a) => BoxReaches(a.Cx - a.Rx - half, a.Cy - a.Ry - half, a.Cx + a.Rx + half, a.Cy + a.Ry + half);
    }

    /// <summary>从第 <paramref name="seen" /> 块起新加的多边形一共多少个顶点(数过的不再数)。</summary>
    private static long CountVertices(List<Polygon> polys, ref int seen)
    {
        long sum = 0;
        for (; seen < polys.Count; seen++)
        {
            sum += polys[seen].Count;
        }
        return sum;
    }

    /// <summary>一条弧上弧长 [<paramref name="s0" />, <paramref name="s1" />] 这一截环带。</summary>
    private void AddArcBand(List<Polygon> polys, ArcStroke arc, double s0, double s1)
    {
        (int i0, double f0) = arc.Locate(s0);
        (int i1, double f1) = arc.Locate(s1);
        if (arc.Segment)
        {
            AddSegmentBand(polys, arc, ArcStroke.Lerp(arc.Center, i0, f0), i0, i1, arc.At(arc.Center, s1, i1, f1));
            return;
        }
        Polygon band = [with(((i1 - i0 + 2) * 2) + 2)];
        band.Add(ArcStroke.Lerp(arc.Outer, i0, f0));
        for (int i = i0 + 1; i <= i1; i++)
        {
            band.Add(arc.Outer[i]);
        }
        band.Add(arc.At(arc.Outer, s1, i1, f1));
        band.Add(arc.At(arc.Inner, s1, i1, f1));
        for (int i = i1; i > i0; i--)
        {
            band.Add(arc.Inner[i]);
        }
        band.Add(ArcStroke.Lerp(arc.Inner, i0, f0));
        AddIfReaches(polys, band);
    }

    /// <summary>
    /// 外接框宽或高为 0 的弧(<see cref="ArcStroke.Segment" />)上从 <paramref name="from" /> 到 <paramref name="to" /> 的一截:中线是一条线段,
    /// 在 ±90°(宽为 0)或 0° / 180°(高为 0)处走到头折返。协议「PolyArc」:宽高有一个为 0 时边界不由实现决定,就是与路径相距 lw/2 的
    /// 两条线 —— 所以每段单调的走位画一个宽为线宽的矩形;在两端之间折返的地方,边界绕着线段的一头转半圈(宽高趋于 0 的椭圆在那一头的极限),
    /// 补一个直径为线宽的圆。中间的采样点只用来看往哪边走,折返点取线段的端点本身(采样点不一定正好落在上面)。
    /// </summary>
    private void AddSegmentBand(List<Polygon> polys, ArcStroke arc, (double X, double Y) from, int i0, int i1, (double X, double Y) to)
    {
        double half = _gc.LineWidth / 2.0;
        bool vertical = arc.Rx == 0;
        double across = vertical ? arc.Cx : arc.Cy, middle = vertical ? arc.Cy : arc.Cx, reach = vertical ? arc.Ry : arc.Rx;
        double runStart = vertical ? from.Y : from.X, previous = runStart;
        int direction = 0;
        for (int i = i0 + 1; i <= i1 + 1; i++)
        {
            (double X, double Y) p = i <= i1 ? arc.Center[i] : to;
            double along = vertical ? p.Y : p.X;
            int step = Math.Sign(along - previous);
            if (step != 0 && direction != 0 && step != direction)
            {
                double tip = middle + (direction * reach);
                AddSegmentRect(polys, vertical, across, runStart, tip, half);
                AddCircle(polys, vertical ? across : tip, vertical ? tip : across, half);
                runStart = tip;
            }
            if (step != 0)
            {
                direction = step;
            }
            previous = along;
        }
        AddSegmentRect(polys, vertical, across, runStart, previous, half);
    }

    /// <summary>线段(竖线在 x = <paramref name="across" />、横线在 y = <paramref name="across" />)上 [a, b] 这一截的宽线矩形。</summary>
    private void AddSegmentRect(List<Polygon> polys, bool vertical, double across, double a, double b, double half)
    {
        if (a == b)
        {
            return;
        }
        AddIfReaches(polys, vertical
            ? [(across - half, a), (across + half, a), (across + half, b), (across - half, b)]
            : [(a, across - half), (b, across - half), (b, across + half), (a, across + half)]);
    }

    /// <summary>
    /// 弧上弧长 <paramref name="at" /> 处的端帽:方向取所在那一小段弦的方向(<paramref name="forward" /> 时朝前,否则朝后);
    /// Projecting 沿它把端面推出半个线宽。
    /// </summary>
    private void AddArcCap(List<Polygon> polys, ArcStroke arc, double at, bool forward, byte cap)
    {
        if (cap is not (2 or 3))
        {
            return;
        }
        double half = _gc.LineWidth / 2.0;
        (int i, double f) = arc.Locate(at);
        (double x, double y) = arc.At(arc.Center, at, i, f);
        double dx = arc.Center[i + 1].X - arc.Center[i].X, dy = arc.Center[i + 1].Y - arc.Center[i].Y, d = Math.Sqrt((dx * dx) + (dy * dy));
        if (cap == 2)
        {
            AddCircle(polys, x, y, half);
        }
        else if (d < 1e-12)
        {
            AddIfReaches(polys, [(x - half, y - half), (x + half, y - half), (x + half, y + half), (x - half, y + half)]);
        }
        else
        {
            double ux = (forward ? dx : -dx) / d * half, uy = (forward ? dy : -dy) / d * half;
            (double X, double Y) a = arc.At(arc.Inner, at, i, f), b = arc.At(arc.Outer, at, i, f);
            AddIfReaches(polys, [a, b, (b.X + ux, b.Y + uy), (a.X + ux, a.Y + uy)]);
        }
    }

    /// <summary>
    /// 两条相接的宽弧之间的接头:<paramref name="into" /> 是前一条终点的端面,<paramref name="outOf" /> 是后一条起点的端面。
    /// </summary>
    /// <remarks>
    /// 协议:join-style 的用法与「在接点上沿两条弧切线的两条线」相同;椭圆上不在 90° 倍数处的端面,接头的形状由实现决定。
    /// 环带的端面是同一参数角上内外两点的连线,对圆就是法线,对椭圆不一定 —— 所以接头搭在两条环带真实的端面角上,不留缝:
    /// Round 补一个直径为线宽、以接点为心的圆;Bevel 补外侧两角与接点围成的三角;Miter 再把外侧两角沿各自的切线延长到相交,
    /// 夹角小于 11° 时退成 Bevel(与 <see cref="AddJoin" /> 相同)。端面歪着时内侧也可能张开一道缝,补上那个三角。
    /// 切线连续(同一个椭圆上相邻的两段)时只补缝:两条共线的线不管按什么 join-style 接,都不比两条线本身多出像素。
    /// 两个端点只是在判据之内、并不逐位重合时,后一条的端面平移到前一条的端点上再算,平移扫过的平行四边形也补上。
    /// </remarks>
    private void AddArcJoin(List<Polygon> polys, ArcFace into, ArcFace outOf, double half)
    {
        double px = into.X, py = into.Y, sx = px - outOf.X, sy = py - outOf.Y;
        (double X, double Y) outer = (outOf.Outer.X + sx, outOf.Outer.Y + sy), inner = (outOf.Inner.X + sx, outOf.Inner.Y + sy);
        if (Math.Abs(sx) > 1e-9 || Math.Abs(sy) > 1e-9)
        {
            AddIfReaches(polys, [outOf.Inner, outOf.Outer, outer, inner]);
        }
        double cross = (into.Ux * outOf.Uy) - (into.Uy * outOf.Ux);
        double dot = (into.Ux * outOf.Ux) + (into.Uy * outOf.Uy);
        bool smooth = Math.Abs(cross) < 1e-9 && dot > 0;
        if (_gc.JoinStyle == 1 && !smooth)
        {
            AddCircle(polys, px, py, half);
            return;
        }
        // 左法线 (−uy, ux) 那一侧记 +1;往哪边转,外侧就在另一边(与 AddJoin 相同)。
        int outside = cross < 0 ? 1 : -1;
        for (int side = 1; side >= -1; side -= 2)
        {
            (double X, double Y) a = FaceCorner(px, py, into.Ux, into.Uy, into.Outer, into.Inner, side, half);
            (double X, double Y) b = FaceCorner(px, py, outOf.Ux, outOf.Uy, outer, inner, side, half);
            if (!smooth && side == outside)
            {
                if (_gc.JoinStyle == 0 && -dot <= MiterLimitCos && MiterTip(a, into, b, outOf, cross) is { } tip)
                {
                    AddIfReaches(polys, [(px, py), a, tip, b]);
                }
                else
                {
                    AddIfReaches(polys, [(px, py), a, b]);
                }
            }
            else if (Ahead(px, py, a, into, b) && !Ahead(px, py, b, outOf, a))
            {
                // 后一条的角伸到了前一条端面的前方、前一条的角又在后一条端面的后方:两个端面之间张开了,补上。
                AddIfReaches(polys, [(px, py), a, b]);
            }
        }
    }

    /// <summary>
    /// 端面在 <paramref name="side" /> 一侧(左法线方向为 +1)的角:环带的外角与内角里落在这一侧的那个;端面不横跨路径时
    /// (轴长为 0 的椭圆在折返的那一头,外角内角都在中线上)按线的法线取半个线宽处。
    /// </summary>
    private static (double X, double Y) FaceCorner(double px, double py, double ux, double uy,
        (double X, double Y) outer, (double X, double Y) inner, int side, double half)
    {
        double nx = -uy * side, ny = ux * side;
        double po = ((outer.X - px) * nx) + ((outer.Y - py) * ny), pi = ((inner.X - px) * nx) + ((inner.Y - py) * ny);
        if (po * pi < 0)
        {
            return po > 0 ? outer : inner;
        }
        return (px + (nx * half), py + (ny * half));
    }

    /// <summary><paramref name="point" /> 是否严格落在端面(接点 → <paramref name="corner" /> 的那条线)朝前进方向的一侧。</summary>
    private static bool Ahead(double px, double py, (double X, double Y) corner, ArcFace face, (double X, double Y) point)
    {
        double fx = corner.X - px, fy = corner.Y - py;
        double toward = (fx * face.Uy) - (fy * face.Ux);
        double at = (fx * (point.Y - py)) - (fy * (point.X - px));
        return Math.Sign(toward) * at > 1e-9 * ((fx * fx) + (fy * fy));
    }

    /// <summary>
    /// Miter 的尖:从 <paramref name="a" /> 沿前一条的切向往前、从 <paramref name="b" /> 沿后一条的切向往回,两条线的交点;
    /// 交点落在任何一条的反方向上(端面歪得厉害)时没有尖,退成 Bevel。
    /// </summary>
    private static (double X, double Y)? MiterTip((double X, double Y) a, ArcFace into, (double X, double Y) b, ArcFace outOf, double cross)
    {
        // a + s·u₁ = b − r·u₂
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double s = ((dx * outOf.Uy) - (dy * outOf.Ux)) / cross;
        double r = ((into.Ux * dy) - (into.Uy * dx)) / cross;
        return s >= -1e-9 && r >= -1e-9 ? (a.X + (into.Ux * s), a.Y + (into.Uy * s)) : null;
    }

    public void FillArc(int x, int y, int w, int h, int angle1, int angle2)
    {
        Polygon points = ArcPoints(x, y, w, h, angle1, angle2);
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
        DrawGlyphRuns(glyph, originX + glyph.Info.LeftBearing, baselineY - glyph.Info.Ascent, copy: false, 0);
        return glyph.Info.Width;
    }

    /// <summary>ImageText:先按 GXcopy 用背景色填字体的行框,再用前景色画字形。</summary>
    public void ImageText(XFont font, ReadOnlySpan<int> codes, int x, int baselineY)
    {
        (int width, _, _, _, _) = font.Measure(codes);
        int top = baselineY - font.Ascent, height = font.Ascent + font.Descent;
        uint background = _gc.Background, foreground = _gc.Foreground;
        for (int yy = top; yy < top + height; yy++)
        {
            FillSpanCopy(yy, x, x + width, background);
        }
        int pen = x;
        foreach (int code in codes)
        {
            if (font.Lookup(code) is not { } glyph)
            {
                continue;
            }
            DrawGlyphRuns(glyph, pen + glyph.Info.LeftBearing, baselineY - glyph.Info.Ascent, copy: true, foreground);
            pen += glyph.Info.Width;
        }
    }
}
