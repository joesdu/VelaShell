using System.Text;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.Logging;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 每插件一份日志(插件管理页「查看日志」):写进去、读尾部、轮转与上限、卸载时清掉,
/// 以及宿主侧几个来源(插件经日志接口写的、宿主替它记的激活)真的落到了这份文件里。
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Plugins")]
public sealed class PluginLogFilesTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 子进程退出与文件释放是异步的,清不掉留给临时目录。
        }
    }

    private string LogDir => Path.Combine(_root, "logs", "plugins");

    [TestMethod]
    public void Append_ThenReadTail_ReturnsTheLinesWithTimestamps()
    {
        PluginLogFiles files = new(LogDir);

        files.Append("velashell.redis", "[Info] connected");
        files.Append("velashell.redis", $"[Error] boom — System.Exception: boom{Environment.NewLine}   at Foo.Bar()");
        PluginLogTail tail = files.ReadTail("velashell.redis");

        Assert.AreEqual(Path.Combine(LogDir, "velashell.redis.log"), tail.FilePath);
        Assert.IsFalse(tail.Truncated);
        Assert.Contains("[Info] connected", tail.Text);
        Assert.Contains("   at Foo.Bar()", tail.Text, "异常堆栈的续行要原样保留 —— 按前缀筛宿主日志正是丢在这里");
        StringAssert.Matches(tail.Text, new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[Info\]"));
    }

    [TestMethod]
    public void NothingWrittenYet_IsAnEmptyTailWithThePath()
    {
        PluginLogTail tail = new PluginLogFiles(LogDir).ReadTail("velashell.redis");

        Assert.AreEqual(string.Empty, tail.Text);
        Assert.AreEqual(Path.Combine(LogDir, "velashell.redis.log"), tail.FilePath);
    }

    /// <summary>写满上限轮转成 .log.1,只留这一份旧的:刷屏的插件也只占两倍上限。</summary>
    [TestMethod]
    public void FullFile_RotatesKeepingOneOlderCopy()
    {
        PluginLogFiles files = new(LogDir, maxBytes: 1024);
        string line = new('x', 200);
        for (int i = 0; i < 40; i++)
        {
            files.Append("p", $"{i:D2} {line}");
        }

        Assert.IsTrue(File.Exists(Path.Combine(LogDir, "p.log.1")));
        Assert.IsFalse(File.Exists(Path.Combine(LogDir, "p.1.log")), "轮转件不能叫 p.1.log:那是插件 p.1 的当前日志");
        Assert.IsFalse(File.Exists(Path.Combine(LogDir, "p.log.2")));
        Assert.IsLessThanOrEqualTo(1024L, new FileInfo(Path.Combine(LogDir, "p.log")).Length);
        Assert.IsLessThanOrEqualTo(1024L, new FileInfo(Path.Combine(LogDir, "p.log.1")).Length);
        Assert.Contains("39 ", files.ReadTail("p").Text, "最新的一行在尾部");
    }

    /// <summary>
    /// <b>id 只差一个 <c>.1</c> 的两个插件(<c>a.b</c> 与 <c>a.b.1</c>)各写各的</b>:<c>a.b</c> 轮转不盖掉 <c>a.b.1</c> 的日志,
    /// 「查看日志」不把别人的行当自己的前文,卸载 <c>a.b</c> 也不删 <c>a.b.1</c> 的。
    /// <para>
    /// 轮转名原先是 <c>&lt;id&gt;.1.log</c>,正好是 <c>a.b.1</c> 的当前日志:一个插件就能往另一个插件的「查看日志」里塞几行假的 <c>[Host]</c>。
    /// </para>
    /// </summary>
    [TestMethod]
    public void TwoIdsOneDotOneApart_RotateReadAndDeleteWithoutTouchingEachOther()
    {
        PluginLogFiles files = new(LogDir, maxBytes: 1024);
        files.Append("a.b.1", "[Host] Activated v9.9.9 —— 这是 a.b.1 自己的行");
        string line = new('x', 200);
        for (int i = 0; i < 20; i++)
        {
            files.Append("a.b", $"{i:D2} {line}");
        }

        Assert.Contains("a.b.1 自己的行", File.ReadAllText(Path.Combine(LogDir, "a.b.1.log")), "a.b 的轮转盖掉了 a.b.1 的日志");
        Assert.DoesNotContain("a.b.1 自己的行", files.ReadTail("a.b").Text, "a.b 的「查看日志」把 a.b.1 的行当成了自己的前文");
        Assert.DoesNotContain(" xxxx", files.ReadTail("a.b.1").Text, "a.b.1 的「查看日志」里混进了 a.b 的行");

        files.Delete("a.b");

        Assert.IsTrue(File.Exists(Path.Combine(LogDir, "a.b.1.log")), "卸载 a.b 删掉了 a.b.1 的日志");
        Assert.Contains("a.b.1 自己的行", files.ReadTail("a.b.1").Text);
    }

    /// <summary>
    /// <b>启动清扫</b>:不在名单上的插件的日志删掉(插件目录在宿主没运行时被删掉也算卸载);
    /// 旧版本的轮转件 <c>&lt;id&gt;.1.log</c> 改名接到 <c>&lt;id&gt;.log.1</c> 上,「查看日志」的前文不丢;
    /// <c>x.1</c> 也装着时那个文件是它的当前日志,不动;不像插件日志的文件一律不碰。
    /// </summary>
    [TestMethod]
    public void SweepUninstalled_DeletesOrphanLogs_AndAdoptsTheLegacyRotatedName()
    {
        Directory.CreateDirectory(LogDir);
        void Write(string name, string text) => File.WriteAllText(Path.Combine(LogDir, name), text + "\n");
        Write("kept.log", "kept-current");
        Write("kept.log.1", "kept-rotated");
        Write("old.log", "old-current");
        Write("old.1.log", "old-legacy-rotated");
        Write("both.log", "both-current");
        Write("both.1.log", "both.1-current");
        Write("dup.log", "dup-current");
        Write("dup.log.1", "dup-rotated-new");
        Write("dup.1.log", "dup-legacy-rotated");
        Write("ghost.log", "ghost-current");
        Write("ghost.log.1", "ghost-rotated");
        Write("ghost.1.log", "ghost-legacy-rotated");
        Write("notes.txt", "not a plugin log");
        PluginLogFiles files = new(LogDir);

        files.SweepUninstalled(new HashSet<string>(StringComparer.Ordinal) { "kept", "old", "both", "both.1", "dup" });

        string[] left = [.. Directory.EnumerateFiles(LogDir).Select(Path.GetFileName).OfType<string>()];
        Assert.AreSequenceEqual(
            ["kept.log", "kept.log.1", "old.log", "old.log.1", "both.log", "both.1.log", "dup.log", "dup.log.1", "notes.txt"],
            left, SequenceOrder.InAnyOrder);
        Assert.AreEqual("old-legacy-rotated\nold-current\n", files.ReadTail("old").Text, "旧轮转件改了名,前文接上");
        Assert.AreEqual("dup-rotated-new\n", File.ReadAllText(Path.Combine(LogDir, "dup.log.1")), "新轮转件已经有了:留新的,删旧的");
        Assert.AreEqual("both.1-current\n", File.ReadAllText(Path.Combine(LogDir, "both.1.log")), "both.1 装着:那是它的当前日志");
    }

    /// <summary>
    /// <b>插件管理器启动时按"盘上还有哪些插件"清扫日志目录</b>:插件目录在宿主没运行时被删掉,
    /// 卸载那一路的删除没机会跑,它的日志原先就永远留着。被禁用的插件仍在盘上,日志保留。
    /// </summary>
    [TestMethod]
    public async Task Start_SweepsTheLogsOfPluginsRemovedWhileVelaShellWasNotRunning()
    {
        string plugins = Path.Combine(_root, "plugins");
        string dir = Path.Combine(plugins, "kept");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            """{ "id": "acme.kept", "version": "1.0.0", "displayName": "Kept", "entry": "Kept.dll" }""");
        File.WriteAllText(Path.Combine(dir, ".disabled"), "");
        Directory.CreateDirectory(LogDir);
        File.WriteAllText(Path.Combine(LogDir, "acme.kept.log"), "kept\n");
        File.WriteAllText(Path.Combine(LogDir, "acme.kept.1.log"), "kept-legacy-rotated\n");
        File.WriteAllText(Path.Combine(LogDir, "ghost.plugin.log"), "ghost\n");
        File.WriteAllText(Path.Combine(LogDir, "ghost.plugin.log.1"), "ghost-rotated\n");
        await using PluginManager manager = new(new()
        {
            PluginRoots = [plugins],
            DataRootDirectory = Path.Combine(_root, "plugin-data"),
            HostVersion = "1.0.0",
            LogFiles = new PluginLogFiles(LogDir),
        });

        await manager.StartAsync();
        // 清扫在启动后的后台家务里跑:给它一点时间。
        for (int i = 0; i < 100 && File.Exists(Path.Combine(LogDir, "ghost.plugin.log")); i++)
        {
            await Task.Delay(50);
        }

        Assert.IsFalse(File.Exists(Path.Combine(LogDir, "ghost.plugin.log")), "已卸载插件的日志应删除");
        Assert.IsFalse(File.Exists(Path.Combine(LogDir, "ghost.plugin.log.1")), "已卸载插件的轮转件应删除");
        Assert.IsTrue(File.Exists(Path.Combine(LogDir, "acme.kept.log")), "禁用 ≠ 卸载,日志保留");
        Assert.Contains("kept-legacy-rotated", manager.ReadLogTail("acme.kept").Text, "旧版本的轮转件要改名接上,前文不丢");
    }

    /// <summary>当前那份不够一屏时,从轮转下来的那份补上前文;截在中间的那一行丢掉,不出半行乱码。</summary>
    [TestMethod]
    public void Tail_SpansTheRotatedFileAndDropsThePartialFirstLine()
    {
        Directory.CreateDirectory(LogDir);
        File.WriteAllText(Path.Combine(LogDir, "p.log.1"), "old-1 中文\nold-2 中文\nold-3\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(LogDir, "p.log"), "new-1\nnew-2\n", Encoding.UTF8);
        PluginLogFiles files = new(LogDir);

        PluginLogTail tail = files.ReadTail("p", maxBytes: 20);

        Assert.IsTrue(tail.Truncated);
        Assert.AreEqual("old-3\nnew-1\nnew-2\n", tail.Text);
    }

    [TestMethod]
    public void Delete_RemovesBothFiles()
    {
        PluginLogFiles files = new(LogDir, maxBytes: 64);
        for (int i = 0; i < 10; i++)
        {
            files.Append("p", "some line that fills the file");
        }

        files.Delete("p");

        Assert.IsFalse(File.Exists(Path.Combine(LogDir, "p.log")));
        Assert.IsFalse(File.Exists(Path.Combine(LogDir, "p.log.1")));
        files.Append("p", "after");
        Assert.Contains("after", files.ReadTail("p").Text, "删完照样能接着写");
    }

    /// <summary>一个拼得出目录外路径的 id 不落文件(插件 id 本是 [a-z0-9.-],这里再守一道)。</summary>
    [TestMethod]
    [DataRow("../evil")]
    [DataRow("a/b")]
    [DataRow("")]
    public void UnsafeId_IsNeverWritten(string id)
    {
        PluginLogFiles files = new(LogDir);

        files.Append(id, "x");

        Assert.IsNull(files.PathFor(id));
        Assert.IsNull(files.ReadTail(id).FilePath);
        Assert.IsFalse(Directory.Exists(LogDir) && Directory.EnumerateFileSystemEntries(LogDir).Any());
    }

    /// <summary>日志接口与隔离进程的输出都进同一份文件;宿主 Trace 那份照旧(不在这里验)。</summary>
    [TestMethod]
    public void Logger_WritesLevelsAndProcessOutputToThePluginFile()
    {
        PluginLogFiles files = new(LogDir);
        TracePluginLogger logger = new("velashell.redis", files);

        logger.Write(PluginLogLevel.Warning, "slow server", new TimeoutException("5s"));
        logger.WriteProcessOutput(isError: true, "Unhandled exception. System.InvalidOperationException: nope");
        logger.WriteHostEvent("Crashed: Plugin process exited unexpectedly.");

        string text = files.ReadTail("velashell.redis").Text;
        Assert.Contains("[Warning] slow server — System.TimeoutException: 5s", text);
        Assert.Contains("[stderr] Unhandled exception. System.InvalidOperationException: nope", text);
        Assert.Contains("[Host] Crashed: Plugin process exited unexpectedly.", text);
    }

    /// <summary>
    /// 真插件走一遍:激活时插件经日志接口写的那一行(隔离进程经 RPC 转回来)与宿主记的「已激活」都在它自己的日志里,
    /// 插件管理器按 id 读得出来。
    /// </summary>
    [TestMethod]
    [DataRow("inProcess")]
    [DataRow("isolated")]
    public async Task RealPlugin_ActivationLandsInItsOwnLog(string hostMode)
    {
        string plugins = Path.Combine(_root, "plugins");
        string dir = Path.Combine(plugins, "fixture");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll", "hostMode": "{{hostMode}}" }
            """);
        await using PluginManager manager = new(new()
        {
            PluginRoots = [plugins],
            DataRootDirectory = Path.Combine(_root, "plugin-data"),
            HostVersion = "1.0.0",
            ActivationTimeout = TimeSpan.FromSeconds(30),
            IsolatedStartupTimeout = TimeSpan.FromSeconds(60),
            DeactivationTimeout = TimeSpan.FromSeconds(10),
            LogFiles = new PluginLogFiles(LogDir),
        });
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);

        // 隔离插件的日志是经管道的通知转回来的,与激活应答不在一条确认链上:给它一点时间。
        string text = string.Empty;
        for (int i = 0; i < 100 && !text.Contains("Test fixture activated", StringComparison.Ordinal); i++)
        {
            text = manager.ReadLogTail("velashell.test-fixture").Text;
            await Task.Delay(100);
        }

        Assert.Contains("[Information] Test fixture activated", text);
        Assert.Contains($"[Host] Activated v0.1.0", text);
        Assert.AreEqual(LogDir, manager.LogDirectory);
    }
}
