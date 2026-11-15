using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>
/// 设置导出文件:只带勾选的分类、机密一律不写;导入只覆盖文件里带着且勾上的分类,旧的整份导出照旧能导。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public sealed class SettingsTransferTests
{
    private const string ProxyPassword = "hunter2-should-never-leave-this-machine";
    private const string WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=s3cr3t-token";

    private static readonly DateTime ExportedAt = new(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] AllIds = [.. SettingsCategories.All.Select(c => c.Id)];

    [TestMethod]
    public void Export_WritesOnlyTheSelectedCategories_AndAHeader()
    {
        string json = SettingsTransfer.Export(Configured(), [SettingsCategories.Appearance, SettingsCategories.Terminal], ExportedAt);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.AreSequenceEqual(
            [SettingsTransfer.HeaderProperty, "theme", "accentColor", "terminalFont", "terminalFontSize", "scrollbackLines",
             "terminalType", "terminalEncoding", "appearance", "terminalBehavior"],
            [.. root.Select(p => p.Key)],
            SequenceOrder.InAnyOrder);
        JsonObject header = root[SettingsTransfer.HeaderProperty]!.AsObject();
        Assert.AreEqual(SettingsTransfer.FormatId, (string?)header["format"]);
        Assert.AreEqual(SettingsTransfer.CurrentVersion, (int?)header["version"]);
        Assert.AreSequenceEqual(["appearance", "terminal"], [.. header["categories"]!.AsArray().Select(n => (string?)n)]);
        Assert.DoesNotContain("proxy.example.com", json, "没勾代理,代理的地址就不该在文件里");
    }

    [TestMethod]
    public void Export_NeverWritesSecrets_EvenWhenTheirCategoriesAreTicked()
    {
        string json = SettingsTransfer.Export(Configured(), AllIds, ExportedAt);

        Assert.DoesNotContain(ProxyPassword, json, "代理密码被写进导出文件了");
        Assert.DoesNotContain("s3cr3t-token", json, "Webhook 地址(本身就是令牌)被写进导出文件了");
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.IsFalse(root["proxy"]!.AsObject().ContainsKey("password"));
        Assert.IsFalse(root["security"]!.AsObject().ContainsKey("webhookUrl"));
        Assert.AreEqual("proxy.example.com", (string?)root["proxy"]!["host"], "勾了代理,地址照常写");
    }

    [TestMethod]
    public void Export_LeavesOutRuntimeState_AndDoesNotTouchTheSource()
    {
        AppSettings settings = Configured();
        string json = SettingsTransfer.Export(settings, AllIds, ExportedAt);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.IsFalse(root["appearance"]!.AsObject().ContainsKey("lastWindowWidth"), "窗口尺寸是运行状态,不是设置");
        Assert.IsFalse(root["appearance"]!.AsObject().ContainsKey("aiPanelWidth"));
        Assert.IsFalse(root["appearance"]!.AsObject().ContainsKey("aiPanelPinned"));
        Assert.IsFalse(root["general"]!.AsObject().ContainsKey("lastOpenProfileIds"));
        Assert.IsTrue(root["security"]!.AsObject().ContainsKey("recordingOptInMigrated"), "迁移标记要带着,否则导入时会再迁移一遍");
        Assert.AreEqual(ProxyPassword, settings.Proxy.Password, "抹的应是副本");
        Assert.AreEqual(WebhookUrl, settings.Security.WebhookUrl);
    }

    [TestMethod]
    public void Import_ReplacesOnlyTheTickedCategoriesThatTheFileCarries()
    {
        AppSettings source = Configured();
        source.Theme = "light";
        source.TerminalFontSize = 20;
        source.General.ConnectTimeoutSeconds = 42;
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(source, [SettingsCategories.Appearance, SettingsCategories.General], ExportedAt))!;
        Assert.AreSequenceEqual([SettingsCategories.General, SettingsCategories.Appearance], [.. file.Categories.Select(c => c.Id)]);
        Assert.IsFalse(file.IsLegacy);

        var local = new AppSettings { Theme = "dark", TerminalFontSize = 13 };
        local.General.ConnectTimeoutSeconds = 7;
        SettingsImportMerge merge = SettingsTransfer.Merge(local, file, [SettingsCategories.Appearance, SettingsCategories.Terminal]);

        Assert.AreSequenceEqual([SettingsCategories.Appearance], [.. merge.Applied.Select(c => c.Id)], "终端不在文件里,常规没勾");
        Assert.AreEqual("light", merge.Settings.Theme);
        Assert.AreEqual(13, merge.Settings.TerminalFontSize, "文件里没带终端,本机的字号不动");
        Assert.AreEqual(7, merge.Settings.General.ConnectTimeoutSeconds, "常规没勾,不覆盖");
        Assert.AreEqual(13, local.TerminalFontSize, "合并不改传进来的那份");
    }

    [TestMethod]
    public void Import_KeepsLocalRuntimeState()
    {
        AppSettings source = Configured();
        source.Appearance.LastWindowWidth = 640;
        // 手写的文件(或别的工具写的)里也可能带着窗口尺寸:照样不采用。
        string json = JsonSerializer.Serialize(new { appearance = new { lastWindowWidth = 640, uiFontSize = 15, aiPanelWidth = 480, aiPanelPinned = true } });
        var local = new AppSettings();
        local.Appearance.LastWindowWidth = 1600;
        local.Appearance.AiPanelWidth = 540;
        local.Appearance.AiPanelPinned = false;

        SettingsImportMerge merge = SettingsTransfer.Merge(local, SettingsTransfer.Parse(json)!, AllIds);

        Assert.AreEqual(1600, merge.Settings.Appearance.LastWindowWidth);
        Assert.AreEqual(540, merge.Settings.Appearance.AiPanelWidth);
        Assert.IsFalse(merge.Settings.Appearance.AiPanelPinned);
        Assert.AreEqual(15, merge.Settings.Appearance.UiFontSize);
    }

    [TestMethod]
    public void Import_OfALegacyFullExport_StillWorks()
    {
        // 本版本之前的导出:整份 AppSettings、camelCase、代理密码抹成空串、Webhook 地址原样带着。
        AppSettings old = Configured();
        old.Proxy.Password = string.Empty;
        old.General.ConnectTimeoutSeconds = 42;
        string json = JsonSerializer.Serialize(old, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

        SettingsImportFile file = SettingsTransfer.Parse(json)!;
        Assert.IsTrue(file.IsLegacy);
        Assert.AreSequenceEqual(AllIds, [.. file.Categories.Select(c => c.Id)], "旧的整份导出带着全部分类");

        SettingsImportMerge merge = SettingsTransfer.Merge(new AppSettings(), file, AllIds);
        Assert.AreEqual(42, merge.Settings.General.ConnectTimeoutSeconds);
        Assert.AreEqual(WebhookUrl, merge.Settings.Security.WebhookUrl, "文件里明写着的 Webhook 地址照用");
        Assert.IsTrue(merge.ProxyPasswordMissing, "代理要认证、密码没带、本机又对不上 —— 要重填");
        Assert.IsFalse(merge.WebhookUrlMissing);
    }

    [TestMethod]
    public void Import_KeepsTheLocalProxyPassword_OnlyForTheSameProxy()
    {
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(Configured(), [SettingsCategories.Proxy], ExportedAt))!;

        SettingsImportMerge same = SettingsTransfer.Merge(Configured(), file, [SettingsCategories.Proxy]);
        Assert.AreEqual(ProxyPassword, same.Settings.Proxy.Password, "同一台代理换了台机器:留着本机的密码");
        Assert.IsFalse(same.ProxyPasswordMissing);

        AppSettings elsewhere = Configured();
        elsewhere.Proxy.Host = "proxy.attacker.example";
        SettingsImportMerge other = SettingsTransfer.Merge(elsewhere, file, [SettingsCategories.Proxy]);
        Assert.AreEqual(string.Empty, other.Settings.Proxy.Password, "文件指向另一台代理:本机的密码不能发给它");
        Assert.AreEqual("proxy.example.com", other.Settings.Proxy.Host);
        Assert.IsTrue(other.ProxyPasswordMissing);
    }

    [TestMethod]
    public void Import_KeepsTheLocalWebhookUrl_WhenTheFileHasNone()
    {
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(Configured(), [SettingsCategories.Security], ExportedAt))!;

        SettingsImportMerge kept = SettingsTransfer.Merge(Configured(), file, [SettingsCategories.Security]);
        Assert.AreEqual(WebhookUrl, kept.Settings.Security.WebhookUrl);
        Assert.IsFalse(kept.WebhookUrlMissing);

        SettingsImportMerge fresh = SettingsTransfer.Merge(new AppSettings(), file, [SettingsCategories.Security]);
        Assert.IsTrue(fresh.Settings.Security.AlertWebhook);
        Assert.IsTrue(fresh.WebhookUrlMissing, "打开了 Webhook 告警却没有地址:要重填");
    }

    [TestMethod]
    public void Import_IgnoresCategoriesThatWereNotTicked_EvenIfTheyAreInTheFile()
    {
        AppSettings source = Configured();
        source.Security.ConfirmExternalLaunch = false;
        SettingsImportFile file = SettingsTransfer.Parse(SettingsTransfer.Export(source, AllIds, ExportedAt))!;

        SettingsImportMerge merge = SettingsTransfer.Merge(new AppSettings(), file, [SettingsCategories.Appearance]);

        Assert.IsTrue(merge.Settings.Security.ConfirmExternalLaunch, "安全审计没勾:文件关掉的确认不能跟着进来");
        Assert.AreEqual("none", merge.Settings.Proxy.Type);
    }

    [TestMethod]
    public void Import_ToleratesCaseCommentsAndNullBlocks()
    {
        const string json = """
            {
              // 手改过的文件
              "Theme": "light",
              "XServer": null,
              "General": { "ConnectTimeoutSeconds": 99, },
            }
            """;
        SettingsImportFile file = SettingsTransfer.Parse(json)!;
        Assert.AreSequenceEqual([SettingsCategories.General, SettingsCategories.Appearance, SettingsCategories.XServer],
            [.. file.Categories.Select(c => c.Id)]);

        var local = new AppSettings();
        local.XServer.DisplayNumber = 3;
        SettingsImportMerge merge = SettingsTransfer.Merge(local, file, AllIds);

        Assert.AreEqual("light", merge.Settings.Theme);
        Assert.AreEqual(99, merge.Settings.General.ConnectTimeoutSeconds);
        Assert.AreEqual(new XServerOptions().DisplayNumber, merge.Settings.XServer.DisplayNumber, "写成 null 的分块取默认值");
    }

    [TestMethod]
    public void Parse_RejectsThingsThatAreNotSettings()
    {
        Assert.IsNull(SettingsTransfer.Parse("{ not json"));
        Assert.IsNull(SettingsTransfer.Parse("null"));
        Assert.IsNull(SettingsTransfer.Parse("[1, 2]"));
        Assert.IsNull(SettingsTransfer.Parse("{}"), "一个分类都没有:以前会被当成一份全默认的设置照单全收");
        Assert.IsNull(SettingsTransfer.Parse("""{ "name": "my-app", "version": "1.0.0" }"""), "选错的 package.json");
        Assert.IsNull(SettingsTransfer.Parse("""{ "general": 5 }"""), "类型对不上");
    }

    // ———— 无端到端口令的云同步:推送前剔机密、拉取时留本机的(与导出 / 导入同一份名单、同一套规矩) ————

    /// <summary>名单里的每一项都不带,其余照旧;传进来的那份不动(它是本机正在用的设置)。</summary>
    [TestMethod]
    public void WithoutSecrets_DropsEverySecretField_AndKeepsTheRest()
    {
        AppSettings source = Configured();

        AppSettings stripped = SettingsTransfer.WithoutSecrets(source);

        JsonObject root = Node(stripped);
        JsonObject defaults = Node(new AppSettings());
        foreach ((string block, string field) in SettingsCategories.SecretFields)
        {
            Assert.IsTrue(JsonNode.DeepEquals(defaults[block]![field], root[block]![field]), $"{block}.{field} 应回到默认值");
        }
        Assert.AreEqual("proxy.example.com", stripped.Proxy.Host, "代理的地址账号不是机密,照常同步");
        Assert.AreEqual("vela", stripped.Proxy.Username);
        Assert.IsTrue(stripped.Security.AlertWebhook, "开关照带,只是地址不带");
        Assert.AreSequenceEqual(["ssh://root@10.0.0.1:22"], stripped.Security.TrustedExternalLaunchTargets);
        Assert.AreEqual(ProxyPassword, source.Proxy.Password, "不能改动本机那份");
        Assert.AreEqual(WebhookUrl, source.Security.WebhookUrl);
    }

    /// <summary>
    /// 守卫:名单里每一项拉取时都要有「没带就留本机的」规矩 —— 往 <see cref="SettingsCategories.SecretFields" /> 加了一项、
    /// 却没在 <see cref="SettingsTransfer.KeepLocalSecrets" /> 里补上,无口令同步拉一次就把本机填的那项清空。
    /// </summary>
    [TestMethod]
    public void KeepLocalSecrets_RestoresEverySecretField_ForTheSameProxy()
    {
        AppSettings local = Configured();
        AppSettings incoming = SettingsTransfer.WithoutSecrets(Configured());

        SettingsTransfer.KeepLocalSecrets(incoming, local);

        JsonObject kept = Node(incoming);
        JsonObject expected = Node(local);
        foreach ((string block, string field) in SettingsCategories.SecretFields)
        {
            Assert.IsTrue(JsonNode.DeepEquals(expected[block]![field], kept[block]![field]), $"{block}.{field} 没有留住本机的值");
        }
    }

    /// <summary>拉到的代理换了一台:本机密码不能交给它;Webhook 地址照旧留本机的。</summary>
    [TestMethod]
    public void KeepLocalSecrets_DropsTheProxyPassword_ForAnotherProxy()
    {
        AppSettings incoming = SettingsTransfer.WithoutSecrets(Configured());
        incoming.Proxy.Port = 1081;

        SettingsTransfer.KeepLocalSecrets(incoming, Configured());

        Assert.AreEqual(string.Empty, incoming.Proxy.Password);
        Assert.AreEqual(WebhookUrl, incoming.Security.WebhookUrl);
    }

    /// <summary>拉到的那份自己带着(端到端加密的同步、旧版本推上去的明文):照用,不拿本机的去顶。</summary>
    [TestMethod]
    public void KeepLocalSecrets_UsesTheSecretsTheIncomingCarries()
    {
        AppSettings incoming = Configured();
        incoming.Proxy.Password = "from-the-other-device";
        incoming.Security.WebhookUrl = "https://hooks.example.com/other";

        SettingsTransfer.KeepLocalSecrets(incoming, Configured());

        Assert.AreEqual("from-the-other-device", incoming.Proxy.Password);
        Assert.AreEqual("https://hooks.example.com/other", incoming.Security.WebhookUrl);
    }

    private static AppSettings Configured()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "proxy.example.com";
        settings.Proxy.Port = 1080;
        settings.Proxy.Username = "vela";
        settings.Proxy.Password = ProxyPassword;
        settings.Security.AlertWebhook = true;
        settings.Security.WebhookUrl = WebhookUrl;
        settings.Security.TrustedExternalLaunchTargets = ["ssh://root@10.0.0.1:22"];
        settings.Appearance.LastWindowWidth = 1280;
        settings.General.LastOpenProfileIds = [Guid.NewGuid()];
        settings.Normalize();
        return settings;
    }

    /// <summary>按导出文件的约定(camelCase)写成 JSON,拿名单里的分组 / 字段名去取值。</summary>
    private static JsonObject Node(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, SettingsTransfer.Options)!.AsObject();
}
