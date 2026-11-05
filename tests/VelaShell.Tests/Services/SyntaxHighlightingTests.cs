using Avalonia.Headless;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using VelaShell.Core.Models;
using VelaShell.Services.Syntax;

namespace VelaShell.Tests.Services;

/// <summary>
/// 语法高亮:文件类型判定 + 自带 xshd 的可加载性与真实着色 + 配色跟随主题且读得出来。
/// <para>
/// 可加载性尤其必要:<see cref="SyntaxHighlightingService" /> 刻意吞掉单份定义的加载异常
/// (坏掉一份不该让编辑器打不开),代价是**正则写错会静默退化成纯文本**。
/// 这组测试就是那个静默失败的守门人。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Syntax")]
public class SyntaxHighlightingTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SyntaxHighlightingTests).Assembly);

    // ---- 扩展名判定 ----

    [TestMethod]
    [DataRow("deploy.sh", SyntaxNames.Shell)]
    [DataRow("docker-compose.yml", SyntaxNames.Yaml)]
    [DataRow("values.yaml", SyntaxNames.Yaml)]
    [DataRow("app.ini", SyntaxNames.Ini)]
    [DataRow("backup.timer", SyntaxNames.Ini)]
    [DataRow("Cargo.toml", SyntaxNames.Toml)]
    [DataRow("syslog.log", SyntaxNames.Log)]
    [DataRow("main.go", SyntaxNames.Go)]
    [DataRow("lib.rs", SyntaxNames.Rust)]
    [DataRow("init.lua", SyntaxNames.Lua)]
    [DataRow("deploy.rb", SyntaxNames.Ruby)]
    [DataRow("check_disk.pl", SyntaxNames.Perl)]
    [DataRow("schema.sql", SyntaxNames.Sql)]
    [DataRow("main.tf", SyntaxNames.Hcl)]
    [DataRow("rules.mk", SyntaxNames.Makefile)]
    [DataRow("site.nginx", SyntaxNames.Nginx)]
    [DataRow("package.json", "Json")]
    [DataRow("events.jsonl", "Json")]
    [DataRow("main.py", "Python")]
    [DataRow("pom.xml", "XML")]
    [DataRow("App.axaml", "XML")]
    [DataRow("App.tsx", "JavaScript")]
    [DataRow("theme.scss", "CSS")]
    public void Detect_ByExtension(string fileName, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(fileName));

    // ---- 特殊文件名(无扩展名)----

    [TestMethod]
    [DataRow("Dockerfile", SyntaxNames.Dockerfile)]
    [DataRow("dockerfile", SyntaxNames.Dockerfile)]
    [DataRow("Makefile", SyntaxNames.Makefile)]
    [DataRow(".bashrc", SyntaxNames.Shell)]
    [DataRow("sshd_config", SyntaxNames.Ini)]
    [DataRow("fstab", SyntaxNames.Ini)]
    [DataRow("known_hosts", SyntaxNames.Ini)]
    [DataRow("nginx.conf", SyntaxNames.Nginx)]
    [DataRow("Gemfile", SyntaxNames.Ruby)]
    [DataRow("Vagrantfile", SyntaxNames.Ruby)]
    [DataRow("Cargo.lock", SyntaxNames.Toml)]
    public void Detect_BySpecialFileName(string fileName, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(fileName));

    /// <summary>远端路径要能剥掉目录再判定 —— 传进来的往往是完整远端路径。</summary>
    [TestMethod]
    [DataRow("/etc/nginx/nginx.conf", SyntaxNames.Nginx)]
    [DataRow("/home/deploy/scripts/backup.sh", SyntaxNames.Shell)]
    [DataRow(@"C:\temp\vela\app.yaml", SyntaxNames.Yaml)]
    public void Detect_StripsDirectories(string path, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(path));

    /// <summary>
    /// nginx 的站点配置文件名千奇百怪(<c>default</c>、<c>api.conf</c>),只有所在目录说明它是 nginx;
    /// 而别处的 .conf 仍按 Ini 着色。
    /// </summary>
    [TestMethod]
    [DataRow("/etc/nginx/conf.d/api.conf", SyntaxNames.Nginx)]
    [DataRow("/etc/nginx/sites-available/default", SyntaxNames.Nginx)]
    [DataRow("/usr/local/openresty/nginx/conf/upstream.conf", SyntaxNames.Nginx)]
    [DataRow("/etc/supervisor/conf.d/app.conf", SyntaxNames.Ini)]
    [DataRow("/etc/nginx/mime.types", null)]
    public void Detect_NginxByDirectory(string path, string? expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(path));

    // ---- shebang(服务器上大量脚本没有扩展名,只有首行能说明类型)----

    [TestMethod]
    [DataRow("#!/bin/bash", SyntaxNames.Shell)]
    [DataRow("#!/bin/sh", SyntaxNames.Shell)]
    [DataRow("#!/usr/bin/env bash", SyntaxNames.Shell)]
    [DataRow("#!/usr/bin/env python3", "Python")]
    [DataRow("#!/usr/bin/python3.11", "Python")]
    [DataRow("#!/usr/bin/env -S node --experimental", "JavaScript")]
    [DataRow("#!/usr/bin/pwsh", "PowerShell")]
    [DataRow("#!/usr/bin/env ruby", SyntaxNames.Ruby)]
    [DataRow("#!/usr/bin/perl -w", SyntaxNames.Perl)]
    [DataRow("#!/usr/bin/lua5.4", SyntaxNames.Lua)]
    [DataRow("#!/usr/bin/make -f", SyntaxNames.Makefile)]
    public void Detect_ByShebang(string firstLine, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect("deploy", firstLine));

    [TestMethod]
    public void Detect_ExtensionWinsOverShebang() =>
        // 扩展名是更强的信号;shebang 只是无扩展名时的兜底。
        Assert.AreEqual("Python", FileTypeDetector.Detect("tool.py", "#!/bin/bash"));

    [TestMethod]
    [DataRow("notes", null)]
    [DataRow("data.bin", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void Detect_UnknownReturnsNull(string? fileName, string? expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(fileName));

    [TestMethod]
    public void Detect_MalformedShebangDoesNotThrow()
    {
        Assert.IsNull(FileTypeDetector.Detect("script", "#!"));
        Assert.IsNull(FileTypeDetector.Detect("script", "#!/usr/bin/env"));
        Assert.IsNull(FileTypeDetector.Detect("script", "#!   "));
        Assert.IsNull(FileTypeDetector.Detect("script", "not a shebang"));
    }

    // ---- 自带 xshd 必须真的能加载 ----

    /// <summary>
    /// 每一种自带类型都要能解析出定义。若某份 xshd 的正则或 XML 写错,
    /// 服务会把异常吞掉、这里就会拿到 null —— 从而把"静默退化成纯文本"变成一次红色测试。
    /// </summary>
    [TestMethod]
    [DataRow(SyntaxNames.Shell)]
    [DataRow(SyntaxNames.Yaml)]
    [DataRow(SyntaxNames.Ini)]
    [DataRow(SyntaxNames.Dockerfile)]
    [DataRow(SyntaxNames.Log)]
    [DataRow(SyntaxNames.Nginx)]
    [DataRow(SyntaxNames.Toml)]
    [DataRow(SyntaxNames.Makefile)]
    [DataRow(SyntaxNames.Go)]
    [DataRow(SyntaxNames.Rust)]
    [DataRow(SyntaxNames.Lua)]
    [DataRow(SyntaxNames.Ruby)]
    [DataRow(SyntaxNames.Perl)]
    [DataRow(SyntaxNames.Sql)]
    [DataRow(SyntaxNames.Hcl)]
    public void BundledDefinitions_Load(string expectedName)
    {
        IHighlightingDefinition? definition = ResolveFor(expectedName);

        Assert.IsNotNull(definition, $"{expectedName} 的 xshd 没能加载 —— 多半是正则或 XML 写错了");
        Assert.AreEqual(expectedName, definition.Name);
        Assert.IsNotEmpty(definition.NamedHighlightingColors, "定义应当声明命名颜色,否则无法跟随主题换肤");
    }

    /// <summary>
    /// 自带定义必须真的能对文本着色,而不只是"能加载但规则一条都不命中"。
    /// <para>
    /// 走 headless 会话:<see cref="DocumentHighlighter" /> 构造时会 <c>Dispatcher.VerifyAccess()</c>,
    /// 必须在 Avalonia UI 线程上创建。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ShellDefinition_ActuallyHighlights() =>
        _session.Dispatch(() =>
        {
            IHighlightingDefinition? definition = ResolveFor(SyntaxNames.Shell);
            Assert.IsNotNull(definition);

            var document = new TextDocument(
                "#!/bin/bash\nNAME=\"world\"\nif [ -n \"$NAME\" ]; then\n  echo \"hi $NAME\"\nfi\n");
            using var highlighter = new DocumentHighlighter(document, definition);

            int coloured = 0;
            for (int line = 1; line <= document.LineCount; line++)
            {
                coloured += highlighter.HighlightLine(line).Sections.Count;
            }
            Assert.IsGreaterThan(0, coloured, "Shell 定义一个高亮区段都没产生,规则没生效");
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 新增的每种语言都要把**具体的词**染成**对的类别** —— 光"产生了区段"证明不了规则写对了
    /// (一条过宽的正则能把整行染成同一种颜色)。
    /// </summary>
    [TestMethod]
    [DataRow("main.go", "package main\n\nfunc main() {\n\tfmt.Println(\"hi\")\n}\n", "func", "Keyword")]
    [DataRow("main.go", "package main\n\nfunc main() {\n\tfmt.Println(\"hi\")\n}\n", "Println", "Function")]
    [DataRow("main.go", "package main\n\nfunc main() {\n\tfmt.Println(\"hi\")\n}\n", "\"hi\"", "String")]
    [DataRow("lib.rs", "fn main() {\n    let v: Vec<u8> = vec![1];\n}\n", "let", "Keyword")]
    [DataRow("lib.rs", "fn main() {\n    let v: Vec<u8> = vec![1];\n}\n", "vec!", "Function")]
    [DataRow("lib.rs", "fn main() {\n    let v: Vec<u8> = vec![1];\n}\n", "u8", "Type")]
    [DataRow("init.lua", "local x = 1 -- note\nprint(x)\n", "local", "Keyword")]
    [DataRow("init.lua", "local x = 1 -- note\nprint(x)\n", "-- note", "Comment")]
    [DataRow("app.rb", "def hello\n  puts \"hi #{@name}\"\nend\n", "def", "Keyword")]
    [DataRow("app.rb", "class Greeter\n  attr_reader :name\nend\n", ":name", "Constant")]
    [DataRow("tool.pl", "my $name = 'x';\nprint \"$name\\n\";\n", "my", "Keyword")]
    [DataRow("tool.pl", "my $name = 'x';\nprint \"$name\\n\";\n", "$name", "Variable")]
    [DataRow("q.sql", "SELECT id FROM users WHERE name = 'bob'; -- hi\n", "SELECT", "Keyword")]
    [DataRow("q.sql", "SELECT id FROM users WHERE name = 'bob'; -- hi\n", "'bob'", "String")]
    [DataRow("q.sql", "SELECT id FROM users WHERE name = 'bob'; -- hi\n", "-- hi", "Comment")]
    [DataRow("Cargo.toml", "[package]\nname = \"vela\"\n", "[package]", "Section")]
    [DataRow("Cargo.toml", "[package]\nname = \"vela\"\n", "name", "Key")]
    [DataRow("Makefile", "CC := gcc\n\nbuild: main.o\n\t$(CC) -o app $(wildcard *.o)\n", "CC", "Key")]
    [DataRow("Makefile", "CC := gcc\n\nbuild: main.o\n\t$(CC) -o app $(wildcard *.o)\n", "build", "Function")]
    [DataRow("Makefile", "CC := gcc\n\nbuild: main.o\n\t$(CC) -o app $(wildcard *.o)\n", "$(CC)", "Variable")]
    [DataRow("/etc/nginx/conf.d/api.conf", "server {\n    listen 443 ssl;\n    root $document_root;\n}\n", "server", "Section")]
    [DataRow("/etc/nginx/conf.d/api.conf", "server {\n    listen 443 ssl;\n    root $document_root;\n}\n", "listen", "Key")]
    [DataRow("/etc/nginx/conf.d/api.conf", "server {\n    listen 443 ssl;\n    root $document_root;\n}\n", "$document_root", "Variable")]
    [DataRow("main.tf", "resource \"aws_instance\" \"web\" {\n  ami = var.ami_id\n}\n", "resource", "Keyword")]
    [DataRow("main.tf", "resource \"aws_instance\" \"web\" {\n  ami = var.ami_id\n}\n", "ami", "Key")]
    [DataRow("main.tf", "resource \"aws_instance\" \"web\" {\n  ami = var.ami_id\n}\n", "var.ami_id", "Variable")]
    [DataRow("plugin.log", "2026-10-24 [ERROR] Exception failed\n", "ERROR", "Error")]
    [DataRow("plugin.log", "2026-10-24 [WARNING] retrying\n", "WARNING", "Warning")]
    [DataRow("plugin.log", "2026-10-24 [SUCCESS] connected\n", "SUCCESS", "Success")]
    public void BundledDefinitions_ColourTheRightTokens(string fileName, string text, string token, string expectedColor)
    {
        IHighlightingDefinition? definition = SyntaxHighlightingService.Resolve(fileName, null, UiThemeCatalog.DefaultDark);
        Assert.IsNotNull(definition, $"{fileName} 没解析出定义");

        string? actual = ColorNameAt(definition, text, token);

        Assert.AreEqual(expectedColor, actual, $"{definition.Name} 里的 '{token}' 应染成 {expectedColor}");
    }

    // ---- 配色 ----

    /// <summary>
    /// 内置与自带定义里出现的每一个命名颜色都要归到调色板角色。
    /// <para>
    /// 回归:AvaloniaEdit 内置定义是给白底调的(Blue / DarkBlue / Black),没收录的名字只走一个
    /// "太接近背景才改"的兜底 —— CSS 的选择器、HTML 的标签、Markdown 的链接、Patch 的增删行
    /// 就这样在暗色主题下顶着一身浅色配色,读起来发暗甚至直接看不见。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EveryNamedColour_InEveryDefinition_HasAPaletteRole()
    {
        var palette = SyntaxPalette.From(UiThemeCatalog.DefaultDark);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (IHighlightingDefinition definition in SyntaxHighlightingService.AllDefinitions)
        {
            IEnumerable<HighlightingColor> colors;
            try
            {
                // 内置定义是延迟加载的;AvaloniaEdit 自己带的个别定义加载即抛(见 BrokenBuiltIns),
                // 那份定义编辑器本来也用不上,这里跳过它、只核对能加载的。
                colors = [.. definition.NamedHighlightingColors];
            }
            catch (HighlightingDefinitionInvalidException)
            {
                continue;
            }
            foreach (HighlightingColor color in colors)
            {
                if (palette.ForRole(color.Name) is null)
                {
                    missing.Add($"{definition.Name}.{color.Name}");
                }
            }
        }
        Assert.IsEmpty(missing, "这些颜色名没有调色板角色:" + string.Join(", ", missing));
    }

    /// <summary>
    /// 每套主题的每个语法角色(含链接色)压在编辑器底色上都要读得出来(≥ 3:1)。
    /// 语法色从主题种子色派生,十几套主题靠眼睛挨个看不出哪一套的注释掉进了背景里。
    /// </summary>
    [TestMethod]
    public void SyntaxRoles_AreReadableOnTheEditorBackground_InEveryTheme()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            var p = SyntaxPalette.From(theme);
            (string Role, Color Value)[] roles =
            [
                ("Comment", p.Comment), ("String", p.String), ("Keyword", p.Keyword), ("Number", p.Number),
                ("Variable", p.Variable), ("Function", p.Function), ("Type", p.Type), ("Error", p.Error),
                ("Link", p.Link), ("Default", p.Default),
            ];
            foreach ((string role, Color value) in roles)
            {
                double ratio = SyntaxPalette.Contrast(value, p.Background);
                if (ratio < SyntaxPalette.MinimumContrast)
                {
                    failures.Add($"{theme.Name}.{role} = {ratio:F2}:1");
                }
            }
        }
        Assert.IsEmpty(failures, "对比度不足:" + string.Join("; ", failures));
    }

    /// <summary>
    /// 界面上 URL / 邮箱链接用的是 <c>VelaInfo</c> 令牌(见 RemoteFileEditorView.axaml),
    /// 它本身不经过语法调色板的提亮 —— 所以单独量一遍:每套主题的信息色压在编辑器底上都要读得出来。
    /// 回归:AvaloniaEdit 缺省链接色是纯蓝 #0000FF,在 Dracula 底上只有约 1.7:1。
    /// </summary>
    [TestMethod]
    public void LinkToken_IsReadableOnTheEditorBackground_InEveryTheme()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            double ratio = SyntaxPalette.Contrast(
                Color.Parse(theme.Palette.Info), Color.Parse(theme.Palette.BgTerminal));
            if (ratio < SyntaxPalette.MinimumContrast)
            {
                failures.Add($"{theme.Name} = {ratio:F2}:1");
            }
        }
        Assert.IsEmpty(failures, "链接色对比度不足:" + string.Join("; ", failures));
        Assert.IsLessThan(SyntaxPalette.MinimumContrast,
            SyntaxPalette.Contrast(Colors.Blue, Color.Parse(UiThemeCatalog.DefaultDark.Palette.BgTerminal)),
            "对照组:缺省纯蓝在暗底上本来就读不出来,这正是改色的理由");
    }

    /// <summary>VelaDark / VelaLight 必须仍然是 Dracula / Alucard 的正典语法色(派生取法没有把老用户的配色改掉)。</summary>
    [TestMethod]
    public void DefaultThemes_KeepTheirCanonicalSyntaxColours()
    {
        var dark = SyntaxPalette.From(UiThemeCatalog.DefaultDark);
        Assert.AreEqual(Color.Parse("#F1FA8C"), dark.String);
        Assert.AreEqual(Color.Parse("#FF79C6"), dark.Keyword);
        Assert.AreEqual(Color.Parse("#BD93F9"), dark.Number);
        Assert.AreEqual(Color.Parse("#50FA7B"), dark.Function);
        Assert.AreEqual(Color.Parse("#8BE9FD"), dark.Type);
        Assert.AreEqual(Color.Parse("#FFB86C"), dark.Variable);
        Assert.AreEqual(Color.Parse("#6272A4"), dark.Comment, "Dracula 注释色恰好压线 3:1,不能被可读性提亮悄悄改掉");
        Assert.AreEqual(Color.Parse("#FF5555"), dark.Error);
        Assert.AreEqual(Color.Parse("#8BE9FD"), dark.Link, "暗色链接是 Dracula 青,而不是纯蓝");

        // Alucard:除变量色(橙红中点,见下)外逐一与原先的正典值一致。
        var light = SyntaxPalette.From(UiThemeCatalog.DefaultLight);
        Assert.AreEqual(Color.Parse("#6C664B"), light.Comment);
        Assert.AreEqual(Color.Parse("#846E15"), light.String);
        Assert.AreEqual(Color.Parse("#A3144D"), light.Keyword);
        Assert.AreEqual(Color.Parse("#644AC9"), light.Number);
        Assert.AreEqual(Color.Parse("#14710A"), light.Function);
        Assert.AreEqual(Color.Parse("#036A96"), light.Type);
        Assert.AreEqual(Color.Parse("#CB3A2A"), light.Error);
        Assert.AreEqual(Color.Parse("#036A96"), light.Link);
        Assert.AreNotEqual(light.String, light.Variable, "亮色主题里 Warning 与 Yellow 同值,字符串与变量不能因此撞色");
    }

    /// <summary>换主题要能换出不同的前景色,否则暗色下会沿用浅色配色。</summary>
    [TestMethod]
    public void Recolor_FollowsThemeVariant()
    {
        Color? darkColor = CommentColor(UiThemeCatalog.DefaultDark);
        Color? lightColor = CommentColor(UiThemeCatalog.DefaultLight);

        Assert.IsNotNull(darkColor);
        Assert.IsNotNull(lightColor);
        Assert.AreNotEqual(darkColor, lightColor, "深浅主题的注释色应当不同");
    }

    /// <summary>
    /// 同为暗色的两套具名主题也要各用各的语法色。
    /// 回归:以前只按明暗二分,Nord / Gruvbox 的编辑器底色跟着主题走,代码却仍是 Dracula 那一套。
    /// </summary>
    [TestMethod]
    public void Recolor_FollowsTheNamedTheme_NotJustTheVariant()
    {
        UiTheme nord = UiThemeCatalog.Get("nord");
        Assert.IsTrue(nord.IsDark);

        IHighlightingDefinition? definition = SyntaxHighlightingService.Resolve("probe.go", null, nord);
        Assert.IsNotNull(definition);
        Color? keyword = definition.NamedHighlightingColors.First(c => c.Name == "Keyword").Foreground?.GetColor(null);

        Assert.AreEqual(SyntaxPalette.From(nord).Keyword, keyword);
        Assert.AreNotEqual(SyntaxPalette.From(UiThemeCatalog.DefaultDark).Keyword, keyword);
    }

    private static Color? CommentColor(UiTheme theme)
    {
        IHighlightingDefinition? definition = SyntaxHighlightingService.Resolve("probe.sh", null, theme);
        Assert.IsNotNull(definition);
        return definition.NamedHighlightingColors.First(c => c.Name == "Comment").Foreground?.GetColor(null);
    }

    /// <summary>取 <paramref name="token" /> 首次出现处最内层高亮区段的颜色名(没被着色时为 null)。</summary>
    private static string? ColorNameAt(IHighlightingDefinition definition, string text, string token) =>
        _session.Dispatch(() =>
        {
            int offset = text.IndexOf(token, StringComparison.Ordinal);
            Assert.AreNotEqual(-1, offset, $"样例文本里没有 '{token}'");
            var document = new TextDocument(text);
            using var highlighter = new DocumentHighlighter(document, definition);
            DocumentLine line = document.GetLineByOffset(offset);
            HighlightedLine highlighted = highlighter.HighlightLine(line.LineNumber);
            return highlighted.Sections
                              .LastOrDefault(s => s.Offset <= offset && offset < s.Offset + s.Length)
                              ?.Color?.Name;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 判定器会返回的每一个定义名,都必须对应一份**真能加载**的定义。
    /// <para>
    /// 回归:AvaloniaEdit 12 内置的 TeX 定义本身是坏的(延迟加载时抛 "Could not find main RuleSet"),
    /// 而 .tex 曾映射到它 —— 编辑器吞掉异常退化成纯文本,谁也看不出来。映射到一个名字写错、
    /// 或自带 xshd 没打进资源的定义,也是同样的静默结局。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EveryDetectedType_ResolvesToALoadableDefinition()
    {
        var failures = new List<string>();
        foreach (string name in FileTypeDetector.KnownDefinitionNames.Order(StringComparer.Ordinal))
        {
            IHighlightingDefinition? definition = SyntaxHighlightingService.AllDefinitions
                                                                           .FirstOrDefault(d => d.Name == name);
            if (definition is null)
            {
                failures.Add($"{name}: 没有同名定义");
                continue;
            }
            try
            {
                _ = definition.MainRuleSet;
            }
            catch (HighlightingDefinitionInvalidException ex)
            {
                failures.Add($"{name}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        Assert.IsEmpty(failures, "判定器指向了用不了的定义:" + string.Join("; ", failures));
    }

    /// <summary>用一个能命中该定义的文件名把它解析出来。</summary>
    private static IHighlightingDefinition? ResolveFor(string name)
    {
        string probeFile = name switch
        {
            SyntaxNames.Shell => "probe.sh",
            SyntaxNames.Yaml => "probe.yaml",
            SyntaxNames.Ini => "probe.ini",
            SyntaxNames.Dockerfile => "Dockerfile",
            SyntaxNames.Log => "probe.log",
            SyntaxNames.Nginx => "probe.nginx",
            SyntaxNames.Toml => "probe.toml",
            SyntaxNames.Makefile => "Makefile",
            SyntaxNames.Go => "probe.go",
            SyntaxNames.Rust => "probe.rs",
            SyntaxNames.Lua => "probe.lua",
            SyntaxNames.Ruby => "probe.rb",
            SyntaxNames.Perl => "probe.pl",
            SyntaxNames.Sql => "probe.sql",
            SyntaxNames.Hcl => "probe.tf",
            _ => "probe.txt",
        };
        return SyntaxHighlightingService.Resolve(probeFile, null, UiThemeCatalog.DefaultDark);
    }
}
