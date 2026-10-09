// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Glyph Bitmap Distribution Format (BDF) Specification, Version 2.1(Adobe,1993)
//   —— STARTFONT / FONT / STARTPROPERTIES / STARTCHAR / ENCODING / DWIDTH / BBX / BITMAP;
//   ENCODING 的第一个数是 −1 时(不在标准编码里),跟着的第二个数是字形号(cursor 字体就这样编号)

using System.Globalization;
using System.Text;

namespace VelaShell.XServer.Fonts;

/// <summary>BDF 的一个属性:带引号的是字符串(QueryFont 里回原子),否则是整数。</summary>
internal readonly record struct BdfProperty(string Value, bool IsString);

/// <summary>一份 BDF 解析出来的原始内容(按 BDF 里的编码)。</summary>
internal sealed class BdfFont
{
    public required string FontName { get; init; }

    public required Dictionary<int, XGlyph> Glyphs { get; init; }

    public required Dictionary<string, BdfProperty> Properties { get; init; }

    public int FontAscent { get; init; }

    public int FontDescent { get; init; }

    public int DefaultChar { get; init; }

    /// <summary>BDF 自己的字符集(<c>CHARSET_REGISTRY-CHARSET_ENCODING</c>,小写;没有这两个属性时为空串)。</summary>
    public string Charset =>
        Properties.TryGetValue("CHARSET_REGISTRY", out BdfProperty registry) && Properties.TryGetValue("CHARSET_ENCODING", out BdfProperty encoding)
            ? $"{registry.Value}-{encoding.Value}".ToLowerInvariant()
            : "";
}

/// <summary>
/// BDF 2.1 解析器 —— 只认本库需要的那几个关键字,其余跳过。直接在解压出来的字节上按行扫,位图的十六进制按原样转成字节
/// (<see cref="XGlyph" /> 与 BITMAP 同一种排法):GNU Unifont 五万多个字形也只要一次线性扫描,不逐行建字符串。
/// </summary>
internal static class BdfParser
{
    public static BdfFont Parse(ReadOnlySpan<byte> data)
    {
        string fontName = "";
        Dictionary<string, BdfProperty> props = [with(StringComparer.OrdinalIgnoreCase)];
        Dictionary<int, XGlyph> glyphs = [];
        bool inProps = false;
        int encoding = -1, dwidth = 0, bbxW = 0, bbxH = 0, bbxX = 0, bbxY = 0;

        int pos = 0;
        while (NextLine(data, ref pos) is { IsEmpty: false } line)
        {
            int space = line.IndexOf((byte)' ');
            ReadOnlySpan<byte> keyword = space < 0 ? line : line[..space];
            ReadOnlySpan<byte> rest = space < 0 ? [] : line[(space + 1)..].Trim((byte)' ');

            if (inProps)
            {
                if (keyword.SequenceEqual("ENDPROPERTIES"u8))
                {
                    inProps = false;
                }
                else if (!keyword.IsEmpty)
                {
                    props[Encoding.Latin1.GetString(keyword)] = PropertyValue(rest);
                }
                continue;
            }

            if (keyword.SequenceEqual("FONT"u8))
            {
                fontName = Encoding.Latin1.GetString(rest);
            }
            else if (keyword.SequenceEqual("STARTPROPERTIES"u8))
            {
                inProps = true;
            }
            else if (keyword.SequenceEqual("STARTCHAR"u8))
            {
                (encoding, dwidth, bbxW, bbxH, bbxX, bbxY) = (-1, 0, 0, 0, 0, 0);
            }
            else if (keyword.SequenceEqual("ENCODING"u8))
            {
                int first = NextInt(ref rest);
                encoding = first >= 0 ? first : rest.IsEmpty ? -1 : NextInt(ref rest);
            }
            else if (keyword.SequenceEqual("DWIDTH"u8))
            {
                dwidth = NextInt(ref rest);
            }
            else if (keyword.SequenceEqual("BBX"u8))
            {
                (bbxW, bbxH, bbxX, bbxY) = (NextInt(ref rest), NextInt(ref rest), NextInt(ref rest), NextInt(ref rest));
            }
            else if (keyword.SequenceEqual("BITMAP"u8))
            {
                XGlyph? glyph = ReadBitmap(data, ref pos, dwidth, bbxW, bbxH, bbxX, bbxY);
                if (encoding >= 0 && glyph is not null)
                {
                    glyphs[encoding] = glyph;
                }
            }
        }

        return new BdfFont
        {
            FontName = fontName,
            Glyphs = glyphs,
            Properties = props,
            FontAscent = IntProperty(props, "FONT_ASCENT"),
            FontDescent = IntProperty(props, "FONT_DESCENT"),
            DefaultChar = IntProperty(props, "DEFAULT_CHAR"),
        };
    }

