using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.ViewModels;
using VelaShell.Core.Data;
using VelaShell.Core.Recording;

namespace VelaShell.Views.Settings;

public partial class SecurityAuditPage : UserControl
{
    public SecurityAuditPage()
    {
        InitializeComponent();
    }

    /// <summary>打开会话录制回放中心(设计 NceE6),非模态独立窗口。</summary>
    private void OpenRecordingPlayer_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app || app.Services is null)
        {
            return;
        }
        if (app.Services.GetService<ISessionRecordingStore>() is not { } store)
        {
            return;
        }
        var viewModel = new RecordingPlayerViewModel(store, app.Services.GetService<ISettingsService>());
        var window = new RecordingPlayerView { DataContext = viewModel };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
        _ = viewModel.InitializeAsync();
    }
}
