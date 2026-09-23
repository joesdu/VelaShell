// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §2「Data types」(PICTFORMINFO / DIRECTFORMAT 的通道移位与掩码;
//   像素值一律是预乘 alpha 的;没有 alpha 通道的格式 alpha 视为 1,只有 alpha 的格式颜色通道视为 0)、
//   §4「Operators」(Porter-Duff 的 Fa / Fb 表、Disjoint 与 Conjoint 两组、Saturate、Add 饱和)、
//   §4「Blend modes」(Multiply … HSLLuminosity,公式引自 PDF Reference 1.7 §7.2.4「Blend Mode」)

namespace VelaShell.XServer.Drawing;

/// <summary>一个预乘 alpha 的颜色,每通道 0–1。</summary>
internal struct Argb(float a, float r, float g, float b)
{
    public float A = a, R = r, G = g, B = b;

    public static Argb Gray(float v) => new(v, v, v, v);
}

/// <summary>RENDER 的一种像素格式(全部是 Direct 类型)。</summary>
internal sealed class PictFormat
{
    private readonly int _alphaShift, _alphaBits, _redShift, _redBits, _greenShift, _greenBits, _blueShift, _blueBits;

    private PictFormat(uint id, byte depth, int alphaShift, int alphaBits, int redShift, int redBits, int greenShift, int greenBits, int blueShift, int blueBits)
    {
        Id = id;
        Depth = depth;
        (_alphaShift, _alphaBits, _redShift, _redBits) = (alphaShift, alphaBits, redShift, redBits);
        (_greenShift, _greenBits, _blueShift, _blueBits) = (greenShift, greenBits, blueShift, blueBits);
    }

    // 格式 ID 是服务端自己的资源 ID,落在任何客户端的 resource-base 之外(同 RANDR 的 CRTC / 输出)。
    public static readonly PictFormat A8R8G8B8 = new(0x50, 32, 24, 8, 16, 8, 8, 8, 0, 8);
    public static readonly PictFormat X8R8G8B8 = new(0x51, 24, 0, 0, 16, 8, 8, 8, 0, 8);
    public static readonly PictFormat R5G6B5 = new(0x52, 16, 0, 0, 11, 5, 5, 6, 0, 5);
    public static readonly PictFormat X1R5G5B5 = new(0x53, 15, 0, 0, 10, 5, 5, 5, 0, 5);
    public static readonly PictFormat A8 = new(0x54, 8, 0, 8, 0, 0, 0, 0, 0, 0);
    public static readonly PictFormat A4 = new(0x55, 4, 0, 4, 0, 0, 0, 0, 0, 0);
    public static readonly PictFormat A1 = new(0x56, 1, 0, 1, 0, 0, 0, 0, 0, 0);

    public static IReadOnlyList<PictFormat> All { get; } = [A8R8G8B8, X8R8G8B8, R5G6B5, X1R5G5B5, A8, A4, A1];

    public static PictFormat? ById(uint id) => id - A8R8G8B8.Id < (uint)All.Count ? All[(int)(id - A8R8G8B8.Id)] : null;   // ID 连续

    public uint Id { get; }

    public byte Depth { get; }

    public bool HasAlpha => _alphaBits > 0;

    public bool HasColor => _redBits > 0;

    public ushort AlphaShift => (ushort)_alphaShift;
    public ushort AlphaMask => Mask(_alphaBits);
    public ushort RedShift => (ushort)_redShift;
    public ushort RedMask => Mask(_redBits);
    public ushort GreenShift => (ushort)_greenShift;
    public ushort GreenMask => Mask(_greenBits);
    public ushort BlueShift => (ushort)_blueShift;
    public ushort BlueMask => Mask(_blueBits);

    private static ushort Mask(int bits) => (ushort)((1 << bits) - 1);

    public Argb Decode(uint raw)
    {
        float a = HasAlpha ? Channel(raw, _alphaShift, _alphaBits) : 1f;
        return HasColor
            ? new Argb(a, Channel(raw, _redShift, _redBits), Channel(raw, _greenShift, _greenBits), Channel(raw, _blueShift, _blueBits))
            : new Argb(a, 0, 0, 0);
    }

    public uint Encode(in Argb c)
    {
        uint v = 0;
        if (HasAlpha)
        {
            v |= Quantize(c.A, _alphaBits) << _alphaShift;
        }
        if (HasColor)
        {
            v |= Quantize(c.R, _redBits) << _redShift;
            v |= Quantize(c.G, _greenBits) << _greenShift;
            v |= Quantize(c.B, _blueBits) << _blueShift;
        }
        return v;
    }

    private static float Channel(uint raw, int shift, int bits)
    {
        uint max = (1u << bits) - 1;
        return ((raw >> shift) & max) / (float)max;
    }

