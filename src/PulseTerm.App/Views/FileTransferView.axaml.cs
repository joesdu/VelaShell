using Avalonia.Controls;
using Avalonia.Input;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views;

public partial class FileTransferView : UserControl
{
    public FileTransferView()
    {
        InitializeComponent();

        // Hovering the toast pauses its auto-hide so results can be inspected; the 3s
        // countdown resumes when the pointer leaves (用户反馈 §9).
        PointerEntered += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(true);
        PointerExited += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(false);
    }
}
