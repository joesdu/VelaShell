using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using System.Linq;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;
using ReactiveUI;

namespace PulseTerm.App.Views;

public partial class MainWindow : Window
{
    /// <summary>File panel height restored when the panel reopens (§6: default 220px, drag to grow).</summary>
    private double _lastFileRowHeight = 220;
    private IDisposable? _fileBrowserVisibilitySub;

    public MainWindow()
    {
        InitializeComponent();

        if (this.FindControl<SidebarView>("SidebarHost") is { } sidebar)
        {
            sidebar.OpenConnectionProfileRequested += OnOpenConnectionProfileRequested;
            sidebar.RecentConnectRequested += OnSidebarRecentConnectRequested;
        }

        DataContextChanged += (_, _) => HookFileBrowserVisibility();
        Opened += OnWindowOpened;
    }

    /// <summary>Collapses/expands the file-panel rows as FileBrowser.IsVisible flips. WhenAnyValue
    /// tracks through the FileBrowser property itself, so per-tab rebinds re-subscribe automatically.</summary>
    private void HookFileBrowserVisibility()
    {
        _fileBrowserVisibilitySub?.Dispose();
        _fileBrowserVisibilitySub = null;

        if (DataContext is not MainWindowViewModel vm)
            return;

        _fileBrowserVisibilitySub = vm
            .WhenAnyValue(x => x.FileBrowser.IsVisible)
            .Subscribe(visible => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetFileRowsVisible(visible)));
    }

    private void SetFileRowsVisible(bool visible)
    {
        if (this.FindControl<Grid>("MainAreaGrid") is not { RowDefinitions.Count: >= 3 } grid)
            return;

        var splitterRow = grid.RowDefinitions[1];
        var fileRow = grid.RowDefinitions[2];

        if (visible)
        {
            splitterRow.Height = new GridLength(5);
            fileRow.MinHeight = 120;
            fileRow.Height = new GridLength(Math.Max(_lastFileRowHeight, 120));
        }
        else
        {
            // Remember the user's dragged height so reopening restores it.
            if (fileRow.Height.IsAbsolute && fileRow.Height.Value > 0)
                _lastFileRowHeight = fileRow.Height.Value;

            fileRow.MinHeight = 0;
            fileRow.Height = new GridLength(0);
            splitterRow.Height = new GridLength(0);
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.TerminalSearchRequested += OnTerminalSearchRequested;
            vm.NewConnectionRequested += (_, _) => _ = OpenProfileDialogAsync(existing: null);

            // 资源管理器树:右键连接/双击连接 + 右键编辑。
            if (vm.Sidebar.SessionTree is { } tree)
            {
                tree.ConnectRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    var tab = await vm.TryConnectProfileAsync(profile);
                    if (tab is null && vm.LastConnectionError is { Length: > 0 } error)
                        await ShowConnectionErrorAsync(error);
                });
                tree.EditRequested += profile => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = OpenProfileDialogAsync(profile));
            }

            await vm.InitializeAsync();
        }
    }

    /// <summary>Menu/palette 终端内查找 → opens the visible terminal view's search bar.</summary>
    private void OnTerminalSearchRequested(object? sender, EventArgs e)
    {
        foreach (var view in this.GetVisualDescendants().OfType<TerminalTabView>())
        {
            if (view.IsEffectivelyVisible)
            {
                view.OpenSearch();
                return;
            }
        }
    }

    // The window uses the native OS title bar per design spec §2 — no custom chrome.

    private async void OnSidebarRecentConnectRequested(object? sender, RecentConnectionEntry entry)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var tab = await vm.TryConnectRecentAsync(entry);
        if (tab is null && vm.LastConnectionError is { Length: > 0 } error)
            await ShowConnectionErrorAsync(error);
    }

    private void OnOpenConnectionProfileRequested(object? sender, EventArgs e)
        => _ = OpenProfileDialogAsync(existing: null);

    /// <summary>打开“新建连接”弹窗;传入 existing 时为编辑既有配置。</summary>
    private async Task OpenProfileDialogAsync(SessionProfile? existing)
    {
        if (DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }

        if (App.Current is not App app || app.Services is null)
        {
            return;
        }

        var connectionProfileViewModel = new ConnectionProfileViewModel(
            existing: existing,
            connectionWorkflowService: app.Services.GetService<IConnectionWorkflowService>(),
            sessionRepository: app.Services.GetService<PulseTerm.Core.Data.ISessionRepository>());

        var dialog = new ConnectionProfileView
        {
            DataContext = connectionProfileViewModel
        };

        var profile = await dialog.ShowDialog<SessionProfile?>(this);
        if (profile is null)
        {
            return;
        }

        // 保存/连接均已持久化配置 —— 资源管理器树同步刷新。
        await mainWindowViewModel.RefreshSessionTreeAsync();

        // 仅“连接”按钮触发实际连接;“保存”只落库。
        if (!connectionProfileViewModel.ConnectAfterClose)
        {
            return;
        }

        // TryConnectProfileAsync never throws — a failed auth/connection is reported, not crashed.
        var tab = await mainWindowViewModel.TryConnectProfileAsync(profile);
        if (tab is null && mainWindowViewModel.LastConnectionError is { Length: > 0 } error)
        {
            await ShowConnectionErrorAsync(error);
        }
    }

    private Task ShowConnectionErrorAsync(string message)
    {
        var okButton = new Button
        {
            Content = "确定",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 6),
        };

        var dialog = new Window
        {
            Title = "连接失败",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.FindResource("PulseBgSurface") as IBrush ?? Brushes.Transparent,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 20,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = this.FindResource("PulseTextPrimary") as IBrush ?? Brushes.White,
                    },
                    okButton,
                },
            },
        };

        okButton.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog(this);
    }
}
