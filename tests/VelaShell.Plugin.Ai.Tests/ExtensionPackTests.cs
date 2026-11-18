using System.IO.Compression;
using System.Text.Json;
using VelaShell.Plugin.Ai.Chat.Extensions;
using VelaShell.Plugin.Ai.Chat.Skills;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>扩展包(ai-plan §12.4):清单校验、MCP 映射与确认指纹、安装 / 卸载、生效规则、命令展开。</summary>
[TestClass]
public sealed class ExtensionPackTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ext-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static object SampleManifest(string id = "acme-ops", string command = "node") => new
    {
        id,
        name = "Acme Ops",
        version = "1.2.0",
        description = "Ops helpers",
        commands = new[] { new { name = "deploy", description = "Deploy a service", prompt = "Deploy {{args}} carefully." } },
        mcpServers = new object[]
        {
            new { name = "tickets", command, args = new[] { "server.js", "--label", "two words", "say \"hi\"", "" }, env = new Dictionary<string, string> { ["MODE"] = "ro" }, workingDirectory = "bin" },
            new { name = "remote", transport = "http", url = "https://mcp.example.com/sse", headers = new Dictionary<string, string> { ["X-Team"] = "ops" } }
        }
    };

    private string Pack(object manifest, string folder = "pack", bool withSkill = true)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, ExtensionManifest.FileName), JsonSerializer.Serialize(manifest));
        if (withSkill)
        {
            Directory.CreateDirectory(Path.Combine(dir, "skills", "incident"));
            File.WriteAllText(Path.Combine(dir, "skills", "incident", SkillCatalog.SkillFileName), "---\nname: incident\ndescription: Handle an incident.\n---\nPage the on-call.");
        }
        return dir;
    }

    [TestMethod]
    public void Load_RejectsInvalidManifests_WithAReason()
    {
        void Rejects(object manifest, string reason)
        {
            string dir = Pack(manifest, Guid.NewGuid().ToString("N"), withSkill: false);
            InvalidDataException ex = Assert.ThrowsExactly<InvalidDataException>(() => ExtensionManifest.Load(dir));
            StringAssert.Contains(ex.Message, reason);
        }

        Rejects(new { id = "Bad Id", name = "x", version = "1" }, "'id'");
        Rejects(new { id = "ok", version = "1" }, "'name'");
        Rejects(new { id = "ok", name = "x" }, "'version'");
        Rejects(new { id = "ok", name = "x", version = "1", skills = "../outside" }, "'skills'");
        Rejects(new { id = "ok", name = "x", version = "1", commands = new[] { new { name = "a", prompt = "p" }, new { name = "A", prompt = "q" } } }, "twice");
        Rejects(new { id = "ok", name = "x", version = "1", commands = new[] { new { name = "a", prompt = " " } } }, "prompt");
        Rejects(new { id = "ok", name = "x", version = "1", mcpServers = new[] { new { name = "s" } } }, "'command'");
        Rejects(new { id = "ok", name = "x", version = "1", mcpServers = new[] { new { name = "s", command = "node", workingDirectory = "../../etc" } } }, "inside the extension pack");
        Rejects(new { id = "ok", name = "x", version = "1", mcpServers = new[] { new { name = "s", transport = "http", url = "ftp://x" } } }, "http(s)");
        Rejects(new { id = "ok", name = "x", version = "1", mcpServers = new[] { new { name = "s", command = "node", env = new Dictionary<string, string> { ["A"] = "1\nB=2" } } } },
            "environment variable");
        string missing = Path.Combine(_root, "empty");
        Directory.CreateDirectory(missing);
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => ExtensionManifest.Load(missing)).Message, "not found");
    }

    [TestMethod]
    public void ToMcpServers_MapsToTheExistingConfigShape_AndArgumentsRoundTrip()
    {
        string dir = Pack(SampleManifest());
        var manifest = ExtensionManifest.Load(dir);
        List<McpServerConfig> servers = manifest.ToMcpServers(dir);

        McpServerConfig stdio = servers.Single(s => s.Id == "ext-acme-ops-tickets");
        Assert.AreEqual("acme-ops_tickets", stdio.Name, "工具名前缀带上扩展包 id");
        Assert.AreEqual(McpTransportType.Stdio, stdio.Transport);
        Assert.AreSequenceEqual(["server.js", "--label", "two words", "say \"hi\"", ""], McpConfigParser.SplitArguments(stdio.Arguments), "参数数组拼成单行后拆回来逐项相同");
        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "bin")), stdio.WorkingDirectory, "相对工作目录解析到扩展包里");
        Assert.AreEqual("ro", McpConfigParser.ParseEnvironmentLines(stdio.EnvironmentVariables)["MODE"]);

        McpServerConfig http = servers.Single(s => s.Id == "ext-acme-ops-remote");
        Assert.AreEqual(McpTransportType.Http, http.Transport);
        Assert.AreEqual("ops", McpConfigParser.ParseHeaderLines(http.Headers)["X-Team"]);
    }

    [TestMethod]
    public void McpFingerprint_CoversLaunchDetailsOnly()
    {
        string a = ExtensionManifest.Load(Pack(SampleManifest(), "a")).McpFingerprint();
        string same = ExtensionManifest.Load(Pack(new
        {
            id = "acme-ops", name = "Renamed", version = "9.9", description = "changed",
            commands = Array.Empty<object>(),
            ((dynamic)SampleManifest()).mcpServers
        }, "b")).McpFingerprint();
        string changed = ExtensionManifest.Load(Pack(SampleManifest(command: "python"), "c")).McpFingerprint();

        Assert.AreEqual(a, same, "只改名字、版本、说明或命令不需要重新确认");
        Assert.AreNotEqual(a, changed, "启动命令变了就要重新确认");
    }

    [TestMethod]
    public void Store_InstallsFromFolderAndZip_ReplacesOnUpgrade_ListsAndUninstalls()
    {
        var store = new ExtensionPackStore(Path.Combine(_root, "data"));
        string source = Pack(SampleManifest());
        using (StagedPack staged = ExtensionPackStore.Stage(source))
        {
            store.Install(staged);
        }
        Assert.IsTrue(File.Exists(Path.Combine(store.Root, "acme-ops", "skills", "incident", SkillCatalog.SkillFileName)), "整个目录复制进来");

        // 常见打包方式:压缩包里有唯一的顶层文件夹,清单在它下面
        string zip = Path.Combine(_root, "acme.zip");
        File.WriteAllText(Path.Combine(source, "NEW.txt"), "v2");
        ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        string temp;
        using (StagedPack staged = ExtensionPackStore.Stage(zip))
        {
            temp = staged.Directory;
            Assert.AreEqual("acme-ops", staged.Manifest.Id);
            store.Install(staged);
        }
        Assert.IsFalse(Directory.Exists(temp), "解压用的临时目录用完就删");
        Assert.IsTrue(File.Exists(Path.Combine(store.Root, "acme-ops", "NEW.txt")), "同 id 再装即升级替换");

        Directory.CreateDirectory(Path.Combine(store.Root, "broken"));
        IReadOnlyList<InstalledPack> listed = store.List();
        Assert.AreSequenceEqual(["acme-ops", "broken"], [.. listed.Select(p => p.Id)]);
        Assert.IsNotNull(listed.Single(p => p.Id == "broken").Error, "清单读不出来的带原因");

        store.Uninstall("acme-ops");
        Assert.IsFalse(Directory.Exists(Path.Combine(store.Root, "acme-ops")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Uninstall("../x"));
    }

    [TestMethod]
    public void Stage_RejectsZipSlipAndNonArchives()
    {
        string zip = Path.Combine(_root, "evil.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(ExtensionManifest.FileName).Open()))
                writer.Write(JsonSerializer.Serialize(SampleManifest()));
            using (var writer = new StreamWriter(archive.CreateEntry("../../escape.txt").Open())) writer.Write("x");
        }
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => ExtensionPackStore.Stage(zip)).Message, "outside");

        string text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "x");
        Assert.ThrowsExactly<InvalidDataException>(() => ExtensionPackStore.Stage(text));
    }

    [TestMethod]
    public void Runtime_OnlyEnabledPacks_AndOnlyApprovedMcp()
    {
        var store = new ExtensionPackStore(Path.Combine(_root, "data"));
        using (StagedPack staged = ExtensionPackStore.Stage(Pack(SampleManifest()))) store.Install(staged);
        string fingerprint = ExtensionManifest.Load(Path.Combine(store.Root, "acme-ops")).McpFingerprint();
        var manual = new McpServerConfig { Id = "manual", Name = "mine" };

        var settings = new AiSettings { McpServers = [manual] };
        Assert.IsEmpty(ExtensionRuntime.Active(settings, store), "装了但设置里没有状态:不生效");

        settings.ExtensionPacks = [new ExtensionPackState { Id = "acme-ops", Enabled = true, ApprovedMcpFingerprint = "stale" }];
        ActivePack pack = ExtensionRuntime.Active(settings, store).Single();
        Assert.IsFalse(pack.McpApproved);
        Assert.AreSequenceEqual(["manual"], [.. ExtensionRuntime.McpServers(settings, [pack]).Select(s => s.Id)], "指纹对不上:不启动这个包的 MCP");

        settings.ExtensionPacks[0].ApprovedMcpFingerprint = fingerprint;
        IReadOnlyList<ActivePack> active = ExtensionRuntime.Active(settings, store);
        Assert.AreSequenceEqual(["manual", "ext-acme-ops-tickets", "ext-acme-ops-remote"], [.. ExtensionRuntime.McpServers(settings, active).Select(s => s.Id)]);

        settings.ExtensionPacks[0].Enabled = false;
        Assert.IsEmpty(ExtensionRuntime.Active(settings, store), "停用即撤下");
    }

    [TestMethod]
    public void Runtime_ExpandsCommands_AndContributesSkillsWithTheLowestPrecedence()
    {
        var store = new ExtensionPackStore(Path.Combine(_root, "data"));
        using (StagedPack staged = ExtensionPackStore.Stage(Pack(SampleManifest()))) store.Install(staged);
        var settings = new AiSettings { ExtensionPacks = [new ExtensionPackState { Id = "acme-ops", Enabled = true }] };
        IReadOnlyList<ActivePack> packs = ExtensionRuntime.Active(settings, store);

        string expanded = ExtensionRuntime.TryExpandCommand("/DEPLOY api-gateway", packs)!;
        StringAssert.Contains(expanded, "/deploy from the extension pack \"Acme Ops\"");
        StringAssert.EndsWith(expanded, "Deploy api-gateway carefully.");
        Assert.IsNull(ExtensionRuntime.TryExpandCommand("/unknown x", packs));
        Assert.AreEqual(("deploy", "Deploy a service", "Acme Ops"), ExtensionRuntime.Commands(packs).Single());

        string userSkills = Path.Combine(_root, "user-skills");
        Directory.CreateDirectory(Path.Combine(userSkills, "incident"));
        File.WriteAllText(Path.Combine(userSkills, "incident", SkillCatalog.SkillFileName), "---\ndescription: My own version.\n---");
        IReadOnlyList<SkillInfo> skills = SkillCatalog.Scan(Path.Combine(_root, "none"), [], ExtensionRuntime.SkillDirectories(packs));
        Assert.AreEqual(SkillSource.Extension, skills.Single().Source);
        Assert.AreEqual("Handle an incident.", skills.Single().Description);
        Assert.AreEqual("My own version.", SkillCatalog.Scan(userSkills, [], ExtensionRuntime.SkillDirectories(packs)).Single().Description,
            "同名时用户级覆盖扩展包的");
        StringAssert.Contains(SkillToolSource.Index(skills), "- incident (extension pack): Handle an incident.");
    }
}
