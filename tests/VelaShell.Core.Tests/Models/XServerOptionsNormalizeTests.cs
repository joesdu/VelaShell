using System.Text.Json;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;

namespace VelaShell.Core.Tests.Models;

/// <summary>X Server 设置的载入规整:磁盘上的内容拦不住,认不出来的回落到默认。</summary>
[TestClass]
[TestCategory("XServer")]
public class XServerOptionsNormalizeTests
{
    /// <summary>老配置里没有 XServer 这一节:反序列化后是默认值,不是 null。</summary>
    [TestMethod]
    public void LegacyConfig_WithoutSection_GetsDefaults()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""{"language":"en"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        settings.Normalize();

        Assert.IsNotNull(settings.XServer);
        Assert.AreEqual(XServerWindowModes.MultiWindow, settings.XServer.WindowMode);
        Assert.AreEqual(XServerOptions.AutoDisplayNumber, settings.XServer.DisplayNumber);
    }

    /// <summary>JSON 里显式写了 null 也一样补上。</summary>
    [TestMethod]
    public void ExplicitNullSection_IsReplaced()
    {
        AppSettings settings = new() { XServer = null! };

        settings.Normalize();

        Assert.IsNotNull(settings.XServer);
    }

    [TestMethod]
    public void UnknownWindowMode_FallsBackToMultiWindow()
    {
        AppSettings settings = new();
        settings.XServer.WindowMode = "tabbed";

        settings.Normalize();

        Assert.AreEqual(XServerWindowModes.MultiWindow, settings.XServer.WindowMode);
    }

    [TestMethod]
    [DataRow(-5, XServerOptions.AutoDisplayNumber)]
    [DataRow(99, XServerOptions.MaxDisplayNumber)]
    [DataRow(7, 7)]
    public void DisplayNumber_IsClamped(int stored, int expected)
    {
        AppSettings settings = new();
        settings.XServer.DisplayNumber = stored;

        settings.Normalize();

        Assert.AreEqual(expected, settings.XServer.DisplayNumber);
    }
}
