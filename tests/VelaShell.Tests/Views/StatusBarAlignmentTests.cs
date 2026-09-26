using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Controls.Controls;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 状态栏一排条目的垂直对齐:文字、分隔线、按钮、图标都得落在内容区(去掉 1px 顶边后高 23)的同一条中线上。
/// </summary>
/// <remarks>
/// 回归的是这样一个问题:普通 <see cref="TextBlock" /> 居中的是行框,混进中文(回退到系统字体)的文字块基线会沉下去,
/// 与纯英文块差出一两个像素;偶数高的分隔线与按钮在 23 高的内容区里也会偏半个像素。
/// 这里按几何量断言(基线与控件边界),不数像素 —— 与测试机上装了哪些中文字体无关。
/// </remarks>
[TestClass]
[TestCategory("StatusBarUI")]
public sealed class StatusBarAlignmentTests
{
    /// <summary>内容区中线:状态栏高 24,顶边 1px,余下 23px 的正中。</summary>
    private const double ContentCenter = 1 + (23 / 2.0);

    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StatusBarAlignmentTests).Assembly);

    [TestMethod]
    public void TextBlocks_ShareOneBaseline_WithCapsCenteredOnTheContentArea()
    {
        _session.Dispatch(() =>
        {
            (StatusBarView bar, Window window) = Show();
            CapCenteredTextBlock[] texts = [.. bar.GetVisualDescendants().OfType<CapCenteredTextBlock>().Where(t => t.IsEffectivelyVisible)];
            Assert.IsTrue(texts.Any(t => ContainsCjk(TextOf(t))), "用例得有混了中文的文字块,否则守不住回退字体那一类偏移。");
            Assert.IsTrue(texts.Any(t => !ContainsCjk(TextOf(t))), "用例得有纯英文的文字块作对照。");
            Assert.IsEmpty(
                bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t is not CapCenteredTextBlock),
                "状态栏上的文字都应走 CapCenteredTextBlock;普通 TextBlock 的基线会随回退字体漂移。");

            double? shared = null;
            foreach (CapCenteredTextBlock text in texts)
            {
                string label = TextOf(text);
                double baseline = BaselineIn(bar, text);
                shared ??= baseline;
                Assert.AreEqual(shared.Value, baseline, 1e-6, $"「{label}」的基线与同排其它文字不在一条线上。");
                Assert.AreEqual(Math.Round(baseline), baseline, 1e-6, $"「{label}」的基线没落在整像素上,字形边缘会发虚。");

                var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
                double capCenter = baseline - (CapCenteredTextBlock.CapHeight(typeface, text.FontSize) / 2);
                Assert.AreEqual(ContentCenter, capCenter, 0.5, $"「{label}」的大写字母中线偏离了内容区中线。");
            }
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void Dividers_Buttons_AndIcons_AreCenteredOnWholePixels()
    {
        _session.Dispatch(() =>
        {
            (StatusBarView bar, Window window) = Show();
            Control[] items =
            [
                .. bar.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("divider")),
                .. bar.GetVisualDescendants().OfType<Button>(),
                .. bar.GetVisualDescendants().OfType<LucideIcon>(),
                .. bar.GetVisualDescendants().OfType<CircularProgressRing>(),
            ];
            items = [.. items.Where(c => c.IsEffectivelyVisible)];
            Assert.IsTrue(items.OfType<CircularProgressRing>().Any(), "用例得让后台活动圆环露出来。");

            foreach (Control item in items)
            {
                Rect bounds = BoundsIn(bar, item);
                string label = $"{item.GetType().Name}({bounds})";
                Assert.AreEqual(ContentCenter, bounds.Center.Y, 1e-6, $"{label} 没有落在内容区中线上。");
                Assert.AreEqual(Math.Round(bounds.Top), bounds.Top, 1e-6, $"{label} 的上沿不在整像素上:23 高的内容区里要用奇数高。");
            }
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static (StatusBarView Bar, Window Window) Show()
    {
        var vm = new StatusBarViewModel
        {
            ConnectionInfo = "SSH • 生产机",
            Latency = "1ms",
            Uptime = "00:00:29",
            StatusText = "已连接",
            TerminalType = "xterm-256color",
            WindowSize = "308×84",
            Encoding = "UTF-8",
            Status = Strings.Connected,
        };
        vm.ApplyBackgroundActivities([new(1, "正在加载插件", "Redis Client", null)]);
        var bar = new StatusBarView { DataContext = vm };
        var window = new Window { Width = 1400, Content = bar, SizeToContent = SizeToContent.Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (bar, window);
    }

    /// <summary>文字首行基线在状态栏坐标里的位置(连同控件自己的平移)。</summary>
    private static double BaselineIn(Visual bar, CapCenteredTextBlock text)
    {
        TextLine line = text.TextLayout.TextLines[0];
        return text.TranslatePoint(new Point(0, text.Padding.Top + line.Baseline), bar)!.Value.Y;
    }

    private static Rect BoundsIn(Visual bar, Control control)
    {
        Point topLeft = control.TranslatePoint(default, bar)!.Value;
        return new Rect(topLeft, control.Bounds.Size);
    }

    private static string TextOf(TextBlock text) =>
        text.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(r => r.Text)) : text.Text ?? "";

    /// <summary>含 CJK 统一表意文字(U+4E00–U+9FFF)。</summary>
    private static bool ContainsCjk(string text) => text.Any(c => c is >= (char)0x4E00 and <= (char)0x9FFF);
}
