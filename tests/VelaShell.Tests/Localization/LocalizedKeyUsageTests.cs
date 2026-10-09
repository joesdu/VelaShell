using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;

namespace VelaShell.Tests.Localization;

/// <summary>
/// 界面引用的每个本地化键都必须在资源里真实存在。
/// </summary>
/// <remarks>
/// 这是缺键唯一的兜底:Strings.cs 取不到键时回退成键名本身(GetString(key) ?? key),
/// 既不抛也不记日志 —— 界面在所有语言下都直接显示英文键名。用户报的 SFTP 面板关闭按钮
/// 到处显示 "Close" 就是这么来的:{loc:Localize Close} 写了,resx 里却从没加过 Close。
///
/// 已有的 LocalizationTests.AllCultures_HaveIdenticalKeySets 管不到这一类:它比的是五种语言
/// 之间键集是否一致,而 Close 是五个文件里都没有 —— 平价成立,照样漏。那条测的是「翻译齐不齐」,
/// 这条测的是「引用的键存不存在」。
/// </remarks>
[TestClass]
[TestCategory("i18n")]
public partial class LocalizedKeyUsageTests
{
    /// <summary>XAML 里的位置参数写法 {loc:Localize SomeKey}(LocalizeExtension 的唯一用法)。</summary>
    [GeneratedRegex(@"\{loc:Localize\s+([A-Za-z0-9_]+)\s*\}")]
    private static partial Regex XamlKey { get; }

    /// <summary>代码里的字面量取词 Strings.Get("SomeKey");变量传参匹配不到,也不该匹配。</summary>
    [GeneratedRegex(@"Strings\.Get\(""([A-Za-z0-9_]+)""\)")]
    private static partial Regex CodeKey { get; }

    /// <summary>XAML 里用 x:Static 直接取 Strings 的静态属性,如 {x:Static res:Strings.Upload}。</summary>
    [GeneratedRegex(@"\{x:Static\s+\w+:Strings\.\w+\s*\}")]
    private static partial Regex XamlStaticString { get; }

    [TestMethod]
    public void EveryLocalizeKeyUsedInXaml_ExistsInResources() => AssertAllKeysDefined("*.axaml", XamlKey, minimumExpected: 100);

    /// <summary>
    /// XAML 取词一律走 {loc:Localize},不许用 x:Static。
    /// </summary>
    /// <remarks>
    /// x:Static 在视图加载时取一次值就定住了。主窗口只加载一次,切了语言它还停在启动时的语言 ——
    /// 用户报的「切英文后侧栏的通知 / 插件 / 设置提示、会话右键的连接 / 删除、SFTP 的上传仍是中文」
    /// 就是这么来的。{loc:Localize} 在换语言时逐条重取。
    /// </remarks>
    [TestMethod]
    public void Xaml_TakesStringsThroughLocalize_NotXStatic()
    {
        List<string> offenders = [];
        foreach (string file in SourceFiles("*.axaml"))
        {
            offenders.AddRange(XamlStaticString.Matches(File.ReadAllText(file))
                                               .Select(match => $"  {Path.GetFileName(file)}: {match.Value}"));
        }

        Assert.IsEmpty(offenders,
                       "以下文案用 x:Static 取词,切换语言后不会刷新,改成 {loc:Localize 键名}:\n" + string.Join("\n", offenders));
    }

    /// <summary>XML 注释(可跨行)。</summary>
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment { get; }

    /// <summary>含汉字的属性值或元素文本。</summary>
    [GeneratedRegex(@"=""([^""]*[一-鿿][^""]*)""|>([^<]*[一-鿿][^<]*)<")]
    private static partial Regex XamlCjkText { get; }

    /// <summary>语言下拉里用各语言本名列出的条目,任何界面语言下都照原样显示。</summary>
    private static readonly HashSet<string> NativeLanguageNames = ["简体中文", "繁體中文", "日本語"];

    /// <summary>
    /// XAML 里不许写死中文 —— 写死的不随界面语言变,切到英文照样是中文。
    /// </summary>
    /// <remarks>
    /// 云同步版本历史曾写 <c>TargetNullValue=未知设备</c>:绑定属性里套不了 {loc:Localize},顺手就写死了。
    /// </remarks>
    [TestMethod]
    public void Xaml_HasNoHardcodedChineseText()
    {
        List<string> offenders = [];
        foreach (string file in SourceFiles("*.axaml"))
        {
            string xaml = XmlComment.Replace(File.ReadAllText(file), "");
            offenders.AddRange(XamlCjkText.Matches(xaml)
                                          .Select(match => (match.Groups[1].Success ? match.Groups[1] : match.Groups[2]).Value.Trim())
                                          .Where(text => !NativeLanguageNames.Contains(text))
                                          .Select(text => $"  {Path.GetFileName(file)}: {text}"));
        }

        Assert.IsEmpty(offenders, "以下 XAML 文案写死了中文,改成 {loc:Localize 键名}(绑定属性里用转换器):\n" + string.Join("\n", offenders));
    }

    /// <summary>含汉字的字符串字面量(含插值、逐字字符串)。</summary>
    [GeneratedRegex(@"\$?@?""(?:[^""\\]|\\.)*[一-鿿](?:[^""\\]|\\.)*""")]
    private static partial Regex CjkStringLiteral { get; }

    /// <summary>整行不是界面文案:调试日志、分析器豁免理由、正则特性(匹配的是对端输出)。</summary>
    [GeneratedRegex(@"Trace\.WriteLine|Debug\.WriteLine|Justification\s*=|\[GeneratedRegex")]
    private static partial Regex NotUiTextLine { get; }

