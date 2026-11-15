using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 代理默认值与它的一次性迁移标记。
/// </summary>
/// <remarks>
/// 默认是 <c>none</c>(维护者 2026-11-15 定):不配置就直连,不隐式跟随系统代理。
/// 旧版本的迁移会把落盘的 none 抬成 system,现在迁移只打标记、不再改写类型。
/// </remarks>
[TestClass]
[TestCategory("DataStore")]
public class ProxyDefaultsTests
{
    [TestMethod]
    public void FreshSettings_ConnectDirectly() => Assert.AreEqual("none", new AppSettings().Proxy.Type);

    [TestMethod]
    public void NullType_FallsBackToDirect()
    {
        var options = new ProxyOptions { Type = null! };

        Assert.AreEqual("none", options.Type);
    }

    [TestMethod]
    public void LegacyNone_IsKeptAndOnlyMarkedMigrated()
    {
        // 老配置:落盘的是 none,而且没有迁移标记 —— 不再被抬成 system
        var legacy = new AppSettings();
        legacy.Proxy.Type = "none";
        legacy.Proxy.DefaultsMigrated = false;

        legacy.Normalize();

        Assert.AreEqual("none", legacy.Proxy.Type);
        Assert.IsTrue(legacy.Proxy.DefaultsMigrated);
    }

    [TestMethod]
    public void SavedSystem_SurvivesEveryLaterLoad()
    {
        // 已落盘的 system 分不清是旧默认还是用户所选,改默认值不能把它改掉
        var settings = new AppSettings();
        settings.Proxy.Type = "system";
        settings.Proxy.DefaultsMigrated = false;

        settings.Normalize();
        settings.Normalize();

        Assert.AreEqual("system", settings.Proxy.Type);
    }

    [TestMethod]
    public void MigrationLeavesAnExplicitProxyAlone()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "127.0.0.1";
        settings.Proxy.Port = 7890;
        settings.Proxy.DefaultsMigrated = false;

        settings.Normalize();

        Assert.AreEqual("socks5", settings.Proxy.Type);
        Assert.AreEqual(7890, settings.Proxy.Port);
        Assert.IsTrue(settings.Proxy.DefaultsMigrated);
    }

    [TestMethod]
    public void MigrationFlagRoundTripsThroughJson()
    {
        var settings = new AppSettings();
        settings.Normalize();
        settings.Proxy.Type = "system";

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        AppSettings reloaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, options), options)!;
        reloaded.Normalize();

        Assert.IsTrue(reloaded.Proxy.DefaultsMigrated);
        Assert.AreEqual("system", reloaded.Proxy.Type);
    }
}
