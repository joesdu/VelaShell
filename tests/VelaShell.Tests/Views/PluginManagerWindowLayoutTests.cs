using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Market;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>插件管理窗口「已安装」页的布局:卡片动作、更多菜单、筛选与状态栏。</summary>
[TestClass]
[DoNotParallelize]
public sealed class PluginManagerWindowLayoutTests
{
    private static HeadlessUnitTestSession _session = null!;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vela-manager-ui", Guid.NewGuid().ToString("N"));

    [ClassInitialize]
    public static void ClassInit(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginManagerWindowLayoutTests).Assembly);

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 留给临时目录。
        }
    }

    private sealed class LatestOnlyMarket(Dictionary<string, PluginMarketVersion> latest) : IPluginMarketClient
    {
        public string BaseUrl => "https://market.test";
        public Task<PluginMarketPage> SearchAsync(string? query = null, string sort = "updated", int page = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginMarketPage { Total = 0, Page = page, Size = 20, Items = [] });
        public Task<PluginMarketDetail> GetDetailAsync(string pluginId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not used");
        public Task<IReadOnlyDictionary<string, PluginMarketVersion>> GetLatestAsync(IReadOnlyCollection<string> pluginIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PluginMarketVersion>>(latest);
        public Task DownloadAsync(string pluginId, string version, string destinationPath, IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("not used");
    }

    /// <summary>落一个只有清单与占位入口的插件目录(惰性激活,不会真的装载)。</summary>
    private static void DropPlugin(string root, string id, string name, string hostMode, string version = "1.0.0")
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "entry.dll"), "");
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "{{version}}", "displayName": "{{name}}", "author": "Acme",
              "entry": "entry.dll", "hostMode": "{{hostMode}}",
              "contributes": { "workspaces": [{ "id": "{{id}}", "displayName": "{{name}}" }] },
              "activationEvents": ["onWorkspace:{{id}}"] }
            """);
    }

    [TestMethod]
    public Task InstalledTab_ShowsUpdateAsPrimaryAction_FoldsTheRestIntoMore_AndFilters() => _session.RunOnUiAsync(async () =>
    {
        string user = Path.Combine(_root, "plugins");
        DropPlugin(user, "acme.rdp", "Remote Desktop", "inProcess", "2.1.4");
        DropPlugin(user, "acme.redis", "Redis Client", "isolated", "2.1.5");
        DropPlugin(user, "acme.mongo", "MongoDB Workbench", "inProcess", "2.1.5");
        await using PluginManager manager = new(new()
        {
            PluginRoots = [user],
            DataRootDirectory = Path.Combine(_root, "data"),
            UserPluginRoot = user,
            HostVersion = "2.0.0"
        });
        await manager.StartAsync();
        var market = new LatestOnlyMarket(new()
        {
            ["acme.rdp"] = new() { Id = "acme.rdp", Version = "2.1.5" }
        });
        using var vm = new PluginManagerViewModel(manager, null, market);
        var window = new PluginManagerWindow { DataContext = vm, Width = 980, Height = 640 };
        window.Show();
        try
        {
            // 构造时已经发起了一次检查更新(重入闸会让这里的第二次直接返回),等它的结果落到列表上。
            for (int i = 0; i < 100 && !vm.HasUpdates; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            window.UpdateLayout();

            Assert.HasCount(3, vm.Plugins);
            Assert.IsTrue(vm.HasUpdates, string.Join("; ", vm.Plugins.Select(p => $"{p.Id}: {p.Error}")));
            StringAssert.Contains(vm.SummaryText, "3");

            Button[] buttons = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
            Button update = buttons.Single(b => b.DataContext is PluginRowViewModel { Id: "acme.rdp" }
                                                && b.Theme == window.FindResource("VelaAccentPillButtonTheme"));
            Point position = update.TranslatePoint(default, window)!.Value;
            Assert.IsTrue(position.X + update.Bounds.Width <= window.Bounds.Width, "更新按钮不该被挤出窗口。");
            Assert.HasCount(3, buttons.Where(b => b.Classes.Contains("row-more")),
                "每一行都应有「更多」按钮(查看日志、卸载都收在里面)。");

            vm.FilterText = "redis";
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("acme.redis", vm.Plugins.Single().Id);
            vm.FilterText = "no-such-plugin";
            Assert.IsEmpty(vm.Plugins);
            Assert.IsTrue(vm.HasNoMatches);
            Assert.IsFalse(vm.IsEmpty, "过滤无结果不是「一个插件都没装」。");
            vm.FilterText = null;
            Assert.HasCount(3, vm.Plugins);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    [DataRow("SHA256:f4aabca5d3f8ee94a82fa27836d792a860af0820ccf50810b3fa93a7dd121b97", "SHA256:f4aabc…121b97")]
    [DataRow("SHA256:abcdef", "SHA256:abcdef")]
    [DataRow("0123456789abcdef0123", "012345…ef0123")]
    public void Fingerprint_IsShortenedInTheMiddle(string fingerprint, string expected) =>
        Assert.AreEqual(expected, PluginRowViewModel.ShortenFingerprint(fingerprint));
}
