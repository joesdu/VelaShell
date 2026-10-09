// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「OpenFont」「ListFonts」两节(名字不区分大小写,
//   '*' 匹配任意串、'?' 匹配单个字符)
//   X Logical Font Description Conventions(XLFD)—— 14 个字段的字体名;CHARSET_REGISTRY-CHARSET_ENCODING
//   Glyph Bitmap Distribution Format (BDF) Specification, Version 2.1

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VelaShell.XServer.Fonts;

/// <summary>
/// 服务端认识的全部核心字体:随库带的 X.Org 位图字体(misc-fixed 全套、cursor、nil2、Adobe 75 / 100 dpi 的
/// Courier / Helvetica / New Century Schoolbook / Symbol / Times)与 GNU Unifont(见 <c>Fonts/Data/README.md</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 数据照 X.Org 安装后的字体目录分 misc、75dpi、100dpi 三个目录(先后就是字体路径的先后),每个目录一份 mkfontdir 格式的
/// <c>fonts.dir</c>:ISO10646-1 的字体(双字节)另以它完整覆盖的单字节字符集的名字出现(ISO8859-1 就是 Unicode 的前 256 个码位,
/// 其余按 <c>charsets.txt</c> 的映射表派生);别名照 X.Org misc 目录的 <c>fonts.alias</c>(<c>fixed</c>、<c>variable</c>、<c>5x7</c>…),
/// 目标不在随库数据里的别名不列出(打开会 BadName)。XLFD 里要的字号没有时退到最接近的(见 <c>FontIndex.NearestSize</c>)。
/// </para>
/// <para>
/// 名字表在第一次用到时建一次;字体在第一次打开时才解压、解析,解析结果与建好的字体在整个进程里共享(只读,
/// 多个服务端实例各在自己的执行线程上打开同一份字体也只解析一次)。
/// </para>
/// </remarks>
internal static class FontCatalog
{
    /// <summary>字体目录,按字体路径的先后。</summary>
    private static readonly string[] Directories = ["misc", "75dpi", "100dpi"];

    /// <summary>
    /// fonts.alias 之外补的短名字(早先的版本就认它们,不改回 BadName):9x18 / 9x18bold 在 X.Org 的 fonts.alias 里没有,
    /// 数据有;8x16 与 12x24 在 X.Org 里是 Sony 的字体(font-sony-misc),没有随库带,退到高度最接近、粗细相同的内置字体。
    /// </summary>
    private static readonly (string Alias, string Target)[] ExtraAliases =
    [
        ("9x18", "-misc-fixed-medium-r-normal--18-120-100-100-c-90-iso8859-1"),
        ("9x18bold", "-misc-fixed-bold-r-normal--18-120-100-100-c-90-iso8859-1"),
        ("8x16", "-misc-fixed-medium-r-normal--15-140-75-75-c-90-iso8859-1"),
        ("12x24", "-misc-fixed-medium-r-normal--20-200-75-75-c-100-iso8859-1"),
    ];

    private static readonly Lazy<FontIndex> Index = new(FontIndex.Load);

    /// <summary>全部可用名字(字体名与别名,小写,按序)。</summary>
    public static IEnumerable<string> AllNames => Index.Value.SortedNames;

