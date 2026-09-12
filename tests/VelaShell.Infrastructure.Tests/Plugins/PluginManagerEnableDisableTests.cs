using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>插件管理页支撑:启用/禁用 + Changed 事件 + .disabled 标记持久。</summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginManagerEnableDisableTests
{
    private string _root = null!;
    private string _dataRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
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

    private string StageFixture()
    {
        string dir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll" }
            """);
        return dir;
    }

    private PluginManager CreateManager() => new(new()
    {
        PluginRoots = [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0",
        CommandsFactory = (_, _) => new RecordingCommands()
    });

    [TestMethod]
    public async Task Disable_StopsActivePlugin_WritesMarker_AndRaisesChanged()
    {
        string dir = StageFixture();
        PluginManager manager = CreateManager();
        int changes = 0;
        manager.Changed += () => Interlocked.Increment(ref changes);
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State);

        await manager.DisableAsync("velashell.test-fixture");
        Assert.AreEqual(PluginState.Disabled, manager.Plugins.Single().State);
        Assert.IsTrue(File.Exists(Path.Combine(dir, ".disabled")), "禁用应落 .disabled 标记(重启仍禁用)");
        Assert.IsGreaterThanOrEqualTo(1, changes);

        await manager.EnableAsync("velashell.test-fixture");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State);
        Assert.IsFalse(File.Exists(Path.Combine(dir, ".disabled")), "启用应移除 .disabled 标记");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Disable_BundledPlugin_PersistsInDataRoot_AcrossRestart()
    {
        // 应用自带插件装在安装目录里,装好之后那里多半只读、升级时还会被整体替换:
        // 禁用状态若只写目录里的 .disabled,写失败被静默吞掉,重启后插件又回到"运行中"。
        string dir = StageFixture();
        string stateFile = Path.Combine(_dataRoot, "plugins.disabled");
        PluginManager CreateBundledManager() => new(new()
        {
            PluginRoots = [_root],
            UserPluginRoot = Path.Combine(Path.GetDirectoryName(_root)!, "user-plugins"),
            DisabledStateFile = stateFile,
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands()
        });

        PluginManager manager = CreateBundledManager();
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        await manager.DisableAsync(TestFixturePlugin.Id);
        Assert.IsFalse(File.Exists(Path.Combine(dir, ".disabled")), "自带插件的禁用状态不该写进安装目录");
        CollectionAssert.Contains(File.ReadAllLines(stateFile), TestFixturePlugin.Id);
        await manager.DisposeAsync();

        PluginManager restarted = CreateBundledManager();
        await restarted.StartAsync();
        Assert.AreEqual(PluginState.Disabled, restarted.Plugins.Single().State, "重启后应仍是禁用");

        await restarted.EnableAsync(TestFixturePlugin.Id);
        Assert.AreEqual(PluginState.Active, restarted.Plugins.Single().State, restarted.Plugins.Single().Error);
        CollectionAssert.DoesNotContain(File.ReadAllLines(stateFile), TestFixturePlugin.Id);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Disable_InProcessPlugin_UnloadsItsAssemblies()
    {
        // 只调 Unload() 不够:可收集 ALC 要等 GC 真把它收走才释放内存,
        // 而原先没有任何东西去催这一轮 GC —— 表现就是"插件全关了,内存纹丝不动"。
        StageFixture();
        PluginManager manager = CreateManager();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.UnloadObserved += (id, collected) =>
        {
            if (id == TestFixturePlugin.Id)
            {
                observed.TrySetResult(collected);
            }
        };
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);

        await manager.DisableAsync(TestFixturePlugin.Id);

        bool collected = await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(collected, "停用后插件的可收集 ALC 应被回收");
        await manager.DisposeAsync();
    }

    /// <summary>可控的界面数来源:测试直接拨数字、手动发变化通知。</summary>
    private sealed class FakeSurfaceSource : IPluginSurfaceSource
    {
        public int Count { get; set; }

        public event Action? SurfacesChanged;

        public int CountOpenSurfaces(PluginManifest manifest) => Count;

        public void Raise() => SurfacesChanged?.Invoke();
    }

    [TestMethod]
    public async Task OpenSurfaceCount_TracksSources_AndRaisesChanged()
    {
        // 进程内插件关掉全部标签后仍然常驻(有意为之),管理页要能据此显示"后台运行"而不是"运行中"。
        StageFixture();
        var source = new FakeSurfaceSource();
        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands(),
            SurfaceSources = [source]
        });
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        Assert.AreEqual(0, manager.GetOpenSurfaceCount(TestFixturePlugin.Id), "没有开着的标签");

        int changes = 0;
        manager.Changed += () => Interlocked.Increment(ref changes);
        source.Count = 2;
        source.Raise();
        Assert.AreEqual(2, manager.GetOpenSurfaceCount(TestFixturePlugin.Id));
        Assert.AreEqual(1, changes, "标签开关要让管理页刷新");

        await manager.DisableAsync(TestFixturePlugin.Id);
        Assert.AreEqual(0, manager.GetOpenSurfaceCount(TestFixturePlugin.Id), "未激活的插件不算开着任何界面");

        await manager.DisposeAsync();
        changes = 0;
        source.Raise();
        Assert.AreEqual(0, changes, "释放后不应再挂在来源上");
    }

    [TestMethod]
    public async Task DisabledMarker_KeepsPluginDisabledAcrossRestart()
    {
        string dir = StageFixture();
        File.WriteAllText(Path.Combine(dir, ".disabled"), "");

        PluginManager manager = CreateManager();
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Disabled, manager.Plugins.Single().State, "盘上有 .disabled 标记者启动即禁用");
        await manager.DisposeAsync();
    }
}
