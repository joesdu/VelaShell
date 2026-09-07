using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace VelaShell.Tests.Views;

/// <summary>
/// 浮层外壳(<see cref="FlyoutPresenter" />)必须走令牌。
/// <para>
/// Fluent 默认的 `FlyoutPresenterBackground` 是一个写死的近黑色,**不在**本项目的令牌体系里:
/// 九套主题换来换去它岿然不动,亮色主题下就是一块突兀的黑砖(用户反馈:"纯黑色背景有点太丑,
/// 和整体主题不太搭")。状态栏后台任务浮层的内容是裸 StackPanel,外观全靠这层 presenter,
/// 首当其冲。
/// </para>
/// <para>
/// 顺带钉住两条样式的**先后**:`.bare` 必须排在基础样式之后才盖得住它 —— 同优先级的样式
/// 按声明顺序后者胜出,写反了会话树与快捷命令那两个浮层就会变回"两层边"。
/// </para>
/// </summary>
[TestClass]
[TestCategory("DialogButtonStyle")]
public sealed class FlyoutPresenterStyleTests
{
    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FlyoutPresenterStyleTests).Assembly);

    [TestMethod]
    public void PlainFlyout_TakesItsChromeFromTheThemeTokens()
    {
        OnUi(() =>
        {
            var presenter = new FlyoutPresenter { Content = new TextBlock { Text = "后台任务" } };

            WithWindow(presenter, () =>
            {
                Assert.AreEqual(Brush("VelaBgSurface"), presenter.Background,
                    "浮层底色必须是令牌,否则换主题它纹丝不动。");
                Assert.AreEqual(Brush("VelaBorderSecondary"), presenter.BorderBrush);
                Assert.AreEqual(new Thickness(1), presenter.BorderThickness);
                Assert.AreEqual(new CornerRadius(6), presenter.CornerRadius,
                    "与右键菜单(MenuFlyoutPresenter)同一个圆角,两者看起来才是同一个产品。");
                // Fluent 给了 MinWidth 下限,窄浮层会被平白撑宽。
                Assert.AreEqual(0d, presenter.MinWidth);
            });
        });
    }

    [TestMethod]
    public void BareFlyout_StaysStrippedSoTheContentBorderIsTheOnlyEdge()
    {
        OnUi(() =>
        {
            var presenter = new FlyoutPresenter { Content = new TextBlock { Text = "会话树" } };
            presenter.Classes.Add("bare");

            WithWindow(presenter, () =>
            {
                Assert.AreEqual(Brushes.Transparent, presenter.Background,
                    "bare 必须盖住基础样式 —— 盖不住就是内容 Border 外面再套一圈框。");
                Assert.AreEqual(new Thickness(0), presenter.BorderThickness);
                Assert.AreEqual(new CornerRadius(0), presenter.CornerRadius);
                Assert.AreEqual(new Thickness(0), presenter.Padding);
            });
        });
    }

    private static void WithWindow(Control content, Action body)
    {
        var window = new Window { Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            body();
        }
        finally
        {
            window.Close();
        }
    }

    private static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out object? value) && value is IBrush brush
            ? brush
            : throw new AssertFailedException($"画刷令牌 {key} 不存在");

    private static void OnUi(Action body) =>
        _session.Dispatch(
            () =>
            {
                body();
                return Task.CompletedTask;
            },
            CancellationToken.None).GetAwaiter().GetResult();
}