    /// <summary>BITMAP 之后的 h 行十六进制,到 ENDCHAR 为止。BBX 不合理的字形丢掉(返回 null)。</summary>
    private static XGlyph? ReadBitmap(ReadOnlySpan<byte> data, ref int pos, int dwidth, int w, int h, int xoff, int yoff)
    {
        bool valid = w is >= 0 and <= short.MaxValue && h is >= 0 and <= short.MaxValue;
        int stride = valid ? (w + 7) >> 3 : 0;
        byte[] bits = valid ? new byte[stride * h] : [];
        for (int row = 0; ; row++)
        {
            ReadOnlySpan<byte> line = NextLine(data, ref pos);
            if (line.IsEmpty && pos >= data.Length)
            {
                break;   // 文件截断:到此为止
            }
            if (line.SequenceEqual("ENDCHAR"u8))
            {
                break;
            }
            if (valid && row < h)
            {
                Span<byte> target = bits.AsSpan(row * stride, stride);
                for (int i = 0; i < stride && (2 * i) + 1 < line.Length; i++)
                {
                    target[i] = (byte)((Nibble(line[2 * i]) << 4) | Nibble(line[(2 * i) + 1]));
                }
            }
        }
        if (!valid)
        {
            return null;
        }
        // BBX:位图宽高与原点偏移(y 向上)。换成 CHARINFO:
        // 左边距 = xoff,右边距 = xoff + w,ascent = yoff + h,descent = −yoff。
        // 最后一个字节里超出宽度的那几位清零:IsSet 只看宽度以内,ImageText 之类按行取也就不会带出垃圾。
        if ((w & 7) != 0)
        {
            byte keep = (byte)(0xFF << (8 - (w & 7)));
            for (int row = 0; row < h; row++)
            {
                bits[(row * stride) + stride - 1] &= keep;
            }
        }
        XCharInfo info = new((short)xoff, (short)(xoff + w), (short)dwidth, (short)(yoff + h), (short)(-yoff));
        return new XGlyph(info, bits);
    }

    /// <summary>下一行(去掉行尾的 \r 与空白);到文件末尾返回空。空行也返回空,调用方靠 pos 区分。</summary>
    private static ReadOnlySpan<byte> NextLine(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            int newline = data[pos..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = newline < 0 ? data[pos..] : data.Slice(pos, newline);
            pos = newline < 0 ? data.Length : pos + newline + 1;
            line = line.TrimEnd(" \t\r"u8);
            if (!line.IsEmpty)
            {
                return line;
            }
        }
        return [];
    }

    private static int Nibble(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => 0,
    };

    /// <summary>取下一个整数并把它从 <paramref name="text" /> 里拿掉;没有或不是整数为 0。</summary>
    private static int NextInt(ref ReadOnlySpan<byte> text)
    {
        text = text.TrimStart((byte)' ');
        int space = text.IndexOf((byte)' ');
        ReadOnlySpan<byte> token = space < 0 ? text : text[..space];
        text = space < 0 ? [] : text[(space + 1)..];
        return int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    /// <summary>
    /// 属性值:带引号的是字符串(两个连写的引号表示一个引号),否则是整数。看上去是数字的字符串(<c>CHARSET_ENCODING "1"</c>)
    /// 照旧是字符串 —— XLFD 的这些字段在 QueryFont 里是原子;原先一律按长得像不像数字判断,回成了整数。
    /// </summary>
    private static BdfProperty PropertyValue(ReadOnlySpan<byte> value)
    {
        string text = Encoding.Latin1.GetString(value);
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? new BdfProperty(text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal), true)
            : new BdfProperty(text, false);
    }

    private static int IntProperty(Dictionary<string, BdfProperty> props, string name) =>
        props.TryGetValue(name, out BdfProperty property) && int.TryParse(property.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
}
