using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat.Skills;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>技能:SKILL.md 头的解析、目录扫描与覆盖、停用,以及交给模型的两件工具与技能目录(ai-plan §12.3)。</summary>
[TestClass]
public sealed class SkillCatalogTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "skills-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Skill(string parent, string folder, string content)
    {
        string dir = Path.Combine(parent, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SkillCatalog.SkillFileName), content);
        return dir;
    }

    [TestMethod]
    public void Parse_ReadsHeaderLines_Quotes_AndBlockValues()
    {
        (Dictionary<string, string> header, string body) = SkillCatalog.Parse(
            "﻿---\r\nname: \"nginx-check\"\r\ndescription: >\r\n  Checks nginx\r\n  config safely.\r\nnotes: |\r\n  line one\r\n  line two\r\n# comment\r\nlicense: 'MIT'\r\n---\r\n\r\n# Steps\r\nRun nginx -t.\r\n");

        Assert.AreEqual("nginx-check", header["name"]);
        Assert.AreEqual("Checks nginx config safely.", header["description"], "> 折成一行");
        Assert.AreEqual("line one\nline two", header["notes"], "| 保留换行");
        Assert.AreEqual("MIT", header["license"]);
        Assert.AreEqual("# Steps\nRun nginx -t.", body);

        (Dictionary<string, string> none, string whole) = SkillCatalog.Parse("Just instructions.");
        Assert.IsEmpty(none);
        Assert.AreEqual("Just instructions.", whole, "没有头时整份都是正文");
    }

    [TestMethod]
    public void Read_ValidatesNameAndDescription_AndFallsBackToTheFolderName()
    {
        SkillInfo ok = SkillCatalog.Read(Skill(_root, "folder-name", "---\ndescription: Does a thing.\n---\nbody"), SkillSource.User);
        Assert.IsTrue(ok.IsValid);
        Assert.AreEqual("folder-name", ok.Name, "头里没写 name 时取目录名");

        SkillInfo noDescription = SkillCatalog.Read(Skill(_root, "x", "---\nname: x\n---\nbody"), SkillSource.User);
        Assert.IsFalse(noDescription.IsValid);
        StringAssert.Contains(noDescription.Error, "description");

        SkillInfo badName = SkillCatalog.Read(Skill(_root, "y", "---\nname: has space\ndescription: d\n---"), SkillSource.User);
        Assert.IsFalse(badName.IsValid, "名字要当斜杠命令用,不许有空格");

        SkillInfo longDescription = SkillCatalog.Read(Skill(_root, "z", $"---\ndescription: {new string('d', 1000)}\n---"), SkillSource.User);
        Assert.HasCount(SkillCatalog.MaxDescriptionLength, longDescription.Description, "说明截断,它每轮都进系统提示词");

        string big = Skill(_root, "big", "---\ndescription: d\n---\n" + new string('x', SkillCatalog.MaxSkillFileBytes + 10));
        Assert.IsFalse(SkillCatalog.Read(big, SkillSource.User).IsValid);
    }

    [TestMethod]
    public void Scan_WorkspaceOverridesUser_KeepsFailedOnes_AndSortsByName()
    {
        string user = Path.Combine(_root, "user");
        string workspace = Path.Combine(_root, "ws");
        string workspaceSkills = Path.Combine(workspace, SkillCatalog.WorkspaceRelativeDirectory);
        Skill(user, "deploy", "---\nname: deploy\ndescription: user version\n---");
        Skill(user, "audit", "---\nname: audit\ndescription: audits\n---");
        Skill(workspaceSkills, "deploy", "---\nname: deploy\ndescription: workspace version\n---");
        Skill(workspaceSkills, "broken", "---\nname: broken\n---");
        Directory.CreateDirectory(Path.Combine(user, "not-a-skill")); // 没有 SKILL.md 的目录不算

        IReadOnlyList<SkillInfo> skills = SkillCatalog.Scan(user, [workspace]);

        Assert.AreSequenceEqual(["audit", "broken", "deploy"], [.. skills.Select(s => s.Name)]);
        SkillInfo deploy = skills.Single(s => s.Name == "deploy");
        Assert.AreEqual(SkillSource.Workspace, deploy.Source, "同名时工作区的优先");
        Assert.AreEqual("workspace version", deploy.Description);
        Assert.IsFalse(skills.Single(s => s.Name == "broken").IsValid, "解析失败的带着原因留在清单里");
        Assert.IsEmpty(SkillCatalog.Scan(Path.Combine(_root, "missing"), []), "目录不存在不是错误");
    }

    [TestMethod]
    public void Enabled_SkipsDisabledInvalid_AndUnreadableWorkspaces()
    {
        string readable = Path.Combine(_root, "readable");
        string unreadable = Path.Combine(_root, "unreadable");
        Skill(Path.Combine(readable, SkillCatalog.WorkspaceRelativeDirectory), "keep", "---\ndescription: k\n---");
        Skill(Path.Combine(readable, SkillCatalog.WorkspaceRelativeDirectory), "off", "---\ndescription: o\n---");
        Skill(Path.Combine(readable, SkillCatalog.WorkspaceRelativeDirectory), "bad", "---\nname: bad\n---");
        Skill(Path.Combine(unreadable, SkillCatalog.WorkspaceRelativeDirectory), "hidden", "---\ndescription: h\n---");

        IReadOnlyList<SkillInfo> enabled = SkillCatalog.Enabled("OFF\n",
            [new LocalWorkspace { RootPath = readable }, new LocalWorkspace { RootPath = unreadable, AllowRead = false }]);

        Assert.AreSequenceEqual(["keep"], [.. enabled.Select(s => s.Name)], "停用的(忽略大小写)、解析失败的、不允许读取的工作区里的都不进来");
    }

    private SkillToolSource SourceWith(out SkillInfo skill)
    {
        string dir = Skill(_root, "nginx-check", "---\nname: nginx-check\ndescription: Checks nginx config.\n---\n# Steps\nRun nginx -t first.");
        Directory.CreateDirectory(Path.Combine(dir, "reference"));
        File.WriteAllText(Path.Combine(dir, "reference", "directives.md"), "listen, server_name");
        File.WriteAllBytes(Path.Combine(dir, "logo.bin"), [1, 0, 2]);
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "outside");
        skill = SkillCatalog.Read(dir, SkillSource.Workspace);
        return new SkillToolSource([skill]);
    }

    private static async Task<IList<AITool>> ToolsOf(SkillToolSource source)
    {
        var tools = new List<AITool>();
        await source.ContributeAsync(tools, CancellationToken.None);
        return tools;
    }

    private static async Task<string> Invoke(IList<AITool> tools, string name, AIFunctionArguments arguments)
        => (await ((AIFunction)tools.Single(t => t.Name == name)).InvokeAsync(arguments))?.ToString() ?? "";

    [TestMethod]
    public async Task LoadSkill_ReturnsInstructionsAndTheFileList()
    {
        SkillToolSource source = SourceWith(out _);
        Assert.IsFalse(source.AppliesTo(ChatMode.Chat), "对话模式不给工具");
        Assert.IsTrue(source.AppliesTo(ChatMode.Plan), "只读,计划模式也给");
        Assert.IsFalse(new SkillToolSource([]).AppliesTo(ChatMode.Agent), "没有可用技能就不摆这两件工具");

        IList<AITool> tools = await ToolsOf(source);
        string loaded = await Invoke(tools, SkillToolSource.LoadToolName, new() { ["name"] = "NGINX-check" });

        StringAssert.Contains(loaded, "Run nginx -t first.");
        StringAssert.Contains(loaded, "- reference/directives.md");
        Assert.DoesNotContain("- SKILL.md", loaded, "SKILL.md 本身不列为附带文件");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(tools, SkillToolSource.LoadToolName, new() { ["name"] = "nope" }));
    }

    [TestMethod]
    public async Task ReadSkillFile_StaysInsideTheSkillFolder_TextOnly()
    {
        IList<AITool> tools = await ToolsOf(SourceWith(out SkillInfo skill));

        Assert.AreEqual("listen, server_name",
            await Invoke(tools, SkillToolSource.ReadFileToolName, new() { ["name"] = "nginx-check", ["path"] = "reference/directives.md" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(tools, SkillToolSource.ReadFileToolName, new() { ["name"] = "nginx-check", ["path"] = "../secret.txt" }), "不许越出技能目录");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(tools, SkillToolSource.ReadFileToolName, new() { ["name"] = "nginx-check", ["path"] = Path.Combine(_root, "secret.txt") }), "绝对路径同样拒绝");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(tools, SkillToolSource.ReadFileToolName, new() { ["name"] = "nginx-check", ["path"] = "logo.bin" }), "二进制文件拒绝");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Invoke(tools, SkillToolSource.ReadFileToolName, new() { ["name"] = "nginx-check", ["path"] = "missing.md" }));
        Assert.AreEqual(SkillSource.Workspace, skill.Source);
    }

    [TestMethod]
    public void Invocation_MatchesSlashName_AndExpandsWithTheSkillBody()
    {
        SourceWith(out SkillInfo skill);
        SkillInfo broken = new("broken", "", _root, SkillSource.User, "bad");
        IReadOnlyList<SkillInfo> skills = [skill, broken];

        Assert.AreEqual(("nginx-check", "reload  prod"), SkillInvocation.Match("  /NGINX-check  reload  prod ", skills) is { } m ? (m.Skill.Name, m.Request) : default);
        Assert.IsNull(SkillInvocation.Match("/unknown do it", skills));
        Assert.IsNull(SkillInvocation.Match("/broken", skills), "解析失败的技能不能调用");
        Assert.IsNull(SkillInvocation.Match("nginx-check /nginx-check", skills), "只认开头的斜杠");
        Assert.IsNull(SkillInvocation.Match("/", skills));

        string expanded = SkillInvocation.TryExpand("/nginx-check reload prod", skills)!;
        StringAssert.Contains(expanded, "explicitly invoked the skill \"nginx-check\"");
        StringAssert.Contains(expanded, "Run nginx -t first.");
        StringAssert.EndsWith(expanded, "User request: reload prod");
        StringAssert.Contains(SkillInvocation.TryExpand("/nginx-check", skills), "(none given: apply the skill to the current conversation)");
        Assert.IsNull(SkillInvocation.TryExpand("plain message", skills));
    }

    [TestMethod]
    public void Index_ListsValidSkillsWithSource_AndCapsTheList()
    {
        Assert.IsNull(SkillToolSource.Index([]), "没有可用技能时不加这一段");
        var skills = Enumerable.Range(0, SkillToolSource.MaxIndexed + 3)
            .Select(i => new SkillInfo($"s{i:D2}", $"does {i}", _root, i == 0 ? SkillSource.Workspace : SkillSource.User))
            .Append(new SkillInfo("broken", "", _root, SkillSource.User, "bad"))
            .ToList();

        string index = SkillToolSource.Index(skills)!;
        StringAssert.Contains(index, "- s00 (workspace): does 0");
        StringAssert.Contains(index, "- s01: does 1");
        StringAssert.Contains(index, "3 more skills are installed but not listed");
        Assert.DoesNotContain("broken", index, "解析失败的不进目录");
        StringAssert.Contains(index, "never override the user's explicit request");
    }
}
