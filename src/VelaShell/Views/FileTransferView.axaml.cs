using Avalonia.Controls;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>文件传输视图,展示传输进度与结果提示。</summary>
public partial class FileTransferView : UserControl
{
    /// <summary>初始化视图,并把指针悬停接线到暂停提示自动隐藏。</summary>
    public FileTransferView()
    {
        InitializeComponent();

        // 悬停在提示上会暂停其自动隐藏,以便查看结果;指针离开后
        // 3 秒倒计时恢复(§9)。
        PointerEntered += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(true);
        PointerExited += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(false);
    }
}
