using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Reactive;
using Avalonia.Styling;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using ReactiveUI.Primitives;
using TextMateSharp.Grammars;
using VelaShell.Core.Resources;
using VelaShell.PluginSdk.Versioning;
using VelaShell.Services;
using VelaShell.Services.Update;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>展示当前版本或可用正式版的更新说明,更新操作固定到展示的清单。</summary>
public partial class ReleaseNotesDialog : Window
{
    private readonly IReleaseNotesSource? _source;
    private readonly SettingsViewModel? _updates;
    private readonly string? _currentVersion;
    private readonly bool _selectVersion;
    private ReleaseNotes? _notes;
    private bool _hasUpdate;
    private bool _downloadStarted;
    private bool _downloading;
    private CancellationTokenSource? _request;
    private bool _closed;
    private readonly List<IDisposable> _skinSubscriptions = [];

    /// <summary>仅初始化界面,供设计器及窗口外框测试使用,不联网。</summary>
    public ReleaseNotesDialog()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Styles.Add(new Style(x => x.OfType<Image>().Class("Link"))
        {
            Setters = { new Setter(AsyncImageLoader.HandlersProperty, Array.Empty<AsyncImageLoaderHandler>()) }
        });
        foreach ((string key, string token) in new[]
        {
            ("BorderColor", "VelaBorderPrimary"), ("BorderBrush", "VelaBorderPrimary"),
            ("SecondaryCardBackgroundColor", "VelaBgInput"), ("CardBackgroundColor", "VelaBgHover"),
            ("ForegroundColor", "VelaTextPrimary"), ("CodeInlineColor", "VelaAccent"),
            ("QuoteBorderColor", "VelaAccent")
        })
        {
            _skinSubscriptions.Add(this.GetResourceObservable(token).Subscribe(new AnonymousObserver<object?>(value =>
            {
                if (value != null && (!Resources.TryGetValue(key, out object? current) || !Equals(current, value)))
                {
                    Resources[key] = value;
                }
            })));
        }
        BodyMarkdown.LinkClick += OnMarkdownLinkClicked;
        ActualThemeVariantChanged += (_, _) => UpdateCodeTheme();
        UpdateCodeTheme();
    }

    /// <summary>窗口打开后通过指定发布源加载更新说明。</summary>
    public ReleaseNotesDialog(IReleaseNotesSource source) : this() => _source = source;

    /// <summary>按运行版本选择日志,并复用关于页的下载、进度与重启状态。</summary>
    public ReleaseNotesDialog(IReleaseNotesSource source, SettingsViewModel updates) : this(source, updates.CurrentUpdateVersion)
    {
        _updates = updates;
        updates.PropertyChanged += OnUpdateStateChanged;
        RefreshUpdateAction();
    }

    internal ReleaseNotesDialog(IReleaseNotesSource source, string? currentVersion) : this(source)
    {
        _currentVersion = currentVersion;
        _selectVersion = true;
    }

    /// <summary>「在 GitHub 上查看」要打开的发布页;没有可用地址时为 null。</summary>
    internal Uri? ReleaseUri { get; private set; }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling);
        }
        FireAndForget.Run(LoadAsync);
    }

    private async Task LoadAsync()
    {
        if (_source == null || _request != null || _closed)
        {
            return;
        }
        using CancellationTokenSource request = new();
        _request = request;
        LoadingPanel.IsVisible = true;
        LoadingRing.IsIndeterminate = true;
        StatusText.IsVisible = false;
        ReleaseHeader.IsVisible = false;
        GitHubButton.IsEnabled = false;
        ReleaseUri = null;
        NotesScroll.IsVisible = false;
        RetryButton.IsVisible = false;
        CheckStatusText.IsVisible = false;
        _notes = null;
        _hasUpdate = false;
        RefreshUpdateAction();
        try
        {
            ReleaseNotes? notes;
            if (!_selectVersion)
            {
                notes = await _source.GetLatestReleaseNotesAsync(request.Token);
            }
            else if (!SemanticVersion.TryParse(_currentVersion, out SemanticVersion current))
            {
                StatusText.Text = Strings.Get("ReleaseNotes_UnknownVersion");
                StatusText.IsVisible = true;
                return;
            }
            else
            {
                Exception? checkError = null;
                ReleaseNotes? latest = null;
                try
                {
                    latest = await _source.GetLatestReleaseNotesAsync(request.Token);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    checkError = ex;
                }
                if (latest != null && (!SemanticVersion.TryParse(latest.Tag, out SemanticVersion latestVersion)
                    || latestVersion.IsPreRelease))
                {
                    checkError = new FormatException("Invalid release version.");
                    latest = null;
                }
                _hasUpdate = latest != null && SemanticVersion.TryParse(latest.Tag, out SemanticVersion newest) && newest > current;
                bool sameVersion = latest != null && SemanticVersion.TryParse(latest.Tag, out SemanticVersion published) && published == current;
                notes = _hasUpdate || sameVersion
                    ? latest
                    : await _source.GetReleaseNotesForVersionAsync(_currentVersion!, request.Token);
                if (checkError != null && !_closed)
                {
                    Trace.WriteLine($"[ReleaseNotes] Update discovery failed: {checkError}");
                    CheckStatusText.Text = Strings.Get("ReleaseNotes_CheckFailed");
                    CheckStatusText.IsVisible = true;
                    RetryButton.IsVisible = true;
                }
            }
            if (_closed)
            {
                return;
            }
            _notes = notes;
            VersionText.Text = notes?.Tag;
            VersionBadge.Text = Strings.Get(_hasUpdate || !_selectVersion ? "ReleaseNotes_Latest" : "ReleaseNotes_CurrentVersion");
            ReleaseHeader.IsVisible = notes != null;
            PublishedText.IsVisible = notes?.PublishedAt != null;
            PublishedText.Text = notes?.PublishedAt is { } publishedAt
                ? Strings.Format("ReleaseNotes_PublishedAt", publishedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
                : null;
            // 发布页地址取清单中的 htmlUrl(那个版本自己的发布页),但只放行本仓库的 releases 页;缺失、不是 https、
            // 别的站点或别的仓库一律退回本仓库的发布列表 —— 按钮写的是「前往发布页」,不能把人带到别处(#655 复审,维护者 2026-10-25 定)
            ReleaseUri = notes is null ? null : ReleasePage.Resolve(notes.HtmlUrl);
            GitHubButton.IsEnabled = ReleaseUri != null;
            if (notes == null || string.IsNullOrWhiteSpace(notes.Body))
            {
                StatusText.Text = Strings.Get(notes == null
                    ? _selectVersion ? "ReleaseNotes_NoCurrentRelease" : "ReleaseNotes_NoRelease"
                    : "ReleaseNotes_EmptyBody");
                StatusText.IsVisible = true;
            }
            else
            {
                BodyMarkdown.DocumentUpdate = new MarkdownDocumentUpdate.Full(ParseNotes(notes.Body));
                NotesScroll.IsVisible = true;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // 关闭窗口引起的取消不显示失败;发布源内部超时则进入错误状态,允许重试。
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReleaseNotes] {ex}");
            if (!_closed)
            {
                // 下载服务仍可能限制请求;保留状态码提示,不把所有失败都归为网络故障。
                bool rateLimited = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests };
                StatusText.Text = Strings.Get(rateLimited ? "ReleaseNotes_RateLimited" : "ReleaseNotes_LoadFailed");
                StatusText.IsVisible = true;
                RetryButton.IsVisible = true;
            }
        }
        finally
        {
            _request = null;
            if (!_closed)
            {
                LoadingPanel.IsVisible = false;
                LoadingRing.IsIndeterminate = false;
                RefreshUpdateAction();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _request?.Cancel();
        _updates?.PropertyChanged -= OnUpdateStateChanged;
        foreach (IDisposable subscription in _skinSubscriptions)
        {
            subscription.Dispose();
        }
        base.OnClosed(e);
    }

    internal static MarkdownDocument ParseNotes(string body) => RemoteMarkdown.Parse(body);

    internal static bool IsAllowedLink(string? url) => RemoteMarkdown.IsAllowedLink(url);

    private void UpdateCodeTheme() => BodyMarkdown.CodeBlockColorTheme =
        ActualThemeVariant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

    private void OnUpdateStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_closed && (e.PropertyName is nameof(SettingsViewModel.IsUpdating)
            or nameof(SettingsViewModel.ReadyUpdate) or nameof(SettingsViewModel.UpdateReady)))
        {
            RefreshUpdateAction();
        }
        else if (!_closed && e.PropertyName is nameof(SettingsViewModel.UpdateStatus))
        {
            // 下载进度每秒刷新多次,只换状态文字;按钮要判断能否自更新,那会在应用目录里建删探测文件。
            RefreshUpdateStatus();
        }
    }

    private bool IsDisplayedUpdateReady() => _notes?.Manifest is { } target && _updates?.ReadyUpdate is { } ready
        && string.Equals(target.Tag, ready.Tag, StringComparison.Ordinal)
        && string.Equals(target.Version, ready.Version, StringComparison.Ordinal)
        && Equals(target.AssetForCurrentPlatform(), ready.AssetForCurrentPlatform());

    private void RefreshUpdateAction()
    {
        UpdateButton.IsVisible = false;
        DownloadNotice.IsVisible = _downloading;
        RefreshUpdateStatus();
        if (_updates == null || _updates.UpdatesManagedByStore || !_hasUpdate || _notes?.Manifest == null)
        {
            return;
        }
        bool manual = !_updates.CanSelfUpdate || OperatingSystem.IsLinux();
        UpdateButton.IsVisible = !manual || ReleaseUri != null;
        UpdateButton.IsEnabled = !_updates.IsUpdating;
        UpdateButton.Content = Strings.Get(manual ? "ReleaseNotes_OpenReleasePage"
            : IsDisplayedUpdateReady() ? "SetAbout_RestartToUpdate" : "ReleaseNotes_UpdateNow");
    }

    private void RefreshUpdateStatus()
    {
        UpdateStatusText.IsVisible = false;
        if (_updates == null)
        {
            return;
        }
        if (_updates.UpdatesManagedByStore)
        {
            UpdateStatusText.Text = Strings.Get("SetAbout_StoreManagedUpdates");
            UpdateStatusText.IsVisible = true;
            return;
        }
        if (_downloadStarted || _updates.IsUpdating || _updates.UpdateReady)
        {
            UpdateStatusText.Text = _updates.UpdateStatus;
            UpdateStatusText.IsVisible = !string.IsNullOrEmpty(_updates.UpdateStatus);
        }
    }

    private void Update_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_updates == null || !_hasUpdate || _notes?.Manifest is not { } manifest
            || _updates.IsUpdating || _updates.UpdatesManagedByStore)
        {
            return;
        }
        if (!_updates.CanSelfUpdate || OperatingSystem.IsLinux())
        {
            if (ReleaseUri is { } uri)
            {
                await Launcher.LaunchUriAsync(uri);
            }
            return;
        }
        if (IsDisplayedUpdateReady())
        {
            await _updates.RestartToUpdateCommand.Execute().FirstAsync();
            return;
        }
        _downloadStarted = true;
        _downloading = true;
        RefreshUpdateAction();
        // 下载由共享 VM 持有;关闭日志窗口只取消日志请求,不取消用户已经启动的更新。
        await _updates.UpdateFromReleaseAsync(manifest);
        // 「关闭后下载仍会继续」只陪着进行中的下载;失败或被当场拒绝后收起,原因留在状态文字里。
        _downloading = false;
        if (!_closed)
        {
            RefreshUpdateAction();
        }
    });

    private void OnMarkdownLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        if (e.HRef is { } uri && IsAllowedLink(uri.OriginalString))
        {
            FireAndForget.Run(async () => await Launcher.LaunchUriAsync(uri));
        }
    }

    private void GitHub_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ReleaseUri is { } uri)
        {
            await Launcher.LaunchUriAsync(uri);
        }
    });

    private void Retry_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(LoadAsync);

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }
}
