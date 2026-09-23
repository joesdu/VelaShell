// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「QueryFont」一节(CHARINFO、min/max-bounds、
//   单字节 / 双字节字体的字符索引、default-char、字体属性)
//   Glyph Bitmap Distribution Format (BDF) Specification, Version 2.1(Adobe)

namespace VelaShell.XServer.Fonts;

/// <summary>一个字符的度量(协议里的 CHARINFO)。</summary>
internal readonly record struct XCharInfo(
    short LeftBearing, short RightBearing, short Width, short Ascent, short Descent, ushort Attributes = 0)
{
    public bool IsEmpty => LeftBearing == 0 && RightBearing == 0 && Width == 0 && Ascent == 0 && Descent == 0;
}

/// <summary>一个字形:度量 + 1 位位图(行优先,每像素一个字节,1 = 着色)。</summary>
internal sealed class XGlyph(XCharInfo info, byte[] bits)
{
    public XCharInfo Info { get; } = info;

    /// <summary>位图宽 = 右边距 − 左边距。</summary>
    public int BitmapWidth => Info.RightBearing - Info.LeftBearing;

    /// <summary>位图高 = ascent + descent。</summary>
    public int BitmapHeight => Info.Ascent + Info.Descent;

    public byte[] Bits { get; } = bits;

    public bool IsSet(int x, int y) => Bits[(y * BitmapWidth) + x] != 0;
}

/// <summary>字体属性(QueryFont 回复里的 FONTPROP):值要么是整数,要么是字符串(回复时换成原子)。</summary>
internal readonly record struct XFontProperty(string Name, int Value, string? StringValue = null);

/// <summary>一份已加载的字体。同一份数据可以被多个客户端多次打开。</summary>
internal sealed class XFont
{
    public required string Name { get; init; }

    /// <summary>双字节字体(ISO10646-1 等):字符码 = byte1 &lt;&lt; 8 | byte2。</summary>
    public bool IsTwoByte { get; init; }

    public short Ascent { get; init; }

    public short Descent { get; init; }

    /// <summary>缺字时用的字符码。</summary>
    public ushort DefaultChar { get; init; }

    public required IReadOnlyDictionary<int, XGlyph> Glyphs { get; init; }

    public IReadOnlyList<XFontProperty> Properties { get; init; } = [];

    /// <summary>单字节字体:最小 / 最大字符码。双字节:byte2 的范围。</summary>
    public byte MinChar2 { get; init; }

    public byte MaxChar2 { get; init; }

    /// <summary>双字节字体的 byte1 范围;单字节为 0。</summary>
    public byte MinByte1 { get; init; }

    public byte MaxByte1 { get; init; }

    public XCharInfo MinBounds { get; init; }

    public XCharInfo MaxBounds { get; init; }

    public bool AllCharsExist { get; init; }

    /// <summary>取一个字符的字形;缺字时退回 default-char;都没有返回 null(按协议,不画也不前进)。</summary>
    public XGlyph? Lookup(int code)
    {
        if (Glyphs.TryGetValue(code, out XGlyph? glyph))
        {
            return glyph;
        }
        return Glyphs.TryGetValue(DefaultChar, out XGlyph? fallback) ? fallback : null;
    }

    /// <summary>QueryFont 回复里 char-infos 数组覆盖的全部字符码,按协议顺序(byte1 外层、byte2 内层)。</summary>
    public IEnumerable<int> CharInfoRange()
    {
        for (int b1 = MinByte1; b1 <= MaxByte1; b1++)
        {
            for (int b2 = MinChar2; b2 <= MaxChar2; b2++)
            {
                yield return IsTwoByte ? (b1 << 8) | b2 : b2;
            }
        }
    }

    /// <summary>字符码对应的 CHARINFO;不存在的字符全零(协议规定)。</summary>
    public XCharInfo InfoOf(int code) => Glyphs.TryGetValue(code, out XGlyph? g) ? g.Info : default;

    /// <summary>一串字符的总宽度与墨迹范围(QueryTextExtents / ImageText 用)。</summary>
    public (int Width, int Left, int Right, int Ascent, int Descent) Measure(ReadOnlySpan<int> codes)
    {
        int width = 0, left = 0, right = 0, ascent = 0, descent = 0;
        bool first = true;
        foreach (int code in codes)
        {
            if (Lookup(code) is not { } glyph)
            {
                continue;
            }
            XCharInfo ci = glyph.Info;
            if (first)
            {
                left = ci.LeftBearing;
                ascent = ci.Ascent;
                descent = ci.Descent;
                first = false;
            }
            else
            {
                left = Math.Min(left, width + ci.LeftBearing);
                ascent = Math.Max(ascent, ci.Ascent);
                descent = Math.Max(descent, ci.Descent);
            }
            right = Math.Max(right, width + ci.RightBearing);
            width += ci.Width;
        }
        return (width, left, right, ascent, descent);
    }
}
