using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VelaShell.Behaviors;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>文件传输视图,展示传输进度与结果提示;表头可拖动,位置跨会话保留。</summary>
public partial class FileTransferView : UserControl
{
    /// <summary>初始化视图,接线指针悬停(暂停自动隐藏)与表头拖拽。</summary>
    public FileTransferView()
    {
        InitializeComponent();

        // 「正在编辑」行上的"打开本地副本所在目录":只有视图拿得到 TopLevel.Launcher。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FileTransferViewModel vm)
            {
                vm.RevealLocalPath = RevealLocalPathAsync;
            }
        };

        // 悬停在提示上会暂停其自动隐藏,以便查看结果;指针离开后
        // 3 秒倒计时恢复(§9)。
        PointerEntered += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(true);
        PointerExited += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(false);

        // 拖拽 + 越界夹紧 + 松手落盘,与消息中心共用一份实现。
        if (this.FindControl<Border>("DragHandle") is { } handle)
        {
            PanelDragHandler.Attach(this, handle);
        }
    }

    /// <summary>
    /// 在系统文件管理器里打开本地副本所在的目录。
    /// </summary>
    /// <remarks>
    /// 定位到<b>目录</b>而不是文件:回传一直失败时用户来这儿是为了把草稿捞走,
    /// 用默认程序再把它打开一遍并不是他要的。
    /// </remarks>
    private async Task RevealLocalPathAsync(string localPath)
    {
        string? directory = Path.GetDirectoryName(localPath);
        if (TopLevel.GetTopLevel(this) is not { } top || string.IsNullOrEmpty(directory))
        {
            return;
        }
        await top.Launcher.LaunchDirectoryInfoAsync(new(directory));
    }
}
