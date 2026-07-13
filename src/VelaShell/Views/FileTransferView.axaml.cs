using Avalonia.Controls;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>File transfer view showing transfer progress and result toasts.</summary>
public partial class FileTransferView : UserControl
{
    /// <summary>Initializes the view and wires pointer hover to pause the toast auto-hide.</summary>
    public FileTransferView()
    {
        InitializeComponent();

        // Hovering the toast pauses its auto-hide so results can be inspected; the 3s
        // countdown resumes when the pointer leaves (用户反馈 §9).
        PointerEntered += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(true);
        PointerExited += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(false);
    }
}
