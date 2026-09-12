using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Manifest;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>惰性激活(蓝图 D7)与空闲回收(蓝图 04)的行为验证。</summary>
// 同 IsolatedPluginTests:真的拉起/回收子进程并按墙钟等收敛,必须串行,
// 否则空闲回收的等待窗口会被并行负载吃掉。
[TestClass]
[DoNotParallelize]
[TestCategory("Plugins")]
public class LazyActivationTests
{
    private static readonly string[] GhostPluginOnly = ["ghost.plugin"];

    private string _root = null!;
    private string _dataRoot = null!;
    private RecordingCommands _commands = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
        _commands = new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    private void StageFixture(string manifestExtras)
    {
        string dir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll",
              "contributes": { "commands": [
                { "id": "velashell.test-fixture.list-sessions", "title": "Test Fixture: List Sessions", "category": "Test Fixture" }
              ] }{{manifestExtras}} }
            """);
    }

    private PluginManager CreateManager(TimeSpan? idleTimeout = null, TimeSpan? inProcessIdleTimeout = null,
        IReadOnlyList<IPluginSurfaceSource>? surfaces = null) => new(new()
    {
        PluginRoots = [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0",
        ActivationTimeout = TimeSpan.FromSeconds(30),
        IsolatedStartupTimeout = TimeSpan.FromSeconds(60), // 惰性激活也真的拉子进程:冷启动预算与激活分开
        DeactivationTimeout = TimeSpan.FromSeconds(10),
        CommandsFactory = (_, _) => _commands,
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(15),
        // 默认关掉进程内回收:其余用例断言的是激活后的状态,不该和一个 1 分钟的计时器赛跑。
        InProcessIdleTimeout = inProcessIdleTimeout ?? Timeout.InfiniteTimeSpan,
        IdleCheckInterval = TimeSpan.FromMilliseconds(300),
        SurfaceSources = surfaces ?? []
    });

    /// <summary>可控的界面数来源:测试直接拨数字。</summary>
    private sealed class FakeSurfaceSource : IPluginSurfaceSource
    {
        public int Count { get; set; }

        public event Action? SurfacesChanged
        {
            add { }
            remove { }
        }

        public int CountOpenSurfaces(PluginManifest manifest) => Count;
    }

    [TestMethod]
    public async Task InProcessLazyPlugin_IdleRecycles_ThenReactivatesOnTrigger()
    {
        // 进程内插件原先激活后一律常驻:用过一次、关掉标签,它的程序集与对象就永远占着宿主内存。
        StageFixture(""", "activationEvents": ["onCommand:velashell.test-fixture.list-sessions"]""");
        PluginManager manager = CreateManager(inProcessIdleTimeout: TimeSpan.FromSeconds(1));
        await manager.StartAsync();
        await _commands.RunAsync("velashell.test-fixture.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);

        await WaitForAsync(() => manager.Plugins.Single().State == PluginState.Discovered,
            TimeSpan.FromSeconds(30), "没有开着的界面、也没有执行中的命令时,进程内惰性插件应被回收");

        // 占位命令已回挂:再触发即重新激活。
        await _commands.RunAsync("velashell.test-fixture.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InProcessLazyPlugin_WithOpenSurface_IsNotRecycled_UntilItCloses()
    {
        StageFixture(""", "activationEvents": ["onCommand:velashell.test-fixture.list-sessions"]""");
        var surfaces = new FakeSurfaceSource { Count = 1 };
        PluginManager manager = CreateManager(inProcessIdleTimeout: TimeSpan.FromSeconds(1), surfaces: [surfaces]);
        await manager.StartAsync();
        await _commands.RunAsync("velashell.test-fixture.list-sessions");

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, "还开着标签就不能回收");

        surfaces.Count = 0;
        await WaitForAsync(() => manager.Plugins.Single().State == PluginState.Discovered,
            TimeSpan.FromSeconds(30), "标签全关、空闲过阈值后应被回收");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task StartupPlugin_IsNeverRecycledInProcess()
    {
        // onStartup 的插件就是要常驻(AI 助手的 IM 桥接靠它随时收消息),空闲回收不碰它。
        StageFixture("");
        PluginManager manager = CreateManager(inProcessIdleTimeout: TimeSpan.FromMilliseconds(500));
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task LazyPlugin_StaysDiscovered_UntilPlaceholderCommandTriggersActivation()
    {
        StageFixture(""", "activationEvents": ["onCommand:velashell.test-fixture.list-sessions"]""");
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        // 发现期:不装载程序集,只有清单声明的占位命令。
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State);
        Assert.IsFalse(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")),
            "惰性插件在触发前不应有任何激活痕迹");
        Assert.HasCount(1, _commands.Registered);

        // 触发占位命令 → 激活 → 真实命令替换占位并补齐其余注册。
        await _commands.RunAsync("velashell.test-fixture.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        Assert.IsTrue(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")));
        Assert.IsGreaterThan(1, _commands.Registered.Count, "激活后应出现插件注册的全部真实命令");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task IsolatedRecyclablePlugin_IdleRecycles_ThenReactivatesOnTrigger()
    {
        StageFixture(""", "hostMode": "isolated", "idlePolicy": "recyclable" """);
        PluginManager manager = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        int firstPid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;

        // 静默等待:超过空闲阈值后应被回收(进程消失、状态回到 Discovered、占位命令回挂)。
        await WaitForAsync(() => manager.Plugins.Single().State == PluginState.Discovered
                                 && manager.GetIsolatedProcessId("velashell.test-fixture") is null,
            TimeSpan.FromSeconds(30), "空闲的可回收插件应被停用并回收进程");

        // 再次触发 → 重新拉起新进程。
        await _commands.RunAsync("velashell.test-fixture.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        int secondPid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;
        Assert.AreNotEqual(firstPid, secondPid);

        await manager.DisposeAsync();
    }

    private sealed class RecordingDataStore : IPluginDataStore
    {
        public List<string> Present { get; } = [];
        public List<string> Purged { get; } = [];

        public PluginSdk.Storage.IPluginStorage CreateStorage(string pluginId) => new InMemoryStorage();
        public PluginSdk.Secrets.ISecretsApi CreateSecrets(string pluginId) => new FakeSecrets();
        public PluginSdk.TimeSeries.ITimeSeriesApi CreateTimeSeries(string pluginId) => new InMemoryTimeSeries();

        public Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([.. Present]);

        public Task PurgeAsync(string pluginId, CancellationToken cancellationToken = default)
        {
            Purged.Add(pluginId);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Start_PurgesDataOfUninstalledPlugins_KeepsInstalledAndDisabled()
    {
        StageFixture("");
        // 盘上还有一个被禁用的插件(数据必须保留)。
        string disabledDir = Path.Combine(_root, "disabled-one");
        Directory.CreateDirectory(disabledDir);
        File.WriteAllText(Path.Combine(disabledDir, "plugin.json"),
            """{ "id": "acme.disabled", "version": "1.0.0", "displayName": "D", "entry": "D.dll" }""");
        File.WriteAllText(Path.Combine(disabledDir, ".disabled"), "");

        // 数据侧:已卸载的 ghost 在 DB 与数据目录都留有数据。
        var dataStore = new RecordingDataStore();
        dataStore.Present.AddRange(["velashell.test-fixture", "acme.disabled", "ghost.plugin"]);
        Directory.CreateDirectory(Path.Combine(_dataRoot, "ghost.plugin"));
        File.WriteAllText(Path.Combine(_dataRoot, "ghost.plugin", "leftover.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dataRoot, "acme.disabled"));

        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => _commands,
            DataStore = dataStore
        });
        await manager.StartAsync();

        Assert.AreSequenceEqual(GhostPluginOnly, dataStore.Purged, "只清除已卸载插件的 DB 数据");
        Assert.IsFalse(Directory.Exists(Path.Combine(_dataRoot, "ghost.plugin")), "已卸载插件的数据目录应删除");
        Assert.IsTrue(Directory.Exists(Path.Combine(_dataRoot, "acme.disabled")), "禁用 ≠ 卸载,数据保留");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public void Manifest_RejectsUnknownActivationEvents_AndForeignCommandIds()
    {
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onFileOpen:*.png"] }
            """), "未知激活事件必须拒绝");
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onCommand:other.plugin.cmd"],
              "contributes": { "commands": [ { "id": "other.plugin.cmd", "title": "T" } ] } }
            """), "越界命令前缀必须拒绝");
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onCommand:a.b.cmd"] }
            """), "onCommand 必须有对应的 contributes.commands 占位声明");

        PluginManifest ok = PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll",
              "activationEvents": ["onCommand:a.b.cmd"],
              "contributes": { "commands": [ { "id": "a.b.cmd", "title": "T", "category": "C" } ] },
              "idlePolicy": "recyclable" }
            """);
        Assert.IsFalse(ok.ActivatesOnStartup);
        Assert.AreEqual(PluginIdlePolicy.Recyclable, ok.IdlePolicy);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }
}
