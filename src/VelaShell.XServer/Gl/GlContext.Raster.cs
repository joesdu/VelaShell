// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 ——
//   §3.3「Points」(非反走样的点:以 (x_w, y_w) 为中心、边长为取整后点大小的正方形覆盖的像素中心)、
//   §3.4「Line Segments」(非反走样线段的「菱形出口」规则;宽线沿次轴方向加宽)、
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
    private bool _depthTest, _stencilTest, _alphaTest, _blend, _logicOp, _fog, _colorSum, _anyColorMask, _fullColorMask;
    private GlTexture? _activeTexture;
    private Vector4 _fogColor;

    /// <summary>取当前状态,准备写片元;没有表面或没有可写的颜色缓冲时返回 false。</summary>
    private bool PrepareRaster()
    {
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
        if (State.Enabled.Contains(GlEnum.SCISSOR_TEST))
        {
            _clipX0 = Math.Max(_clipX0, State.ScissorX);
            _clipY0 = Math.Max(_clipY0, State.ScissorY);
            _clipX1 = Math.Min(_clipX1, State.ScissorX + State.ScissorWidth);
            _clipY1 = Math.Min(_clipY1, State.ScissorY + State.ScissorHeight);
        }
        _depthTest = State.Enabled.Contains(GlEnum.DEPTH_TEST);
        _stencilTest = State.Enabled.Contains(GlEnum.STENCIL_TEST);
        _alphaTest = State.Enabled.Contains(GlEnum.ALPHA_TEST);
        _logicOp = State.Enabled.Contains(GlEnum.COLOR_LOGIC_OP);
        _blend = !_logicOp && State.Enabled.Contains(GlEnum.BLEND);
        _fog = State.Enabled.Contains(GlEnum.FOG);
        _fogColor = State.FogColor;
        _colorSum = State.Enabled.Contains(GlEnum.LIGHTING) && State.LightModelColorControl == GlEnum.SEPARATE_SPECULAR_COLOR;
        _anyColorMask = State.ColorMask[0] || State.ColorMask[1] || State.ColorMask[2] || State.ColorMask[3];
        _fullColorMask = State.ColorMask[0] && State.ColorMask[1] && State.ColorMask[2] && State.ColorMask[3];
        _activeTexture = CompleteTexture();
        return _clipX0 < _clipX1 && _clipY0 < _clipY1;
    }

    /// <summary>当前生效的纹理:2D 优先于 1D,不完整的视为未启用(§3.8.10、§3.8.15)。</summary>
    private GlTexture? CompleteTexture()
    {
        foreach ((uint cap, uint bound) in new[] { (GlEnum.TEXTURE_2D, State.Texture2D), (GlEnum.TEXTURE_1D, State.Texture1D) })
        {
            if (!State.Enabled.Contains(cap))
            {
                continue;
            }
            GlTexture? t = bound == 0 ? DefaultTexture(cap) : Shared.Textures.GetValueOrDefault(bound);
            return t is not null && t.IsComplete ? t : null;
        }
        return null;
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
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((c.X - b.X) * (py - b.Y)) - ((c.Y - b.Y) * (px - b.X));
                float w1 = ((a.X - c.X) * (py - c.Y)) - ((a.Y - c.Y) * (px - c.X));
                float w2 = ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
                if (w0 < 0 || w1 < 0 || w2 < 0
                    || (w0 == 0 && !topLeft0) || (w1 == 0 && !topLeft1) || (w2 == 0 && !topLeft2))
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
                Fragment(x, y, z,
                    (p0 * a.Color) + (p1 * b.Color) + (p2 * c.Color),
                    (p0 * a.Spec) + (p1 * b.Spec) + (p2 * c.Spec),
                    (p0 * a.Tex) + (p1 * b.Tex) + (p2 * c.Tex),
                    (p0 * a.Fog) + (p1 * b.Fog) + (p2 * c.Fog));
            }
        }
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
        int w = Math.Max(1, (int)MathF.Round(width));
        // 沿主轴逐个像素中心取样;起点含、终点不含(菱形出口规则的常见近似)。
        float start = xMajor ? MathF.Min(a.X, b.X) : MathF.Min(a.Y, b.Y);
        float end = xMajor ? MathF.Max(a.X, b.X) : MathF.Max(a.Y, b.Y);
        int i0 = (int)MathF.Floor(start + 0.5f), i1 = (int)MathF.Floor(end + 0.5f);
        for (int i = i0; i < i1; i++)
        {
            float center = i + 0.5f;
            float t = ((center - (xMajor ? a.X : a.Y)) / (xMajor ? dx : dy));
            if (t is < 0 or > 1)
            {
                continue;
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
    }

    private void RasterPoint(RasterVertex p, float size)
    {
        if (!PrepareRaster())
        {
            return;
        }
        int s = Math.Max(1, (int)MathF.Round(size));
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

    /// <summary>一个片元:纹理、颜色求和、雾,然后逐片元测试、混合、写入(x、y 是 GL 窗口坐标,已在剪裁框内)。</summary>
    private void Fragment(int x, int y, float z, Vector4 color, Vector3 spec, Vector4 tex, float fog)
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
            if (ReferenceEquals(front, surface.Front))
            {
                surface.FrontDirty = true;
            }
        }
        if (_targetBack is { } back)
        {
            back[index] = Blend(back[index], color);
        }
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
        Vector4 dst = Unpack(dstPixel, Draw!.HasAlpha);
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
        if (_fullColorMask)
        {
            return Draw.HasAlpha ? packed : packed | 0xFF000000;
        }
        uint keep = (State.ColorMask[0] ? 0 : 0x00FF0000u) | (State.ColorMask[1] ? 0 : 0x0000FF00u)
                    | (State.ColorMask[2] ? 0 : 0x000000FFu) | (State.ColorMask[3] ? 0 : 0xFF000000u);
        return (dstPixel & keep) | (packed & ~keep);
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
        uint color = Pack(State.ClearColor);
        if (!surface.HasAlpha)
        {
            color |= 0xFF000000;
        }
        uint keep = (State.ColorMask[0] ? 0 : 0x00FF0000u) | (State.ColorMask[1] ? 0 : 0x0000FF00u)
                    | (State.ColorMask[2] ? 0 : 0x000000FFu) | (State.ColorMask[3] ? 0 : 0xFF000000u);
        float depth = (float)State.ClearDepth;
        uint stencilMask = State.StencilWriteMask & 0xFF;
        byte stencil = (byte)(State.ClearStencil & 0xFF);
        bool full = _clipX0 == 0 && _clipY0 == 0 && _clipX1 == _fbWidth && _clipY1 == _fbHeight;
        foreach (uint[]? buffer in new[] { _targetFront, _targetBack })
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
                surface.FrontDirty = true;
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
