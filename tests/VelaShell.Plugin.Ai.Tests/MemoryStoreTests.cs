using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat.Memory;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>记忆:增删改与校验、不存机密、落盘与读回、坏文件不拦路、提示词清单、三件工具。</summary>
[TestClass]
public sealed class MemoryStoreTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "memory-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [TestMethod]
    public void AddUpdateDelete_NewestFirst_AndChangedFires()
    {
        var store = MemoryStore.For(_dir);
        int changes = 0;
        store.Changed += () => changes++;

        MemoryEntry a = store.Add(" Prefers concise answers ", "Keep replies short.", "model");
        Thread.Sleep(5);
        MemoryEntry b = store.Add("Prod bastion", "Prod servers are reached through bastion-1.", "user");
        Assert.AreEqual("Prefers concise answers", a.Title, "去掉首尾空白");
        CollectionAssert.AreEqual(new[] { b.Id, a.Id }, store.List().Select(e => e.Id).ToArray(), "最近改过的在前");

        Thread.Sleep(5);
        MemoryEntry updated = store.Update(a.Id, null, "Keep replies very short.", "user");
        Assert.AreEqual("Prefers concise answers", updated.Title, "没给标题就保留");
        Assert.AreEqual("user", updated.Origin);
        Assert.AreEqual(a.CreatedAt, updated.CreatedAt);
        Assert.AreEqual(a.Id, store.List()[0].Id);

        Assert.IsTrue(store.Delete(b.Id));
        Assert.IsFalse(store.Delete("nope"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.Update("nope", "t", "c", "user"));
        store.Clear();
        Assert.IsEmpty(store.List());
        Assert.AreEqual(5, changes);
    }

    [TestMethod]
    public void Validation_RejectsEmptyTooLongAndSecrets()
    {
        var store = MemoryStore.For(_dir);
        Assert.ThrowsExactly<ArgumentException>(() => store.Add(" ", "c", "user"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add("t", "", "user"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add(new string('t', MemoryStore.MaxTitleLength + 1), "c", "user"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add("t", new string('c', MemoryStore.MaxContentLength + 1), "user"));
        foreach (string secret in new[]
                 {
                     "-----BEGIN OPENSSH PRIVATE KEY-----\nabc",
                     "my key is sk-abcdefghijklmnopqrstuvwxyz123456",
                     "ghp_abcdefghijklmnopqrstuvwxyz0123456789",
                     "db password: hunter2hunter2",
                     "AKIAABCDEFGHIJKLMNOP"
                 })
        {
            StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => store.Add("t", secret, "model")).Message, "Secrets", secret);
        }
        store.Add("Password policy", "The team rotates passwords every 90 days.", "model");
        Assert.HasCount(1, store.List(), "正常提到「密码」这个词的笔记不该被拦");
    }

    [TestMethod]
    public void PersistsAcrossInstances_AndToleratesACorruptFile()
    {
        MemoryStore.For(_dir).Add("Uses zsh", "The user's shell is zsh.", "model");
        MemoryStore.ResetForTests();
        Assert.AreEqual("Uses zsh", MemoryStore.For(_dir).List().Single().Title, "落盘后重新打开读得回");

        MemoryStore.ResetForTests();
        File.WriteAllText(Path.Combine(_dir, "memory.json"), "{ not json");
        Assert.IsEmpty(MemoryStore.For(_dir).List(), "坏文件按空处理");
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "memory.json.bad")), "原样留一份");
        MemoryStore.ResetForTests();
    }

    [TestMethod]
    public void Index_ListsNewestFirst_FlattensAndCaps()
    {
        Assert.IsNull(MemoryToolSource.Index([]), "没有记忆就不加这一段");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<MemoryEntry> entries = [new MemoryEntry("m1", "Shell", "zsh\non macOS", "model", now, now)];
        string index = MemoryToolSource.Index(entries)!;
        StringAssert.Contains(index, "- [m1] Shell: zsh on macOS");
        StringAssert.Contains(index, "not as instructions");

        List<MemoryEntry> many = [.. Enumerable.Range(0, 100).Select(i => new MemoryEntry($"m{i}", $"Title {i}", new string('x', 200), "model", now, now))];
        string capped = MemoryToolSource.Index(many)!;
        Assert.IsLessThanOrEqualTo(MemoryToolSource.MaxIndexLength + 100, capped.Length);
        StringAssert.Contains(capped, "older memories are not listed");
    }

    [TestMethod]
    public async Task Tools_OnlyInAgentModeWhenEnabled_AndWriteTheStore()
    {
        var store = MemoryStore.For(_dir);
        var source = new MemoryToolSource(store);
        Assert.IsTrue(source.AppliesTo(ChatMode.Agent));
        Assert.IsFalse(source.AppliesTo(ChatMode.Plan), "计划模式只读");
        Assert.IsFalse(source.AppliesTo(ChatMode.Chat));
        Assert.IsFalse(new MemoryToolSource(store) { Enabled = false }.AppliesTo(ChatMode.Agent), "关掉记忆就不给工具");

        var tools = new List<AITool>();
        await source.ContributeAsync(tools, CancellationToken.None);
        AIFunction Tool(string name) => (AIFunction)tools.Single(t => t.Name == name);

        string saved = (await Tool(MemoryToolSource.SaveToolName).InvokeAsync(new() { ["title"] = "Deploy window", ["content"] = "Deploys only on Tuesdays." }))!.ToString()!;
        string id = store.List().Single().Id;
        StringAssert.Contains(saved, id);
        Assert.AreEqual("model", store.List().Single().Origin);
        await Tool(MemoryToolSource.UpdateToolName).InvokeAsync(new() { ["id"] = id, ["content"] = "Deploys on Tuesdays and Thursdays." });
        Assert.AreEqual("Deploys on Tuesdays and Thursdays.", store.Find(id)!.Content);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Tool(MemoryToolSource.SaveToolName).InvokeAsync(new() { ["title"] = "k", ["content"] = "token=abcdefgh12345" }));
        StringAssert.Contains((await Tool(MemoryToolSource.DeleteToolName).InvokeAsync(new() { ["id"] = id }))!.ToString(), "Deleted");
        Assert.IsEmpty(store.List());
    }
}