    /// <summary>按模式列名字(ListFonts)。</summary>
    public static List<string> Match(string pattern, int max)
    {
        List<string> result = [];
        foreach (string name in Index.Value.SortedNames)
        {
            if (result.Count >= max)
            {
                break;
            }
            if (WildcardMatch(pattern, name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>按名字打开(OpenFont):名字可以带通配符,取第一个匹配的;找不到返回 null(BadName)。</summary>
    public static XFont? Open(string name) => Index.Value.Open(name);

    /// <summary>X 的字体名通配:'*' 任意串,'?' 单个字符,不区分大小写。</summary>
    internal static bool WildcardMatch(string pattern, string name)
    {
        int p = 0, n = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                n = ++starN;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    /// <summary>XLFD 的最后两个字段 CHARSET_REGISTRY-CHARSET_ENCODING;不是 XLFD 时为空串。</summary>
    private static string CharsetOf(string xlfd)
    {
        int last = xlfd.LastIndexOf('-');
        int second = last > 0 ? xlfd.LastIndexOf('-', last - 1) : -1;
        return xlfd.StartsWith('-') && second > 0 ? xlfd[(second + 1)..] : "";
    }

    /// <summary>一个字体名指向哪份数据:目录、文件、以哪个字符集的身份出现。</summary>
    private readonly record struct FontSource(string Directory, string File, string Charset);

    /// <summary>进程里共享的名字表与已加载的字体(建好之后名字表只读;两份缓存可以并发访问)。</summary>
    private sealed class FontIndex
    {
        private readonly Dictionary<string, FontSource> _fonts = [with(StringComparer.OrdinalIgnoreCase)];
        private readonly Dictionary<string, string> _aliases = [with(StringComparer.OrdinalIgnoreCase)];
        private readonly Dictionary<string, int[]> _charsets = [with(StringComparer.OrdinalIgnoreCase)];
        private readonly ConcurrentDictionary<string, Lazy<BdfFont>> _parsed = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<XFont>> _built = new(StringComparer.Ordinal);

        /// <summary>字体名(不含别名),按序。</summary>
        private string[] _sortedFonts = [];

        public string[] SortedNames { get; private set; } = [];

        public static FontIndex Load()
        {
            FontIndex index = new();
            foreach (string directory in Directories)
            {
                foreach (string line in ReadLines($"{directory}.fonts.dir").Skip(1))
                {
                    int space = line.IndexOf(' ', StringComparison.Ordinal);
                    if (space > 0)
                    {
                        string name = line[(space + 1)..].Trim().ToLowerInvariant();
                        index._fonts.TryAdd(name, new FontSource(directory, line[..space], CharsetOf(name)));   // 前面的目录优先
                    }
                }
            }
            foreach (string line in ReadLines("charsets.txt"))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0x81 && !line.StartsWith('#'))
                {
                    index._charsets[fields[0]] = [.. fields.Skip(1).Select(f => f == "-" ? -1 : int.Parse(f, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
                }
            }
            index._sortedFonts = [.. index._fonts.Keys.Order(StringComparer.Ordinal)];
            foreach ((string alias, string target) in ParseAliases(ReadLines("misc.fonts.alias")))
            {
                if (index.Resolve(target) is not null)
                {
                    index._aliases.TryAdd(alias.ToLowerInvariant(), target);
                }
            }
            foreach ((string alias, string target) in ExtraAliases)
            {
                index._aliases.TryAdd(alias, target);
            }
            index.SortedNames = [.. index._sortedFonts.Concat(index._aliases.Keys.Where(a => !index._fonts.ContainsKey(a))).Order(StringComparer.Ordinal)];
            return index;
        }

        public XFont? Open(string name)
        {
            for (int depth = 0; depth < 4; depth++)   // 别名指向别名:最多跟几层,防成环
            {
                if (_aliases.TryGetValue(name, out string? target))
                {
                    name = target;
                    continue;
                }
                if (Resolve(name) is { } resolved)
                {
                    return Build(resolved);
                }
                // 通配符匹配上的是个别名:接着解它。
                if (SortedNames.FirstOrDefault(n => WildcardMatch(name, n)) is { } alias && _aliases.ContainsKey(alias))
                {
                    name = alias;
                    continue;
                }
                return NearestSize(name) is { } nearest ? Build(nearest) : null;
            }
            return null;
        }

        /// <summary>名字(可带通配符)→ 字体名:完全一致的优先,否则按序第一个匹配上的字体(不看别名)。</summary>
        private string? Resolve(string pattern)
        {
            if (_fonts.ContainsKey(pattern))
            {
                return pattern.ToLowerInvariant();
            }
            if (pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal))
            {
                foreach (string name in _sortedFonts)
                {
                    if (WildcardMatch(pattern, name))
                    {
                        return name;
                    }
                }
            }
            return null;
        }

        /// <summary>就近回退时必须对得上的 XLFD 字段:FOUNDRY、FAMILY_NAME、WEIGHT_NAME、SLANT、CHARSET_REGISTRY、CHARSET_ENCODING。</summary>
        private static readonly int[] NearestSizeFields = [1, 2, 3, 4, 13, 14];

        /// <summary>
        /// 完整的 14 字段 XLFD 要了一个我们没有的字号(<c>-adobe-helvetica-medium-r-normal--13-*-*-*-*-*-iso8859-1</c>):
        /// foundry、family、weight、slant、charset 都对得上的里面,取像素高度最接近的(一样近取小的)。原先直接 BadName。
        /// 尺寸按 PIXEL_SIZE,没给时按 POINT_SIZE 与 RESOLUTION_Y(没给按 75 dpi)换算;两个都没给就不猜(通配本来就该匹配上)。
        /// setwidth、add-style、spacing、平均宽度不看 —— 宁可给一个近似的也别让程序打不开字体。
        /// </summary>
        private string? NearestSize(string pattern)
        {
            string[] want = pattern.ToLowerInvariant().Split('-');
            if (want.Length != 15 || want[0].Length != 0)
            {
                return null;
            }
            int target;
            if (int.TryParse(want[7], NumberStyles.None, CultureInfo.InvariantCulture, out int pixels) && pixels > 0)
            {
                target = pixels;
            }
            else if (int.TryParse(want[8], NumberStyles.None, CultureInfo.InvariantCulture, out int decipoints) && decipoints > 0)
            {
                int dpi = int.TryParse(want[10], NumberStyles.None, CultureInfo.InvariantCulture, out int y) && y > 0 ? y : 75;
                target = (int)Math.Round(decipoints / 10.0 * dpi / 72.27);
            }
            else
            {
                return null;
            }
            string? best = null;
            int bestDistance = int.MaxValue, bestPixels = 0;
            foreach (string name in SortedNames)
            {
                string[] have = name.Split('-');
                if (!_fonts.ContainsKey(name) || have.Length != 15
                    || !int.TryParse(have[7], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
                    || !NearestSizeFields.All(i => WildcardMatch(want[i], have[i])))
                {
                    continue;
                }
                int distance = Math.Abs(size - target);
                if (distance < bestDistance || (distance == bestDistance && size < bestPixels))
                {
                    (best, bestDistance, bestPixels) = (name, distance, size);
                }
            }
            return best;
        }

        private XFont Build(string name)
        {
            FontSource source = _fonts[name];
            return _built.GetOrAdd(name, key => new Lazy<XFont>(() =>
            {
                BdfFont bdf = _parsed.GetOrAdd($"{source.Directory}.{source.File}",
                    resource => new Lazy<BdfFont>(() => BdfParser.Parse(Decompress(resource)))).Value;
                return Derive(bdf, key, source.Charset);
            })).Value;
        }

        /// <summary>
        /// 按名字的字符集建字体:与 BDF 自己的字符集相同(或名字不是 XLFD,如 cursor)时原样;BDF 是 ISO10646-1、名字是单字节字符集时,
        /// 按映射表把那个字符集的每个字节对到 Unicode 的字形(XLFD 的派生名字只为完整覆盖的字符集登记过,见 build-fonts.cs)。
        /// </summary>
        private XFont Derive(BdfFont bdf, string name, string charset)
        {
            if (charset.Length == 0 || charset == bdf.Charset)
            {
                bool twoByte = bdf.Charset == "iso10646-1" || bdf.Glyphs.Keys.Any(k => k > 0xFF);
                Dictionary<int, XGlyph> glyphs = twoByte && bdf.Glyphs.Keys.Any(k => k > 0xFFFF)
                    ? bdf.Glyphs.Where(kv => kv.Key <= 0xFFFF).ToDictionary()   // 核心字体的字符码最多 16 位
                    : bdf.Glyphs;
                return Assemble(bdf, name, twoByte, glyphs, bdf.DefaultChar, null);
            }
            int[]? upper = charset == "iso8859-1" ? null : _charsets.GetValueOrDefault(charset);
            Dictionary<int, XGlyph> derived = [];
            int defaultChar = 0;
            for (int b = 0; b <= 0xFF; b++)
            {
                int code = b < 0x80 || upper is null ? b : upper[b - 0x80];
                if (code >= 0 && bdf.Glyphs.TryGetValue(code, out XGlyph? glyph))
                {
                    derived[b] = glyph;
                    if (code == bdf.DefaultChar)
                    {
                        defaultChar = b;
                    }
                }
            }
            return Assemble(bdf, name, false, derived, defaultChar, charset);
        }

        private static XFont Assemble(BdfFont bdf, string name, bool twoByte, Dictionary<int, XGlyph> glyphs, int defaultChar, string? derivedCharset)
        {
            int minB1 = 0, maxB1 = 0, minC2 = 255, maxC2 = 0;
            if (twoByte && glyphs.Count > 0)
            {
                minB1 = glyphs.Keys.Min(k => k >> 8);
                maxB1 = glyphs.Keys.Max(k => k >> 8);
            }
            foreach (int code in glyphs.Keys)
            {
                minC2 = Math.Min(minC2, code & 0xFF);
                maxC2 = Math.Max(maxC2, code & 0xFF);
            }
            if (glyphs.Count == 0)
            {
                (minC2, maxC2) = (0, 0);
            }

            XCharInfo[] infos = [.. glyphs.Values.Select(g => g.Info)];
            XCharInfo minBounds = infos.Length == 0 ? default : new(
                infos.Min(i => i.LeftBearing), infos.Min(i => i.RightBearing), infos.Min(i => i.Width),
                infos.Min(i => i.Ascent), infos.Min(i => i.Descent));
            XCharInfo maxBounds = infos.Length == 0 ? default : new(
                infos.Max(i => i.LeftBearing), infos.Max(i => i.RightBearing), infos.Max(i => i.Width),
                infos.Max(i => i.Ascent), infos.Max(i => i.Descent));

            // 派生的单字节字体:CHARSET_REGISTRY / CHARSET_ENCODING 换成名字里的那个字符集(如 ISO8859 / 2、KOI8 / R)。
            int dash = derivedCharset?.LastIndexOf('-') ?? -1;
            List<XFontProperty> props = [new("FONT", 0, name)];
            foreach ((string key, BdfProperty property) in bdf.Properties)
            {
                if (key.StartsWith('_') || key is "FONT")
                {
                    continue;
                }
                string value = derivedCharset is null ? property.Value : key switch
                {
                    "CHARSET_REGISTRY" => derivedCharset[..dash].ToUpperInvariant(),
                    "CHARSET_ENCODING" => derivedCharset[(dash + 1)..].ToUpperInvariant(),
                    _ => property.Value,
                };
                props.Add(!property.IsString && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    ? new XFontProperty(key, number)
                    : new XFontProperty(key, 0, value));
            }

            int expected = (maxB1 - minB1 + 1) * (maxC2 - minC2 + 1);
            return new XFont
            {
                Name = name,
                IsTwoByte = twoByte,
                Ascent = (short)bdf.FontAscent,
                Descent = (short)bdf.FontDescent,
                DefaultChar = (ushort)defaultChar,
                Glyphs = glyphs,
                Properties = props,
                MinByte1 = (byte)minB1,
                MaxByte1 = (byte)maxB1,
                MinChar2 = (byte)minC2,
                MaxChar2 = (byte)maxC2,
                MinBounds = minBounds,
                MaxBounds = maxBounds,
                AllCharsExist = glyphs.Count == expected,
            };
        }

        /// <summary>fonts.alias:每行「别名 目标」,任一边可以加引号(名字里有空格时);! 开头的是注释。</summary>
        private static IEnumerable<(string Alias, string Target)> ParseAliases(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('!'))
                {
                    continue;
                }
                List<string> fields = [];
                for (int i = 0; i < line.Length && fields.Count < 2;)
                {
                    if (char.IsWhiteSpace(line[i]))
                    {
                        i++;
                        continue;
                    }
                    int end = line[i] == '"' ? line.IndexOf('"', i + 1) : line.IndexOfAny([' ', '\t'], i);
                    end = end < 0 ? line.Length : end;
                    fields.Add(line[i] == '"' ? line[(i + 1)..end] : line[i..end]);
                    i = end + 1;
                }
                if (fields.Count == 2)
                {
                    yield return (fields[0], fields[1]);
                }
            }
        }

        private static Stream OpenResource(string name) =>
            typeof(FontCatalog).Assembly.GetManifestResourceStream($"VelaShell.XServer.Fonts.{name}")
            ?? throw new InvalidOperationException($"内置字体资源缺失:{name}");

        private static List<string> ReadLines(string name)
        {
            using StreamReader reader = new(OpenResource(name), Encoding.Latin1);
            List<string> lines = [];
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>解压一份字体(数据是 Brotli 压缩的原样 BDF)。</summary>
        private static byte[] Decompress(string resource)
        {
            using Stream compressed = OpenResource(resource);
            using BrotliStream brotli = new(compressed, CompressionMode.Decompress);
            using MemoryStream bdf = new();
            brotli.CopyTo(bdf);
            return bdf.ToArray();
        }
    }
}
