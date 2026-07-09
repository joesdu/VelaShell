using System.Globalization;
using VelaShell.App.ViewModels;
using VelaShell.Core.Localization;
using VelaShell.Core.Services;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.App.Tests.Integration;

[TestClass]
public class HeadlessUiTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public void MainWindowViewModel_Initializes_WithAllSubViewModels()
    {
        var viewModel = new MainWindowViewModel();

        Assert.IsNotNull(viewModel.Sidebar);
        Assert.IsNotNull(viewModel.TabBar);
        Assert.IsNotNull(viewModel.StatusBar);
        Assert.IsNotNull(viewModel.OpenSettingsCommand);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void MainWindowViewModel_Sidebar_IsCorrectType()
    {
        var viewModel = new MainWindowViewModel();

        Assert.IsNotNull(viewModel.Sidebar);
        Assert.IsInstanceOfType(viewModel.Sidebar, typeof(SidebarViewModel));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void MainWindowViewModel_TabBar_IsCorrectType()
    {
        var viewModel = new MainWindowViewModel();

        Assert.IsNotNull(viewModel.TabBar);
        Assert.IsInstanceOfType(viewModel.TabBar, typeof(TabBarViewModel));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void MainWindowViewModel_StatusBar_IsCorrectType()
    {
        var viewModel = new MainWindowViewModel();

        Assert.IsNotNull(viewModel.StatusBar);
        Assert.IsInstanceOfType(viewModel.StatusBar, typeof(StatusBarViewModel));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ThemeService_SwitchToDark_AppliesCorrectly()
    {
        var themeService = new ThemeService("light");

        themeService.SetTheme("dark");

        Assert.AreEqual("dark", themeService.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ThemeService_SwitchToLight_AppliesCorrectly()
    {
        var themeService = new ThemeService("dark");

        themeService.SetTheme("light");

        Assert.AreEqual("light", themeService.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ThemeService_RoundTrip_MaintainsState()
    {
        var themeService = new ThemeService("dark");
        var events = new List<string>();
        themeService.ThemeChanged += name => events.Add(name);

        themeService.SetTheme("light");
        themeService.SetTheme("dark");
        themeService.SetTheme("light");

        Assert.AreEqual("light", themeService.CurrentTheme);
        Assert.AreEqual(3, events.Count());
        CollectionAssert.AreEqual(new List<string> { "light", "dark", "light" }, events);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void LocalizationService_DefaultLanguage_ReturnsEnglishStrings()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");

            var service = new LocalizationService();

            Assert.AreEqual("en", service.CurrentLanguage);
            var appName = service.GetString("AppName");
            Assert.IsFalse(string.IsNullOrEmpty(appName));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void LocalizationService_SetLanguageChinese_ChangesCurrentLanguage()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            var service = new LocalizationService();

            service.SetLanguage("zh-CN");

            Assert.AreEqual("zh-CN", service.CurrentLanguage);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void LocalizationService_SwitchLanguage_RoundTrip()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            var service = new LocalizationService();

            service.SetLanguage("zh-CN");
            Assert.AreEqual("zh-CN", service.CurrentLanguage);

            service.SetLanguage("en");
            Assert.AreEqual("en", service.CurrentLanguage);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void LocalizationService_MissingKey_ReturnsKeyAsDefault()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var service = new LocalizationService();

            var result = service.GetString("NonExistentKey_XYZ_12345");

            Assert.AreEqual("NonExistentKey_XYZ_12345", result);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
