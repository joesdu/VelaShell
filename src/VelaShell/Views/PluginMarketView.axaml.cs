using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using LiveMarkdown.Avalonia;
using TextMateSharp.Grammars;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>商店浏览视图,安装确认交由拥有窗口统一处理。</summary>
public partial class PluginMarketView : UserControl
{
    private PluginMarketViewModel? _subscribed;
    /// <summary>请求拥有窗口按统一信任流程安装当前选择。</summary>
    public event EventHandler? InstallRequested;

    /// <summary>初始化浏览交互及安全 Markdown 渲染。</summary>
    public PluginMarketView()
    {
        InitializeComponent();
        DescriptionMarkdown.LinkClick += OnLinkClick;
        DataContextChanged += (_, _) => BindViewModel();
        AttachedToVisualTree += (_, _) => BindViewModel();
        DetachedFromVisualTree += (_, _) => UnbindViewModel();
        ActualThemeVariantChanged += (_, _) => UpdateCodeTheme();
        UpdateCodeTheme();
    }

    private PluginMarketViewModel? ViewModel => DataContext as PluginMarketViewModel;

    private void BindViewModel()
    {
        UnbindViewModel();
        _subscribed = ViewModel;
        if (_subscribed is null) return;
        _subscribed.PropertyChanged += OnStateChanged;
        RenderDescription();
    }

    private void UnbindViewModel()
    {
        _subscribed?.PropertyChanged -= OnStateChanged;
        _subscribed = null;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginMarketViewModel.Description)) RenderDescription();
    }

    private void RenderDescription() => DescriptionMarkdown.DocumentUpdate =
        new MarkdownDocumentUpdate.Full(RemoteMarkdown.Parse(ViewModel?.Description ?? ""));

    private void UpdateCodeTheme() => DescriptionMarkdown.CodeBlockColorTheme =
        ActualThemeVariant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

    private void Search_Click(object? sender, RoutedEventArgs e) => Run(vm => vm.SearchAsync());
    private void RetrySearch_Click(object? sender, RoutedEventArgs e) => Run(vm => vm.RetrySearchAsync());
    private void RetryDetail_Click(object? sender, RoutedEventArgs e) => Run(vm => vm.LoadDetailAsync());
    private void Previous_Click(object? sender, RoutedEventArgs e) => Run(vm => vm.PreviousAsync());
    private void Next_Click(object? sender, RoutedEventArgs e) => Run(vm => vm.NextAsync());
    private void Install_Click(object? sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);
    private void OpenWeb_Click(object? sender, RoutedEventArgs e) => OpenLink(ViewModel?.DetailUrl);

    private void OnLinkClick(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        OpenLink(e.HRef?.OriginalString);
    }

    private void OpenLink(string? url)
    {
        if (RemoteMarkdown.IsAllowedLink(url) && TopLevel.GetTopLevel(this) is { } top)
            FireAndForget.Run(async () => { await top.Launcher.LaunchUriAsync(new Uri(url!)); });
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Run(vm => vm.SearchAsync());
    }

    private void Sort_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && sender is ComboBox { SelectedIndex: >= 0 } sort && ViewModel is { } vm)
        {
            vm.SortIndex = sort.SelectedIndex;
            Run(model => model.SearchAsync());
        }
    }

    private void Run(Func<PluginMarketViewModel, Task> operation)
    {
        if (ViewModel is { } vm) FireAndForget.Run(() => operation(vm));
    }
}
