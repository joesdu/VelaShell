using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Docking;

/// <summary>Dock document for a standalone, session-bound dual-pane SFTP browser.</summary>
public sealed class SftpDocument : DockDocument, IDockViewProvider
{
    public SftpDocument(SftpDocumentViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Id = viewModel.SessionId.ToString("N");
        Title = viewModel.Title;
    }

    public SftpDocumentViewModel ViewModel { get; }

    public IBrush ConnectionAccentBrush => ConnectionAccent.BrushFor(ViewModel.Profile.Id);

    public string ConnectionTooltip =>
        $"{Title} · SFTP · {ViewModel.Profile.Username}@{ViewModel.Profile.Host}:{ViewModel.Profile.Port}";

    public Control CreateView() => new SftpDocumentView { DataContext = ViewModel };
}
