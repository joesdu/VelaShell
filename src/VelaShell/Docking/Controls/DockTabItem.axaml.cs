using Avalonia.Interactivity;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Docking.Controls;

/// <summary>Terminal-specific tab behavior retained separately from SFTP tabs.</summary>
public partial class DockTabItem : DockTabItemBase
{
    /// <summary>Initializes the terminal-specific tab control.</summary>
    public DockTabItem() => InitializeComponent();

    private TerminalTabViewModel? Terminal => (DataContext as Docking.TerminalDocument)?.Terminal;

    private void JoinChannelA_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.A);
    private void JoinChannelB_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.B);
    private void JoinChannelC_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.C);
    private void JoinChannelD_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.D);
    private void LeaveSyncChannel_Click(object? sender, RoutedEventArgs e) => Terminal?.LeaveSyncChannel();
}
