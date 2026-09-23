// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Glyph Bitmap Distribution Format (BDF) Specification, Version 2.1(Adobe,1993)
//   —— STARTFONT / FONT / STARTPROPERTIES / STARTCHAR / ENCODING / DWIDTH / BBX / BITMAP

using System.Globalization;

namespace VelaShell.XServer.Fonts;

/// <summary>一份 BDF 解析出来的原始内容(按 Unicode 码位)。</summary>
internal sealed class BdfFont
{
    public required string FontName { get; init; }

    public required Dictionary<int, XGlyph> Glyphs { get; init; }

    public required Dictionary<string, string> Properties { get; init; }

    public int FontAscent { get; init; }

    public int FontDescent { get; init; }

    public int DefaultChar { get; init; }
}

/// <summary>BDF 2.1 解析器 —— 只认本库需要的那几个关键字,其余跳过。</summary>
internal static class BdfParser
{
    public static BdfFont Parse(TextReader reader)
    {
        string fontName = "";
        Dictionary<string, string> props = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, XGlyph> glyphs = [];
        bool inProps = false;

        int encoding = -1, dwidth = 0, bbxW = 0, bbxH = 0, bbxX = 0, bbxY = 0;
        List<string>? bitmapRows = null;

        while (reader.ReadLine() is { } raw)
        {
            string line = raw.TrimEnd();
            if (bitmapRows is not null)
            {
                if (line == "ENDCHAR")
                {
                    if (encoding >= 0)
                    {
                        glyphs[encoding] = BuildGlyph(dwidth, bbxW, bbxH, bbxX, bbxY, bitmapRows);
                    }
                    bitmapRows = null;
                }
                else
                {
                    bitmapRows.Add(line);
                }
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string keyword = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[(space + 1)..].Trim();

            if (inProps)
            {
                if (keyword == "ENDPROPERTIES")
                {
                    inProps = false;
                }
                else if (keyword.Length > 0)
                {
                    props[keyword] = rest.Trim('"');
                }
                continue;
            }

            switch (keyword)
            {
                case "FONT":
                    fontName = rest;
                    break;
                case "STARTPROPERTIES":
                    inProps = true;
                    break;
                case "STARTCHAR":
                    encoding = -1;
                    dwidth = 0;
                    break;
                case "ENCODING":
                    encoding = ParseInts(rest)[0];
                    break;
                case "DWIDTH":
                    dwidth = ParseInts(rest)[0];
                    break;
                case "BBX":
                    int[] bbx = ParseInts(rest);
                    (bbxW, bbxH, bbxX, bbxY) = (bbx[0], bbx[1], bbx[2], bbx[3]);
                    break;
                case "BITMAP":
                    bitmapRows = [];
                    break;
            }
        }

        return new BdfFont
        {
            FontName = fontName,
            Glyphs = glyphs,
            Properties = props,
            FontAscent = props.TryGetValue("FONT_ASCENT", out string? a) ? int.Parse(a, CultureInfo.InvariantCulture) : 0,
            FontDescent = props.TryGetValue("FONT_DESCENT", out string? d) ? int.Parse(d, CultureInfo.InvariantCulture) : 0,
            DefaultChar = props.TryGetValue("DEFAULT_CHAR", out string? dc) ? int.Parse(dc, CultureInfo.InvariantCulture) : 0,
        };
    }

    /// <summary>只读出 FONT 那一行(列字体名时用,不必解析几百个字形)。</summary>
    public static string ReadFontName(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("FONT ", StringComparison.Ordinal))
            {
                return line[5..].Trim();
            }
            if (line.StartsWith("STARTCHAR", StringComparison.Ordinal))
            {
                break;
            }
        }
        return "";
    }

    private static XGlyph BuildGlyph(int dwidth, int w, int h, int xoff, int yoff, List<string> rows)
    {
        // BBX:位图宽高与原点偏移(y 向上)。换成 CHARINFO:
        // 左边距 = xoff,右边距 = xoff + w,ascent = yoff + h,descent = −yoff。
        XCharInfo info = new(
            (short)xoff, (short)(xoff + w), (short)dwidth, (short)(yoff + h), (short)(-yoff));
        byte[] bits = new byte[Math.Max(0, w * h)];
        for (int y = 0; y < h && y < rows.Count; y++)
        {
            string hex = rows[y];
            for (int x = 0; x < w; x++)
            {
                int nibbleIndex = x / 4;
                if (nibbleIndex >= hex.Length)
                {
                    break;
                }
                int nibble = Convert.ToInt32(hex[nibbleIndex].ToString(), 16);
                if ((nibble & (8 >> (x % 4))) != 0)
                {
                    bits[(y * w) + x] = 1;
                }
            }
        }
        return new XGlyph(info, bits);
    }

    private static int[] ParseInts(string text) =>
        [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
}
