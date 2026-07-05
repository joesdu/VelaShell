using PulseTerm.Core.Services;

namespace PulseTerm.App.Tests.Services;

[TestClass]
public class ThemeServiceSwitchTests
{
    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_Light_ChangesCurrentTheme()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("light");

        Assert.AreEqual("light", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_Light_FiresThemeChangedEvent()
    {
        var sut = new ThemeService("dark");
        string? received = null;
        sut.ThemeChanged += name => received = name;

        sut.SetTheme("light");

        Assert.AreEqual("light", received);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_Dark_SwitchesBackFromLight()
    {
        var sut = new ThemeService("light");

        sut.SetTheme("dark");

        Assert.AreEqual("dark", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_SameTheme_DoesNotFireEvent()
    {
        var sut = new ThemeService("dark");
        var fired = false;
        sut.ThemeChanged += _ => fired = true;

        sut.SetTheme("dark");

        Assert.IsFalse(fired);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_InvalidTheme_ThrowsArgumentException()
    {
        var sut = new ThemeService("dark");

        var act = () => sut.SetTheme("ocean");

        Assert.ThrowsExactly<ArgumentException>(act);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_CaseInsensitive_AcceptsUpperCase()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("Light");

        Assert.AreEqual("light", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_System_ChangesCurrentTheme()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("system");

        Assert.AreEqual("system", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void Constructor_DefaultsToValid_Theme()
    {
        var darkSut = new ThemeService("dark");
        Assert.AreEqual("dark", darkSut.CurrentTheme);

        var lightSut = new ThemeService("light");
        Assert.AreEqual("light", lightSut.CurrentTheme);

        var systemSut = new ThemeService("system");
        Assert.AreEqual("system", systemSut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void Constructor_InvalidTheme_DefaultsToDark()
    {
        var sut = new ThemeService("neon");

        Assert.AreEqual("dark", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void RoundTrip_DarkToLightToDark_AllEventsReceived()
    {
        var sut = new ThemeService("dark");
        var events = new List<string>();
        sut.ThemeChanged += name => events.Add(name);

        sut.SetTheme("light");
        sut.SetTheme("dark");
        sut.SetTheme("light");

        CollectionAssert.AreEqual(new List<string> { "light", "dark", "light" }, events);
    }
}
