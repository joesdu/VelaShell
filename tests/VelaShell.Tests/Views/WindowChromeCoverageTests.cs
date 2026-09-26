using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 所有自绘卡片窗体都经 <see cref="WindowChrome" /> 装外框,旧写法不许回来。
/// </summary>
/// <remarks>
/// 旧写法是每扇窗自己在 XAML 里写 <c>WindowDecorations="None"</c> + <c>TransparencyLevelHint="Transparent"</c> +
/// 卡片 <c>Margin="16"</c> + <c>BoxShadow</c>,再在代码里各写一段 macOS 分支。这只在 Windows 上成立:
/// Linux 的合成器会把透明边距画成卡片外的一圈「空白」,不支持透明的 X11 干脆画成实色。
/// 而 XAML 里的本地值优先级高于样式,只要有一处写死,那扇窗就不跟着平台切换 —— 所以用扫描钉住。
/// </remarks>
[TestClass]
[TestCategory("WindowChrome")]
public sealed partial class WindowChromeCoverageTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WindowChromeCoverageTests).Assembly);

    [TestMethod]
    public void WindowXaml_LeavesTheFrameToWindowChrome()
    {
        var offenders = new List<string>();
        int windows = 0;
        foreach (string file in WindowXamlFiles())
        {
            windows++;
            string name = Path.GetFileName(file);
            bool main = name == "MainWindow.axaml";
            string xaml = File.ReadAllText(file);
            string root = StartTag(xaml, xaml.IndexOf("<Window", StringComparison.Ordinal));

            if (root.Contains("TransparencyLevelHint=", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: 根上写死了 TransparencyLevelHint");
            }
            // 主窗口的 None 是 Windows / Linux 的值,macOS 由 WindowChrome 在代码里换成 Full。
            if (!main && root.Contains("WindowDecorations=", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: 根上写死了 WindowDecorations");
            }
            if (!main && root.Contains("Background=\"Transparent\"", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: 根上写死了透明背景");
            }

            if (!main)
            {
                MatchCollection cards = CardRegex.Matches(xaml);
                if (cards.Count != 1)
                {
                    offenders.Add($"{name}: 应有且只有一张 Classes=\"window-card\" 的卡片,实际 {cards.Count} 张");
                }
                else
                {
                    string card = StartTag(xaml, xaml.LastIndexOf("<Border", cards[0].Index, StringComparison.Ordinal));
                    foreach (string attribute in new[] { "Margin", "CornerRadius", "BorderThickness", "BoxShadow" })
                    {
                        if (Regex.IsMatch(card, $@"\s{attribute}="))
                        {
                            offenders.Add($"{name}: 卡片上写死了 {attribute}(交给 Themes/WindowChrome.axaml)");
                        }
                    }
                }
                // 内半径 7 的四角写法只会出现在贴着卡片四角的子元素上,它们要挂 window-card-* 类,
                // 卡片变直角时才跟着变直角。
                foreach (Match match in FourCornerRegex.Matches(xaml).Where(match => match.Groups[1].Value.Split(',').Contains("7")))
                {
                    offenders.Add($"{name}: CornerRadius=\"{match.Groups[1].Value}\" 应改挂 window-card-top / -bottom / -left / -right / -bottom-left");
                }
            }

            string codeBehind = File.ReadAllText(file + ".cs");
            if (!codeBehind.Contains("WindowChrome.Apply(this, WindowChromeKind.", StringComparison.Ordinal))
            {
                offenders.Add($"{name}.cs: 构造时没有调 WindowChrome.Apply");
            }
            // 主窗口的 IsMacOS 管的是输入法会话,与外框无关。
            if (!main && codeBehind.Contains("OperatingSystem.IsMacOS()", StringComparison.Ordinal))
            {
                offenders.Add($"{name}.cs: 又出现了按 macOS 单独处理外框的分支");
            }
        }

        Assert.IsGreaterThanOrEqualTo(21, windows, "扫到的窗体数不对,扫描路径可能失效了。");
        Assert.IsEmpty(offenders, "这些窗体没有把外框交给 WindowChrome:\n" + string.Join("\n", offenders));
    }

    [TestMethod]
    public void EveryWindow_IsDressedByWindowChrome()
    {
        Type[] types = [.. typeof(WindowChrome).Assembly.GetTypes()
            .Where(type => typeof(Window).IsAssignableFrom(type)
                && !type.IsAbstract
                && type.Namespace?.StartsWith("VelaShell.Views", StringComparison.Ordinal) == true
                // 内置 X 服务端里远端 X 程序的窗口:有没有系统边框由 X 客户端自己决定,不是卡片窗口。
                && type != typeof(VelaShell.Views.XServer.XNativeWindow))];
        Assert.IsGreaterThanOrEqualTo(21, types.Length);

        var missing = new List<string>();
        _session.Dispatch(() =>
        {
            foreach (Type type in types)
            {
                var window = (Window)Activator.CreateInstance(type)!;
                if (WindowChrome.PlatformOf(window) is null)
                {
                    missing.Add(type.Name);
                }
            }
        }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsEmpty(missing, "这些窗口构造后没有经 WindowChrome 装外框:" + string.Join(", ", missing));
    }

    /// <summary>
    /// 没有标题栏的窗口:头部没有关闭 / 最小化这类窗口按钮,不算标题栏,保持设计稿的 48(用户决定,2026-09-26)。
    /// 设置窗口左上角那条是左侧导航的抬头兼拖动区,压到 28 与右侧页面标题对不上;
    /// 消息框(提示 / 确认 / 输入)关闭一律走按钮栏或 Esc,头部压到 28 显得局促。
    /// </summary>
    private static readonly Type[] WindowsWithoutTitleBar = [typeof(SettingsView), typeof(MessageDialog)];

    [TestMethod]
    public void EveryWindow_HasOneTitleBar_OfTheSharedHeight()
    {
        // 全部窗口(主窗口、独立窗口、对话框)的标题栏一个高度:由 window-titlebar 类的样式统一给出,
        // 任何一扇窗在 XAML 里写死高度、或者标题行用了定高的 RowDefinition,这里都会量出别的数。
        Type[] types = [.. typeof(WindowChrome).Assembly.GetTypes()
            .Where(type => typeof(Window).IsAssignableFrom(type)
                && !type.IsAbstract
                && type.Namespace?.StartsWith("VelaShell.Views", StringComparison.Ordinal) == true
                && type != typeof(VelaShell.Views.XServer.XNativeWindow))];

        var offenders = new List<string>();
        _session.Dispatch(() =>
        {
            foreach (Type type in types)
            {
                var window = (Window)Activator.CreateInstance(type)!;
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    Border[] bars = [.. window.GetLogicalDescendants().OfType<Border>()
                        .Where(border => border.Classes.Contains("window-titlebar"))];
                    int expected = WindowsWithoutTitleBar.Contains(type) ? 0 : 1;
                    if (bars.Length != expected)
                    {
                        offenders.Add($"{type.Name}: 应有 {expected} 个 window-titlebar,实际 {bars.Length} 个");
                    }
                    else if (bars.Length == 1 && Math.Abs(bars[0].Bounds.Height - WindowChrome.TitleBarHeight) > 0.01)
                    {
                        offenders.Add($"{type.Name}: 标题栏高 {bars[0].Bounds.Height},应为 {WindowChrome.TitleBarHeight}");
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsEmpty(offenders, string.Join("\n", offenders));
    }

    /// <summary>src/VelaShell/Views 下根元素是 Window 的 .axaml(跳过 obj/bin)。</summary>
    private static IEnumerable<string> WindowXamlFiles()
    {
        string views = Path.Combine(RepoRoot(), "src", "VelaShell", "Views");
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                && !file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                && File.ReadLines(file).FirstOrDefault()?.TrimStart().StartsWith("<Window", StringComparison.Ordinal) == true);
    }

    /// <summary>从 <paramref name="start" /> 开始的起始标签(到第一个不在引号里的 '>')。</summary>
    private static string StartTag(string xaml, int start)
    {
        bool quoted = false;
        for (int i = start; i < xaml.Length; i++)
        {
            if (xaml[i] == '"')
            {
                quoted = !quoted;
            }
            else if (xaml[i] == '>' && !quoted)
            {
                return xaml[start..(i + 1)];
            }
        }
        return xaml[start..];
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }

    [GeneratedRegex(@"Classes=""window-card""")]
    private static partial Regex CardRegex { get; }

    /// <summary>四个分量分开写的圆角,如 7,7,0,0。</summary>
    [GeneratedRegex(@"CornerRadius=""((?:\d+,){3}\d+)""")]
    private static partial Regex FourCornerRegex { get; }
}