    /// <summary>允许出现中文字面量的文件,各附理由。</summary>
    private static readonly Dictionary<string, string> ChineseLiteralAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["InteractivePromptDetector.cs"] = "匹配远端输出里的中文提示词(密码 / 验证码 / 请选择)",
        ["FluentFtpInterop.cs"] = "匹配 FTP 服务器回复里的中文关键词",
        ["ConnectionDiagnosticsService.cs"] = "匹配错误原因里的中文关键词",
        ["RemoteProcessProbe.cs"] = "参数守卫,只在调用方写错时抛",
        ["SyncCrypto.cs"] = "调用方捕获后换成本地化的「口令错误」",
        ["ConPtyShellStream.cs"] = "仅 Windows 的平台守卫,别的平台走不到这个类",
        ["VelaTerminalControl.cs"] = "控件库自带的默认值,宿主下发设置时换成本地化文案",
        ["SessionCsvHeaderAliases.cs"] = "CSV 表头的中日韩别名:匹配用户自己做的表格里的列名,不是界面文案(#571)",
    };

    /// <summary>
    /// C# 里的界面文案不许写死中文。
    /// </summary>
    /// <remarks>
    /// 连接诊断的步骤名、导出报告,捐赠页的「已复制」,路由追踪的 TTL 提示都曾写死在代码里,
    /// 切到英文照样是中文 —— 资源键与平价测试都管不到没走资源的文案。SSH / X 服务端两个库的中文诊断
    /// 文本另行跟踪(feature-plan.md「SSH 库剩余中文诊断文本的界面本地化」),不在此列。
    /// </remarks>
    [TestMethod]
    public void Code_HasNoHardcodedChineseText()
    {
        List<string> offenders = [];
        int scanned = 0;
        foreach (string file in SourceFiles("*.cs"))
        {
            string relative = Path.GetRelativePath(SourceRoot(), file);
            if (relative.StartsWith("VelaShell.Ssh" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("VelaShell.XServer" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                ChineseLiteralAllowed.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }
            scanned++;
            string[] lines = File.ReadAllLines(file);
            bool inSuppression = false;
            for (int i = 0; i < lines.Length; i++)
            {
                // [SuppressMessage(…)] 的分类与理由常写中文且跨行,整段跳过
                if (lines[i].Contains("SuppressMessage(", StringComparison.Ordinal))
                {
                    inSuppression = true;
                }
                if (inSuppression)
                {
                    inSuppression = !lines[i].Contains(")]", StringComparison.Ordinal);
                    continue;
                }
                if (NotUiTextLine.IsMatch(lines[i]))
                {
                    continue;
                }
                offenders.AddRange(CjkStringLiteral.Matches(StripComment(lines[i]))
                                                   .Select(match => $"  {relative}:{i + 1}: {match.Value}"));
            }
        }

        Assert.IsGreaterThanOrEqualTo(300, scanned, $"只扫到 {scanned} 个 C# 文件,扫描八成失效了。");
        Assert.IsEmpty(offenders, "以下 C# 字符串写死了中文,不随界面语言变,改成 Strings.Get(\"键名\"):\n" + string.Join("\n", offenders));
    }

    /// <summary>去掉一行里的注释(不在字符串里的 // 之后、/// 与 * 开头的整行)。</summary>
    private static string StripComment(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            return "";
        }
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i];
            }
        }
        return line;
    }

    /// <summary>src 下匹配的源文件,跳过 bin / obj。</summary>
    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(SourceRoot(), pattern, SearchOption.AllDirectories)
                 .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [TestMethod]
    public void EveryLocalizeKeyUsedInCode_ExistsInResources() => AssertAllKeysDefined("*.cs", CodeKey, minimumExpected: 50);

    /// <summary>
    /// 扫 src 下所有匹配文件里的键,逐个比对中性资源。
    /// </summary>
    /// <param name="filePattern">要扫描的文件通配符(如 *.axaml)。</param>
    /// <param name="pattern">从文件内容里捞出键名的正则,第 1 个捕获组为键。</param>
    /// <param name="minimumExpected">
    /// 至少该扫到多少个键。没有这道下限,一旦扫描路径失效(挪目录、改布局)就一个键都找不到,
    /// 测试会安静地变成永远通过的空壳 —— 那正是它本该拦住的那种失败。
    /// </param>
    private static void AssertAllKeysDefined(string filePattern, Regex pattern, int minimumExpected)
    {
        HashSet<string> defined = DefinedKeys();
        Dictionary<string, string> missing = [];
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(SourceRoot(), filePattern, SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value;
                found.Add(key);
                if (!defined.Contains(key))
                {
                    missing.TryAdd(key, Path.GetFileName(file));
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(minimumExpected, found.Count,
                                      $"只在 {filePattern} 里扫到 {found.Count} 个键,远低于预期 —— 扫描八成失效了,别让这条测试变成空壳。");
        Assert.IsEmpty(missing,
                       "以下键被界面引用但资源里没有,会在所有语言下显示成英文键名:\n" +
                       string.Join("\n", missing.Select(entry => $"  {entry.Key}  ({entry.Value})")));
    }

    /// <summary>中性(英文)资源里已定义的全部键。</summary>
    private static HashSet<string> DefinedKeys()
    {
        var manager = new ResourceManager("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);
        ResourceSet neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var keys = neutral.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.Ordinal);
        Assert.IsNotEmpty(keys, "中性资源为空,后面的比对就没意义了。");
        return keys;
    }

    /// <summary>从测试输出目录向上找到仓库里的 src 目录。</summary>
    private static string SourceRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "src");
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库的 src 目录(找不到同级的 VelaShell.slnx)。");
    }
}
