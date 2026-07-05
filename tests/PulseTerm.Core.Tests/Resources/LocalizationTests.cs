using System.Globalization;
using PulseTerm.Core.Localization;
using PulseTerm.Core.Resources;

namespace PulseTerm.Core.Tests.Resources;

[TestClass]
[TestCategory("i18n")]
public class LocalizationTests : IDisposable
{
    private readonly CultureInfo _originalCulture;

    public LocalizationTests()
    {
        _originalCulture = CultureInfo.CurrentUICulture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _originalCulture;
        CultureInfo.CurrentCulture = _originalCulture;
    }

    [TestMethod]
    public void DefaultCulture_ReturnsEnglishStrings()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");

        Assert.AreEqual("Quick Connect", Strings.QuickConnect);
        Assert.AreEqual("Recent Connections", Strings.RecentConnections);
        Assert.AreEqual("Settings", Strings.Settings);
        Assert.AreEqual("New Tab", Strings.NewTab);
        Assert.AreEqual("Connect", Strings.Connect);
    }

    [TestMethod]
    public void ChineseCulture_ReturnsChineseStrings()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");

        Assert.AreEqual("快速连接", Strings.QuickConnect);
        Assert.AreEqual("最近连接", Strings.RecentConnections);
        Assert.AreEqual("设置", Strings.Settings);
        Assert.AreEqual("新标签页", Strings.NewTab);
        Assert.AreEqual("连接", Strings.Connect);
    }

    [TestMethod]
    public void LocalizationService_GetString_ReturnsEnglishByDefault()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        var service = new LocalizationService();

        Assert.AreEqual("Quick Connect", service.GetString("QuickConnect"));
        Assert.AreEqual("Server Groups", service.GetString("ServerGroups"));
        Assert.AreEqual("Disconnect", service.GetString("Disconnect"));
    }

    [TestMethod]
    public void LocalizationService_GetString_ReturnsChineseForZhCN()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        var service = new LocalizationService();

        Assert.AreEqual("快速连接", service.GetString("QuickConnect"));
        Assert.AreEqual("服务器分组", service.GetString("ServerGroups"));
        Assert.AreEqual("断开", service.GetString("Disconnect"));
    }

    [TestMethod]
    public void LocalizationService_GetString_MissingKey_ReturnsKeyName()
    {
        var service = new LocalizationService();

        Assert.AreEqual("NonExistentKey", service.GetString("NonExistentKey"));
    }

    [TestMethod]
    public void LocalizationService_CurrentLanguage_ReturnsCurrentUICulture()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        var service = new LocalizationService();

        Assert.AreEqual("en", service.CurrentLanguage);

        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        Assert.AreEqual("zh-CN", service.CurrentLanguage);
    }

    [TestMethod]
    public void LocalizationService_SetLanguage_ChangesCurrentCulture()
    {
        var service = new LocalizationService();

        service.SetLanguage("zh-CN");
        Assert.AreEqual("zh-CN", CultureInfo.CurrentUICulture.Name);
        Assert.AreEqual("zh-CN", service.CurrentLanguage);

        service.SetLanguage("en");
        Assert.AreEqual("en", CultureInfo.CurrentUICulture.Name);
        Assert.AreEqual("en", service.CurrentLanguage);
    }

    [TestMethod]
    public void AllRequiredStrings_ExistInEnglishResx()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en");

        Assert.IsFalse(string.IsNullOrEmpty(Strings.QuickConnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RecentConnections));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ServerGroups));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Settings));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Notifications));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.CloseTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Search));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Copy));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Split));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Broadcast));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.SyncGroup));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.FileName));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Size));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Permissions));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Modified));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Upload));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Download));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Refresh));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RemoteForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalPort));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RemoteAddress));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTunnel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ActiveTunnels));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.SearchCommands));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.SystemMonitor));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Network));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Docker));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Custom));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connecting));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Latency));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Save));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Cancel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Delete));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Edit));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.OK));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Error));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Warning));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Language));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Theme));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Font));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.FontSize));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ScrollbackLines));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Password));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.PrivateKey));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Username));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Host));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Port));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.HostKeyVerification));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.TrustThisHost));
    }

    [TestMethod]
    public void AllRequiredStrings_ExistInChineseResx()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");

        Assert.IsFalse(string.IsNullOrEmpty(Strings.QuickConnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RecentConnections));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ServerGroups));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Settings));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Notifications));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.CloseTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Search));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Copy));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Split));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Broadcast));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.SyncGroup));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.FileName));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Size));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Permissions));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Modified));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Upload));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Download));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Refresh));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RemoteForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalPort));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RemoteAddress));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTunnel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ActiveTunnels));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.SearchCommands));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.SystemMonitor));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Network));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Docker));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Custom));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connecting));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Latency));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Save));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Cancel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Delete));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Edit));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.OK));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Error));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Warning));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Language));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Theme));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Font));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.FontSize));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ScrollbackLines));

        Assert.IsFalse(string.IsNullOrEmpty(Strings.Password));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.PrivateKey));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Username));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Host));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Port));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.HostKeyVerification));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.TrustThisHost));
    }
}
