using System.Text.RegularExpressions;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 行级差异(Myers)、从工具参数 / 文件改动换算差异、「已修改文件」的合并规则,以及工具类别图标映射。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed partial class LineDiffTests
{
    private static string Kinds(LineDiffResult result)
        => string.Concat(result.Lines.Select(l => l.Kind switch
        {
            DiffLineKind.Added => '+',
            DiffLineKind.Removed => '-',
            _ => ' '
        }));

    [TestMethod]
    public void IdenticalTexts_HaveNoChanges()
    {
        LineDiffResult result = LineDiff.Compute("a\nb\nc\n", "a\nb\nc");
        Assert.IsFalse(result.HasChanges);
        Assert.HasCount(3, result.Lines);
        Assert.IsEmpty(result.Collapse());
        Assert.AreEqual("", result.ToUnified());
    }

    [TestMethod]
    public void SplitLines_NormalizesLineEndings_AndDropsTrailingNewline()
    {
        Assert.AreSequenceEqual(["a", "b", "", "c"], LineDiff.SplitLines("a\r\nb\r\n\r\nc\r\n"));
        Assert.IsEmpty(LineDiff.SplitLines(null));
        Assert.IsEmpty(LineDiff.SplitLines(""));
        Assert.IsFalse(LineDiff.Compute("x\r\ny\r\n", "x\ny\n").HasChanges, "只换了换行符不算改动");
    }

    [TestMethod]
    public void Insertion_Deletion_AndNumbers()
    {
        LineDiffResult result = LineDiff.Compute("a\nb\nc", "a\nx\nb\nc");
        Assert.AreEqual(" + ", Kinds(result)[..3]);
        Assert.AreEqual(1, result.Added);
        Assert.AreEqual(0, result.Removed);
        DiffLine added = result.Lines[1];
        Assert.AreEqual(0, added.OldNumber);
        Assert.AreEqual(2, added.NewNumber);
        Assert.AreEqual("x", added.Text);
        DiffLine c = result.Lines[^1];
        Assert.AreEqual(3, c.OldNumber);
        Assert.AreEqual(4, c.NewNumber);

        LineDiffResult deleted = LineDiff.Compute("a\nb\nc", "a\nc");
        Assert.AreEqual(" - ", Kinds(deleted));
        Assert.AreEqual(2, deleted.Lines[1].OldNumber);
        Assert.AreEqual(0, deleted.Lines[1].NewNumber);
    }

    /// <summary>同一处改动里删除行在前、新增行在后,与 git 一致。</summary>
    [TestMethod]
    public void Replacement_ListsRemovalsBeforeAdditions()
    {
        LineDiffResult result = LineDiff.Compute("keep\nold1\nold2\nend", "keep\nnew1\nnew2\nnew3\nend");
        Assert.AreEqual(" --+++ ", Kinds(result));
        Assert.AreEqual(3, result.Added);
        Assert.AreEqual(2, result.Removed);
    }

    /// <summary>最短编辑脚本:中间穿插的相同行要对齐,而不是整段删了再加。</summary>
    [TestMethod]
    public void Myers_FindsTheShortestScript()
    {
        // 经典例子 ABCABBA → CBABAC,最短编辑距离是 5
        LineDiffResult result = LineDiff.Compute("A\nB\nC\nA\nB\nB\nA", "C\nB\nA\nB\nA\nC");
        Assert.AreEqual(5, result.Added + result.Removed);
        string[] rebuiltNew = [.. result.Lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text)];
        string[] rebuiltOld = [.. result.Lines.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.Text)];
        Assert.AreSequenceEqual(LineDiff.SplitLines("C\nB\nA\nB\nA\nC"), rebuiltNew);
        Assert.AreSequenceEqual(LineDiff.SplitLines("A\nB\nC\nA\nB\nB\nA"), rebuiltOld);
    }

    [TestMethod]
    public void Collapse_KeepsThreeLinesOfContext_AndFoldsTheRest()
    {
        string before = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        string after = before.Replace("line 15", "line fifteen", StringComparison.Ordinal);
        IReadOnlyList<DiffLine> collapsed = LineDiff.Compute(before, after).Collapse();
        Assert.AreEqual(DiffLineKind.Gap, collapsed[0].Kind);
        Assert.AreEqual(11, collapsed[0].HiddenCount);
        Assert.AreEqual(12, collapsed[1].OldNumber, "改动前留 3 行");
        Assert.AreEqual(DiffLineKind.Gap, collapsed[^1].Kind);
        Assert.AreEqual(12, collapsed[^1].HiddenCount);
        Assert.AreEqual(1 + 3 + 2 + 3 + 1, collapsed.Count);
    }

    [TestMethod]
    public void ToUnified_WritesGitHunks()
    {
        string before = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"l{i}"));
        string after = before.Replace("l5", "L5", StringComparison.Ordinal);
        string unified = LineDiff.Compute(before, after).ToUnified();
        Assert.AreEqual("@@ -2,7 +2,7 @@\n l2\n l3\n l4\n-l5\n+L5\n l6\n l7\n l8\n", unified);

        Assert.AreEqual("@@ -0,0 +1,2 @@\n+a\n+b\n", LineDiff.Compute(null, "a\nb").ToUnified());
    }

    /// <summary>差得太多就不找最短脚本,整段删、整段加 —— 结果仍然正确,只是不再逐行对齐。</summary>
    [TestMethod]
    public void HugeEditDistance_FallsBackToReplaceAll()
    {
        string[] a = [.. Enumerable.Range(0, 3000).Select(i => $"a{i}")];
        string[] b = [.. Enumerable.Range(0, 3000).Select(i => $"b{i}")];
        LineDiffResult result = LineDiff.Compute(a, b);
        Assert.AreEqual(3000, result.Added);
        Assert.AreEqual(3000, result.Removed);
    }

    // ---------- ToolDiff ----------

    [TestMethod]
    public void PatchArguments_TopLevelPair()
    {
        ToolFileDiff diff = ToolDiff.FromArguments("patch_local_file",
            """{"path":"src/app.cs","oldText":"int a = 1;\nint b = 2;","newText":"int a = 1;\nint b = 3;","replaceAll":false}""")!;
        Assert.AreEqual("src/app.cs", diff.Path);
        Assert.IsFalse(diff.HasLineNumbers, "片段不知道在文件里的位置");
        Assert.HasCount(1, diff.Sections);
        Assert.AreEqual(1, diff.Added);
        Assert.AreEqual(1, diff.Removed);
    }

    [TestMethod]
    public void PatchArguments_EditsArray_AndStringifiedArray()
    {
        const string edits = """[{"oldText":"a","newText":"b"},{"oldText":"c\nd","newText":"c","replaceAll":true}]""";
        ToolFileDiff diff = ToolDiff.FromArguments("patch_remote_file", "{\"path\":\"/etc/x.conf\",\"edits\":" + edits + "}")!;
        Assert.HasCount(2, diff.Sections);
        Assert.AreEqual(1, diff.Added);
        Assert.AreEqual(2, diff.Removed);

        string stringified = System.Text.Json.JsonSerializer.Serialize(new { path = "/etc/x.conf", edits });
        ToolFileDiff fromString = ToolDiff.FromArguments("patch_remote_file", stringified)!;
        Assert.HasCount(2, fromString.Sections);
    }

    [TestMethod]
    public void WriteArguments_AreAllAdded_WithRealNumbers()
    {
        ToolFileDiff diff = ToolDiff.FromArguments("write_remote_file", """{"path":"/tmp/a","content":"x\ny\n"}""")!;
        Assert.IsTrue(diff.HasLineNumbers);
        Assert.AreEqual(2, diff.Added);
        Assert.AreEqual(0, diff.Removed);
    }

    [TestMethod]
    public void NonEditTools_AndBrokenArguments_HaveNoDiff()
    {
        Assert.IsNull(ToolDiff.FromArguments("read_local_file", """{"path":"a"}"""));
        Assert.IsNull(ToolDiff.FromArguments("patch_local_file", "not json"));
        Assert.IsNull(ToolDiff.FromArguments("patch_local_file", """{"path":"a"}"""));
    }

    [TestMethod]
    public void FromChange_UsesFullTexts_AndKnowsWhenTextIsMissing()
    {
        ToolFileDiff modified = ToolDiff.FromChange(new FileChange("patch_local_file", "local:w", "a.txt",
            FileChangeKind.Modified, "1\n2\n3\n", "1\ntwo\n3\n"))!;
        Assert.IsTrue(modified.HasLineNumbers);
        Assert.AreEqual(2, modified.Sections[0].Lines.Single(l => l.Kind == DiffLineKind.Added).NewNumber);

        Assert.AreEqual(2, ToolDiff.FromChange(new FileChange("write_local_file", "local:w", "n.txt",
            FileChangeKind.Created, null, "a\nb"))!.Added);
        Assert.AreEqual(1, ToolDiff.FromChange(new FileChange("delete_local_path", "local:w", "n.txt",
            FileChangeKind.Deleted, "a", null))!.Removed);
        Assert.IsNull(ToolDiff.FromChange(new FileChange("write_local_file", "local:w", "bin.dat",
            FileChangeKind.Modified, null, null)), "二进制 / 超限:没有文本可比");
    }

    [TestMethod]
    public void SamePath_ToleratesSeparatorsAndRelativeVsAbsolute()
    {
        Assert.IsTrue(ToolDiff.SamePath(@"src\app.cs", "src/app.cs"));
        Assert.IsTrue(ToolDiff.SamePath(@"C:\work\proj\src\app.cs", "src/app.cs"));
        Assert.IsFalse(ToolDiff.SamePath("src/app.cs", "src/other.cs"));
        Assert.AreSequenceEqual(["a/b", "c/d"], [.. ToolDiff.PathsOf("""{"path":"a/b","newPath":"c/d","content":"x"}""")]);
    }

    // ---------- ChangedFileList ----------

    private static FileChange Change(string path, FileChangeKind kind, string? before, string? after, string? oldPath = null)
        => new("write_local_file", "local:w", path, kind, before, after, oldPath);

    [TestMethod]
    public void ChangedFiles_MergeBeforeFromFirstAndAfterFromLast()
    {
        var list = new ChangedFileList();
        list.Add(Change("a.txt", FileChangeKind.Modified, "1\n2\n", "1\nX\n"));
        list.Add(Change(@"a.txt", FileChangeKind.Modified, "1\nX\n", "1\nX\nY\n"));
        ChangedFileEntry entry = list.Entries.Single();
        Assert.AreEqual("1\n2\n", entry.Before);
        Assert.AreEqual("1\nX\nY\n", entry.After);
        Assert.AreEqual("M", entry.Badge);
        Assert.AreEqual(2, entry.Added);
        Assert.AreEqual(1, entry.Removed);
        Assert.AreEqual(2, list.TotalAdded);
    }

    [TestMethod]
    public void ChangedFiles_CreateThenDelete_Disappears_DeleteThenCreate_IsModified()
    {
        var list = new ChangedFileList();
        list.Add(Change("new.txt", FileChangeKind.Created, null, "x"));
        list.Add(Change("new.txt", FileChangeKind.Modified, "x", "y"));
        Assert.AreEqual("A", list.Entries.Single().Badge, "新建后再改仍算新建");
        Assert.IsNull(list.Add(Change("new.txt", FileChangeKind.Deleted, "y", null)));
        Assert.IsEmpty(list.Entries);

        list.Add(Change("old.txt", FileChangeKind.Deleted, "keep", null));
        Assert.AreEqual("D", list.Entries.Single().Badge);
        list.Add(Change("old.txt", FileChangeKind.Created, null, "keep\nmore"));
        Assert.AreEqual("M", list.Entries.Single().Badge);
        Assert.AreEqual(1, list.Entries.Single().Added);
    }

    [TestMethod]
    public void ChangedFiles_RenameMovesTheEntry()
    {
        var list = new ChangedFileList();
        list.Add(Change("a.txt", FileChangeKind.Modified, "1", "2"));
        list.Add(Change("b.txt", FileChangeKind.Renamed, "2", "2", oldPath: "a.txt"));
        ChangedFileEntry entry = list.Entries.Single();
        Assert.AreEqual("b.txt", entry.Path);
        Assert.AreEqual("a.txt", entry.OldPath);
        Assert.AreEqual("R", entry.Badge);
        Assert.AreEqual("1", entry.Before, "改名前的修改也算在里面");

        list.Clear();
        list.Add(Change("n.txt", FileChangeKind.Created, null, "z"));
        list.Add(Change("m.txt", FileChangeKind.Renamed, "z", "z", oldPath: "n.txt"));
        Assert.AreEqual("A", list.Entries.Single().Badge, "新建的文件改名仍算新建");
        Assert.AreEqual("m.txt", list.Entries.Single().Path);
    }

    [TestMethod]
    public void ChangedFiles_AreKeyedByLocationToo()
    {
        var list = new ChangedFileList();
        list.Add(new FileChange("patch_remote_file", "remote:s1", "/etc/hosts", FileChangeKind.Modified, "a", "b"));
        list.Add(new FileChange("patch_remote_file", "remote:s2", "/etc/hosts", FileChangeKind.Modified, "a", "c"));
        Assert.HasCount(2, list.Entries);
    }

    // ---------- ToolIcons ----------

    /// <summary>内置工具目录里的每个工具都登记了类别 —— 新加工具时漏登记,卡片上就会画成「外部工具」的插头。</summary>
    [TestMethod]
    public void EveryBuiltInTool_HasAKind()
    {
        List<string> missing = [.. AgentToolbox.Catalog.Select(t => t.Name).Where(name => !ToolIcons.IsKnown(name))];
        Assert.IsEmpty(missing, "register these tools in ToolIcons.Kinds: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void IconMapping_CoversCategories_AndMcpToolsGetThePlug()
    {
        Assert.AreEqual("AiIcon.file-text", ToolIcons.IconKey("read_local_file"));
        Assert.AreEqual("AiIcon.file-pen", ToolIcons.IconKey("patch_remote_file"));
        Assert.AreEqual("AiIcon.search", ToolIcons.IconKey("search_local_files"));
        Assert.AreEqual("AiIcon.search", ToolIcons.IconKey("find_local_files"));
        Assert.AreEqual("AiIcon.square-terminal", ToolIcons.IconKey("run_command"));
        Assert.AreEqual("AiIcon.bot", ToolIcons.IconKey("delegate_subagent"));
        Assert.AreEqual("AiIcon.plug", ToolIcons.IconKey("github_read_file"), "MCP 工具不按前缀猜");
        Assert.AreEqual(ToolKind.External, ToolIcons.KindOf("myserver_search"));
    }

    [GeneratedRegex(@"x:Key=""(AiIcon\.[a-z0-9-]+)""")]
    private static partial Regex IconKeyDefinition { get; }

    /// <summary>映射里用到的每个图标键都在 AiTheme.axaml 里有几何 —— 拼错一个字母,卡片上那枚图标就是空的。</summary>
    [TestMethod]
    public void EveryMappedIcon_IsDefinedInAiTheme()
    {
        string theme = File.ReadAllText(FindRepoFile("plugins", "VelaShell.Plugin.Ai", "Ui", "AiTheme.axaml"));
        HashSet<string> defined = [.. IconKeyDefinition.Matches(theme).Select(m => m.Groups[1].Value)];
        List<string> missing = [.. ToolIcons.AllIconKeys.Concat(["AiIcon.file", "AiIcon.file-diff", "AiIcon.layers", "AiIcon.x"])
            .Where(key => !defined.Contains(key))];
        Assert.IsEmpty(missing, "missing geometries: " + string.Join(", ", missing));
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not find {Path.Combine(parts)} above '{AppContext.BaseDirectory}'.");
    }
}
