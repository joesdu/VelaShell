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

    public SessionProfile Profile { get; }
    public SshSession Session { get; }
    public Guid SessionId { get; }
    public string Title { get; }
    public LocalFilePaneViewModel LocalFiles { get; }
    public FileBrowserViewModel RemoteFiles { get; }
    public ReactiveCommand<Unit, Unit> UploadSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteLocalSelectedCommand { get; }
    public Func<string, Task<bool>>? ConfirmLocalDelete { get; set; }

    internal Task InitialLoadTask => _initialLoad;

    public async Task UploadSelectedAsync(CancellationToken cancellationToken = default)
    {
        string[] paths = LocalFiles.SelectedEntries
            .Where(entry => !entry.IsParentEntry)
            .Select(entry => entry.FullPath)
            .ToArray();
        await RemoteFiles.UploadLocalPathsAsync(paths, cancellationToken);
    }

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

    public void Detach()
    {
        _lifetime.Cancel();
        RemoteFiles.Detach();
        LocalFiles.Detach();
    }

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
