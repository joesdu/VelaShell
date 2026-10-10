// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 ——
//   §3.3「Points」(非反走样的点:以 (x_w, y_w) 为中心、边长为取整后点大小的正方形覆盖的像素中心)、
//   §3.4「Line Segments」(非反走样线段的「菱形出口」规则;宽线沿次轴方向加宽;§3.4.2 线的点画:计数器 s、factor、16 位图样)、
//   §3.5.2「Stippling」(多边形的点画:按窗口坐标 mod 32 取 32×32 图样的位)、
//   §3.5.1「Basic Polygon Rasterization」(取像素中心判定覆盖,属性按重心坐标插值,式 3.9 的透视校正)、
//   §3.8.8–3.8.9「Texture Minification / Magnification」(NEAREST / LINEAR)、§3.8.7「Texture Wrap Modes」、
//   §3.8.10「Texture Completeness」(要 mipmap 的缩小过滤而各级不全时,纹理视为未启用)、
//   §3.8.13「Texture Environments and Texture Functions」(Table 3.22:REPLACE / MODULATE / DECAL / BLEND / ADD 按基本内部格式)、
//   §3.9「Color Sum」、§3.10「Fog」(LINEAR / EXP / EXP2,C = f·C_r + (1−f)·C_f)、
//   §4.1.2–4.1.8「Scissor / Alpha / Stencil / Depth Test / Blending」、§4.1.10「Logical Operation」、
//   §4.2.2「Fine Control of Buffer Updates」(ColorMask、DepthMask、StencilMask)、§4.2.3「Clearing the Buffers」
//   (清除受剪裁测试与各写掩码约束)。
//
//   帧缓冲按 X 的行序存(第 0 行在最上面):窗口坐标 y(GL,向上)落在第 H−1−y 行。

