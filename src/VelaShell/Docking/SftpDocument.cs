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
    /// <summary>Initializes the SFTP dock document from the given view model.</summary>
    public SftpDocument(SftpDocumentViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Id = viewModel.SessionId.ToString("N");
        Title = viewModel.Title;
    }

    /// <summary>Backing view model for the SFTP document.</summary>
    public SftpDocumentViewModel ViewModel { get; }

    /// <summary>Accent brush derived from the connection profile for visual identification.</summary>
    public IBrush ConnectionAccentBrush => ConnectionAccent.BrushFor(ViewModel.Profile.Id);

    /// <summary>Tooltip text showing connection details and profile information.</summary>
    public string ConnectionTooltip =>
        $"{Title} · SFTP · {ViewModel.Profile.Username}@{ViewModel.Profile.Host}:{ViewModel.Profile.Port}";

    /// <summary>Creates the SFTP document view for docking.</summary>
    public Control CreateView() => new SftpDocumentView { DataContext = ViewModel };
}
