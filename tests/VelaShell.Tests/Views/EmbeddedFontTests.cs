using Avalonia.Headless;
using Avalonia.Media;

namespace VelaShell.Tests.Views;

/// <summary>
/// 内置字体资源接线回归:Cascadia Mono(终端默认)必须能从 fonts:VelaShell 集合解析出字形。
/// 守住三件易碎品:csproj 的 AvaloniaResource 包含、TTF 内部族名与代码引用串一致、
/// EmbeddedFontCollection 的 URI 注册。
/// </summary>
[TestClass]
[TestCategory("EmbeddedFonts")]
public sealed class EmbeddedFontTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(EmbeddedFontTests).Assembly);

    [TestMethod]
    public void BundledFamilies_ResolveWithExpectedGlyphs()
    {
        _session.Dispatch(() =>
        {
            // (族名, 代表性字符):字符必须由该字体自身提供,证明解析没有静默回退到系统字体。
            (string Family, char Probe)[] cases =
            [
                ("Cascadia Mono", 'A')
            ];
            foreach ((string family, char probe) in cases)
            {
                var typeface = new Typeface(new FontFamily($"fonts:VelaShell#{family}"));
                Assert.IsTrue(
                    FontManager.Current.TryGetGlyphTypeface(typeface, out GlyphTypeface? glyph),
                    $"内置字体族 '{family}' 应可从 fonts:VelaShell 集合解析。");
                Assert.IsTrue(
                    glyph!.FamilyName.StartsWith(family, StringComparison.OrdinalIgnoreCase),
                    $"'{family}' 解析到的族名是 '{glyph.FamilyName}'——引用串与 TTF 内部族名不一致。");
                Assert.IsTrue(
                    glyph.CharacterToGlyphMap.TryGetGlyph(probe, out _),
                    $"'{family}' 应含有字符 '{probe}' 的字形。");
            }

            // 终端加粗走独立静态字重(而非合成加粗):Bold 请求必须命中 Bold 面。
            var bold = new Typeface(new FontFamily("fonts:VelaShell#Cascadia Mono"), FontStyle.Normal, FontWeight.Bold);
            Assert.IsTrue(FontManager.Current.TryGetGlyphTypeface(bold, out GlyphTypeface? boldGlyph));
            Assert.AreEqual(FontWeight.Bold, boldGlyph!.Weight, "Cascadia Mono 的加粗应解析到内置 Bold 静态字重。");
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
