using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// 网络代理页的排版回归:说明文字必须**在卡片内换行**,不能顶出可视区。
/// </summary>
/// <remarks>
/// 用户截图反馈:副标题与「代理类型」的说明都跑到界面外。
/// 根因是 <c>TextBlock.page-subtitle</c> 漏了 <c>TextWrapping</c>:它按单行自身宽度量,
/// 超出卡片的部分被直接裁掉(内容区只有纵向滚动条,横向没得救),同页的 row-desc
/// 也因此紧贴右侧控件。headless 里实测:NoWrap 时副标题 Desired 高 14px(只有一行,
/// 文字被裁),Wrap 后 28px(两行,完整显示)—— 这就是断言要抓的差别。
/// </remarks>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class ProxySettingsPageUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ProxySettingsPageUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    /// <summary>本页所有说明文字(副标题 + 各行说明)都要换行,否则长句会被裁掉。</summary>
    [TestMethod]
    public void ProxyPage_EveryDescription_Wraps() =>
        OnProxyPage((page, _) =>
        {
            TextBlock[] descriptions = [.. page.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.Classes.Contains("page-subtitle") || t.Classes.Contains("row-desc"))];

            Assert.IsNotEmpty(descriptions, "没找到说明文字,断言会失去意义。");
            foreach (TextBlock text in descriptions)
            {
                Assert.AreEqual(
                    TextWrapping.Wrap,
                    text.TextWrapping,
                    $"说明文字未换行,长句会被裁在卡片右缘:「{text.Text}」");
            }
        });

    /// <summary>副标题真的折了行 —— 只把属性设上、布局没生效的话这条会红。</summary>
    [TestMethod]
    public void ProxyPage_Subtitle_WrapsInsteadOfBeingClipped() =>
        OnProxyPage((page, _) =>
        {
            TextBlock subtitle = page.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(t => t.Classes.Contains("page-subtitle"));

            // 单行 ≈ 1.27 倍字号(实测 11px → 14px);超过两倍字号就说明真的折了行。
            Assert.IsGreaterThan(
                subtitle.FontSize * 2,
                subtitle.DesiredSize.Height,
                $"副标题只有一行,文字被裁掉了:「{subtitle.Text}」");
        });

    [TestMethod]
    public void ProxyPage_ModeHelp_IsATableThatCoversAllFourModes() =>
        OnProxyPage((page, scroll) =>
        {
            StackPanel help = HelpPanel(page);
            Border table = help.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("table"));
            Grid[] rows = [.. table.GetVisualDescendants().OfType<Grid>()];

            Assert.HasCount(4, rows, "四种模式各占一行");
            TextBlock[] names = [.. rows.Select(row => row.GetVisualDescendants().OfType<TextBlock>().First())];
            TextBlock[] descs = [.. rows.Select(row => row.GetVisualDescendants().OfType<TextBlock>().Skip(1).First())];

            // 模式名对齐成一列 —— 这就是"表格化"相对于一整块纯文本的全部价值。
            Assert.AreEqual(1, names.Select(n => n.Bounds.Width).Distinct().Count(), "模式名没有对齐成一列");
            Assert.IsTrue(descs.All(d => d.TextWrapping == TextWrapping.Wrap), "说明要换行,否则会顶到表格外");
            Assert.IsTrue(names.All(n => n.Text is { Length: > 0 }), "每行都要有模式名");

            // 整页不该需要横向滚动。
            Assert.IsLessThanOrEqualTo(scroll.Viewport.Width + 0.5, scroll.Extent.Width, "内容比可视区宽,会顶出卡片");

            // 断言按**每种模式的判别特征**写,不绑界面语言(测试宿主跑在哪个语言下都成立)。
            string text = string.Join("\n", help.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            StringAssert.Contains(text, "HTTP_PROXY", "无代理那条要写明它不读环境变量");
            StringAssert.Contains(text, "PAC", "系统代理那条要写明它含 PAC、是解析器");
            StringAssert.Contains(text, "CONNECT", "HTTP 代理那条要写明它用 CONNECT 打隧道");
            StringAssert.Contains(text, "socks5h", "SOCKS5 那条要写明默认由代理解析主机名");
        });

    /// <summary>四种模式的说明区(标题 + 表格 + 两条注)。</summary>
    private static StackPanel HelpPanel(ProxySettingsPage page) =>
        page.GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(panel => panel.Name == "ProxyModesHelp");

    private static void OnProxyPage(Action<ProxySettingsPage, ScrollViewer> body) =>
        _session.Dispatch(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            viewModel.SelectSection(SettingsSectionKey.Proxy);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            ProxySettingsPage page = window.GetVisualDescendants().OfType<ProxySettingsPage>().Single();
            Panel host = window.GetVisualDescendants().OfType<Panel>().First(panel => panel.Name == "PageHost");
            ScrollViewer scroll = host.GetVisualAncestors().OfType<ScrollViewer>().First();

            body(page, scroll);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
}
