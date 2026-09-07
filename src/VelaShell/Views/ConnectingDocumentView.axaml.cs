using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using Avalonia.VisualTree;

namespace VelaShell.Views;

/// <summary>
/// 文档型连接在握手完成前的占位视图:居中一张卡片,连接中转圈 + 「取消」,
/// 失败后换成原因 + 「重新连接 / 关闭标签页」(与终端标签页内的断开覆盖层同款)。
/// </summary>
public partial class ConnectingDocumentView : UserControl
{
    /// <summary>初始化「连接中」占位视图。</summary>
    public ConnectingDocumentView() => InitializeComponent();

    private ConnectingDocument? Document => DataContext as ConnectingDocument;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Document?.Cancel();

    private void Retry_Click(object? sender, RoutedEventArgs e) => Document?.Retry();

    /// <summary>
    /// 关闭这个占位标签。走工作区的 <see cref="DockWorkspace.RequestClose" />(与标签上的 × 同一条路),
    /// 而不是自己去调宿主 —— 关闭前的确认闸挂在工作区那一层。
    /// </summary>
    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (Document is { } document
            && this.FindAncestorOfType<DockWorkspaceControl>()?.Workspace is { } workspace)
        {
            workspace.RequestClose(document);
        }
    }
}
