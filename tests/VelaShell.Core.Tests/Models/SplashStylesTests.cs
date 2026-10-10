using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

[TestClass]
public sealed class SplashStylesTests
{
    [TestMethod]
    public void FactoryDefault_IsClassic()
    {
        Assert.AreEqual(SplashStyles.Classic, new AppSettings().Appearance.SplashStyle);
    }

    [TestMethod]
    public void All_PutsClassicFirstAndNoneLast()
    {
        // 设置页下拉按这个顺序摆,SettingsViewModel.SplashStyleIndex 按它映射。
        Assert.AreEqual(SplashStyles.Classic, SplashStyles.All[0]);
        Assert.AreEqual(SplashStyles.None, SplashStyles.All[^1]);
        Assert.HasCount(6, SplashStyles.All);
    }

    [TestMethod]
    [DataRow("classic", "classic")]
    [DataRow("terminal", "terminal")]
    [DataRow("Constellation", "constellation")]
    [DataRow(" prompt ", "prompt")]
    [DataRow("MASCOT", "mascot")]
    [DataRow("none", "none")]
    [DataRow("", "classic")]
    [DataRow(null, "classic")]
    [DataRow("aurora", "classic")]
    public void Normalize_KeepsKnownStylesAndFallsBackToClassic(string? value, string expected)
    {
        Assert.AreEqual(expected, SplashStyles.Normalize(value));
    }

    [TestMethod]
    public void AppSettingsNormalize_ResetsUnknownStyle()
    {
        AppSettings settings = new();
        settings.Appearance.SplashStyle = "from-a-newer-version";

        settings.Normalize();

        Assert.AreEqual(SplashStyles.Classic, settings.Appearance.SplashStyle);
    }

    [TestMethod]
    public void AppSettingsNormalize_KeepsOff()
    {
        AppSettings settings = new();
        settings.Appearance.SplashStyle = SplashStyles.None;

        settings.Normalize();

        Assert.AreEqual(SplashStyles.None, settings.Appearance.SplashStyle);
    }
}