    private static uint Quantize(float v, int bits)
    {
        uint max = (1u << bits) - 1;
        int q = (int)((v * max) + 0.5f);
        return (uint)Math.Clamp(q, 0, (int)max);
    }
}

/// <summary>RENDER 的合成运算:按通道算 result = src × Fa + dst × Fb,或 PDF 混合模式。</summary>
internal static class RenderOps
{
    public const byte Clear = 0, Src = 1, Dst = 2, Over = 3, Add = 12, Saturate = 13;

    public static bool IsValid(byte op) =>
        op <= 13 || op is >= 0x10 and <= 0x1B || op is >= 0x20 and <= 0x2B || op is >= 0x30 and <= 0x3E;

    /// <summary>
    /// 合成一个像素。<paramref name="s" /> 是已经乘过遮罩的源颜色(alpha 通道也在里面);
    /// <paramref name="sa" /> 是每个颜色通道各自的源 alpha(分量 alpha 遮罩时三个通道不同,否则都等于 s.A)。
    /// </summary>
    public static Argb Combine(byte op, in Argb s, in Argb sa, in Argb d)
    {
        if (op >= 0x30)
        {
            return Blend(op, s, sa, d);
        }
        float da = d.A;
        return new Argb(
            Channel(op, s.A, s.A, d.A, da),
            Channel(op, s.R, sa.R, d.R, da),
            Channel(op, s.G, sa.G, d.G, da),
            Channel(op, s.B, sa.B, d.B, da));
    }

    private static float Channel(byte op, float sc, float sa, float dc, float da)
    {
        (float fa, float fb) = Factors(op, sa, da);
        float v = (sc * fa) + (dc * fb);
        return v > 1f ? 1f : v;
    }

    /// <summary>min(1, n / d):n ≥ d(含 0 / 0)时为 1。</summary>
    private static float MinOne(float n, float d) => n >= d ? 1f : n / d;

    /// <summary>max(1 − n / d, 0):n ≥ d(含 0 / 0)时为 0。</summary>
    private static float OneMinus(float n, float d) => n >= d ? 0f : 1f - (n / d);

    private static (float Fa, float Fb) Factors(byte op, float sa, float da)
    {
        if (op < 0x10)
        {
            return op switch
            {
                0 => (0, 0),
                1 => (1, 0),
                2 => (0, 1),
                3 => (1, 1 - sa),
                4 => (1 - da, 1),
                5 => (da, 0),
                6 => (0, sa),
                7 => (1 - da, 0),
                8 => (0, 1 - sa),
                9 => (da, 1 - sa),
                10 => (1 - da, sa),
                11 => (1 - da, 1 - sa),
                12 => (1, 1),
                _ => (MinOne(1 - da, sa), 1),   // Saturate
            };
        }
        if (op < 0x20)
        {
            // Disjoint:假定源与目标的覆盖区域尽量不重叠。
            return (op - 0x10) switch
            {
                0 => (0, 0),
                1 => (1, 0),
                2 => (0, 1),
                3 => (1, MinOne(1 - sa, da)),
                4 => (MinOne(1 - da, sa), 1),
                5 => (OneMinus(1 - da, sa), 0),
                6 => (0, OneMinus(1 - sa, da)),
                7 => (MinOne(1 - da, sa), 0),
                8 => (0, MinOne(1 - sa, da)),
                9 => (OneMinus(1 - da, sa), MinOne(1 - sa, da)),
                10 => (MinOne(1 - da, sa), OneMinus(1 - sa, da)),
                _ => (MinOne(1 - da, sa), MinOne(1 - sa, da)),
            };
        }
        // Conjoint:假定源与目标的覆盖区域尽量重叠。
        return (op - 0x20) switch
        {
            0 => (0, 0),
            1 => (1, 0),
            2 => (0, 1),
            3 => (1, OneMinus(sa, da)),
            4 => (OneMinus(da, sa), 1),
            5 => (MinOne(da, sa), 0),
            6 => (0, MinOne(sa, da)),
            7 => (OneMinus(da, sa), 0),
            8 => (0, OneMinus(sa, da)),
            9 => (MinOne(da, sa), OneMinus(sa, da)),
            10 => (OneMinus(da, sa), MinOne(sa, da)),
            _ => (OneMinus(da, sa), OneMinus(sa, da)),
        };
    }

    // ------------------------------------------------------------------ 混合模式

