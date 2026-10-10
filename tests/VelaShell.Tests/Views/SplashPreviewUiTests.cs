using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using SkiaSharp;
using VelaShell.Controls;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Services;
using VelaShell.Splash;
using VelaShell.ViewModels;
using SettingsView = VelaShell.Views.SettingsView;

namespace VelaShell.Tests.Views;

/// <summary>
/// 设置页的启动画面预览:真走一遍 Avalonia 的 Skia 渲染(自定义绘制在渲染线程上租用 Skia 画布),
/// 截帧看像素 —— 只断言控件存在的话,「控件在、画布没租到、什么都没画」能一路绿到底。
/// </summary>
[TestClass]
public sealed class SplashPreviewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SplashPreviewUiTests).Assembly);
    }

    [TestMethod]
    [DataRow("classic", "dark")]
    [DataRow("terminal", "light")]
    [DataRow("constellation", "nord")]
    [DataRow("prompt", "github-light")]
    [DataRow("mascot", "tokyo-night")]
    public void Preview_PaintsTheChosenStyleInTheChosenTheme(string style, string theme)
    {
        _session.Dispatch(() =>
        {
            SplashPreview preview = new() { SplashStyle = style, ThemeId = theme, Width = 600 };
            Window window = new()
            {
                Width = 640,
                Height = 380,
                Background = Brushes.Magenta,
                Content = new Border { Padding = new(0), Child = preview }
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.IsNotNull(frame);
            SaveForReview(frame, $"splash-preview-{style}-{theme}.png");

            SplashPalette palette = SplashPalette.Resolve(theme, null, systemPrefersDark: true);
            SKColor expected = style is SplashStyles.Terminal or SplashStyles.Prompt ? palette.Terminal : palette.Page;
            (byte r, byte g, byte b) = PixelAt(frame, (int)preview.Bounds.X + 3, (int)preview.Bounds.Y + 170);
            int distance = Math.Abs(r - expected.Red) + Math.Abs(g - expected.Green) + Math.Abs(b - expected.Blue);
            Assert.IsLessThanOrEqualTo(8, distance, $"预览贴边处应是主题底色 {expected},实际 #{r:X2}{g:X2}{b:X2}。");
            Assert.AreEqual(340, preview.Bounds.Height, 0.5, "预览按 600:340 的比例布局。");
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AppearancePage_OffersTheSixChoicesWithALivePreview()
    {
        _session.Dispatch(() =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            SettingsViewModel viewModel = new(settings, Substitute.For<IThemeService>());
            SettingsView window = new() { DataContext = viewModel, Width = 1000, Height = 1400 };
            window.Show();
            viewModel.SelectSection(SettingsSectionKey.Appearance);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            SplashPreview? preview = window.GetVisualDescendants().OfType<SplashPreview>().SingleOrDefault();
            Assert.IsNotNull(preview, "外观页上没有启动画面预览。");
            Assert.AreEqual(OperatingSystem.IsWindows(), preview.IsEffectivelyVisible, "启动画面只在 Windows 上有,设置项也只在 Windows 上出现。");
            if (OperatingSystem.IsWindows())
            {
                ComboBox styles = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(box => box.ItemCount == SplashStyles.All.Count && box.SelectedIndex == viewModel.SplashStyleIndex
                                   && box.IsEffectivelyVisible && box.GetVisualAncestors().Contains(preview.GetVisualParent()!));
                Assert.AreEqual(0, styles.SelectedIndex);
                Assert.AreEqual(SplashStyles.Classic, preview.SplashStyle);

                preview.BringIntoView();
                Dispatcher.UIThread.RunJobs();
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                Assert.IsNotNull(frame);
                SaveForReview(frame, "settings-appearance-splash.png");
            }
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SettingsIndex_MapsToStylesInDropdownOrder()
    {
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>());

        Assert.AreEqual(0, vm.SplashStyleIndex, "出厂是经典样式,排在第一个。");
        Assert.IsTrue(vm.ShowSplashPreview);

        vm.SplashStyleIndex = 2;
        Assert.AreEqual(SplashStyles.Constellation, vm.Appearance.SplashStyle);

        vm.SplashStyleIndex = SplashStyles.All.Count - 1;
        Assert.AreEqual(SplashStyles.None, vm.Appearance.SplashStyle);
        Assert.IsFalse(vm.ShowSplashPreview, "选了「不显示」就收起预览。");

        vm.SplashStyleIndex = -1;
        Assert.AreEqual(SplashStyles.None, vm.Appearance.SplashStyle, "-1 是换语言时下拉清空选中项,不算用户的选择。");
    }

    private static (byte R, byte G, byte B) PixelAt(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        int pixel = Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + x * 4);
        byte c0 = (byte)(pixel & 0xFF);
        byte c1 = (byte)((pixel >> 8) & 0xFF);
        byte c2 = (byte)((pixel >> 16) & 0xFF);
        return buffer.Format == PixelFormat.Rgba8888 ? (c0, c1, c2) : (c2, c1, c0);
    }

    private static void SaveForReview(WriteableBitmap frame, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }
}
