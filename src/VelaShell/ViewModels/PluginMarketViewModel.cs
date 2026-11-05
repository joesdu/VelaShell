using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Market;
using VelaShell.PluginSdk.Versioning;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>按需加载的商店浏览页。所有可绑定状态只在 UI 线程更新。</summary>
public sealed class PluginMarketViewModel(PluginManager manager, IPluginMarketClient? market) : ReactiveObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _detail;
    private CancellationTokenSource? _download;
    private int _page = 1;
    private long _total;
    private bool _loaded;
    private bool _disposed;
    private string _appliedQuery = "";
    private string _appliedSort = "updated";

    /// <summary>当前查询页的条目。</summary>
    public ObservableCollection<PluginMarketEntry> Entries { get; } = [];
    /// <summary>是否配置了商店客户端。</summary>
    public bool CanBrowse => market is not null;
    /// <summary>尚未提交的搜索词;翻页使用上次提交的查询。</summary>
    public string Query { get; set; } = "";
    /// <summary>排序选择:最近更新、下载量、评分。</summary>
    public int SortIndex { get; set; }
    /// <summary>排序选择的本地化标签。</summary>
    public string[] SortLabels { get; } = [Strings.Get("PluginMarket_SortUpdated"), Strings.Get("PluginMarket_SortDownloads"), Strings.Get("PluginMarket_SortRating")];

    /// <summary>选择变化会取消旧详情请求并加载新详情。</summary>
    public PluginMarketEntry? SelectedEntry
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            this.RaiseAndSetIfChanged(ref field, value);
            FireAndForget.Run(LoadDetailAsync);
        }
    }

    /// <summary>当前条目的完整详情。</summary>
    public PluginMarketDetail? Detail { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>详情中最高的有效稳定版本。</summary>
    public PluginMarketRelease? Release { get; private set; }
    /// <summary>是否正在加载目录。</summary>
    public bool IsLoading { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>是否正在加载当前详情。</summary>
    public bool IsLoadingDetail { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>目录错误;与成功但无结果区分。</summary>
    public string? SearchError { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>当前详情的可重试错误。</summary>
    public string? DetailError { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>下载、取消或失败的本地化提示。</summary>
    public string? OperationNotice { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>下载进度,范围 0–100。</summary>
    public int DownloadProgress
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasDownloadProgress));
            this.RaisePropertyChanged(nameof(DownloadFraction));
        }
    }
    /// <summary>仅活动下载有确定进度;安装阶段恢复不定进度。</summary>
    public bool HasDownloadProgress => IsDownloading && DownloadProgress > 0;
    /// <summary>圆环绑定的进度,范围 0–1。</summary>
    public double DownloadFraction => DownloadProgress / 100d;
    /// <summary>取消活动下载,不打断已开始的安装事务。</summary>
    public void CancelDownload() => _download?.Cancel();
    /// <summary>是否存在可取消的下载。</summary>
    public bool IsDownloading
    {
        get;
        private set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(HasDownloadProgress)); }
    }
    /// <summary>父窗口的包操作锁;阻止并行安装。</summary>
    public bool IsInstalling
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value) OperationNotice = null;
            RefreshInstalledState();
        }
    }

    /// <summary>是否可展示详情内容。</summary>
    public bool HasDetail => Detail is not null;
    /// <summary>详情标题;未加载详情时返回空文本以避免绑定空对象。</summary>
    public string DetailDisplayName => Detail?.DisplayName ?? "";
    /// <summary>详情标识;未加载详情时返回空文本以避免绑定空对象。</summary>
    public string DetailId => Detail?.Id ?? "";
    /// <summary>详情摘要;未填写或未加载详情时返回空文本。</summary>
    public string DetailSummary => Detail?.Summary ?? "";
    /// <summary>未选择条目时显示占位提示。</summary>
    public bool ShowDetailPlaceholder => Detail is null && !IsLoadingDetail && DetailError is null;
    /// <summary>成功查询且没有匹配结果。</summary>
    public bool IsEmpty => _loaded && !IsLoading && SearchError is null && Entries.Count == 0;
    /// <summary>是否可翻到上一页。</summary>
    public bool CanPrevious => !IsLoading && _page > 1;
    /// <summary>是否可翻到下一页。</summary>
    public bool CanNext => !IsLoading && _page * 20L < _total;
    /// <summary>当前页、总页数与匹配数。</summary>
    public string PageText => Strings.Format("PluginMarket_Page", _page, Math.Max(1, (_total + 19) / 20), _total);
    /// <summary>供安全 Markdown 渲染器使用的描述或下架原因。</summary>
    public string Description => Detail?.DescriptionRemovedReason ?? Detail?.DescriptionMarkdown ?? "";
    /// <summary>选定稳定版的显示版本。</summary>
    public string VersionText => Release is { } release ? $"v{release.Version}" : "";
    /// <summary>选定稳定版的包大小。</summary>
    public string SizeText => Release is { } release ? Strings.Format("PluginMarket_Size", release.PackageSize / 1048576d) : "";
    /// <summary>作者显示文本。</summary>
    public string AuthorText => Strings.Format("PluginManager_Author", Detail?.Author ?? "");
    /// <summary>许可证显示文本。</summary>
    public string LicenseText => Strings.Format("PluginMarket_License", Detail?.License ?? "—");
    /// <summary>当前插件的商店网页地址。</summary>
    public string DetailUrl => $"{market?.BaseUrl}/plugins/{Uri.EscapeDataString(Detail?.Id ?? "")}";
    /// <summary>宿主、SDK 或 API 兼容性阻断原因。</summary>
    public string? Blocker => Release is { } release
        ? manager.DescribeUpdateBlocker(release.ApiLevel, release.MinHostVersion, release.MinSdkVersion) : null;
    private PluginDescriptor? Installed => manager.Plugins.FirstOrDefault(p => p.Id == Detail?.Id);
    /// <summary>兼容且允许新装或升级用户插件时启用安装。</summary>
    public bool CanInstall => !IsInstalling && manager.IsInstallSupported && Detail is not null && Release is not null
        && Blocker is null && (Installed is null || (!Installed.IsDevelopment && manager.IsUninstallable(Installed.Id)
            && Installed.Manifest is { } manifest
            && PluginManagerViewModel.CompareForUpdate(Release.Version, manifest.Version) is null));
    /// <summary>安装、升级或已安装状态的按钮文本。</summary>
    public string InstallText => Installed is null ? Strings.Get("PluginMarket_Install")
        : Release is not null && PluginManagerViewModel.CompareForUpdate(Release.Version, Installed.Manifest?.Version) is null
            ? Strings.Format("PluginManager_UpdateTo", Release.Version) : Strings.Get("PluginMarket_InstalledTab");
    /// <summary>安装条件或本机版本的说明。</summary>
    public string StateText => Release is null ? Strings.Get("PluginMarket_NoStableRelease")
        : Blocker is { } blocker ? Strings.Format("PluginManager_UpdateNeedsHost", Release.Version, blocker)
        : Installed is { } installed ? Strings.Format("PluginMarket_InstalledVersion", installed.Manifest?.Version ?? "")
        : Strings.Get("PluginMarket_Ready");

    /// <summary>首次进入商店才加载;失败可再次进入重试。</summary>
    public Task EnsureLoadedAsync() => _loaded || IsLoading ? Task.CompletedTask : SearchAsync();
    /// <summary>提交搜索词和排序,回到第一页。</summary>
    public Task SearchAsync() => LoadPageAsync(1, Query.Trim(), SortIndex switch { 1 => "downloads", 2 => "rating", _ => "updated" });
    /// <summary>保持已提交的查询条件翻到上一页。</summary>
    public Task PreviousAsync() => CanPrevious ? LoadPageAsync(_page - 1, _appliedQuery, _appliedSort) : Task.CompletedTask;
    /// <summary>保持已提交的查询条件翻到下一页。</summary>
    public Task NextAsync() => CanNext ? LoadPageAsync(_page + 1, _appliedQuery, _appliedSort) : Task.CompletedTask;
    /// <summary>重试当前页。</summary>
    public Task RetrySearchAsync() => LoadPageAsync(_page, _appliedQuery, _appliedSort);

    private async Task LoadPageAsync(int page, string query, string sort)
    {
        if (market is null || _disposed) return;
        _search?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _search = request;
        IsLoading = true;
        SearchError = null;
        SelectedEntry = null;
        Entries.Clear();
        _page = page;
        _appliedQuery = query;
        _appliedSort = sort;
        NotifySearchState();
        try
        {
            PluginMarketPage result = await market.SearchAsync(query, sort, page, request.Token);
            if (request.IsCancellationRequested || _disposed) return;
            _total = result.Total;
            foreach (PluginMarketEntry entry in result.Items) Entries.Add(entry);
            _loaded = true;
            SelectedEntry = Entries.FirstOrDefault();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested && !_disposed)
                SearchError = Strings.Format("PluginMarket_LoadFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_search, request))
            {
                _search = null;
                IsLoading = false;
                NotifySearchState();
            }
        }
    }

    /// <summary>加载或重试当前详情;忽略已被新选择替代的响应。</summary>
    public async Task LoadDetailAsync()
    {
        _detail?.Cancel();
        _detail = null;
        Detail = null;
        Release = null;
        DetailError = null;
        IsLoadingDetail = false;
        NotifyDetailState();
        if (market is null || SelectedEntry is not { } entry || _disposed) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detail = request;
        IsLoadingDetail = true;
        NotifyDetailState();
        try
        {
            PluginMarketDetail result = await market.GetDetailAsync(entry.Id, request.Token);
            if (request.IsCancellationRequested || _disposed) return;
            Detail = result;
            Release = SelectStableRelease(result.Versions);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested && !_disposed)
                DetailError = Strings.Format("PluginMarket_LoadFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_detail, request))
            {
                _detail = null;
                IsLoadingDetail = false;
                NotifyDetailState();
            }
        }
    }

    internal static PluginMarketRelease? SelectStableRelease(IEnumerable<PluginMarketRelease> versions) => versions
        .Where(v => SemanticVersion.TryParse(v.Version, out SemanticVersion parsed) && !parsed.IsPreRelease)
        .OrderByDescending(v => v.Version, SemanticVersion.TextComparer).FirstOrDefault();

    /// <summary>快照安装目标,防止后续选择变化改变下载与安装对象。</summary>
    public PluginMarketVersion? CreateInstallRequest() => CanInstall && Detail is { } detail && Release is { } release
        ? new()
        {
            Id = detail.Id,
            Version = release.Version,
            ApiLevel = release.ApiLevel,
            MinHostVersion = release.MinHostVersion,
            MinSdkVersion = release.MinSdkVersion
        }
        : null;

    /// <summary>下载到独立临时包;成功后由调用方负责删除,失败或取消返回空。</summary>
    public async Task<string?> DownloadAsync(PluginMarketVersion package)
    {
        if (market is null || _disposed || IsDownloading) return null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = request;
        string path = Path.Combine(new Infrastructure.Persistence.VelaShellStoragePaths().PluginUpdateDirectory,
            $"market-{Guid.NewGuid():N}.vpx");
        IsDownloading = true;
        DownloadProgress = 0;
        OperationNotice = Strings.Get("PluginMarket_Downloading");
        try
        {
            await market.DownloadAsync(package.Id, package.Version, path,
                new Progress<int>(value => { if (!_disposed && ReferenceEquals(_download, request)) DownloadProgress = value; }), request.Token);
            request.Token.ThrowIfCancellationRequested();
            OperationNotice = null;
            return path;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            OperationNotice = Strings.Get("PluginMarket_Cancelled");
            PluginManagerViewModel.DiscardDownloadedUpdate(path);
            return null;
        }
        catch (Exception ex)
        {
            OperationNotice = Strings.Format("PluginManager_InstallFailed", ex.Message);
            PluginManagerViewModel.DiscardDownloadedUpdate(path);
            return null;
        }
        finally
        {
            _download = null;
            IsDownloading = false;
            PluginManagerViewModel.DiscardDownloadedUpdate(path + ".partial");
        }
    }

    /// <summary>安装目录变化或操作锁变化后刷新按钮与本机版本。</summary>
    public void RefreshInstalledState()
    {
        this.RaisePropertyChanged(nameof(CanInstall));
        this.RaisePropertyChanged(nameof(InstallText));
        this.RaisePropertyChanged(nameof(StateText));
    }

    private void NotifySearchState()
    {
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(CanPrevious));
        this.RaisePropertyChanged(nameof(CanNext));
        this.RaisePropertyChanged(nameof(PageText));
    }

    private void NotifyDetailState()
    {
        foreach (string name in new[] { nameof(HasDetail), nameof(DetailDisplayName), nameof(DetailId), nameof(DetailSummary),
            nameof(ShowDetailPlaceholder), nameof(Description), nameof(VersionText), nameof(SizeText), nameof(AuthorText),
            nameof(LicenseText), nameof(DetailUrl), nameof(Blocker) })
            this.RaisePropertyChanged(name);
        RefreshInstalledState();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
