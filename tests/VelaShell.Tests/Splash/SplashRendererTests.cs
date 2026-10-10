using System.Globalization;
using SkiaSharp;
using VelaShell.Core.Models;
using VelaShell.Splash;
using VelaShell.Splash.Designs;

namespace VelaShell.Tests.Splash;

/// <summary>
/// 五套启动画面样式在不同主题、不同时刻下都画得出来,而且画在该画的地方。
/// </summary>
/// <remarks>
/// 渲染进一块光栅画布再看像素:卡片圆角外必须透明(分层窗口靠它露出投影、穿透点击),
/// 卡片内贴边处必须是该样式的底色(说明配色确实来自所选主题),内容区必须真画了东西。
/// 设 <c>VELASHELL_VISUAL_QA_DIR</c> 时把每一张都存成 PNG,方便人眼过一遍。
/// </remarks>
[TestClass]
public sealed class SplashRendererTests
{
    private static readonly string[] Themes = ["dark", "light", "tokyo-night", "github-light", "sakura"];

    private static readonly double[] Moments = [0, 200, 700, 2000, 4500];

    public static IEnumerable<object[]> StyleAndTheme =>
        from style in SplashStyles.All
        where style != SplashStyles.None
        from theme in Themes
        select new object[] { style, theme };

    [TestMethod]
    [DynamicData(nameof(StyleAndTheme))]
    public void EveryStyle_RendersInEveryTheme(string style, string theme)
    {
        SplashPalette palette = SplashPalette.Resolve(theme, null, systemPrefersDark: true);
        using SplashRenderer renderer = CreateRenderer(style, palette);
        SplashSimulation simulation = new();

        foreach (double moment in Moments)
        {
            using SKBitmap bitmap = Render(renderer, simulation.At(moment));

            Assert.AreEqual(0, bitmap.GetPixel(0, 0).Alpha, $"{style}/{theme}@{moment}: 圆角外应当透明。");
            SKColor expected = style is SplashStyles.Terminal or SplashStyles.Prompt ? palette.Terminal : palette.Page;
            AssertClose(expected, bitmap.GetPixel(3, 170), $"{style}/{theme}@{moment}: 贴边处应是主题底色。");
            Assert.IsGreaterThan(120, CountInk(bitmap, expected), $"{style}/{theme}@{moment}: 内容区几乎什么都没画。");
            SaveForReview(bitmap, $"splash-{style}-{theme}-{moment:0}.png");
        }
    }

    [TestMethod]
    public void NoneAndUnknownStyles_FallBackToClassic()
    {
        SplashPalette palette = SplashPalette.Resolve("dark", null, true);
        using SplashRenderer none = CreateRenderer(SplashStyles.None, palette);
        using SplashRenderer unknown = CreateRenderer("hologram", palette);

        Assert.AreEqual(SplashStyles.None, none.Style);
        Assert.AreEqual(SplashStyles.Classic, unknown.Style);
        using SKBitmap bitmap = Render(none, new SplashSimulation().At(500));
        AssertClose(palette.Page, bitmap.GetPixel(3, 170), "「不显示」不会走到渲染;真走到了也按经典样式画。");
    }

    [TestMethod]
    public void Palette_FollowsSystemAndAccentOverride()
    {
        Assert.AreEqual(SplashPalette.From(UiThemeCatalog.DefaultDark, null), SplashPalette.Resolve("system", null, true));
        Assert.AreEqual(SplashPalette.From(UiThemeCatalog.DefaultLight, null), SplashPalette.Resolve("system", null, false));
        Assert.AreEqual(SplashPalette.From(UiThemeCatalog.DefaultDark, null), SplashPalette.Resolve("no-such-theme", "", true));

        SplashPalette custom = SplashPalette.Resolve("nord", "#FF8800", true);
        Assert.AreEqual(new SKColor(0xFF, 0x88, 0x00), custom.Accent);
        Assert.AreEqual(SplashPalette.Resolve("nord", null, true).Accent, SplashPalette.Resolve("nord", "not a color", true).Accent);
    }

    [TestMethod]
    public void ClassicDots_SlowDownInTheMiddleAndFadeAtTheEnds()
    {
        (double start, double startOpacity) = ClassicSplash.DotAt(0);
        (double end, double endOpacity) = ClassicSplash.DotAt(0.9999);
        Assert.IsLessThan(0, start);
        Assert.IsGreaterThan(1, end);
        Assert.AreEqual(0, startOpacity, 1e-9);
        Assert.AreEqual(0, endOpacity, 1e-2);

        double middleSpeed = ClassicSplash.DotAt(0.55).Position - ClassicSplash.DotAt(0.45).Position;
        double edgeSpeed = ClassicSplash.DotAt(0.25).Position - ClassicSplash.DotAt(0.15).Position;
        Assert.IsLessThan(edgeSpeed, middleSpeed, "中段应当比两头慢。");
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("ja-JP")]
    [DataRow("ko-KR")]
    public void Strings_ResolveInEveryLanguage(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            SplashStrings strings = SplashStrings.Load();

            string[] all = [.. strings.Stages, .. strings.MascotLines, strings.Starting, strings.Ready, strings.Tagline,
                            strings.ConstellationName, strings.MascotHello, strings.MascotReady];
            Assert.IsFalse(all.Any(s => s.StartsWith("Splash_", StringComparison.Ordinal)), "有文案没取到,显示成了键名。");
            Assert.StartsWith("v", strings.Version);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static SplashRenderer CreateRenderer(string style, SplashPalette palette) =>
        SplashRenderer.Create(style, palette, SplashStrings.Load(), SplashResources.Create(CultureInfo.GetCultureInfo("zh-CN")));

    private static SKBitmap Render(SplashRenderer renderer, SplashFrame frame)
    {
        SKBitmap bitmap = new(new SKImageInfo((int)SplashRenderer.Width, (int)SplashRenderer.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Transparent);
        renderer.Render(canvas, frame);
        canvas.Flush();
        return bitmap;
    }

    private static int CountInk(SKBitmap bitmap, SKColor background)
    {
        int count = 0;
        for (int y = 10; y < bitmap.Height - 10; y += 2)
        {
            for (int x = 10; x < bitmap.Width - 10; x += 2)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.Red - background.Red) + Math.Abs(pixel.Green - background.Green) + Math.Abs(pixel.Blue - background.Blue) > 30)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static void AssertClose(SKColor expected, SKColor actual, string message)
    {
        int distance = Math.Abs(expected.Red - actual.Red) + Math.Abs(expected.Green - actual.Green) + Math.Abs(expected.Blue - actual.Blue);
        Assert.IsLessThanOrEqualTo(6, distance, $"{message} 期望 {expected},实际 {actual}");
    }

    private static void SaveForReview(SKBitmap bitmap, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        data.SaveTo(output);
    }
}