using System.Numerics;
using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    /// <summary>窗口坐标里的一个顶点。</summary>
    private struct RasterVertex
    {
        public float X;
        public float Y;
        public float Z;
        public float InvW;
        public Vector4 Color;
        public Vector3 Spec;
        public Vector4 Tex;
        public float Fog;
    }

    // 每个图元开始时从状态里取一份,片元循环里不再查集合。
    private uint[]? _targetFront;
    private uint[]? _targetBack;
    private int _fbWidth;
    private int _fbHeight;
    private int _clipX0, _clipY0, _clipX1, _clipY1;
    private bool _depthTest, _stencilTest, _alphaTest, _blend, _logicOp, _fog, _colorSum, _anyColorMask;

    /// <summary>写颜色时保留目标原值的位(ColorMask 关掉的通道);0 = 四个通道都写。</summary>
    private uint _colorKeep;

    /// <summary>绘制表面有没有 alpha 通道(没有的话目标 alpha 视为 1)。</summary>
    private bool _surfaceAlpha;

    /// <summary>写的颜色缓冲就是表面的前缓冲(单缓冲的 BACK 也落在这里):写了要记脏范围。</summary>
    private bool _targetIsFront;
    private GlTexture? _activeTexture;
    private Vector4 _fogColor;

    /// <summary>
    /// <see cref="PrepareRaster" /> 已经为当前这条命令取过状态(结果在 <see cref="_rasterReady" />):一个图元拆出的各个三角形、
    /// 各段线共用一份,不每个都重新查一遍开关、重新判断纹理完整(xs_plan GL-P1)。每条命令开始执行、换表面时作废。
    /// </summary>
    private bool _rasterPrepared;
    private bool _rasterReady;

    /// <summary>状态可能变了:下一次光栅化重新取。</summary>
    private void InvalidateRaster() => _rasterPrepared = false;

    /// <summary>取当前状态,准备写片元;没有表面或没有可写的颜色缓冲时返回 false。同一条命令里只取一次。</summary>
    private bool PrepareRaster()
    {
        if (_rasterPrepared)
        {
            return _rasterReady;
        }
        FlushDrawn();   // 目标缓冲可能要变了:之前画过的先按原来的目标记下
        _rasterPrepared = true;
        _rasterReady = false;
        if (Draw is not { } surface || RenderModeValue != GlEnum.RENDER)
        {
            return false;
        }
        (_targetFront, _targetBack) = State.DrawBuffer switch
        {
            GlEnum.FRONT or GlEnum.FRONT_LEFT or GlEnum.LEFT => (surface.Front, null),
            GlEnum.BACK or GlEnum.BACK_LEFT => (surface.Color(back: true), null),
            GlEnum.FRONT_AND_BACK => (surface.Front, surface.Back),
            _ => (null, null),
        };
        _fbWidth = surface.Width;
        _fbHeight = surface.Height;
        (_clipX0, _clipY0, _clipX1, _clipY1) = (0, 0, _fbWidth, _fbHeight);
        if (State.Enabled.Has(GlEnum.SCISSOR_TEST))
        {
            _clipX0 = Math.Max(_clipX0, State.ScissorX);
            _clipY0 = Math.Max(_clipY0, State.ScissorY);
            _clipX1 = Math.Min(_clipX1, State.ScissorX + State.ScissorWidth);
            _clipY1 = Math.Min(_clipY1, State.ScissorY + State.ScissorHeight);
        }
        _depthTest = State.Enabled.Has(GlEnum.DEPTH_TEST);
        _stencilTest = State.Enabled.Has(GlEnum.STENCIL_TEST);
        _alphaTest = State.Enabled.Has(GlEnum.ALPHA_TEST);
        _logicOp = State.Enabled.Has(GlEnum.COLOR_LOGIC_OP);
        _blend = !_logicOp && State.Enabled.Has(GlEnum.BLEND);
        _fog = State.Enabled.Has(GlEnum.FOG);
        _fogColor = State.FogColor;
        _colorSum = State.Enabled.Has(GlEnum.LIGHTING) && State.LightModelColorControl == GlEnum.SEPARATE_SPECULAR_COLOR;
        _anyColorMask = State.ColorMask[0] || State.ColorMask[1] || State.ColorMask[2] || State.ColorMask[3];
        _colorKeep = (State.ColorMask[0] ? 0 : 0x00FF0000u) | (State.ColorMask[1] ? 0 : 0x0000FF00u)
                     | (State.ColorMask[2] ? 0 : 0x000000FFu) | (State.ColorMask[3] ? 0 : 0xFF000000u);
        _surfaceAlpha = surface.HasAlpha;
        _targetIsFront = ReferenceEquals(_targetFront, surface.Front);
        _activeTexture = CompleteTexture();
        _rasterReady = _clipX0 < _clipX1 && _clipY0 < _clipY1;
        return _rasterReady;
    }

    /// <summary>当前生效的纹理:2D 优先于 1D,不完整的视为未启用(§3.8.10、§3.8.15)。</summary>
    private GlTexture? CompleteTexture()
    {
        uint cap = State.Enabled.Has(GlEnum.TEXTURE_2D) ? GlEnum.TEXTURE_2D
            : State.Enabled.Has(GlEnum.TEXTURE_1D) ? GlEnum.TEXTURE_1D
            : 0u;
        if (cap == 0)
        {
            return null;
        }
        GlTexture? t = BoundTexture(cap);
        return t is not null && t.IsComplete ? t : null;
    }

    // ------------------------------------------------------------------ 三角形

    private void RasterTriangle(RasterVertex a, RasterVertex b, RasterVertex c)
    {
        if (!PrepareRaster())
        {
            return;
        }
        float area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (area == 0 || !float.IsFinite(area))
        {
            return;
        }
        if (area < 0)
        {
            (b, c) = (c, b);
            area = -area;
        }
        int minX = Math.Max(_clipX0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(_clipX1 - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(_clipY0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(_clipY1 - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
        {
            return;
        }
        // 共享边只归一个三角形:边函数为 0 时只有「上边或左边」算在内。
        bool topLeft0 = IsTopLeft(b, c), topLeft1 = IsTopLeft(c, a), topLeft2 = IsTopLeft(a, b);
        float inv = 1 / area;
        // 只插值这次真用得上的属性:没有纹理不插纹理坐标,没开颜色求和不插副颜色,没开雾不插雾坐标,三个顶点同色不插颜色。
        bool flatColor = a.Color == b.Color && b.Color == c.Color;
        bool texture = _activeTexture is not null, spec = _colorSum, fog = _fog;
        uint[]? stipple = State.Enabled.Has(GlEnum.POLYGON_STIPPLE) ? State.PolygonStipple : null;   // §3.5.2:按窗口坐标 mod 32 取位
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            uint stippleRow = stipple is null ? uint.MaxValue : stipple[y & 31];
            if (stippleRow == 0)
            {
                WorkBudget.Charge(1);
                continue;
            }
            // 扫描线:先由三条边函数解出这一行可能被覆盖的那一段,只在段里逐像素判定 —— 细长的斜三角形不再白扫整个包围盒
            // (原先每个包围盒像素都算三条边函数)。段放宽了一个像素,逐像素仍按原式判定,覆盖与原先一样。
            if (!RowSpan(a, b, c, py, minX, maxX, out int x0, out int x1))
            {
                WorkBudget.Charge(1);
                continue;
            }
            WorkBudget.Charge(1 + ((long)(x1 - x0 + 1) * (1 + FragmentWork)));   // 段里每个像素:边函数,加上可能的片元
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float w0 = ((c.X - b.X) * (py - b.Y)) - ((c.Y - b.Y) * (px - b.X));
                float w1 = ((a.X - c.X) * (py - c.Y)) - ((a.Y - c.Y) * (px - c.X));
                float w2 = ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
                if (w0 < 0 || w1 < 0 || w2 < 0
                    || (w0 == 0 && !topLeft0) || (w1 == 0 && !topLeft1) || (w2 == 0 && !topLeft2)
                    || ((stippleRow >> (x & 31)) & 1) == 0)
                {
                    continue;
                }
                float l0 = w0 * inv, l1 = w1 * inv, l2 = w2 * inv;
                float z = (l0 * a.Z) + (l1 * b.Z) + (l2 * c.Z);
                // 透视校正(式 3.9):按 1/w 加权。
                float p0 = l0 * a.InvW, p1 = l1 * b.InvW, p2 = l2 * c.InvW;
                float sum = p0 + p1 + p2;
                if (sum != 0)
                {
                    (p0, p1, p2) = (p0 / sum, p1 / sum, p2 / sum);
                }
                ShadeFragment(x, y, z,
                    flatColor ? a.Color : (p0 * a.Color) + (p1 * b.Color) + (p2 * c.Color),
                    spec ? (p0 * a.Spec) + (p1 * b.Spec) + (p2 * c.Spec) : default,
                    texture ? (p0 * a.Tex) + (p1 * b.Tex) + (p2 * c.Tex) : default,
                    fog ? (p0 * a.Fog) + (p1 * b.Fog) + (p2 * c.Fog) : 0);
            }
        }
    }

    /// <summary>
    /// 扫描线 <paramref name="py" /> 上三条边函数都可能 ≥ 0 的像素列 [<paramref name="x0" />, <paramref name="x1" />](已夹到包围盒,
    /// 两头各放宽一个像素);一个都没有返回 false。边函数在一行里是 px 的一次函数 A·px + K:A &gt; 0 给下界,A &lt; 0 给上界。
    /// </summary>
    private static bool RowSpan(in RasterVertex a, in RasterVertex b, in RasterVertex c, float py, int minX, int maxX, out int x0, out int x1)
    {
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        static bool Edge(double slope, double constant, ref double low, ref double high)
        {
            if (slope > 0)
            {
                low = Math.Max(low, -constant / slope);
            }
            else if (slope < 0)
            {
                high = Math.Min(high, -constant / slope);
            }
            return slope != 0 || constant >= 0;   // 水平的边:整行都在外侧时这一行没有像素
        }
        bool any = Edge(b.Y - c.Y, ((c.X - b.X) * (py - b.Y)) + ((c.Y - b.Y) * b.X), ref lo, ref hi)
                   & Edge(c.Y - a.Y, ((a.X - c.X) * (py - c.Y)) + ((a.Y - c.Y) * c.X), ref lo, ref hi)
                   & Edge(a.Y - b.Y, ((b.X - a.X) * (py - a.Y)) + ((b.Y - a.Y) * a.X), ref lo, ref hi);
        if (double.IsNaN(lo) || double.IsNaN(hi))
        {
            (x0, x1) = (minX, maxX);   // 坐标大到算溢出了:整行逐像素判定(与原先一样)
            return true;
        }
        x0 = (int)Math.Clamp(Math.Floor(lo - 0.5) - 1, minX, maxX + 1.0);
        x1 = (int)Math.Clamp(Math.Ceiling(hi - 0.5) + 1, minX - 1.0, maxX);
        return any && x0 <= x1;
    }

    /// <summary>从 p 到 q 的边在逆时针三角形里是上边(水平且向左)或左边(向下)。</summary>
    private static bool IsTopLeft(in RasterVertex p, in RasterVertex q)
    {
        float dx = q.X - p.X, dy = q.Y - p.Y;
        return (dy == 0 && dx < 0) || dy < 0;
    }

    // ------------------------------------------------------------------ 线段与点

    private void RasterLine(RasterVertex a, RasterVertex b, float width)
    {
        if (!PrepareRaster())
        {
            return;
        }
        float dx = b.X - a.X, dy = b.Y - a.Y;
        bool xMajor = MathF.Abs(dx) >= MathF.Abs(dy);
        float length = xMajor ? MathF.Abs(dx) : MathF.Abs(dy);
        if (length == 0 || !float.IsFinite(length))
        {
            return;
        }
        int w = Math.Clamp((int)MathF.Round(width), 1, MaxLineWidth);
        // 沿主轴逐个像素中心取样;起点含、终点不含(菱形出口规则的常见近似)。主轴上只走裁剪范围之内的那一段。
        float start = xMajor ? MathF.Min(a.X, b.X) : MathF.Min(a.Y, b.Y);
        float end = xMajor ? MathF.Max(a.X, b.X) : MathF.Max(a.Y, b.Y);
        int lo = xMajor ? _clipX0 : _clipY0, hi = xMajor ? _clipX1 : _clipY1;
        int i0 = (int)Math.Clamp(MathF.Floor(start + 0.5f), lo, hi), i1 = (int)Math.Clamp(MathF.Floor(end + 0.5f), lo, hi);
        // 线的点画(§3.4.2):片元从起点往终点数,第 s 个片元看图样的第 (s / factor) mod 16 位;这里按主轴从小到大走,
        // 终点在小的那头时倒过来数。计数器在一条折线里接着数(见 _lineStippleCounter)。
        bool stipple = State.Enabled.Has(GlEnum.LINE_STIPPLE);
        bool forward = (xMajor ? dx : dy) > 0;
        int total = stipple ? CountLineFragments(a, xMajor, dx, dy, i0, i1) : 0, produced = 0;
        for (int i = i0; i < i1; i++)
        {
            float center = i + 0.5f;
            float t = ((center - (xMajor ? a.X : a.Y)) / (xMajor ? dx : dy));
            if (t is < 0 or > 1)
            {
                continue;
            }
            if (stipple)
            {
                int counter = _lineStippleCounter + (forward ? produced : total - 1 - produced);
                produced++;
                if (((State.LineStipplePattern >> (counter / State.LineStippleFactor % 16)) & 1) == 0)
                {
                    continue;
                }
            }
            float minor = xMajor ? a.Y + (t * dy) : a.X + (t * dx);
            int m0 = (int)MathF.Floor(minor - ((w - 1) / 2f));
            float z = a.Z + (t * (b.Z - a.Z));
            float ia = (1 - t) * a.InvW, ib = t * b.InvW, s = ia + ib;
            float pa = s != 0 ? ia / s : 1 - t, pb = s != 0 ? ib / s : t;
            Vector4 color = (pa * a.Color) + (pb * b.Color);
            Vector3 spec = (pa * a.Spec) + (pb * b.Spec);
            Vector4 tex = (pa * a.Tex) + (pb * b.Tex);
            float fog = (pa * a.Fog) + (pb * b.Fog);
            for (int k = 0; k < w; k++)
            {
                int x = xMajor ? i : m0 + k, y = xMajor ? m0 + k : i;
                if (x >= _clipX0 && x < _clipX1 && y >= _clipY0 && y < _clipY1)
                {
                    Fragment(x, y, z, color, spec, tex, fog);
                }
            }
        }
        _lineStippleCounter += total;
    }

    /// <summary>
    /// 线的点画计数器 s(§3.4.2):每产生一个片元加一;Begin 时、LINES 的每条线段之前、每个多边形按线框画之前清零,
    /// LINE_STRIP / LINE_LOOP 的各段接着数。
    /// </summary>
    private int _lineStippleCounter;

    /// <summary>这条线段在 [i0, i1) 里产生几个片元(主轴位置的 t 落在 [0, 1] 里的个数)。</summary>
    private static int CountLineFragments(in RasterVertex a, bool xMajor, float dx, float dy, int i0, int i1)
    {
        int count = 0;
        for (int i = i0; i < i1; i++)
        {
            float t = ((i + 0.5f - (xMajor ? a.X : a.Y)) / (xMajor ? dx : dy));
            count += t is >= 0 and <= 1 ? 1 : 0;
        }
        return count;
    }

    private void RasterPoint(RasterVertex p, float size)
    {
        if (!PrepareRaster())
        {
            return;
        }
        int s = Math.Clamp((int)MathF.Round(size), 1, MaxLineWidth);
        int x0 = (int)MathF.Floor(p.X - (s / 2f) + 0.5f), y0 = (int)MathF.Floor(p.Y - (s / 2f) + 0.5f);
        for (int y = Math.Max(y0, _clipY0); y < Math.Min(y0 + s, _clipY1); y++)
        {
            for (int x = Math.Max(x0, _clipX0); x < Math.Min(x0 + s, _clipX1); x++)
            {
                Fragment(x, y, p.Z, p.Color, p.Spec, p.Tex, p.Fog);
            }
        }
    }

    // ------------------------------------------------------------------ 片元

    /// <summary>一个片元(纹理、雾、逐片元测试、混合)按这么多个工作量单位计。</summary>
    private const int FragmentWork = 8;

    /// <summary>一个片元:扣工作量,然后着色、测试、写入(x、y 是 GL 窗口坐标,已在剪裁框内)。</summary>
    private void Fragment(int x, int y, float z, Vector4 color, Vector3 spec, Vector4 tex, float fog)
    {
        WorkBudget.Charge(FragmentWork);
        ShadeFragment(x, y, z, color, spec, tex, fog);
    }

    /// <summary>一个片元:纹理、颜色求和、雾,然后逐片元测试、混合、写入。工作量由调用方扣(三角形按扫描线成段扣)。</summary>
    private void ShadeFragment(int x, int y, float z, Vector4 color, Vector3 spec, Vector4 tex, float fog)
    {
        if (_activeTexture is { } texture)
        {
            color = ApplyTexture(texture, color, tex);
        }
        if (_colorSum)
        {
            color = new Vector4(new Vector3(color.X, color.Y, color.Z) + spec, color.W);
        }
        if (_fog)
        {
            float f = State.FogMode switch
            {
                GlEnum.LINEAR => State.FogEnd == State.FogStart ? 1 : (State.FogEnd - fog) / (State.FogEnd - State.FogStart),
                GlEnum.EXP2 => MathF.Exp(-(State.FogDensity * fog) * (State.FogDensity * fog)),
                _ => MathF.Exp(-State.FogDensity * fog),
            };
            f = Math.Clamp(f, 0, 1);
            color = new Vector4((f * new Vector3(color.X, color.Y, color.Z)) + ((1 - f) * new Vector3(_fogColor.X, _fogColor.Y, _fogColor.Z)), color.W);
        }
        color = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
        WritePixel(x, y, z, color);
    }

    /// <summary>逐片元测试与写入;DrawPixels / Bitmap 的片元也走这里。</summary>
    private void WritePixel(int x, int y, float z, Vector4 color)
    {
        if (_alphaTest && !Compare(State.AlphaFunc, color.W, State.AlphaRef))
        {
            return;
        }
        GlSurface surface = Draw!;
        int index = ((_fbHeight - 1 - y) * _fbWidth) + x;
        if (_stencilTest)
        {
            int masked = State.StencilRef & (int)State.StencilValueMask & 0xFF;
            int stored = surface.Stencil[index] & (int)State.StencilValueMask;
            if (!Compare(State.StencilFunc, masked, stored))
            {
                UpdateStencil(surface, index, State.StencilFail);
                return;
            }
        }
        if (_depthTest)
        {
            if (!Compare(State.DepthFunc, z, surface.Depth[index]))
            {
                if (_stencilTest)
                {
                    UpdateStencil(surface, index, State.StencilDepthFail);
                }
                return;
            }
            if (State.DepthMask)
            {
                surface.Depth[index] = z;
            }
        }
        if (_stencilTest)
        {
            UpdateStencil(surface, index, State.StencilDepthPass);
        }
        if (!_anyColorMask)
        {
            return;
        }
        if (_targetFront is { } front)
        {
            front[index] = Blend(front[index], color);
            // 画过的范围先在这里攒着(几次比较),一条命令执行完再一次记进表面(FlushDrawn)—— 原先每个片元调一次 MarkFrontDirty。
            int row = _fbHeight - 1 - y;
            if (x < _drawnX0)
            {
                _drawnX0 = x;
            }
            if (x >= _drawnX1)
            {
                _drawnX1 = x + 1;
            }
            if (row < _drawnY0)
            {
                _drawnY0 = row;
            }
            if (row >= _drawnY1)
            {
                _drawnY1 = row + 1;
            }
        }
        if (_targetBack is { } back)
        {
            back[index] = Blend(back[index], color);
        }
    }

    // 这条命令里片元写过的范围(缓冲的列、行,第 0 行在最上面);没写过时是空的。
    private int _drawnX0 = int.MaxValue, _drawnY0 = int.MaxValue, _drawnX1, _drawnY1;

    /// <summary>一条命令执行完:片元写过的范围记进表面(写的是前缓冲的话记成要拷出的脏范围)。</summary>
    private void FlushDrawn()
    {
        if (_drawnX1 > _drawnX0 && _targetIsFront && Draw is { } surface)
        {
            surface.MarkFrontDirty(new XRect(_drawnX0, _drawnY0, _drawnX1 - _drawnX0, _drawnY1 - _drawnY0));
        }
        (_drawnX0, _drawnY0, _drawnX1, _drawnY1) = (int.MaxValue, int.MaxValue, 0, 0);
    }

    private static bool Compare(uint func, float a, float b) => func switch
    {
        GlEnum.NEVER => false,
        GlEnum.LESS => a < b,
        GlEnum.EQUAL => a == b,
        GlEnum.LEQUAL => a <= b,
        GlEnum.GREATER => a > b,
        GlEnum.NOTEQUAL => a != b,
        GlEnum.GEQUAL => a >= b,
        _ => true,
    };

    private void UpdateStencil(GlSurface surface, int index, uint op)
    {
        int value = surface.Stencil[index];
        int next = op switch
        {
            GlEnum.ZERO => 0,
            GlEnum.REPLACE => State.StencilRef & 0xFF,
            GlEnum.INCR => Math.Min(value + 1, 255),
            GlEnum.DECR => Math.Max(value - 1, 0),
            GlEnum.INVERT => ~value & 0xFF,
            GlEnum.INCR_WRAP => (value + 1) & 0xFF,
            GlEnum.DECR_WRAP => (value - 1) & 0xFF,
            _ => value,
        };
        uint mask = State.StencilWriteMask & 0xFF;
        surface.Stencil[index] = (byte)((value & ~mask) | (next & mask));
    }

    /// <summary>混合(或逻辑运算)后按颜色掩码合进目标像素。没有 alpha 的表面目标 alpha 视为 1。</summary>
    private uint Blend(uint dstPixel, Vector4 src)
    {
        Vector4 result = src;
        if (_logicOp)
        {
            uint s = Pack(src), d = dstPixel;
            uint r = State.LogicOp switch
            {
                0x1500 => 0,               // CLEAR
                0x1501 => s & d,           // AND
                0x1502 => s & ~d,          // AND_REVERSE
                0x1504 => ~s & d,          // AND_INVERTED
                0x1505 => d,               // NOOP
                0x1506 => s ^ d,           // XOR
                0x1507 => s | d,           // OR
                0x1508 => ~(s | d),        // NOR
                0x1509 => ~(s ^ d),        // EQUIV
                0x150A => ~d,              // INVERT
                0x150B => s | ~d,          // OR_REVERSE
                0x150C => ~s,              // COPY_INVERTED
                0x150D => ~s | d,          // OR_INVERTED
                0x150E => ~(s & d),        // NAND
                0x150F => 0xFFFFFFFF,      // SET
                _ => s,                    // COPY
            };
            result = Unpack(r, hasAlpha: true);
        }
        else if (_blend)
        {
            Vector4 dst = Unpack(dstPixel, _surfaceAlpha);
            Vector4 sf = BlendFactor(State.BlendSrcRgb, State.BlendSrcAlpha, src, dst);
            Vector4 df = BlendFactor(State.BlendDstRgb, State.BlendDstAlpha, src, dst);
            result = State.BlendEquation switch
            {
                GlEnum.FUNC_SUBTRACT => (src * sf) - (dst * df),
                GlEnum.FUNC_REVERSE_SUBTRACT => (dst * df) - (src * sf),
                GlEnum.MIN => Vector4.Min(src, dst),
                GlEnum.MAX => Vector4.Max(src, dst),
                _ => (src * sf) + (dst * df),
            };
            result = Vector4.Clamp(result, Vector4.Zero, Vector4.One);
        }
        uint packed = Pack(result);
        if (_colorKeep == 0)
        {
            return _surfaceAlpha ? packed : packed | 0xFF000000;
        }
        return (dstPixel & _colorKeep) | (packed & ~_colorKeep);
    }

    private Vector4 BlendFactor(uint rgb, uint alpha, Vector4 s, Vector4 d)
    {
        Vector4 c = State.BlendColor;
        Vector4 Factor(uint f) => f switch
        {
            GlEnum.ZERO => Vector4.Zero,
            GlEnum.ONE => Vector4.One,
            GlEnum.SRC_COLOR => s,
            GlEnum.ONE_MINUS_SRC_COLOR => Vector4.One - s,
            GlEnum.DST_COLOR => d,
            GlEnum.ONE_MINUS_DST_COLOR => Vector4.One - d,
            GlEnum.SRC_ALPHA => new Vector4(s.W),
            GlEnum.ONE_MINUS_SRC_ALPHA => new Vector4(1 - s.W),
            GlEnum.DST_ALPHA => new Vector4(d.W),
            GlEnum.ONE_MINUS_DST_ALPHA => new Vector4(1 - d.W),
            GlEnum.CONSTANT_COLOR => c,
            GlEnum.ONE_MINUS_CONSTANT_COLOR => Vector4.One - c,
            GlEnum.CONSTANT_ALPHA => new Vector4(c.W),
            GlEnum.ONE_MINUS_CONSTANT_ALPHA => new Vector4(1 - c.W),
            GlEnum.SRC_ALPHA_SATURATE => new Vector4(new Vector3(MathF.Min(s.W, 1 - d.W)), 1),
            _ => Vector4.One,
        };
        Vector4 fr = Factor(rgb), fa = Factor(alpha);
        return new Vector4(fr.X, fr.Y, fr.Z, fa.W);
    }

    private static Vector4 Unpack(uint p, bool hasAlpha) =>
        new(((p >> 16) & 0xFF) / 255f, ((p >> 8) & 0xFF) / 255f, (p & 0xFF) / 255f, hasAlpha ? (p >> 24) / 255f : 1);

    private static uint Pack(Vector4 c) =>
        ((uint)((c.W * 255) + 0.5f) << 24) | ((uint)((c.X * 255) + 0.5f) << 16) | ((uint)((c.Y * 255) + 0.5f) << 8) | (uint)((c.Z * 255) + 0.5f);

    // ------------------------------------------------------------------ Clear

    private void Clear(uint mask)
    {
        if ((mask & ~(GlEnum.COLOR_BUFFER_BIT | GlEnum.DEPTH_BUFFER_BIT | GlEnum.STENCIL_BUFFER_BIT | GlEnum.ACCUM_BUFFER_BIT)) != 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (InBeginEnd)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        if (Draw is not { } surface || !PrepareRaster())
        {
            return;
        }
        WorkBudget.Charge(2L * (_clipX1 - _clipX0) * (_clipY1 - _clipY0));   // 颜色、深度、模板各扫一遍剪裁框
        uint color = Pack(State.ClearColor);
        if (!surface.HasAlpha)
        {
            color |= 0xFF000000;
        }
        uint keep = _colorKeep;
        float depth = (float)State.ClearDepth;
        uint stencilMask = State.StencilWriteMask & 0xFF;
        byte stencil = (byte)(State.ClearStencil & 0xFF);
        bool full = _clipX0 == 0 && _clipY0 == 0 && _clipX1 == _fbWidth && _clipY1 == _fbHeight;
        foreach (uint[]? buffer in (ReadOnlySpan<uint[]?>)[_targetFront, _targetBack])
        {
            if ((mask & GlEnum.COLOR_BUFFER_BIT) == 0 || buffer is null || !_anyColorMask)
            {
                continue;
            }
            if (full && keep == 0)
            {
                Array.Fill(buffer, color);
            }
            else
            {
                ForEachClearRow((start, count) =>
                {
                    for (int i = start; i < start + count; i++)
                    {
                        buffer[i] = (buffer[i] & keep) | (color & ~keep);
                    }
                });
            }
            if (ReferenceEquals(buffer, surface.Front))
            {
                // 剪裁框(GL 坐标,y 向上)换成行:[H − y1, H − y0)。
                surface.MarkFrontDirty(new XRect(_clipX0, _fbHeight - _clipY1, _clipX1 - _clipX0, _clipY1 - _clipY0));
            }
        }
        if ((mask & GlEnum.DEPTH_BUFFER_BIT) != 0 && State.DepthMask)
        {
            ForEachClearRow((start, count) => surface.Depth.AsSpan(start, count).Fill(depth));
        }
        if ((mask & GlEnum.STENCIL_BUFFER_BIT) != 0 && stencilMask != 0)
        {
            ForEachClearRow((start, count) =>
            {
                for (int i = start; i < start + count; i++)
                {
                    surface.Stencil[i] = (byte)((surface.Stencil[i] & ~stencilMask) | (stencil & stencilMask));
                }
            });
        }
    }

    private void ForEachClearRow(Action<int, int> row)
    {
        for (int y = _clipY0; y < _clipY1; y++)
        {
            row(((_fbHeight - 1 - y) * _fbWidth) + _clipX0, _clipX1 - _clipX0);
        }
    }
}
