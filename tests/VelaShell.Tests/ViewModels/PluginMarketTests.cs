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

namespace VelaShell.Tests.ViewModels;

[TestClass]
[TestCategory("Plugins")]
public sealed class PluginMarketTests
{
    private static HeadlessUnitTestSession _session = null!;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vela-market-ui", Guid.NewGuid().ToString("N"));

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginMarketTests).Assembly);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private PluginManager Manager() => new(new()
    {
        PluginRoots = [],
        DataRootDirectory = Path.Combine(_root, "data"),
        UserPluginRoot = Path.Combine(_root, "plugins"),
        HostVersion = "2.0.0"
    });

    private static PluginMarketEntry Entry(string id) => new() { Id = id, DisplayName = id };
    private static PluginMarketDetail Detail(string id, string version = "1.0.0") => new()
    {
        Id = id,
        DisplayName = id,
        Versions = [new() { Version = version }],
        DescriptionMarkdown = "# Description"
    };

    private sealed class Market : IPluginMarketClient
    {
        public string BaseUrl => "https://market.test";
        public int SearchCalls { get; private set; }
        public Func<string?, int, CancellationToken, Task<PluginMarketPage>> Search { get; set; } = (_, page, _) =>
            Task.FromResult(new PluginMarketPage { Total = 0, Page = page, Size = 20, Items = [] });
        public Func<string, CancellationToken, Task<PluginMarketDetail>> ReadDetail { get; set; } = (id, _) =>
            Task.FromResult(Detail(id));
        public Task<PluginMarketPage> SearchAsync(string? query = null, string sort = "updated", int page = 1, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return Search(query, page, cancellationToken);
        }
        public Task<PluginMarketDetail> GetDetailAsync(string pluginId, CancellationToken cancellationToken = default) => ReadDetail(pluginId, cancellationToken);
        public Task<IReadOnlyDictionary<string, PluginMarketVersion>> GetLatestAsync(IReadOnlyCollection<string> pluginIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PluginMarketVersion>>(new Dictionary<string, PluginMarketVersion>());
        public Func<IProgress<int>?, CancellationToken, Task> Download { get; set; } = (_, _) =>
            throw new InvalidOperationException("No test should download a production plugin.");
        public Task DownloadAsync(string pluginId, string version, string destinationPath, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            Download(progress, cancellationToken);
    }

    [TestMethod]
    public Task Search_IsLazyAndIgnoresAResponseFromThePreviousQuery() => _session.RunOnUiAsync(async () =>
    {
        await using PluginManager manager = Manager();
        var old = new TaskCompletionSource<PluginMarketPage>();
        CancellationToken oldToken = default;
        var market = new Market
        {
            Search = (query, page, token) =>
            {
                if (query == "old") { oldToken = token; return old.Task; }
                return Task.FromResult(new PluginMarketPage { Total = 0, Page = page, Size = 20, Items = [] });
            }
        };
        using var vm = new PluginMarketViewModel(manager, market);
        Assert.AreEqual(0, market.SearchCalls);
        vm.Query = "old";
        Task previous = vm.SearchAsync();
        vm.Query = "new";
        await vm.SearchAsync();
        Assert.IsTrue(oldToken.IsCancellationRequested);
        old.SetResult(new() { Total = 1, Page = 1, Size = 20, Items = [Entry("old")] });
        await previous;
        Assert.IsEmpty(vm.Entries);
        Assert.IsTrue(vm.IsEmpty);
        Assert.IsFalse(vm.IsLoading);
    });

    [TestMethod]
    public Task Detail_SelectionAndCloseRejectLateResults() => _session.RunOnUiAsync(async () =>
    {
        await using PluginManager manager = Manager();
        var old = new TaskCompletionSource<PluginMarketDetail>();
        var closed = new TaskCompletionSource<PluginMarketDetail>();
        CancellationToken oldToken = default;
        CancellationToken closedToken = default;
        var market = new Market
        {
            ReadDetail = (id, token) =>
            {
                if (id == "old") { oldToken = token; return old.Task; }
                if (id == "closed") { closedToken = token; return closed.Task; }
                return Task.FromResult(Detail(id));
            }
        };
        using var vm = new PluginMarketViewModel(manager, market);
        Assert.AreEqual("", vm.DetailDisplayName);
        Assert.AreEqual("", vm.DetailId);
        Assert.AreEqual("", vm.DetailSummary);
        vm.SelectedEntry = Entry("old");
        vm.SelectedEntry = Entry("new");
        Assert.IsTrue(oldToken.IsCancellationRequested);
        old.SetResult(Detail("old"));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.AreEqual("new", vm.Detail!.Id);
        Assert.AreEqual("new", vm.DetailDisplayName);
        Assert.AreEqual("new", vm.DetailId);
        Assert.AreEqual("", vm.DetailSummary);
        vm.SelectedEntry = Entry("closed");
        vm.Dispose();
        Assert.IsTrue(closedToken.IsCancellationRequested);
        closed.SetResult(Detail("closed"));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.IsNull(vm.Detail);
    });

    [TestMethod]
    public Task FailuresRemainRetryableAndCompatibilityUsesTheSelectedStableVersion() => _session.RunOnUiAsync(async () =>
    {
        await using PluginManager manager = Manager();
        var market = new Market { Search = (_, _, _) => throw new HttpRequestException("offline") };
        using var vm = new PluginMarketViewModel(manager, market);
        await vm.SearchAsync();
        Assert.IsNotNull(vm.SearchError);
        Assert.IsFalse(vm.IsEmpty);
        market.Search = (_, page, _) => Task.FromResult(new PluginMarketPage { Total = 21, Page = page, Size = 20, Items = [Entry("test")] });
        market.ReadDetail = (id, _) => Task.FromResult(Detail(id) with
        {
            Versions = [new() { Version = "5.0.0-beta.1" }, new() { Version = "1.0.0" },
                new() { Version = "2.0.0", MinHostVersion = "999.0.0" }, new() { Version = "garbage" }]
        });
        await vm.RetrySearchAsync();
        Assert.IsNull(vm.SearchError);
        Assert.IsTrue(vm.CanNext);
        Assert.AreEqual("2.0.0", vm.Release!.Version);
        Assert.IsFalse(vm.CanInstall);
        Assert.IsNotNull(vm.Blocker);
        Assert.IsNull(vm.CreateInstallRequest());
        await vm.NextAsync();
        Assert.IsFalse(vm.CanNext);
        Assert.IsTrue(vm.CanPrevious);
        market.ReadDetail = (id, _) => Task.FromResult(Detail(id));
        await vm.LoadDetailAsync();
        Assert.IsTrue(vm.CanInstall);
        Assert.AreEqual("test", vm.CreateInstallRequest()!.Id);
        vm.IsInstalling = true;
        Assert.IsFalse(vm.CanInstall);
    });

    [TestMethod]
    public Task CancelDownload_StopsTheRequestAndClearsDeterminateProgress() => _session.RunOnUiAsync(async () =>
    {
        await using PluginManager manager = Manager();
        var market = new Market
        {
            Download = async (progress, token) =>
            {
                progress!.Report(50);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        using var vm = new PluginMarketViewModel(manager, market);
        Task<string?> download = vm.DownloadAsync(new() { Id = "acme.test", Version = "1.0.0" });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.IsTrue(vm.IsDownloading);
        Assert.IsTrue(vm.HasDownloadProgress);
        Assert.AreEqual(0.5, vm.DownloadFraction);
        vm.CancelDownload();
        Assert.IsNull(await download);
        Assert.IsFalse(vm.IsDownloading);
        Assert.IsFalse(vm.HasDownloadProgress);
        Assert.IsNotNull(vm.OperationNotice);
        vm.IsInstalling = true;
        Assert.IsNull(vm.OperationNotice, "A new operation must not retain the previous cancellation notice.");
    });

    [TestMethod]
    public Task FailedInstallationReturnsFalseInsteadOfAuthorizingPublisherTrust() => _session.RunOnUiAsync(async () =>
    {
        await using PluginManager manager = Manager();
        using var vm = new PluginManagerViewModel(manager, null);
        Assert.IsFalse(await vm.InstallFromVpxAsync(Path.Combine(_root, "missing.vpx")));
    });

    [TestMethod]
    [DataRow(740)]
    [DataRow(980)]
    public Task Window_LoadsStoreOnlyOnEntryAndKeepsInstallActionInsideNarrowLayout(int width) => _session.RunOnUiAsync(async () =>
    {
        await using PluginManager manager = Manager();
        var market = new Market
        {
            Search = (_, page, _) => Task.FromResult(new PluginMarketPage { Total = 1, Page = page, Size = 20, Items = [Entry("acme.plugin")] }),
            ReadDetail = (id, _) => Task.FromResult(Detail(id) with
            {
                DisplayName = string.Concat(Enumerable.Repeat("Very long plugin name 插件名称 ", 8)),
                DescriptionMarkdown = string.Concat(Enumerable.Repeat("## Description\nLong content\n\n", 60))
            })
        };
        using var vm = new PluginManagerViewModel(manager, null, market);
        var window = new PluginManagerWindow { DataContext = vm, Width = width, Height = 520 };
        window.Show();
        try
        {
            Assert.AreEqual(0, market.SearchCalls);
            window.FindControl<TabControl>("ManagerTabs")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.AreEqual(1, market.SearchCalls);
            PluginMarketView view = window.GetVisualDescendants().OfType<PluginMarketView>().Single();
            Button install = view.FindControl<Button>("InstallButton")!;
            Assert.IsTrue(install.IsEffectivelyVisible);
            Assert.IsTrue(install.IsEnabled);
            GridSplitter splitter = view.FindControl<GridSplitter>("MarketSplitter")!;
            Assert.IsTrue(splitter.IsEffectivelyVisible);
            Avalonia.Point position = install.TranslatePoint(default, window)!.Value;
            Assert.IsTrue(position.X >= 0 && position.X + install.Bounds.Width <= window.Bounds.Width);
            Assert.IsTrue(position.Y >= 0 && position.Y + install.Bounds.Height <= window.Bounds.Height);
            Assert.IsTrue(view.FindControl<ListBox>("CatalogList")!.Bounds.Width > 150);
        }
        finally { window.Close(); }
    });
}
