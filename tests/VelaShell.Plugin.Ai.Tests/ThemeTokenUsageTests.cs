using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 插件界面必须真的落在软件自己那套主题上。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这道关。</b><c>{DynamicResource Xxx}</c> 拼错一个字母不会报错、不会抛异常 ——
/// 它只是<b>什么都不做</b>,于是那条分隔线是隐形的、那段文字用的是 Fluent 默认前景色。
/// 真事:协作接入页的保存栏写了 <c>VelaBorderSubtle</c>,而这套令牌里根本没有这个键
/// (只有 <c>VelaBorderPrimary</c> / <c>VelaBorderSecondary</c>),那条线从落地起就没画出来过,
/// 直到用户说"这个窗口没应用主题"才被发现。
/// </para>
/// <para>
/// headless 用例挡不住这一类:测试进程里压根没装载宿主的令牌字典,所有键都解析不到,
/// 拼对拼错看起来一模一样。所以这里按<b>文本</b>比对 —— 用的键必须能在仓库里找到定义。
/// </para>
/// </remarks>
[TestClass]
public sealed class ThemeTokenUsageTests
{
    /// <summary><c>{DynamicResource Key}</c> / <c>{StaticResource Key}</c>。</summary>
    private static readonly Regex Used = new(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9._]+)\s*\}",
        RegexOptions.Compiled);

    /// <summary>XAML 里的 <c>x:Key="Key"</c>。</summary>
    private static readonly Regex DefinedInXaml = new(@"x:Key=""([A-Za-z0-9._]+)""", RegexOptions.Compiled);

    /// <summary>
    /// C# 里运行时塞进去的键(如 <c>Resources["AiCopyTip"] = …</c>)。
    /// </summary>
    /// <remarks>
    /// 本地化的提示文案就是这么给的:XAML 里引用一个键,构造时按当前语言把值填进
    /// <c>Resources</c>。它们同样是"有定义"的键,漏算就会把好代码判成红。
    /// </remarks>
    private static readonly Regex DefinedInCode = new(@"Resources\[""([A-Za-z0-9._]+)""\]", RegexOptions.Compiled);

    /// <summary>XML 注释。里面的示例代码不算引用。</summary>
    private static readonly Regex Comments = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>从测试程序集往上找到仓库根(带 <c>VelaShell.slnx</c> 的那一层)。</summary>
    /// <remarks>
    /// 找不到就<b>失败</b>而不是跳过:MSTest 把跳过记成通过,一条永远"绿"的守门用例
    /// 比没有这条用例更糟 —— 它会让人以为这一类问题已经被挡住了。
    /// </remarks>
    private static DirectoryInfo RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException(
            $"Could not find VelaShell.slnx above '{AppContext.BaseDirectory}'; this test must run from the repository.");
    }

    private static HashSet<string> DefinedKeys(DirectoryInfo root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in (string[])["src", "plugins"])
        {
            string path = Path.Combine(root.FullName, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(path, "*.axaml", SearchOption.AllDirectories))
            {
                foreach (Match match in DefinedInXaml.Matches(File.ReadAllText(file)))
                {
                    keys.Add(match.Groups[1].Value);
                }
            }
            foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (Match match in DefinedInCode.Matches(File.ReadAllText(file)))
                {
                    keys.Add(match.Groups[1].Value);
                }
            }
        }
        return keys;
    }

    [TestMethod]
    public void EveryResourceKeyUsedByThePluginIsDefinedSomewhere()
    {
        DirectoryInfo root = RepositoryRoot();
        HashSet<string> defined = DefinedKeys(root);
        Assert.IsGreaterThan(50, defined.Count, $"only found {defined.Count} defined keys — the scan is probably looking in the wrong place");

        string ui = Path.Combine(root.FullName, "plugins", "VelaShell.Plugin.Ai", "Ui");
        List<string> missing = [];
        foreach (string file in Directory.EnumerateFiles(ui, "*.axaml"))
        {
            string text = Comments.Replace(File.ReadAllText(file), "");
            foreach (Match match in Used.Matches(text))
            {
                if (!defined.Contains(match.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.IsEmpty(missing,
            "these resource keys resolve to nothing at runtime (no error, just no styling): "
            + string.Join(", ", missing.Distinct()));
    }

    /// <summary>聊天正文恢复 13 号,工具栏与说明保持 11 号;字号仍从宿主令牌动态取得。</summary>
    [TestMethod]
    [DataRow("ChatPanelView.axaml", "SelectableTextBlock.body", 13)]
    [DataRow("ChatPanelView.axaml", "Border.inputWrap ae|TextEditor", 13)]
    [DataRow("ChatPanelView.axaml", "TextBlock.placeholder", 12)]
    [DataRow("ChatPanelView.axaml", "md|MarkdownRenderer md|MarkdownTextBlock", 13)]
    [DataRow("ChatPanelView.axaml", "md|MarkdownRenderer Border.Heading1Block md|MarkdownTextBlock", 16)]
    [DataRow("ChatPanelView.axaml", "md|MarkdownRenderer Border.Heading2Block md|MarkdownTextBlock", 15)]
    [DataRow("ChatPanelView.axaml", "md|MarkdownRenderer Border.Heading3Block md|MarkdownTextBlock", 14)]
    [DataRow("ChatPanelView.axaml", "md|CodeBlock", 11)]
    [DataRow("ChatPanelView.axaml", "TextBlock.dim", 11)]
    [DataRow("ChatPanelView.axaml", "TextBlock.meta", 11)]
    [DataRow("ChatPanelView.axaml", "TextBlock.toolName", 11)]
    [DataRow("ChatPanelView.axaml", "Grid#InputToolbar ComboBox", 11)]
    [DataRow("ChatPanelView.axaml", "Grid#TopBar ComboBox", 11)]
    [DataRow("DialogStyles.axaml", "TextBlock.label", 11)]
    [DataRow("DialogStyles.axaml", "TextBlock.dim", 11)]
    [DataRow("DialogStyles.axaml", "TextBlock.hint", 11)]
    [DataRow("DialogStyles.axaml", "TextBlock.section-title", 11)]
    [DataRow("DialogStyles.axaml", "TextBlock.body", 12)]
    [DataRow("DialogStyles.axaml", "TextBox", 12)]
    [DataRow("DialogStyles.axaml", "ComboBox", 12)]
    public void TextStyles_UseCompactHostFontTokens(string file, string selector, int size)
    {
        string path = Path.Combine(RepositoryRoot().FullName, "plugins", "VelaShell.Plugin.Ai", "Ui", file);
        XNamespace axaml = "https://github.com/avaloniaui";
        XElement style = XDocument.Load(path).Descendants(axaml + "Style")
            .Single(e => (string?)e.Attribute("Selector") == selector);
        XElement setter = style.Elements(axaml + "Setter")
            .Single(e => (string?)e.Attribute("Property") == "FontSize");

        Assert.AreEqual($"{{DynamicResource VelaFontSize{size}}}", (string?)setter.Attribute("Value"),
            $"{file}: {selector} must keep the compact size and follow the host UI font setting");
    }

    /// <summary>连弱化文字也不再用 10 号;同时防止临时占位键混进最终代码。</summary>
    [TestMethod]
    public void FontTokens_InXamlAndCode_AreDefinedAndAtLeast11()
    {
        DirectoryInfo root = RepositoryRoot();
        HashSet<string> defined = DefinedKeys(root);
        string ui = Path.Combine(root.FullName, "plugins", "VelaShell.Plugin.Ai", "Ui");
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(ui)
                     .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            string text = Comments.Replace(File.ReadAllText(file), "");
            foreach (Match match in Regex.Matches(text, @"\bVelaFontSize[0-9_]+\b"))
            {
                string key = match.Value;
                Assert.Contains(key, defined, $"{Path.GetFileName(file)}: {key} is not a host font token");
                Assert.IsTrue(int.TryParse(key["VelaFontSize".Length..], out int size) && size >= 11,
                    $"{Path.GetFileName(file)}: {key} is smaller than the 11px minimum");
                count++;
            }
        }
        Assert.IsGreaterThan(50, count, "the scan must cover both XAML styles and code-created controls");
    }

    /// <summary>
    /// 协作接入页的每个 <c>&lt;Button&gt;</c> / <c>&lt;CheckBox&gt;</c> 都必须带上样式表里那个 class。
    /// </summary>
    /// <remarks>
    /// <b>不要退回"每个按钮自己写 <c>Theme=</c>"那种写法。</b>两条都是踩出来的:
    /// <list type="number">
    /// <item>
    /// <c>VelaAccentPillButtonTheme</c> <b>没有</b> <c>HorizontalContentAlignment</c> 这个 setter,
    /// 只挂主题不补这一项,纯文字内容会被 Stretch 拉开 —— 看起来就是"文字没居中"。
    /// class 那条路在样式表里显式补上了。
    /// </item>
    /// <item>
    /// 代码里 new 出来的控件<b>不能</b>用 <c>TryFindResource</c> 取主题:那时它还没进逻辑树,
    /// 资源查找一律落空,而且是静默落空。写成 class,样式在进树时才套,DynamicResource 那时才解析。
    /// </item>
    /// </list>
    /// </remarks>
    [TestMethod]
    public void EveryButtonAndCheckBoxOnTheCollaborationPageCarriesItsClass()
    {
        string file = Path.Combine(RepositoryRoot().FullName,
            "plugins", "VelaShell.Plugin.Ai", "Ui", "CollaborationView.axaml");
        string text = Comments.Replace(File.ReadAllText(file), "");

        List<string> bare =
        [
            .. Regex.Matches(text, @"<(?:Button|CheckBox)\b[^>]*>", RegexOptions.Singleline)
                    .Select(m => m.Value)
                    .Where(e => !Regex.IsMatch(e, @"Classes=""(host|primary)"""))
                    .Select(e => Regex.Replace(e, @"\s+", " ").Trim())
        ];

        Assert.IsEmpty(bare,
            "these controls carry no host class, so they fall back to the default look "
            + "(and their labels are not centred): " + string.Join(" | ", bare));
    }
}
