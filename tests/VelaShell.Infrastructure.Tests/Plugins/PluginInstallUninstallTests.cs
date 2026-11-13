using System.IO.Compression;
using System.Security.Cryptography;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Packaging;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>卸载与 .vpx 安装:仅用户目录可卸载,zip-slip 防护,覆盖安装保数据,卸载才清数据。</summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginInstallUninstallTests
{
    private string _appRoot = null!;
    private string _userRoot = null!;
    private string _dataRoot = null!;

    /// <summary>
    /// 测试自备的 SonnetDB(信任库/安装回执)所在处。**必须落在 <see cref="_dataRoot" /> 之外**:
    /// PluginManager 启动时会把数据根下每个不对应已装插件的子目录当作卸载残留整个删掉
    /// (见 PurgeUninstalledDataAsync),而这些库的目录名不是插件 id。Windows 上删不动仍被打开的
    /// 文件,目录侥幸留存;Linux 允许删掉正被打开的文件,于是引擎 Dispose 回写 catalog 时
    /// 撞上 DirectoryNotFoundException —— 同一份测试在两个平台上的结局不同,根子在放错了地方。
    /// </summary>
    private string _trustDbRoot = null!;

    private RecordingDataStore _dataStore = null!;

    private sealed class RecordingDataStore : IPluginDataStore
    {
        public List<string> Purged { get; } = [];
        public PluginSdk.Storage.IPluginStorage CreateStorage(string pluginId) => new InMemoryStorage();
        public PluginSdk.Secrets.ISecretsApi CreateSecrets(string pluginId) => new FakeSecrets();
        public PluginSdk.TimeSeries.ITimeSeriesApi CreateTimeSeries(string pluginId) => new InMemoryTimeSeries();
        public Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task PurgeAsync(string pluginId, CancellationToken cancellationToken = default)
        {
            Purged.Add(pluginId);
            return Task.CompletedTask;
        }
    }

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _appRoot = Path.Combine(baseDir, "app-plugins");
        _userRoot = Path.Combine(baseDir, "user-plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        _trustDbRoot = Path.Combine(baseDir, "test-dbs");
        Directory.CreateDirectory(_appRoot);
        Directory.CreateDirectory(_userRoot);
        Directory.CreateDirectory(_trustDbRoot);
        _dataStore = new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_appRoot)!, recursive: true);
        }
        catch
        {
            // ALC 卸载异步,dll 偶尔还锁着:留给临时目录。
        }
    }

    private PluginManager CreateManager(PluginTrustRepository? trustRepository = null,
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry? protocolRegistry = null,
        Core.Services.IBackgroundActivityService? activity = null) => new(new()
        {
            PluginRoots = [_appRoot, _userRoot],
            DataRootDirectory = _dataRoot,
            UserPluginRoot = _userRoot,
            TrustRepository = trustRepository,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands(),
            DataStore = _dataStore,
            ProtocolRegistry = protocolRegistry,
            Activity = activity
        });

    /// <summary>把夹具插件摊成一个待打包目录(plugin.json + dll)。</summary>
    private static string StagePlugin(string id, string manifestExtras = "", string version = "1.0.0", bool withSymbols = false)
    {
        string stage = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(stage, "VelaShell.TestPlugin.dll"));
        if (withSymbols)
        {
            File.Copy(Path.ChangeExtension(typeof(TestFixturePlugin).Assembly.Location, ".pdb"),
                Path.Combine(stage, "VelaShell.TestPlugin.pdb"));
        }
        File.WriteAllText(Path.Combine(stage, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "{{version}}", "displayName": "Packaged", "author": "Test Author",
              "entry": "VelaShell.TestPlugin.dll"{{manifestExtras}} }
            """);
        return stage;
    }

    /// <summary>打一个真正的 .vpx(专属容器:魔数 + 摘要 + 掩码)。</summary>
    private static string BuildVpx(string id = "acme.packaged", string manifestExtras = "", string version = "1.0.0",
        bool withSymbols = false)
    {
        string stage = StagePlugin(id, manifestExtras, version, withSymbols);
        string vpx = stage + ".vpx";
        VpxContainer.Pack(stage, vpx);
        return vpx;
    }

    /// <summary>打一个"把 zip 改成 .vpx 后缀"的假包。</summary>
    private static string BuildRenamedZip(string id = "acme.renamed")
    {
        string stage = StagePlugin(id);
        string vpx = stage + ".vpx";
        ZipFile.CreateFromDirectory(stage, vpx);
        return vpx;
    }

    [TestMethod]
    [DataRow("acme.other", "2.0.0", "")]
    [DataRow("acme.packaged", "1.5.0", "")]
    [DataRow("acme.packaged", "2.0.0", ", \"minHostVersion\": \"999.0.0\"")]
    public async Task MarketInstall_RejectsWrongIdentityOrCompatibilityBeforeReplacingTheInstalledPlugin(
        string packageId, string packageVersion, string extras)
    {
        await using PluginManager manager = CreateManager();
        await manager.StartAsync();
        string original = BuildVpx();
        string offered = BuildVpx(packageId, extras, packageVersion);
        try
        {
            await manager.InstallFromVpxAsync(original, allowUntrustedPackage: true);
            PluginDescriptor before = manager.Plugins.Single();
            await Assert.ThrowsExactlyAsync<PluginSdk.Manifest.PluginManifestException>(() =>
                manager.InstallFromVpxAsync(offered, allowUntrustedPackage: true,
                    expectedPluginId: "acme.packaged", expectedVersion: "2.0.0"));
            Assert.AreSame(before, manager.Plugins.Single());
            Assert.AreEqual("1.0.0", manager.Plugins.Single().Manifest!.Version);
            Assert.AreEqual(PluginState.Active, before.State);
            Assert.IsEmpty(_dataStore.Purged);
            Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, "acme.other")));
        }
        finally
        {
            File.Delete(original);
            File.Delete(offered);
            Directory.Delete(original[..^4], true);
            Directory.Delete(offered[..^4], true);
        }
    }

    [TestMethod]
    public async Task FailedInstall_RemovesStagingAndItsLease()
    {
        await using PluginManager manager = CreateManager();
        string package = Path.Combine(_userRoot, "broken.vpx");
        File.WriteAllText(package, "not a package");
        await Assert.ThrowsExactlyAsync<VpxFormatException>(() =>
            manager.InstallFromVpxAsync(package, allowUntrustedPackage: true));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(PluginManager.PackageStagingRoot(_userRoot)));
    }

    [TestMethod]
    public async Task CancelledInstall_RemovesStagingAndItsLease()
    {
        await using PluginManager manager = CreateManager();
        string package = BuildVpx();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                manager.InstallFromVpxAsync(package, allowUntrustedPackage: true, cancellationToken: cancellation.Token));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(PluginManager.PackageStagingRoot(_userRoot)));
            Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, "acme.packaged")));
        }
        finally
        {
            File.Delete(package);
            Directory.Delete(package[..^4], true);
        }
    }

    [TestMethod]
    public void PackageStagingRoot_IsUnderUserPluginRoot()
    {
        string stagingRoot = PluginManager.PackageStagingRoot(_userRoot);

        Assert.AreEqual(Path.Combine(_userRoot, ".staging", "velashell-vpx"), stagingRoot);
        Assert.IsTrue(stagingRoot.StartsWith(_userRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstallFromVpx_ExtractsActivatesAndPersistsToUserRoot()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string vpx = BuildVpx();

        string id = await manager.InstallFromVpxAsync(vpx, allowUntrustedPackage: true);
        Assert.AreEqual("acme.packaged", id);
        PluginDescriptor descriptor = manager.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_userRoot, id, "plugin.json")), "应解包进用户插件目录");
        Assert.IsTrue(manager.IsUninstallable(id));

        await manager.DisposeAsync();
    }

    /// <summary>
    /// 版本号 SemanticVersion 排不了序的包(<c>1.0.0-beta.01</c>)在安装时就拒,原因写明怎么改,用户目录里什么都不落 ——
    /// 装上去也只会在下次发现时变成一行 Invalid(第二轮第 28 条)。
    /// </summary>
    [TestMethod]
    public async Task InstallFromVpx_RejectsVersionSemanticVersionCannotOrder()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        PluginSdk.Manifest.PluginManifestException ex = await Assert.ThrowsExactlyAsync<PluginSdk.Manifest.PluginManifestException>(
            () => manager.InstallFromVpxAsync(BuildVpx(id: "acme.loose", version: "1.0.0-beta.01"), allowUntrustedPackage: true));

        Assert.Contains("'1.0.0-beta.1'", ex.Message);
        Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, "acme.loose")));
        Assert.DoesNotContain(p => p.Id == "acme.loose", manager.Plugins);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_ReportsProgressToTheBackgroundLedger_AndClearsIt()
    {
        // 安装是纯等待的几秒(验签 → 解压 → 全目录哈希落凭据),这段时间状态栏的圆环
        // 必须转起来;更要紧的是**转完必须停**,否则一次安装会让它一直转到关程序。
        using var activity = new Core.Services.BackgroundActivityService();
        var progress = new List<double?>();
        string? title = null;
        activity.Changed += () =>
        {
            foreach (Core.Services.BackgroundActivitySnapshot snapshot in activity.Activities)
            {
                if (snapshot.Title == Strings.Get("Msg_PluginInstalling"))
                {
                    title = snapshot.Title;
                    lock (progress)
                    {
                        progress.Add(snapshot.Progress);
                    }
                }
            }
        };
        PluginManager manager = CreateManager(activity: activity);
        await manager.StartAsync();

        await manager.InstallFromVpxAsync(BuildVpx(), allowUntrustedPackage: true);

        Assert.AreEqual(Strings.Get("Msg_PluginInstalling"), title, "安装过程必须在账本上露过面。");
        Assert.IsGreaterThan(1, progress.Count, "安装应分阶段上报进度,而不是从头到尾一个 0%。");
        Assert.IsEmpty(activity.Activities, "安装结束后账本必须归零。");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_WhenRejected_StillClearsTheBackgroundLedger()
    {
        // 失败路径同样要收干净:装坏包比装好包常见,而"装失败之后圆环永远在转"
        // 比没有圆环更糟 —— 它会让人以为后台还有什么没做完。
        using var activity = new Core.Services.BackgroundActivityService();
        PluginManager manager = CreateManager(activity: activity);
        await manager.StartAsync();

        await Assert.ThrowsExactlyAsync<VpxFormatException>(
            () => manager.InstallFromVpxAsync(BuildRenamedZip()));

        Assert.IsEmpty(activity.Activities);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Uninstall_RemovesDirectory_AndPurgesData()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string id = await manager.InstallFromVpxAsync(BuildVpx(), allowUntrustedPackage: true);

        Assert.IsTrue(await manager.UninstallAsync(id));
        Assert.DoesNotContain(p => p.Id == id, manager.Plugins);
        Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, id)), "插件目录应被删除");
        Assert.Contains(id, _dataStore.Purged);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Uninstall_BuiltInPlugin_IsRejected()
    {
        // 应用自带插件(app-plugins 目录,只读语义)不可卸载。
        string dir = Path.Combine(_appRoot, "builtin");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll" }
            """);
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        Assert.IsFalse(manager.IsUninstallable("velashell.test-fixture"));
        Assert.IsFalse(await manager.UninstallAsync("velashell.test-fixture"));
        Assert.IsTrue(Directory.Exists(dir), "自带插件目录不该被删");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_RenamedZip_IsRejected()
    {
        // "改后缀就能装"正是专属容器要消灭的东西:裸 zip 一律拒,且拒绝原因要告诉人怎么补救。
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        VpxFormatException ex = await Assert.ThrowsExactlyAsync<VpxFormatException>(
            () => manager.InstallFromVpxAsync(BuildRenamedZip()));
        Assert.Contains("vela-plugin pack", ex.Message);
        Assert.IsEmpty(manager.Plugins);

        await manager.DisposeAsync();
    }

    private static string BuildSignedVpx(ECDsa signingKey, string id = "acme.signed")
    {
        string stage = StagePlugin(id);
        string vpx = stage + ".vpx";
        VpxContainer.Pack(stage, vpx, new VpxPackOptions { SigningKey = signingKey });
        return vpx;
    }

    [TestMethod]
    public async Task InstallFromVpx_UnsignedPackage_RequiresExplicitApproval()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string package = BuildVpx("acme.unsigned");

        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.InstallFromVpxAsync(package));

        Assert.Contains("Explicit approval", ex.Message);
        Assert.AreEqual(VpxSignatureState.Unsigned, manager.InspectPackageSignature(package));
        Assert.DoesNotContain(p => p.Id == "acme.unsigned", manager.Plugins);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task TrustPublisher_PersistsKey_AndFuturePackagesInstallWithoutBypass()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "trust-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string first = BuildSignedVpx(publisher, "acme.signed-one");

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        PluginPackageTrustInfo before = manager.InspectPackageTrust(first);
        Assert.AreEqual(VpxSignatureState.Untrusted, before.State);
        Assert.StartsWith("SHA256:", before.PublisherFingerprint);

        string fingerprint = await manager.TrustPackagePublisherAsync(first);
        Assert.AreEqual(before.PublisherFingerprint, fingerprint);
        Assert.AreEqual(VpxSignatureState.Trusted, manager.InspectPackageSignature(first));
        await manager.InstallFromVpxAsync(first);
        await manager.DisposeAsync();

        string second = BuildSignedVpx(publisher, "acme.signed-two");
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();
        Assert.AreEqual(VpxSignatureState.Trusted, restarted.InspectPackageSignature(second));
        await restarted.InstallFromVpxAsync(second);
        Assert.Contains(p => p.Id == "acme.signed-two", restarted.Plugins);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_ModifiedInstalledPlugin_IsRejectedByProtectedReceipt()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "receipt-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        const string id = "acme.receipt-tamper";
        await manager.InstallFromVpxAsync(BuildVpx(id), allowUntrustedPackage: true);
        await manager.DisposeAsync();

        await File.AppendAllTextAsync(Path.Combine(_userRoot, id, "plugin.json"), " ");
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = Assert.ContainsSingle(plugin => plugin.Id == id, restarted.Plugins);
        Assert.AreEqual(PluginState.Invalid, descriptor.State);
        Assert.Contains("changed after installation", descriptor.Error);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_ModifiedInstalledPlugin_WithdrawsItsConnectionTabs()
    {
        // 内容校验推迟到发现之后:页签在校验有结论之前就已经挂出去了。
        // 校验判负时必须把它撤下来 —— 留着的话用户点下去只会得到一次静默的无反应。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "tab-withdraw-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        const string id = "acme.tab-tamper";
        const string workspace = """, "contributes": { "workspaces": [ { "id": "acme.tab-tamper.cache", "displayName": "Cache", "defaultPort": 6379 } ] }, "activationEvents": ["onWorkspace:acme.tab-tamper.cache"]""";

        var firstRegistry = new Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        PluginManager manager = CreateManager(repository, firstRegistry);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id, workspace), allowUntrustedPackage: true);
        Assert.HasCount(1, firstRegistry.Tabs, "装上之后连接页应出现这个工作台页签。");
        await manager.DisposeAsync();

        await File.AppendAllTextAsync(Path.Combine(_userRoot, id, "plugin.json"), " ");
        var registry = new Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        PluginManager restarted = CreateManager(repository, registry);
        await restarted.StartAsync();

        Assert.AreEqual(PluginState.Invalid, restarted.Plugins.Single(p => p.Id == id).State);
        Assert.IsEmpty(registry.Tabs, "被改动过的插件不该在连接页上留下页签。");

        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_DirectlyDroppedPlugin_IsAdoptedAsItsOwnBaseline()
    {
        // 命令行 vela-plugin install 与"直接把目录放进插件根"落到的都是这条路径:
        // 目录先于宿主存在,没有受保护收据。文档写明这两条路都能装,宿主必须收养它而不是拒绝。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "direct-drop-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        PluginManager firstRun = CreateManager(repository);
        await firstRun.StartAsync(); // 先建一次空的信任状态。
        await firstRun.DisposeAsync();

        const string id = "acme.direct-drop";
        Directory.Move(StagePlugin(id), Path.Combine(_userRoot, id));
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = Assert.ContainsSingle(plugin => plugin.Id == id, restarted.Plugins);
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        await restarted.DisposeAsync();

        // 收养必须真的落进了信任库:下一次启动不该再收养一遍。
        PluginTrustState state = await repository.LoadAsync();
        InstalledPluginReceipt receipt = state.Receipts[id];
        Assert.IsTrue(receipt.LegacyAdopted, "旁装目录建立的是 TOFU 基线,不是管理页那份收据。");
        Assert.IsNull(receipt.PackageSha256, "没有包可以证明它出自哪儿,不该伪造一个包摘要。");
    }

    [TestMethod]
    public async Task Restart_SideLoadedPluginChangedOnDisk_IsRebaselinedNotRejected()
    {
        // 旁装换来的代价就是没有事后防篡改(CLI 手册明写)。这里的内容变化通常是
        // `vela-plugin update` 换了版本 —— 把它判成"被人动过"会让更新完的插件全变红。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "sideload-update-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        const string id = "acme.sideload-update";
        Directory.Move(StagePlugin(id), Path.Combine(_userRoot, id));

        PluginManager first = CreateManager(repository);
        await first.StartAsync();
        Assert.AreEqual(PluginState.Active, first.Plugins.Single(p => p.Id == id).State);
        await first.DisposeAsync();

        await File.AppendAllTextAsync(Path.Combine(_userRoot, id, "extra.txt"), "updated");
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = restarted.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_ReceiptWithoutDirectory_IsDroppedSoTheIdCanBeInstalledAgain()
    {
        // vela-plugin uninstall 只删目录,够不着宿主进程里的信任库。留着那份孤儿收据,
        // 同一个 id 再装回来时内容必然与旧收据对不上,插件会以"文件被改过"被拒。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "orphan-receipt-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        const string id = "acme.reinstalled";
        // 懒激活:入口 dll 始终没被装载,测试才能像命令行那样把目录整个删掉。
        const string lazy = """, "activationEvents": ["onWorkspace:acme.reinstalled.ws"], "contributes": { "workspaces": [ { "id": "acme.reinstalled.ws", "displayName": "WS", "defaultPort": 6379 } ] }""";

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id, lazy), allowUntrustedPackage: true);
        await manager.DisposeAsync();

        // 宿主之外把目录删掉 —— 命令行 uninstall 就是这个形状,它够不着信任库。
        Directory.Delete(Path.Combine(_userRoot, id), recursive: true);
        PluginManager afterUninstall = CreateManager(repository);
        await afterUninstall.StartAsync();
        await afterUninstall.DisposeAsync();
        Assert.IsFalse((await repository.LoadAsync()).Receipts.ContainsKey(id), "目录没了,收据该跟着清掉。");

        // 同一个 id 再装回来(内容与上一次不同)时必须能正常装载。
        string replacement = StagePlugin(id, lazy);
        await File.WriteAllTextAsync(Path.Combine(replacement, "extra.txt"), "second install");
        Directory.Move(replacement, Path.Combine(_userRoot, id));

        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = restarted.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Discovered, descriptor.State, descriptor.Error);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_TamperedPackage_IsRejected()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string vpx = BuildVpx("acme.tampered");

        // 动载荷里的一个字节:头部摘要立刻对不上。
        byte[] bytes = File.ReadAllBytes(vpx);
        bytes[VpxContainer.HeaderSize + 64] ^= 0xFF;
        File.WriteAllBytes(vpx, bytes);

        await Assert.ThrowsExactlyAsync<VpxFormatException>(() => manager.InstallFromVpxAsync(vpx));
        Assert.DoesNotContain(p => p.Id == "acme.tampered", manager.Plugins);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_ZipSlipInsideContainer_IsRejected()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        // 合法容器 + 恶意载荷:zip-slip 防护必须在容器之内继续生效。
        string vpx = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N") + ".vpx");
        Directory.CreateDirectory(Path.GetDirectoryName(vpx)!);
        using (var payload = new MemoryStream())
        {
            using (ZipArchive zip = new(payload, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("plugin.json");
                using StreamWriter writer = new(zip.CreateEntry("../escaped.txt").Open());
                writer.Write("pwned");
            }
            payload.Position = 0;
            using FileStream destination = File.Create(vpx);
            VpxContainer.Write(destination, payload);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.InstallFromVpxAsync(vpx, allowUntrustedPackage: true));
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_SameId_ReplacesOldVersion()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx("acme.dup"), allowUntrustedPackage: true);
        // 覆盖安装同 id:不抛,替换。
        string id = await manager.InstallFromVpxAsync(BuildVpx("acme.dup"), allowUntrustedPackage: true);
        Assert.ContainsSingle(p => p.Id == "acme.dup", manager.Plugins);
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single(p => p.Id == id).State);
        await manager.DisposeAsync();
    }

    /// <summary>
    /// 插件漏了引用(静态事件、计时器、界面元素)时它的 ALC 永远收不回来。只要插件的异常堆栈被格式化过一次
    /// (宿主与插件写日志都会 <c>ex.ToString()</c>),运行时就以 <c>FileShare.Read</c> 打开它的 <c>.pdb</c>
    /// 并按程序集缓存读取器 —— 这个句柄随 ALC 活多久就开多久,而不带共享删除的句柄会挡住整个目录的换名。
    /// 就地装载时这把安装目录锁到宿主退出:更新报 Access denied,卸载留下删不掉的目录。
    /// 从影子副本装载后,被锁的是副本 —— 升级与卸载照常完成。
    /// </summary>
    [TestMethod]
    public async Task LeakedInProcessPlugin_LoadedFromShadow_CanStillBeUpgradedAndUninstalled()
    {
        const string id = "acme.leaky";
        string shadowRoot = Path.Combine(Path.GetDirectoryName(_appRoot)!, "plugin-shadow");
        await using PluginManager manager = new(new()
        {
            PluginRoots = [_appRoot, _userRoot],
            DataRootDirectory = _dataRoot,
            UserPluginRoot = _userRoot,
            InstalledShadowRootDirectory = shadowRoot,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands(),
            DataStore = _dataStore
        });
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id, withSymbols: true), allowUntrustedPackage: true);
        PluginDescriptor first = manager.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Active, first.State, first.Error);
        Assert.StartsWith(shadowRoot, first.LoadDirectory!, StringComparison.OrdinalIgnoreCase,
            "已安装的进程内插件应从影子副本装载。");

        // 模拟泄漏:攥住旧版的 ALC,它就永远不会被 GC 收走。
        System.Runtime.Loader.AssemblyLoadContext leaked = System.Runtime.Loader.AssemblyLoadContext.All
            .Single(alc => alc.Name == $"plugin:{id}");
        // 让插件代码抛一次并格式化堆栈:运行时由此打开并缓存它的 .pdb(就是 RDP 插件锁住安装目录的那条路)。
        Type entry = leaked.Assemblies.Single().GetType(typeof(TestFixturePlugin).FullName!)!;
        var probe = (PluginSdk.IVelaPlugin)Activator.CreateInstance(entry)!;
        ArgumentNullException thrown = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => probe.ActivateAsync(null!, CancellationToken.None));
        Assert.Contains(":line", thrown.ToString(), "堆栈应带上行号,即确实读了 .pdb。");

        await manager.InstallFromVpxAsync(BuildVpx(id, version: "2.0.0"), allowUntrustedPackage: true);
        PluginDescriptor upgraded = manager.Plugins.Single(p => p.Id == id);
        Assert.AreEqual("2.0.0", upgraded.Manifest!.Version);
        Assert.AreEqual(PluginState.Active, upgraded.State, upgraded.Error);

        Assert.IsTrue(await manager.UninstallAsync(id));
        Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, id)), "旧 ALC 仍活着,安装目录也必须删得掉。");
        GC.KeepAlive(leaked);
    }

    /// <summary>
    /// 旧版目录换名失败(被占用)时,旧版已经从运行时摘下来了 —— 必须原样挂回去,
    /// 否则插件从管理页与连接页上消失,直到重启宿主。错误信息要说清楚是占用、旧版还在。
    /// 只在 Windows 上跑:用「不带共享删除的打开句柄」挡住目录换名是 Windows 的共享模式语义,
    /// Linux / macOS 没有强制共享锁,换名照样成功 —— 这类占用在那两个平台上本来就不会发生。
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task Upgrade_WhenTheInstalledDirectoryIsLocked_KeepsTheOldVersionAttached()
    {
        const string id = "acme.locked";
        await using PluginManager manager = CreateManager(); // 不配影子根:就地装载
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id), allowUntrustedPackage: true);

        // 用一个不带共享删除的句柄占住目录里的文件 —— 与杀毒软件扫描、.pdb 读取器缓存同一种锁。
        string target = Path.Combine(_userRoot, id);
        using (new FileStream(Path.Combine(target, "plugin.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            IOException ex = await Assert.ThrowsAsync<IOException>(() =>
                manager.InstallFromVpxAsync(BuildVpx(id, version: "2.0.0"), allowUntrustedPackage: true));
            Assert.Contains(id, ex.Message);
        }

        PluginDescriptor kept = manager.Plugins.Single(p => p.Id == id);
        Assert.AreEqual("1.0.0", kept.Manifest!.Version, "旧版必须还挂在运行时上。");
        Assert.AreEqual(PluginState.Active, kept.State, kept.Error);
        Assert.IsTrue(File.Exists(Path.Combine(target, "plugin.json")), "旧版目录原封不动。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, ".upgrade"))
                       && Directory.EnumerateFileSystemEntries(Path.Combine(_userRoot, ".upgrade"), "*.owned").Any(),
            "没挪成的备份不该留下认领标记。");
    }

    [TestMethod]
    public async Task InstallFromVpx_OverAnInstalledVersion_KeepsThePluginsData()
    {
        const string id = "acme.upgrade";
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id), allowUntrustedPackage: true);

        // 插件攒在自己数据目录里的东西 —— AI 插件的供应商配置与聊天记录就在这一层。
        string dataDirectory = Path.Combine(_dataRoot, id);
        Directory.CreateDirectory(dataDirectory);
        string keepsake = Path.Combine(dataDirectory, "settings.json");
        await File.WriteAllTextAsync(keepsake, """{"provider":"kept"}""");

        await manager.InstallFromVpxAsync(BuildVpx(id, version: "2.0.0"), allowUntrustedPackage: true);

        Assert.IsTrue(File.Exists(keepsake), "升级换的是插件的代码,用户攒下的东西必须原样还在。");
        Assert.DoesNotContain(id, _dataStore.Purged, "覆盖安装不该走卸载那条清库路径。");
        PluginDescriptor upgraded = manager.Plugins.Single(p => p.Id == id);
        Assert.AreEqual("2.0.0", upgraded.Manifest!.Version, "跑的应该是新版。");
        Assert.AreEqual(PluginState.Active, upgraded.State);
        Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, ".upgrade")),
            "换完就该把旧版备份连同那个窝一起收掉,别在插件根下养垃圾。");
        await manager.DisposeAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Startup_ReclaimsOwnedUpgradeBackupOnlyAfterProtectedReceiptMatches(bool tampered)
    {
        const string id = "acme.backupcleanup";
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "backup-cleanup-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        string package = BuildVpx(id);
        try
        {
            await using (PluginManager installer = CreateManager(repository))
            {
                await installer.StartAsync();
                await installer.InstallFromVpxAsync(package, allowUntrustedPackage: true);
            }
            string backupRoot = Path.Combine(_userRoot, ".upgrade");
            Directory.CreateDirectory(backupRoot);
            string owned = Path.Combine(backupRoot, $"{id}-{Guid.NewGuid():N}");
            string legacy = Path.Combine(backupRoot, $"{id}-{Guid.NewGuid():N}");
            Directory.Move(StagePlugin(id), owned);
            Directory.Move(StagePlugin(id), legacy);
            PluginArtifactMaintenance.MarkBackup(owned);
            if (tampered) File.WriteAllText(Path.Combine(_userRoot, id, "changed.txt"), "tampered");

            await using PluginManager restarted = CreateManager(repository);
            await restarted.StartAsync();
            Assert.AreEqual(tampered, Directory.Exists(owned));
            Assert.AreEqual(tampered, File.Exists(owned + ".owned"));
            Assert.IsTrue(Directory.Exists(legacy), "An unmarked backup has no cleanup ownership proof.");
        }
        finally
        {
            File.Delete(package);
            Directory.Delete(package[..^4], true);
        }
    }

    [TestMethod]
    public async Task InstallFromVpx_UpgradeFromTheSamePublisher_InstallsWithoutAskingAgain()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "publisher-same-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string id = "acme.pinned";

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildSignedVpx(publisher, id), allowUntrustedPackage: true);
        string? pinned = await manager.GetPinnedPublisherFingerprintAsync(id);
        Assert.StartsWith("SHA256:", pinned, "管理页装的包,发布者要被钉进收据。");

        // 同一把私钥签的下一版:身份没变,这一闸不该拦。
        await manager.InstallFromVpxAsync(BuildSignedVpx(publisher, id), allowUntrustedPackage: true);

        Assert.ContainsSingle(p => p.Id == id, manager.Plugins);
        Assert.AreEqual(pinned, await manager.GetPinnedPublisherFingerprintAsync(id));
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_PackageFromAnotherPublisher_IsRejectedUntilTheChangeIsApproved()
    {
        // 顶替已装插件的形状:同一个 id,签名完全有效,只是签它的换了个人。
        // 验签本身对此一言不发 —— 拦住它的只有"还是不是上次那个人"这一问。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "publisher-rotate-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string id = "acme.hijacked";

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildSignedVpx(original, id), allowUntrustedPackage: true);
        string? pinned = await manager.GetPinnedPublisherFingerprintAsync(id);

        string replacement = BuildSignedVpx(impostor, id);
        PluginPublisherChangedException rejected = await Assert.ThrowsExactlyAsync<PluginPublisherChangedException>(
            () => manager.InstallFromVpxAsync(replacement, allowUntrustedPackage: true));

        Assert.AreEqual(id, rejected.PluginId);
        Assert.AreEqual(pinned, rejected.PinnedFingerprint);
        Assert.AreNotEqual(pinned, rejected.PackageFingerprint, "对话框要摆两个指纹,它们必须真的不一样。");
        // 拦下的那一刻,用户手上那个还装着的插件必须一根毫毛都没动 —— 闸在卸载旧版之前。
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single(p => p.Id == id).State);
        Assert.AreEqual(pinned, await manager.GetPinnedPublisherFingerprintAsync(id));

        // 用户看过两个指纹、认了:装上去,并且新公钥成为此后的基线。
        await manager.InstallFromVpxAsync(replacement, allowUntrustedPackage: true, allowPublisherChange: true);
        Assert.AreEqual(rejected.PackageFingerprint, await manager.GetPinnedPublisherFingerprintAsync(id));
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_UnsignedPackageOverASignedPlugin_IsRejectedAsAPublisherChange()
    {
        // "把签名去掉"不是一次密钥轮换,是降级:它让下一次覆盖安装再也无从比对。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "publisher-downgrade-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string id = "acme.downgraded";

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildSignedVpx(publisher, id), allowUntrustedPackage: true);

        PluginPublisherChangedException rejected = await Assert.ThrowsExactlyAsync<PluginPublisherChangedException>(
            () => manager.InstallFromVpxAsync(BuildVpx(id), allowUntrustedPackage: true));

        Assert.IsNull(rejected.PackageFingerprint, "没签名就是没有身份,不该给它编一个指纹。");
        Assert.AreEqual(await manager.GetPinnedPublisherFingerprintAsync(id), rejected.PinnedFingerprint);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task SideLoadedPlugin_PinsNoPublisher_AndIsStillReplaceableFromThePluginManager()
    {
        // 命令行 vela-plugin install 与"直接把目录放进插件根"落到的都是这条路:收据是 TOFU 基线,
        // 里面没有公钥,也没有任何东西能替它补一个。发布者连续性闸对这一类必须闭嘴 ——
        // 否则命令行装过的插件从此在管理页一个都更新不上,而那是文档写明支持的安装路径。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "publisher-sideload-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string id = "acme.cli-installed";
        Directory.Move(StagePlugin(id), Path.Combine(_userRoot, id));

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single(p => p.Id == id).State);
        Assert.IsNull(await manager.GetPinnedPublisherFingerprintAsync(id), "旁装目录没有可钉的发布者。");

        // 从管理页装一个签名包上去:没有可比对的身份,不该被当成"换发布者"拦下。
        await manager.InstallFromVpxAsync(BuildSignedVpx(publisher, id), allowUntrustedPackage: true);

        Assert.ContainsSingle(p => p.Id == id, manager.Plugins);
        Assert.StartsWith("SHA256:", await manager.GetPinnedPublisherFingerprintAsync(id),
                          "这一次是宿主亲手解的包,从此就钉得住发布者了。");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task UnreadableTrustStore_BlocksInstallAndTrust_UntilTheUserResetsIt()
    {
        // 用户实际撞上的形状:信任库由旧密钥加密,secret.key 换过之后
        // "信任发布者并安装"两步都失败,且重启也不会好。
        using var engine = new SonnetDbEngine(Path.Combine(_trustDbRoot, "unreadable-trust-db"));
        await new PluginTrustRepository(engine, new AesSecretProtector(Path.Combine(_trustDbRoot, "old.key")))
            .SaveAsync(new PluginTrustState());
        var repository = new PluginTrustRepository(engine, new AesSecretProtector(Path.Combine(_trustDbRoot, "new.key")));
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string package = BuildSignedVpx(publisher, "acme.after-reset");

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        Assert.Contains("authentication failed", await manager.GetTrustStoreErrorAsync() ?? "");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.TrustPackagePublisherAsync(package));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.InstallFromVpxAsync(package, allowUntrustedPackage: true));
        Assert.DoesNotContain(p => p.Id == "acme.after-reset", manager.Plugins);

        Assert.IsNotNull(await manager.ResetTrustStoreAsync(), "读不出来的旧文档要留备份。");

        Assert.IsNull(await manager.GetTrustStoreErrorAsync());
        await manager.TrustPackagePublisherAsync(package);
        await manager.InstallFromVpxAsync(package);
        Assert.Contains(p => p.Id == "acme.after-reset", manager.Plugins);
        await manager.DisposeAsync();
    }
}