    /// <summary>result = (1 − αs)·d + (1 − αd)·s + αs·αd·B(cs, cd);alpha = αs + αd − αs·αd。</summary>
    private static Argb Blend(byte op, in Argb s, in Argb sa, in Argb d)
    {
        float da = d.A;
        float alpha = s.A + da - (s.A * da);
        if (op <= 0x3A)
        {
            return new Argb(alpha,
                Separable(op, s.R, sa.R, d.R, da),
                Separable(op, s.G, sa.G, d.G, da),
                Separable(op, s.B, sa.B, d.B, da));
        }

        float a = s.A;
        (float r, float g, float b) cs = a > 0 ? (s.R / a, s.G / a, s.B / a) : (0, 0, 0);
        (float r, float g, float b) cd = da > 0 ? (d.R / da, d.G / da, d.B / da) : (0, 0, 0);
        (float r, float g, float b) mixed = op switch
        {
            0x3B => SetLum(SetSat(cs, Sat(cd)), Lum(cd)),   // HSLHue
            0x3C => SetLum(SetSat(cd, Sat(cs)), Lum(cd)),   // HSLSaturation
            0x3D => SetLum(cs, Lum(cd)),                    // HSLColor
            _ => SetLum(cd, Lum(cs)),                       // HSLLuminosity
        };
        float both = a * da;
        return new Argb(alpha,
            Clamp01(((1 - a) * d.R) + ((1 - da) * s.R) + (both * mixed.r)),
            Clamp01(((1 - a) * d.G) + ((1 - da) * s.G) + (both * mixed.g)),
            Clamp01(((1 - a) * d.B) + ((1 - da) * s.B) + (both * mixed.b)));
    }

    private static float Separable(byte op, float sc, float sa, float dc, float da)
    {
        float cs = sa > 0 ? Math.Min(1f, sc / sa) : 0;
        float cd = da > 0 ? Math.Min(1f, dc / da) : 0;
        float b = op switch
        {
            0x30 => cs * cd,                                // Multiply
            0x31 => cs + cd - (cs * cd),                    // Screen
            0x32 => HardLight(cd, cs),                      // Overlay = HardLight 交换参数
            0x33 => Math.Min(cs, cd),                       // Darken
            0x34 => Math.Max(cs, cd),                       // Lighten
            0x35 => cd <= 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cd / (1 - cs)),        // ColorDodge
            0x36 => cd >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cd) / cs),   // ColorBurn
            0x37 => HardLight(cs, cd),                      // HardLight
            0x38 => SoftLight(cs, cd),                      // SoftLight
            0x39 => Math.Abs(cs - cd),                      // Difference
            _ => cs + cd - (2 * cs * cd),                   // Exclusion
        };
        return Clamp01(((1 - sa) * dc) + ((1 - da) * sc) + (sa * da * b));
    }

    private static float HardLight(float cs, float cd) => cs <= 0.5f ? cd * 2 * cs : cd + (2 * cs) - 1 - (cd * ((2 * cs) - 1));

    private static float SoftLight(float cs, float cd)
    {
        if (cs <= 0.5f)
        {
            return cd - ((1 - (2 * cs)) * cd * (1 - cd));
        }
        float dd = cd <= 0.25f ? ((((16 * cd) - 12) * cd) + 4) * cd : MathF.Sqrt(cd);
        return cd + (((2 * cs) - 1) * (dd - cd));
    }

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static float Lum((float r, float g, float b) c) => (0.3f * c.r) + (0.59f * c.g) + (0.11f * c.b);

    private static float Sat((float r, float g, float b) c) => Math.Max(c.r, Math.Max(c.g, c.b)) - Math.Min(c.r, Math.Min(c.g, c.b));

    private static (float r, float g, float b) ClipColor((float r, float g, float b) c)
    {
        float l = Lum(c);
        float n = Math.Min(c.r, Math.Min(c.g, c.b));
        float x = Math.Max(c.r, Math.Max(c.g, c.b));
        if (n < 0)
        {
            c = (l + ((c.r - l) * l / (l - n)), l + ((c.g - l) * l / (l - n)), l + ((c.b - l) * l / (l - n)));
        }
        if (x > 1)
        {
            c = (l + ((c.r - l) * (1 - l) / (x - l)), l + ((c.g - l) * (1 - l) / (x - l)), l + ((c.b - l) * (1 - l) / (x - l)));
        }
        return c;
    }

    private static (float r, float g, float b) SetLum((float r, float g, float b) c, float l)
    {
        float delta = l - Lum(c);
        return ClipColor((c.r + delta, c.g + delta, c.b + delta));
    }

    private static (float r, float g, float b) SetSat((float r, float g, float b) c, float s)
    {
        float max = Math.Max(c.r, Math.Max(c.g, c.b));
        float min = Math.Min(c.r, Math.Min(c.g, c.b));
        if (max <= min)
        {
            return (0, 0, 0);
        }
        float Scale(float v) => (v - min) * s / (max - min);
        return (Scale(c.r), Scale(c.g), Scale(c.b));
    }
}
