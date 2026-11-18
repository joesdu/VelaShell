using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>「你应该知道」条目本身:类别、去重、截断、上限、关掉,工具调用与从历史重建。</summary>
[TestClass]
public sealed class ConversationNotesTests
{
    [TestMethod]
    public void Add_NormalizesKind_DedupesByTitle_AndIgnoresEmptyTitles()
    {
        var notes = new ConversationNotes();
        int changed = 0;
        notes.Changed += () => changed++;

        Assert.IsNull(notes.Add("warning", "   ", "x"), "标题为空不记");
        ConversationNote first = notes.Add("WARNING", " Port 22 is open to the internet ", "sshd listens on 0.0.0.0")!;
        Assert.AreEqual("warning", first.Kind);
        Assert.AreEqual("Port 22 is open to the internet", first.Title, "首尾空白去掉");
        Assert.AreEqual("info", notes.Add("bogus", "Disk is 91% full", null)!.Kind, "认不出的类别按 info");

        ConversationNote updated = notes.Add("action", "port 22 IS open to the internet", null)!;
        Assert.AreEqual(first.Id, updated.Id, "同标题(忽略大小写)原位更新,不叠两条");
        Assert.AreEqual("action", updated.Kind);
        Assert.AreEqual("sshd listens on 0.0.0.0", updated.Detail, "没给新说明时保留旧的");
        Assert.HasCount(2, notes.Visible);
        Assert.AreEqual(3, changed);
    }

    [TestMethod]
    public void Add_ClipsLongTextAndKeepsTheNewestWithinTheCap()
    {
        var notes = new ConversationNotes();
        ConversationNote note = notes.Add("info", new string('t', 500), new string('d', 2000))!;
        Assert.HasCount(ConversationNotes.MaxTitleLength, note.Title);
        Assert.EndsWith("…", note.Title);
        Assert.HasCount(ConversationNotes.MaxDetailLength, note.Detail!);

        for (int i = 0; i < ConversationNotes.MaxNotes + 5; i++) notes.Add("info", $"note {i}", null);
        Assert.HasCount(ConversationNotes.MaxNotes, notes.Visible);
        Assert.AreEqual($"note {ConversationNotes.MaxNotes + 4}", notes.Visible[^1].Title, "超出上限时丢最早的");
    }

    [TestMethod]
    public void Dismiss_HidesUntilTheSameNoteIsWrittenAgain()
    {
        var notes = new ConversationNotes();
        ConversationNote a = notes.Add("warning", "A", null)!;
        notes.Add("action", "B", null);

        notes.Dismiss(a.Id);
        CollectionAssert.AreEqual(new[] { "B" }, notes.Visible.Select(n => n.Title).ToArray());
        notes.Add("warning", "A", "again");
        Assert.HasCount(2, notes.Visible, "模型再提同一件事时重新显示");

        notes.DismissAll();
        Assert.IsEmpty(notes.Visible);
        notes.Clear();
        notes.Add("info", "A", null);
        Assert.HasCount(1, notes.Visible, "清空后关掉的记录也不留");
    }

    [TestMethod]
    public async Task Tool_PinsANoteForTheUser()
    {
        var notes = new ConversationNotes();
        var tool = (AIFunction)notes.CreateTool();
        Assert.AreEqual(ConversationNotes.ToolName, tool.Name);
        StringAssert.Contains(tool.Description, "sparingly");

        object? result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["kind"] = "action",
            ["title"] = "Restart nginx to apply the new config",
            ["detail"] = "Run systemctl reload nginx on web-1."
        });

        StringAssert.Contains(result?.ToString(), "Pinned");
        ConversationNote note = notes.Visible.Single();
        Assert.AreEqual("action", note.Kind);
        Assert.AreEqual("Run systemctl reload nginx on web-1.", note.Detail);
    }

    [TestMethod]
    public void Restore_RebuildsFromSavedToolCalls_AndSkipsOthersAndBrokenArguments()
    {
        var notes = new ConversationNotes();
        notes.Restore(new ChatToolCall("note_for_user", """{"kind":"warning","title":"Certificate expires in 3 days"}""", "Pinned"));
        notes.Restore(new ChatToolCall("read_local_file", """{"title":"not a note"}""", ""));
        notes.Restore(new ChatToolCall("note_for_user", """{"kind":"warning","title":""", ""));
        notes.Restore(new ChatToolCall("note_for_user", "[]", ""));

        ConversationNote restored = notes.Visible.Single();
        Assert.AreEqual("warning", restored.Kind);
        Assert.AreEqual("Certificate expires in 3 days", restored.Title);
    }
}
