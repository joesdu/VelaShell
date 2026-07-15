using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

/// <summary>侧边栏视图:承载资源管理器、快捷命令、最近连接与底部设置入口。</summary>
public partial class SidebarView : UserControl
{
    private GridLength _quickCommandsExpandedHeight = new(160);
    private GridLength _recentConnectionsExpandedHeight = new(180);
    private bool _quickCommandsExpanded = true;
    private bool _recentConnectionsExpanded = true;
    private SidebarViewModel? _viewModel;

    /// <summary>创建侧边栏视图并加载其可视组件。</summary>
    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>用户请求打开“新建连接”配置弹窗时触发(顶部新建按钮)。</summary>
    public event EventHandler? OpenConnectionProfileRequested;

    /// <summary>Raised by the footer gear button to open the settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user double-clicks a recent connection to reconnect to it.</summary>
    public event EventHandler<RecentConnectionEntry>? RecentConnectRequested;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as SidebarViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        ApplyQuickCommandsVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.IsQuickCommandsVisible))
        {
            ApplyQuickCommandsVisibility();
        }
    }

    private void OpenConnectionProfile_Click(object? sender, RoutedEventArgs e)
    {
        OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleQuickCommands_Click(object? sender, RoutedEventArgs e)
    {
        RowDefinition quickCommandsRow = SessionAndQuickGrid.RowDefinitions[2];
        if (_quickCommandsExpanded && quickCommandsRow.ActualHeight > 36)
        {
            _quickCommandsExpandedHeight = new(quickCommandsRow.ActualHeight);
        }
        _quickCommandsExpanded = !_quickCommandsExpanded;
        ApplyQuickCommandsVisibility();
    }

    private void ApplyQuickCommandsVisibility()
    {
        bool visible = _viewModel is { IsQuickCommandsVisible: true, QuickCommands: not null };
        RowDefinition splitterRow = SessionAndQuickGrid.RowDefinitions[1];
        RowDefinition quickCommandsRow = SessionAndQuickGrid.RowDefinitions[2];
        if (
            !visible
            && QuickCommandsSection.IsVisible
            && _quickCommandsExpanded
            && quickCommandsRow.ActualHeight > 36
        )
        {
            _quickCommandsExpandedHeight = new(quickCommandsRow.ActualHeight);
        }
        QuickCommandsSection.IsVisible = visible;
        if (!visible)
        {
            splitterRow.Height = new(0);
            quickCommandsRow.MinHeight = 0;
            quickCommandsRow.Height = new(0);
            QuickCommandsDivider.IsVisible = false;
            QuickCommandsSplitter.IsVisible = false;
            return;
        }

        QuickCommandsContent.IsVisible = _quickCommandsExpanded;
        QuickCommandsExpandedIcon.IsVisible = _quickCommandsExpanded;
        QuickCommandsCollapsedIcon.IsVisible = !_quickCommandsExpanded;
        if (_quickCommandsExpanded)
        {
            splitterRow.Height = new(5);
            quickCommandsRow.MinHeight = 100;
            quickCommandsRow.Height = _quickCommandsExpandedHeight;
            QuickCommandsDivider.IsVisible = true;
            QuickCommandsSplitter.IsVisible = true;
        }
        else
        {
            splitterRow.Height = new(0);
            quickCommandsRow.MinHeight = 36;
            quickCommandsRow.Height = new(36);
            QuickCommandsDivider.IsVisible = false;
            QuickCommandsSplitter.IsVisible = false;
        }
    }

    private void ToggleRecentConnections_Click(object? sender, RoutedEventArgs e)
    {
        RowDefinition recentRow = SidebarSectionsGrid.RowDefinitions[2];
        if (_recentConnectionsExpanded && recentRow.ActualHeight > 36)
        {
            _recentConnectionsExpandedHeight = new(recentRow.ActualHeight);
        }
        _recentConnectionsExpanded = !_recentConnectionsExpanded;
        RecentConnectionsContent.IsVisible = _recentConnectionsExpanded;
        RecentConnectionsExpandedIcon.IsVisible = _recentConnectionsExpanded;
        RecentConnectionsCollapsedIcon.IsVisible = !_recentConnectionsExpanded;
        RowDefinition splitterRow = SidebarSectionsGrid.RowDefinitions[1];
        if (_recentConnectionsExpanded)
        {
            splitterRow.Height = new(5);
            recentRow.MinHeight = 100;
            recentRow.Height = _recentConnectionsExpandedHeight;
            RecentConnectionsDivider.IsVisible = true;
            RecentConnectionsSplitter.IsVisible = true;
        }
        else
        {
            splitterRow.Height = new(0);
            recentRow.MinHeight = 36;
            recentRow.Height = new(36);
            RecentConnectionsDivider.IsVisible = false;
            RecentConnectionsSplitter.IsVisible = false;
        }
    }

    private void RecentConnection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RecentConnectionItemViewModel item })
        {
            RecentConnectRequested?.Invoke(this, item.Entry);
        }
    }
}
