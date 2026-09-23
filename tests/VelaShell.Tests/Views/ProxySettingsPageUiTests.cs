using Avalonia;
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
/// 也因此紧贴右侧控件。
/// <para>
/// 断言刻意**不依赖字体度量**:这几句中文里的 CJK 走的是系统的回退字体,Windows 与 CI 的
/// ubuntu runner 量出来的宽度不一样(同一句话本地两行、CI 一行),拿"几行"当断言就是拿
/// 运行环境当断言 —— 第一版就是这么在 CI 上假红的。
/// </para>
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

    /// <summary>副标题真的会折行 —— 只把属性设上、布局没生效的话这条会红。</summary>
    /// <remarks>
    /// **不拿真实窗口宽度下的行数当断言。** 那句中文在 CI 的 ubuntu runner 上由系统的 CJK
    /// 回退字体渲染,比本地 Windows 窄,一行就装得下(实测高度 13px vs 本地 28px)——
    /// 拿它断言等于断言运行环境的字体度量,同一份代码在两个平台上给出不同结论。
    /// 这里改成"把可用宽度砍成自然宽度的一半":无论字体多窄,只要 <c>TextWrapping</c> 真的生效
    /// 就必须变成多行;不生效(NoWrap)则高度纹丝不动。与字体、平台都无关。
    /// </remarks>
    [TestMethod]
    public void ProxyPage_Subtitle_WrapsWhenWidthIsTight() =>
        OnProxyPage((page, _) =>
        {
            TextBlock subtitle = page.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(t => t.Classes.Contains("page-subtitle"));

            subtitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double naturalWidth = subtitle.DesiredSize.Width;
            double oneLineHeight = subtitle.DesiredSize.Height;
            Assert.IsGreaterThan(0, naturalWidth, "副标题没量出宽度,下面的断言会失去意义");

            subtitle.Measure(new Size(naturalWidth / 2, double.PositiveInfinity));
            Assert.IsGreaterThan(
                oneLineHeight,
                subtitle.DesiredSize.Height,
                $"可用宽度只有一半时仍然只有一行 —— TextWrapping 没生效,文字会被裁:「{subtitle.Text}」");
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
            Assert.HasCount(1, names.Select(n => n.Bounds.Width).Distinct(), "模式名没有对齐成一列");
            Assert.IsTrue(descs.All(d => d.TextWrapping == TextWrapping.Wrap), "说明要换行,否则会顶到表格外");
            Assert.IsTrue(names.All(n => n.Text is { Length: > 0 }), "每行都要有模式名");

            // 整页不该需要横向滚动。
            Assert.IsLessThanOrEqualTo(scroll.Viewport.Width + 0.5, scroll.Extent.Width, "内容比可视区宽,会顶出卡片");

            // 断言按**每种模式的判别特征**写,不绑界面语言(测试宿主跑在哪个语言下都成立)。
            string text = string.Join("\n", help.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("HTTP_PROXY", text, "无代理那条要写明它不读环境变量");
            Assert.Contains("PAC", text, "系统代理那条要写明它含 PAC、是解析器");
            Assert.Contains("CONNECT", text, "HTTP 代理那条要写明它用 CONNECT 打隧道");
            Assert.Contains("socks5h", text, "SOCKS5 那条要写明默认由代理解析主机名");
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
