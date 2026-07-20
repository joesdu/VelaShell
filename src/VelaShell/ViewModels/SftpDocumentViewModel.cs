using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>Owns all state and lifetime resources for one standalone SFTP document.</summary>
public sealed class SftpDocumentViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly IConnectionWorkflowService _workflow;
    private readonly SerializedSftpService _serializedSftp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _initialLoad;
    private Task? _closeTask;
    private readonly object _closeSync = new();

    /// <summary>Initializes the SFTP document view model and begins loading local/remote file trees.</summary>
    public SftpDocumentViewModel(
        SessionProfile profile,
        SshSession session,
        IConnectionWorkflowService workflow,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _serializedSftp = new SerializedSftpService(sftpService ?? throw new ArgumentNullException(nameof(sftpService)), session.SessionId);
        SessionId = session.SessionId;
        Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        RemoteFiles = new FileBrowserViewModel(_serializedSftp, SessionId)
        {
            ServerDisplayName = Title,
            TransferSink = transferSink,
            TransferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions)),
            IsVisible = true,
            IsDragEnabled = true,
        };
        LocalFiles = new LocalFilePaneViewModel(transferOptions);
        LocalFiles.UploadSelectedAsync = () => UploadSelectedAsync();
        RemoteFiles.PickFolderForDownload = () => Task.FromResult<string?>(LocalFiles.CurrentPath);
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(UploadSelectedAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(DownloadSelectedAsync);
        DeleteLocalSelectedCommand = LocalFiles.DeleteSelectedCommand;
        LocalFiles.ConfirmDelete = message => ConfirmLocalDelete?.Invoke(message) ?? Task.FromResult(false);
        _initialLoad = LoadAsync();
    }

    /// <summary>The session profile used to establish the connection.</summary>
    public SessionProfile Profile { get; }
    /// <summary>The active SSH session.</summary>
    public SshSession Session { get; }
    /// <summary>Unique identifier for the SSH session.</summary>
    public Guid SessionId { get; }
    /// <summary>Display title for the SFTP document tab.</summary>
    public string Title { get; }
    /// <summary>Local file browser pane.</summary>
    public LocalFilePaneViewModel LocalFiles { get; }
    /// <summary>Remote file browser pane.</summary>
    public FileBrowserViewModel RemoteFiles { get; }
    /// <summary>Command to upload selected local files to the remote server.</summary>
    public ReactiveCommand<Unit, Unit> UploadSelectedCommand { get; }
    /// <summary>Command to download selected remote files to the local machine.</summary>
    public ReactiveCommand<Unit, Unit> DownloadSelectedCommand { get; }
    /// <summary>Command to delete selected local files.</summary>
    public ReactiveCommand<Unit, Unit> DeleteLocalSelectedCommand { get; }
    /// <summary>Callback for confirming local file deletions.</summary>
    public Func<string, Task<bool>>? ConfirmLocalDelete { get; set; }

    internal Task InitialLoadTask => _initialLoad;

    /// <summary>Uploads selected local files to the remote server.</summary>
    public async Task UploadSelectedAsync(CancellationToken cancellationToken = default)
    {
        string[] paths = LocalFiles.SelectedEntries
            .Where(entry => !entry.IsParentEntry)
            .Select(entry => entry.FullPath)
            .ToArray();
        await RemoteFiles.UploadLocalPathsAsync(paths, cancellationToken);
    }

    /// <summary>Downloads selected remote files to the local machine.</summary>
    public async Task DownloadSelectedAsync(CancellationToken cancellationToken = default)
    {
        RemoteFileInfoViewModel[] entries = RemoteFiles.SelectedFiles
            .Where(entry => !entry.IsParentEntry)
            .ToArray();
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(LocalFiles.CurrentPath))
        {
            return;
        }
        await RemoteFiles.DownloadRemoteEntriesAsync(entries, LocalFiles.CurrentPath, cancellationToken);
        if (RemoteFiles.ErrorMessage is null)
        {
            await LocalFiles.RefreshAsync(cancellationToken);
        }
    }

    /// <summary>Detaches file browsers and cancels the lifetime token.</summary>
    public void Detach()
    {
        _lifetime.Cancel();
        RemoteFiles.Detach();
        LocalFiles.Detach();
    }

    /// <summary>Closes the SFTP session and cleans up resources, ensuring it runs only once.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_closeSync)
        {
            _closeTask ??= CloseCoreAsync();
            return cancellationToken.CanBeCanceled
                ? _closeTask.WaitAsync(cancellationToken)
                : _closeTask;
        }
    }

    /// <summary>Asynchronously disposes the SFTP document view model.</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        await LocalFiles.LoadInitialAsync(_lifetime.Token);
        await RemoteFiles.LoadInitialAsync(_lifetime.Token);
    }

    private async Task CloseCoreAsync()
    {
        Detach();
        try
        {
            await _serializedSftp.CloseAsync().ConfigureAwait(false);
            await _initialLoad.ConfigureAwait(false);
        }
        finally
        {
            await _workflow.DisconnectAsync(SessionId).ConfigureAwait(false);
        }
    }
}
