// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「AllocNamedColor」「LookupColor」(名字不区分大小写;
//   颜色数据库由服务端提供)
//   Xlib - C Language X Interface 第 6.4 节「Color Strings」(#RGB 系列与 rgb:r/g/b 的写法)
//   颜色值:X11 颜色名称表的常用子集(这些名字与数值是事实上的约定;与 CSS 冲突处取 X11 的值:
//   gray = 190、green = 0,255,0、maroon = 176,48,96、purple = 160,32,240)

using System.Globalization;

namespace VelaShell.XServer.Resources;

/// <summary>颜色名 → 16 位 RGB。</summary>
internal static class ColorNames
{
    private static readonly Dictionary<string, (byte R, byte G, byte B)> Table = Build();

    /// <summary>解析颜色名或数值写法;认不出来返回 null(BadName)。</summary>
    public static (ushort R, ushort G, ushort B)? Lookup(string spec)
    {
        string s = spec.Trim();
        if (s.StartsWith('#'))
        {
            return ParseHash(s[1..]);
        }
        if (s.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRgb(s[4..]);
        }
        string key = s.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (Table.TryGetValue(key, out var rgb))
        {
            return (Expand(rgb.R), Expand(rgb.G), Expand(rgb.B));
        }
        // grayN / greyN:N 从 0 到 100,按百分比取灰度。
        foreach (string prefix in (string[])["gray", "grey"])
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && n is >= 0 and <= 100)
            {
                byte v = (byte)Math.Round(n * 255 / 100.0);
                return (Expand(v), Expand(v), Expand(v));
            }
        }
        return null;
    }

    private static ushort Expand(byte v) => (ushort)((v << 8) | v);

    /// <summary>#RGB / #RRGGBB / #RRRGGGBBB / #RRRRGGGGBBBB:每个分量取高位。</summary>
    private static (ushort, ushort, ushort)? ParseHash(string hex)
    {
        if (hex.Length is not (3 or 6 or 9 or 12) || !hex.All(Uri.IsHexDigit))
        {
            return null;
        }
        int n = hex.Length / 3;
        ushort Part(int i) => (ushort)(Convert.ToUInt32(hex.Substring(i * n, n), 16) << (16 - (4 * n)));
        return (Part(0), Part(1), Part(2));
    }

    /// <summary>rgb:r/g/b,每个分量 1–4 位十六进制,按位数缩放到 16 位。</summary>
    private static (ushort, ushort, ushort)? ParseRgb(string text)
    {
        string[] parts = text.Split('/');
        if (parts.Length != 3 || parts.Any(p => p.Length is < 1 or > 4 || !p.All(Uri.IsHexDigit)))
        {
            return null;
        }
        static ushort Scale(string p)
        {
            uint max = (1u << (4 * p.Length)) - 1;
            return (ushort)(Convert.ToUInt32(p, 16) * 0xFFFF / max);
        }
        return (Scale(parts[0]), Scale(parts[1]), Scale(parts[2]));
    }

    private static Dictionary<string, (byte, byte, byte)> Build()
    {
        (string Name, byte R, byte G, byte B)[] entries =
        [
            ("black", 0, 0, 0), ("white", 255, 255, 255), ("red", 255, 0, 0), ("green", 0, 255, 0),
            ("blue", 0, 0, 255), ("yellow", 255, 255, 0), ("cyan", 0, 255, 255), ("magenta", 255, 0, 255),
            ("gray", 190, 190, 190), ("grey", 190, 190, 190), ("darkgray", 169, 169, 169), ("darkgrey", 169, 169, 169),
            ("lightgray", 211, 211, 211), ("lightgrey", 211, 211, 211), ("dimgray", 105, 105, 105), ("dimgrey", 105, 105, 105),
            ("slategray", 112, 128, 144), ("slategrey", 112, 128, 144), ("lightslategray", 119, 136, 153),
            ("lightslategrey", 119, 136, 153), ("darkslategray", 47, 79, 79), ("darkslategrey", 47, 79, 79),
            ("gainsboro", 220, 220, 220), ("whitesmoke", 245, 245, 245), ("snow", 255, 250, 250), ("ghostwhite", 248, 248, 255),
            ("floralwhite", 255, 250, 240), ("oldlace", 253, 245, 230), ("linen", 250, 240, 230), ("antiquewhite", 250, 235, 215),
            ("papayawhip", 255, 239, 213), ("blanchedalmond", 255, 235, 205), ("bisque", 255, 228, 196),
            ("peachpuff", 255, 218, 185), ("navajowhite", 255, 222, 173), ("moccasin", 255, 228, 181),
            ("cornsilk", 255, 248, 220), ("ivory", 255, 255, 240), ("lemonchiffon", 255, 250, 205), ("seashell", 255, 245, 238),
            ("honeydew", 240, 255, 240), ("mintcream", 245, 255, 250), ("azure", 240, 255, 255), ("aliceblue", 240, 248, 255),
            ("lavender", 230, 230, 250), ("lavenderblush", 255, 240, 245), ("mistyrose", 255, 228, 225),
            ("navy", 0, 0, 128), ("navyblue", 0, 0, 128), ("midnightblue", 25, 25, 112), ("cornflowerblue", 100, 149, 237),
            ("darkslateblue", 72, 61, 139), ("slateblue", 106, 90, 205), ("mediumslateblue", 123, 104, 238),
            ("mediumblue", 0, 0, 205), ("royalblue", 65, 105, 225), ("dodgerblue", 30, 144, 255), ("deepskyblue", 0, 191, 255),
            ("skyblue", 135, 206, 235), ("lightskyblue", 135, 206, 250), ("steelblue", 70, 130, 180),
            ("lightsteelblue", 176, 196, 222), ("lightblue", 173, 216, 230), ("powderblue", 176, 224, 230),
            ("paleturquoise", 175, 238, 238), ("darkturquoise", 0, 206, 209), ("mediumturquoise", 72, 209, 204),
            ("turquoise", 64, 224, 208), ("lightcyan", 224, 255, 255), ("cadetblue", 95, 158, 160),
            ("mediumaquamarine", 102, 205, 170), ("aquamarine", 127, 255, 212), ("darkgreen", 0, 100, 0),
            ("darkolivegreen", 85, 107, 47), ("darkseagreen", 143, 188, 143), ("seagreen", 46, 139, 87),
            ("mediumseagreen", 60, 179, 113), ("lightseagreen", 32, 178, 170), ("palegreen", 152, 251, 152),
            ("springgreen", 0, 255, 127), ("lawngreen", 124, 252, 0), ("chartreuse", 127, 255, 0),
            ("mediumspringgreen", 0, 250, 154), ("greenyellow", 173, 255, 47), ("limegreen", 50, 205, 50),
            ("yellowgreen", 154, 205, 50), ("forestgreen", 34, 139, 34), ("olivedrab", 107, 142, 35),
            ("darkkhaki", 189, 183, 107), ("khaki", 240, 230, 140), ("palegoldenrod", 238, 232, 170),
            ("lightgoldenrodyellow", 250, 250, 210), ("lightyellow", 255, 255, 224), ("gold", 255, 215, 0),
            ("lightgoldenrod", 238, 221, 130), ("goldenrod", 218, 165, 32), ("darkgoldenrod", 184, 134, 11),
            ("rosybrown", 188, 143, 143), ("indianred", 205, 92, 92), ("saddlebrown", 139, 69, 19), ("sienna", 160, 82, 45),
            ("peru", 205, 133, 63), ("burlywood", 222, 184, 135), ("beige", 245, 245, 220), ("wheat", 245, 222, 179),
            ("sandybrown", 244, 164, 96), ("tan", 210, 180, 140), ("chocolate", 210, 105, 30), ("firebrick", 178, 34, 34),
            ("brown", 165, 42, 42), ("darksalmon", 233, 150, 122), ("salmon", 250, 128, 114), ("lightsalmon", 255, 160, 122),
            ("orange", 255, 165, 0), ("darkorange", 255, 140, 0), ("coral", 255, 127, 80), ("lightcoral", 240, 128, 128),
            ("tomato", 255, 99, 71), ("orangered", 255, 69, 0), ("hotpink", 255, 105, 180), ("deeppink", 255, 20, 147),
            ("pink", 255, 192, 203), ("lightpink", 255, 182, 193), ("palevioletred", 219, 112, 147), ("maroon", 176, 48, 96),
            ("mediumvioletred", 199, 21, 133), ("violetred", 208, 32, 144), ("violet", 238, 130, 238), ("plum", 221, 160, 221),
            ("orchid", 218, 112, 214), ("mediumorchid", 186, 85, 211), ("darkorchid", 153, 50, 204),
            ("darkviolet", 148, 0, 211), ("blueviolet", 138, 43, 226), ("purple", 160, 32, 240),
            ("mediumpurple", 147, 112, 219), ("thistle", 216, 191, 216), ("darkblue", 0, 0, 139), ("darkcyan", 0, 139, 139),
            ("darkmagenta", 139, 0, 139), ("darkred", 139, 0, 0), ("lightgreen", 144, 238, 144),
        ];
        Dictionary<string, (byte, byte, byte)> table = new(StringComparer.Ordinal);
        foreach ((string name, byte r, byte g, byte b) in entries)
        {
            table[name] = (r, g, b);
        }
        return table;
    }
}
