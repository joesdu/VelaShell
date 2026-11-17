using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>文件类工具:分段读取、内容搜索 / 文件查找、多处编辑与文件改动事件。</summary>
public sealed partial class AgentToolboxTests
{
    private static string NumberedLines(int count, string prefix = "line")
        => string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix}{i}\n"));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---- 分段读取 ----

    [TestMethod]
    public async Task ReadLocalFile_ReturnsNumberedRange_AndTellsHowToContinue()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "ten.txt"), NumberedLines(10));
            AgentToolbox toolbox = CreateLocalToolbox(context, root);

            string range = await InvokeAsync(toolbox, "read_local_file", new() { ["path"] = "ten.txt", ["offset"] = 3, ["limit"] = 2 });
            Assert.StartsWith("3\tline3\n4\tline4\n", range);
            Assert.DoesNotContain("line5", range);
            Assert.Contains("of 10", range);
            Assert.Contains("offset=5", range);

            string whole = await InvokeAsync(toolbox, "read_local_file", new() { ["path"] = "ten.txt" });
            Assert.AreEqual(string.Concat(Enumerable.Range(1, 10).Select(i => $"{i}\tline{i}\n")), whole,
                "整份读完时不该再带「继续读」的提示");

            string past = await InvokeAsync(toolbox, "read_local_file", new() { ["path"] = "ten.txt", ["offset"] = 50 });
            Assert.Contains("past the end", past);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>超过单次大小上限的文件不再整份拒绝:按行区间读其中一段;不带区间时按上限截断并指出下一段。</summary>
    [TestMethod]
    public async Task ReadLocalFile_LargerThanLimit_CanBeReadByRange()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "big.log"), NumberedLines(5000));
            AgentToolbox toolbox = CreateLocalToolbox(context, root);
            toolbox.LocalMaxFileBytes = 1024;

            string range = await InvokeAsync(toolbox, "read_local_file", new() { ["path"] = "big.log", ["offset"] = 4000, ["limit"] = 3 });
            Assert.StartsWith("4000\tline4000\n4001\tline4001\n4002\tline4002\n", range);
            Assert.Contains("of 5000", range);

            string head = await InvokeAsync(toolbox, "read_local_file", new() { ["path"] = "big.log" });
            Assert.StartsWith("1\tline1\n", head);
            Assert.IsLessThan(1200, head.Length, "返回文本总量仍受大小上限约束");
            Assert.Contains("to read more", head);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadLocalFile_RefusesBinary()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), [0x50, 0x4B, 0x00, 0x01, 0x0A, 0x41]);
            AgentToolbox toolbox = CreateLocalToolbox(context, root);
            string result = await InvokeAsync(toolbox, "read_local_file", new() { ["path"] = "blob.bin" });
            Assert.Contains("binary", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadRemoteFile_ReturnsNumberedRange()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddFile(session.SessionId, "/var/log/app.log", Encoding.UTF8.GetBytes(NumberedLines(30)));
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "read_remote_file",
            new() { ["path"] = "/var/log/app.log", ["offset"] = 10, ["limit"] = 2 });

        Assert.StartsWith("10\tline10\n11\tline11\n", result);
        Assert.Contains("of 30", result);
        Assert.Contains("offset=12", result);
    }

    // ---- 本地搜索 ----

    private static string CreateSearchWorkspace()
    {
        string root = CreateTempWorkspace();
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "using System;\nclass Foo\n{\n    // TODO foo\n}\n");
        File.WriteAllText(Path.Combine(root, "src", "deep", "b.cs"), "namespace X;\nrecord Foo2;\n");
        File.WriteAllText(Path.Combine(root, "src", "notes.txt"), "Foo in a text file\n");
        File.WriteAllText(Path.Combine(root, "bin", "c.cs"), "class Foo {}\n");
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "x.js"), "Foo\n");
        File.WriteAllBytes(Path.Combine(root, "src", "data.bin"), [.. Encoding.ASCII.GetBytes("Foo"), 0, 1, 2]);
        return root;
    }

    [TestMethod]
    public async Task SearchLocalFiles_FindsMatches_SkipsBinaryAndBuildDirectories()
    {
        using var context = new TestPluginContext();
        string root = CreateSearchWorkspace();
        try
        {
            AgentToolbox toolbox = CreateLocalToolbox(context, root, _ => throw new AssertFailedException("搜索是只读的,不该走审批"));

            string all = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "Foo" });
            Assert.Contains("src/a.cs:2: class Foo", all);
            Assert.Contains("src/deep/b.cs:2: record Foo2;", all);
            Assert.Contains("src/notes.txt:1: Foo in a text file", all);
            Assert.DoesNotContain("bin/c.cs", all);
            Assert.DoesNotContain("node_modules", all);
            Assert.DoesNotContain("data.bin", all, "二进制文件要跳过");

            string csOnly = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "Foo", ["glob"] = "*.cs" });
            Assert.DoesNotContain("notes.txt", csOnly);
            Assert.Contains("src/a.cs:2:", csOnly);

            string scoped = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "Foo", ["path"] = "src/deep" });
            Assert.Contains("src/deep/b.cs", scoped);
            Assert.DoesNotContain("src/a.cs", scoped);

            string regex = await InvokeAsync(toolbox, "search_local_files",
                new() { ["pattern"] = @"todo\s+FOO", ["isRegex"] = true, ["ignoreCase"] = true });
            Assert.Contains("src/a.cs:4:", regex);

            string context1 = await InvokeAsync(toolbox, "search_local_files",
                new() { ["pattern"] = "class Foo", ["glob"] = "src/*.cs", ["contextLines"] = 1 });
            Assert.Contains("src/a.cs-1- using System;", context1);
            Assert.Contains("src/a.cs:2: class Foo", context1);
            Assert.Contains("src/a.cs-3- {", context1);

            string limited = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "Foo", ["maxMatches"] = 1 });
            Assert.Contains("Stopped after 1 matches", limited);

            string none = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "no-such-text" });
            Assert.StartsWith("No matches", none);

            string invalid = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "(", ["isRegex"] = true });
            Assert.Contains("Invalid regular expression", invalid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SearchAndFindLocalFiles_RefuseOutsideWorkspace()
    {
        using var context = new TestPluginContext();
        string root = CreateSearchWorkspace();
        try
        {
            AgentToolbox toolbox = CreateLocalToolbox(context, root);
            string search = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "Foo", ["path"] = ".." });
            Assert.Contains("outside", search);
            string find = await InvokeAsync(toolbox, "find_local_files", new() { ["pattern"] = "*", ["path"] = Path.GetTempPath() });
            Assert.Contains("outside", find);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>工作区里的符号链接指向外面时,搜索与查找都不顺着它走。</summary>
    [TestMethod]
    public async Task SearchAndFindLocalFiles_DoNotFollowSymbolicLinks()
    {
        using var context = new TestPluginContext();
        string root = CreateSearchWorkspace();
        string outside = CreateTempWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.cs"), "Foo secret\n");
            string link = Path.Combine(root, "src", "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Windows 没开开发者模式时建不了符号链接,改建目录联接(同属名称代理类重解析点,不需要特权)
                if (!OperatingSystem.IsWindows() || !TryCreateJunction(link, outside))
                {
                    Assert.Inconclusive($"This environment cannot create symbolic links: {ex.Message}");
                }
            }
            AgentToolbox toolbox = CreateLocalToolbox(context, root);

            string search = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "secret" });
            Assert.StartsWith("No matches", search);

            string find = await InvokeAsync(toolbox, "find_local_files", new() { ["pattern"] = "secret.cs" });
            Assert.StartsWith("No files match", find);

            string direct = await InvokeAsync(toolbox, "search_local_files", new() { ["pattern"] = "secret", ["path"] = "src/linked" });
            Assert.Contains("symbolic link", direct);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [TestMethod]
    public async Task FindLocalFiles_MatchesGlobs()
    {
        using var context = new TestPluginContext();
        string root = CreateSearchWorkspace();
        try
        {
            AgentToolbox toolbox = CreateLocalToolbox(context, root);

            string cs = await InvokeAsync(toolbox, "find_local_files", new() { ["pattern"] = "*.cs" });
            Assert.AreEqual("src/a.cs\nsrc/deep/b.cs\n", cs, "bin/ 下的文件要跳过,同层按名称排序");

            string byPath = await InvokeAsync(toolbox, "find_local_files", new() { ["pattern"] = "src/*.{cs,txt}" });
            Assert.AreEqual("src/a.cs\nsrc/notes.txt\n", byPath, "含 / 的模式按相对路径匹配,* 不跨目录");

            string deep = await InvokeAsync(toolbox, "find_local_files", new() { ["pattern"] = "src/**/b.cs" });
            Assert.AreEqual("src/deep/b.cs\n", deep);

            string limited = await InvokeAsync(toolbox, "find_local_files", new() { ["pattern"] = "*", ["maxResults"] = 1 });
            Assert.Contains("More files match", limited);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("*.cs", "src/a.cs", true)]
    [DataRow("*.cs", "a.csx", false)]
    [DataRow("**/*.cs", "a.cs", true)]
    [DataRow("**/*.cs", "x/y/a.cs", true)]
    [DataRow("src/*.cs", "src/x/a.cs", false)]
    [DataRow("*.{ts,tsx}", "a.tsx", true)]
    [DataRow("file?.txt", "file1.txt", true)]
    public void GlobToRegex_FollowsGlobSemantics(string glob, string path, bool expected)
    {
        bool byPath = glob.Contains('/');
        string subject = byPath ? path : path[(path.LastIndexOf('/') + 1)..];
        Assert.AreEqual(expected, AgentToolbox.GlobToRegex(glob).IsMatch(subject));
    }

    // ---- 远端搜索 ----

    /// <summary>
    /// 远端搜索的命令由工具自己拼:模型给的每个值都是单引号包住的一个参数,pattern 走 -e、路径在 -- 之后,
    /// 所以注入不了第二条命令,也不必审批。
    /// </summary>
    [TestMethod]
    public async Task SearchRemoteFiles_RunsQuotedGrepWithoutApproval()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddDirectory(session.SessionId, "/srv/app");
        string? command = null;
        context.FakeRemoteExec.Handler = (_, cmd) =>
        {
            command = cmd;
            return "/srv/app/app.conf:3:port=8080\n";
        };
        int approvals = 0;
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ =>
            {
                approvals++;
                return Task.FromResult(false);
            }
        };

        string result = await InvokeAsync(toolbox, "search_remote_files", new()
        {
            ["pattern"] = "it's; rm -rf / #",
            ["path"] = "/srv/app",
            ["glob"] = "*.{conf,ini}",
            ["ignoreCase"] = true
        });

        Assert.Contains("/srv/app/app.conf:3:port=8080", result);
        Assert.AreEqual(0, approvals, "远端搜索是只读的,不该走审批");
        Assert.IsNotNull(command);
        Assert.StartsWith("'grep' '-rnIsH' '-i' '-F'", command);
        Assert.Contains("'-e' 'it'\\''s; rm -rf / #' '--' '/srv/app'", command);
        Assert.Contains("'--include=*.conf' '--include=*.ini'", command);
        Assert.Contains("'--exclude-dir=node_modules'", command);
        Assert.EndsWith("| head -n 100", command);
    }

    [TestMethod]
    public async Task SearchRemoteFiles_ReportsMissingPathAndRejectsMultilineInput()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string missing = await InvokeAsync(toolbox, "search_remote_files", new() { ["pattern"] = "x", ["path"] = "/nope" });
        Assert.Contains("No such path", missing);

        string multiline = await InvokeAsync(toolbox, "search_remote_files", new() { ["pattern"] = "a\nb", ["path"] = "/" });
        Assert.Contains("single-line", multiline);
        Assert.IsEmpty(context.FakeRemoteExec.Executed);
    }

    // ---- 多处编辑 ----

    [TestMethod]
    public async Task PatchLocalFile_AppliesSeveralEditsWithOneApproval()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        string file = Path.Combine(root, "app.ini");
        File.WriteAllText(file, "host=a\nport=1\nhost=a\n");
        try
        {
            var requests = new List<ApprovalRequest>();
            AgentToolbox toolbox = CreateLocalToolbox(context, root, request =>
            {
                requests.Add(request);
                return Task.FromResult(true);
            });

            string result = await InvokeAsync(toolbox, "patch_local_file", new()
            {
                ["path"] = "app.ini",
                ["edits"] = Json("""[{"oldText":"port=1","newText":"port=2"},{"oldText":"host=a","newText":"host=b","replaceAll":true}]""")
            });

            Assert.Contains("Patched", result);
            Assert.AreEqual("host=b\nport=2\nhost=b\n", File.ReadAllText(file));
            Assert.HasCount(1, requests, "多处编辑只审批一次");
            Assert.Contains("2 edits", requests[0].Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PatchLocalFile_FailingEditLeavesFileUntouched()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        string file = Path.Combine(root, "app.ini");
        File.WriteAllText(file, "host=a\nport=1\nhost=a\n");
        try
        {
            int approvals = 0;
            AgentToolbox toolbox = CreateLocalToolbox(context, root, _ =>
            {
                approvals++;
                return Task.FromResult(true);
            });

            string missing = await InvokeAsync(toolbox, "patch_local_file", new()
            {
                ["path"] = "app.ini",
                ["edits"] = Json("""[{"oldText":"port=1","newText":"port=2"},{"oldText":"nope","newText":"x"}]""")
            });
            Assert.Contains("edits[1].old_text was not found", missing);

            string ambiguous = await InvokeAsync(toolbox, "patch_local_file", new()
            {
                ["path"] = "app.ini",
                ["oldText"] = "host=a",
                ["newText"] = "host=b"
            });
            Assert.Contains("occurs 2 times", ambiguous);
            Assert.Contains("replaceAll", ambiguous);

            Assert.AreEqual("host=a\nport=1\nhost=a\n", File.ReadAllText(file));
            Assert.AreEqual(0, approvals, "校验不过就不该打扰用户");

            string all = await InvokeAsync(toolbox, "patch_local_file", new()
            {
                ["path"] = "app.ini",
                ["oldText"] = "host=a",
                ["newText"] = "host=b",
                ["replaceAll"] = true
            });
            Assert.Contains("2 replacement(s)", all);
            Assert.AreEqual("host=b\nport=1\nhost=b\n", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PatchRemoteFile_AppliesEditsArray()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddFile(session.SessionId, "/etc/app.conf", Encoding.UTF8.GetBytes("a=1\nb=2\n"));
        int approvals = 0;
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ =>
            {
                approvals++;
                return Task.FromResult(true);
            }
        };

        string result = await InvokeAsync(toolbox, "patch_remote_file", new()
        {
            ["path"] = "/etc/app.conf",
            ["edits"] = Json("""[{"oldText":"a=1","newText":"a=10"},{"oldText":"b=2","newText":"b=20"}]""")
        });

        Assert.Contains("Patched", result);
        Assert.AreEqual(1, approvals);
        Assert.AreEqual("a=10\nb=20\n", Encoding.UTF8.GetString(context.FakeRemoteFs.GetFile(session.SessionId, "/etc/app.conf")!));
    }

    // ---- 文件改动事件 ----

    [TestMethod]
    public async Task FileChanged_ReportsLocalWritePatchRenameAndDelete()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        try
        {
            AgentToolbox toolbox = CreateLocalToolbox(context, root);
            var changes = new List<FileChange>();
            toolbox.FileChanged = changes.Add;

            await InvokeAsync(toolbox, "write_local_file", new() { ["path"] = "a.txt", ["content"] = "one\n" });
            await InvokeAsync(toolbox, "write_local_file", new() { ["path"] = "a.txt", ["content"] = "two\n" });
            await InvokeAsync(toolbox, "patch_local_file", new() { ["path"] = "a.txt", ["oldText"] = "two", ["newText"] = "three" });
            await InvokeAsync(toolbox, "rename_local_path", new() { ["oldPath"] = "a.txt", ["newPath"] = "b.txt" });
            await InvokeAsync(toolbox, "delete_local_path", new() { ["path"] = "b.txt" });
            await InvokeAsync(toolbox, "make_local_directory", new() { ["path"] = "dir" });

            Assert.HasCount(5, changes, "建目录不是文件改动,不发事件");
            Assert.AreEqual(new FileChange("write_local_file", "local:local", "a.txt", FileChangeKind.Created, null, "one\n"), changes[0]);
            Assert.AreEqual(new FileChange("write_local_file", "local:local", "a.txt", FileChangeKind.Modified, "one\n", "two\n"), changes[1]);
            Assert.AreEqual(new FileChange("patch_local_file", "local:local", "a.txt", FileChangeKind.Modified, "two\n", "three\n"), changes[2]);
            Assert.AreEqual(new FileChange("rename_local_path", "local:local", "b.txt", FileChangeKind.Renamed, "three\n", "three\n", "a.txt"), changes[3]);
            Assert.AreEqual(new FileChange("delete_local_path", "local:local", "b.txt", FileChangeKind.Deleted, "three\n", null), changes[4]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileChanged_NotRaisedWhenDenied_AndSubscriberFailureDoesNotBreakTheTool()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        try
        {
            int raised = 0;
            AgentToolbox denied = CreateLocalToolbox(context, root, _ => Task.FromResult(false));
            denied.FileChanged = _ => raised++;
            await InvokeAsync(denied, "write_local_file", new() { ["path"] = "a.txt", ["content"] = "x" });
            Assert.AreEqual(0, raised, "被拒绝的写入没有发生,不该发事件");

            AgentToolbox toolbox = CreateLocalToolbox(context, root);
            toolbox.FileChanged = _ => throw new InvalidOperationException("ui is broken");
            string result = await InvokeAsync(toolbox, "write_local_file", new() { ["path"] = "a.txt", ["content"] = "x" });
            Assert.Contains("Wrote", result);
            Assert.AreEqual("x", File.ReadAllText(Path.Combine(root, "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileChanged_ReportsRemoteWritePatchAndRename()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddDirectory(session.SessionId, "/etc");
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            Approval = ApprovalMode.Bypass
        };
        var changes = new List<FileChange>();
        toolbox.FileChanged = changes.Add;
        string location = "remote:" + session.SessionId;

        await InvokeAsync(toolbox, "write_remote_file", new() { ["path"] = "/etc/app.conf", ["content"] = "a=1\n" });
        await InvokeAsync(toolbox, "write_remote_file", new() { ["path"] = "/etc/app.conf", ["content"] = "a=2\n" });
        await InvokeAsync(toolbox, "patch_remote_file", new() { ["path"] = "/etc/app.conf", ["oldText"] = "a=2", ["newText"] = "a=3" });
        await InvokeAsync(toolbox, "rename_remote_path", new() { ["oldPath"] = "/etc/app.conf", ["newPath"] = "/etc/app.conf.bak" });

        Assert.HasCount(4, changes);
        Assert.AreEqual(new FileChange("write_remote_file", location, "/etc/app.conf", FileChangeKind.Created, null, "a=1\n"), changes[0]);
        Assert.AreEqual(new FileChange("write_remote_file", location, "/etc/app.conf", FileChangeKind.Modified, "a=1\n", "a=2\n"), changes[1]);
        Assert.AreEqual(new FileChange("patch_remote_file", location, "/etc/app.conf", FileChangeKind.Modified, "a=2\n", "a=3\n"), changes[2]);
        Assert.AreEqual(new FileChange("rename_remote_path", location, "/etc/app.conf.bak", FileChangeKind.Renamed, "a=3\n", "a=3\n", "/etc/app.conf"), changes[3]);
    }

    /// <summary>子 Agent 的改动也要汇总到聊天面板:子工具箱没有自己的订阅时沿用父工具箱的。</summary>
    [TestMethod]
    public async Task FileChanged_SubagentEditsReachParentSubscriber()
    {
        using var context = new TestPluginContext();
        string root = CreateTempWorkspace();
        File.WriteAllText(Path.Combine(root, "a.txt"), "old\n");
        try
        {
            await using AgentToolbox parent = CreateLocalToolbox(context, root);
            var changes = new List<FileChange>();
            parent.FileChanged = changes.Add;
            await using CommandJobRunScope run = parent.BeginRun("test", "turn");
            await using AgentToolbox child = parent.CreateSubagentToolbox();
            AIFunction patch = child.CreateSubagentTools(true, run).OfType<AIFunction>().Single(t => t.Name == "patch_local_file");

            await patch.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["oldText"] = "old",
                ["newText"] = "new"
            }), CancellationToken.None);

            Assert.HasCount(1, changes);
            Assert.AreEqual("new\n", changes[0].After);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
