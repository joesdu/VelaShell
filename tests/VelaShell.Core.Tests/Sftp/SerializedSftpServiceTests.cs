using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SerializedSftpServiceTests
{
    [TestMethod]
    public async Task OperationsAndCloseAsync_WhenConcurrent_SerializesWorkAndRejectsQueuedRequests()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);

        // When
        Task<List<RemoteFileInfo>> list = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();
        Task upload = service.UploadFileAsync(inner.SessionId, "local", "/upload");
        Task delete = service.DeleteAsync(inner.SessionId, "/delete");
        Task close = service.CloseAsync();
        Task<RemoteFileInfo> afterClose = service.GetFileInfoAsync(inner.SessionId, "/after-close");
        inner.ReleaseFirstOperation();

        // Then
        await list;
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => upload);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => delete);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => afterClose);
        await close;
        Assert.AreEqual(1, inner.MaximumConcurrency);
        Assert.AreEqual(1, inner.CloseCalls);
        CollectionAssert.AreEqual(new List<string> { "ListDirectory", "CloseSession" }, inner.OperationNames);
    }

    [TestMethod]
    public async Task CloseAsync_WhenCalledRepeatedly_IsHarmlessAndWaitsForActiveOperation()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);
        Task<List<RemoteFileInfo>> active = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();

        // When
        Task firstClose = service.CloseAsync();
        Task secondClose = service.CloseAsync();

        // Then
        Assert.IsFalse(firstClose.IsCompleted, "because close must wait for the gate holder");
        Assert.IsFalse(secondClose.IsCompleted, "because all close callers await the same lifecycle");
        Assert.AreEqual(0, inner.CloseCalls);
        inner.ReleaseFirstOperation();
        await active;
        await Task.WhenAll(firstClose, secondClose);
        Assert.AreEqual(1, inner.CloseCalls);
    }

    [TestMethod]
    public async Task CloseAsync_WhenCallerCancelsWait_CancelsOnlyCallerAndCloseCompletesWithNonCancellableToken()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);

        // Start an active operation that holds the gate.
        Task<List<RemoteFileInfo>> active = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();

        // Call CloseAsync with a cancellable token.
        using var cancellation = new CancellationTokenSource();
        Task close = service.CloseAsync(cancellation.Token);

        // Cancel the caller's wait — only this caller gets OCE.
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => close);

        // Release the active operation, allowing the shared close to drain.
        inner.ReleaseFirstOperation();
        await active;

        // Inner close completes exactly once with a non-cancellable token.
        await service.CloseAsync();
        Assert.AreEqual(1, inner.CloseCalls);
        Assert.AreEqual(CancellationToken.None, inner.CancellationTokens[^1]);

        // Later CloseAsync on the same cached cleanup succeeds without re-closing.
        await service.CloseAsync();
        Assert.AreEqual(1, inner.CloseCalls);
    }

    [TestMethod]
    public async Task DisposeAsync_WhenCalledRepeatedly_ClosesTheBoundSessionOnce()
    {
        // Given
        var inner = new BlockingSftpService();
        var service = new SerializedSftpService(inner, inner.SessionId);

        // When
        await service.DisposeAsync();
        await service.DisposeAsync();

        // Then
        Assert.AreEqual(1, inner.CloseCalls);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => service.ListDirectoryAsync(inner.SessionId, "/"));
    }

    [TestMethod]
    public async Task Operations_WhenSessionIdDoesNotMatch_RejectWithoutCallingInnerService()
    {
        // Given
        var inner = new BlockingSftpService();
        var service = new SerializedSftpService(inner, inner.SessionId);

        // When
        Task operation = service.CreateDirectoryAsync(Guid.NewGuid(), "/wrong-session");

        // Then
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => operation);
        Assert.IsEmpty(inner.OperationNames);
    }

    [TestMethod]
    public async Task QueuedOperation_WhenCancelled_DoesNotPoisonLaterDocumentWork()
    {
        // Given
        var inner = new BlockingSftpService(blockFirstOperation: true);
        var service = new SerializedSftpService(inner, inner.SessionId);
        Task<List<RemoteFileInfo>> active = service.ListDirectoryAsync(inner.SessionId, "/");
        await inner.WaitForFirstOperationAsync();
        using var cancellation = new CancellationTokenSource();
        Task<bool> queued = service.ExistsAsync(inner.SessionId, "/cancelled", cancellation.Token);

        // When
        cancellation.Cancel();
        inner.ReleaseFirstOperation();

        // Then
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queued);
        await active;
        Assert.IsTrue(await service.ExistsAsync(inner.SessionId, "/resumed"));
        Assert.AreEqual(1, inner.MaximumConcurrency);
        CollectionAssert.AreEqual(new List<string> { "ListDirectory", "Exists" }, inner.OperationNames);
    }

    [TestMethod]
    public async Task AllOperations_WhenOpen_DelegateBoundSessionCancellationAndProgress()
    {
        // Given
        var inner = new BlockingSftpService();
        var service = new SerializedSftpService(inner, inner.SessionId);
        using var cancellation = new CancellationTokenSource();
        var transferProgress = new List<TransferProgress>();
        var deleteProgress = new List<SftpDeleteProgress>();

        // When
        await service.ListDirectoryAsync(inner.SessionId, "/", cancellation.Token);
        await service.UploadFileAsync(inner.SessionId, "local", "/upload", new InlineProgress<TransferProgress>(transferProgress.Add), cancellation.Token);
        await service.DownloadFileAsync(inner.SessionId, "/download", "local", new InlineProgress<TransferProgress>(transferProgress.Add), cancellation.Token);
        await service.DeleteAsync(inner.SessionId, "/delete", new InlineProgress<SftpDeleteProgress>(deleteProgress.Add), cancellation.Token);
        await service.CreateDirectoryAsync(inner.SessionId, "/directory", cancellation.Token);
        await service.CreateFileAsync(inner.SessionId, "/file", cancellation.Token);
        await service.EnsureDirectoryAsync(inner.SessionId, "/ensure", cancellation.Token);
        await service.RenameAsync(inner.SessionId, "/old", "/new", cancellation.Token);
        await service.SetPermissionsAsync(inner.SessionId, "/permissions", 755, cancellation.Token);
        await service.GetFileInfoAsync(inner.SessionId, "/info", cancellation.Token);
        await service.ExistsAsync(inner.SessionId, "/exists", cancellation.Token);
        await service.GetWorkingDirectoryAsync(inner.SessionId, cancellation.Token);
        await service.CloseSessionAsync(inner.SessionId, cancellation.Token);

        // Then
        CollectionAssert.AreEqual(
            new List<string> { "ListDirectory", "Upload", "Download", "Delete", "CreateDirectory", "CreateFile", "EnsureDirectory", "Rename", "SetPermissions", "GetFileInfo", "Exists", "GetWorkingDirectory", "CloseSession" },
            inner.OperationNames);
        Assert.IsTrue(inner.CancellationTokens[..^1].All(token => token == cancellation.Token));
        Assert.AreEqual(CancellationToken.None, inner.CancellationTokens[^1]);
        Assert.HasCount(2, transferProgress);
        Assert.HasCount(1, deleteProgress);
        Assert.AreEqual(1, inner.CloseCalls);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class BlockingSftpService : ISftpService
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _firstOperationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeOperations;
        private int _blockFirstOperation;

        public BlockingSftpService(bool blockFirstOperation = false)
        {
            _blockFirstOperation = blockFirstOperation ? 1 : 0;
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public int CloseCalls { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public List<string> OperationNames { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task WaitForFirstOperationAsync() => _firstOperationStarted.Task;

        public void ReleaseFirstOperation() => _releaseFirstOperation.TrySetResult();

        public Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) => InvokeAsync("ListDirectory", sessionId, new List<RemoteFileInfo>(), cancellationToken);

        public Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, long resumeOffset = 0)
        {
            progress?.Report(CreateTransferProgress(localPath));
            return InvokeAsync("Upload", sessionId, cancellationToken);
        }

        public Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, long resumeOffset = 0)
        {
            progress?.Report(CreateTransferProgress(remotePath));
            return InvokeAsync("Download", sessionId, cancellationToken);
        }

        public Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(CreateTransferProgress(sourcePath));
            return InvokeAsync("Copy", sessionId, cancellationToken);
        }

        public Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(new(1, 1, remotePath));
            return InvokeAsync("Delete", sessionId, cancellationToken);
        }

        public Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("CreateDirectory", sessionId, cancellationToken);
        public Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("CreateFile", sessionId, cancellationToken);
        public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("EnsureDirectory", sessionId, cancellationToken);
        public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => InvokeAsync("Rename", sessionId, cancellationToken);
        public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default) => InvokeAsync("SetPermissions", sessionId, cancellationToken);
        public Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("GetFileInfo", sessionId, CreateRemoteFileInfo(remotePath), cancellationToken);
        public Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => InvokeAsync("Exists", sessionId, true, cancellationToken);
        public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default) => InvokeAsync("GetWorkingDirectory", sessionId, "/", cancellationToken);

        public Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return InvokeAsync("CloseSession", sessionId, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task InvokeAsync(string operationName, Guid sessionId, CancellationToken cancellationToken)
        {
            _ = await InvokeAsync(operationName, sessionId, true, cancellationToken);
        }

        private async Task<T> InvokeAsync<T>(string operationName, Guid sessionId, T result, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                OperationNames.Add(operationName);
                CancellationTokens.Add(cancellationToken);
                int active = ++_activeOperations;
                MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            }

            _firstOperationStarted.TrySetResult();
            try
            {
                if (Interlocked.Exchange(ref _blockFirstOperation, 0) == 1)
                {
                    await _releaseFirstOperation.Task.WaitAsync(cancellationToken);
                }

                return result;
            }
            finally
            {
                lock (_sync)
                {
                    _activeOperations--;
                }
            }
        }

        private static TransferProgress CreateTransferProgress(string fileName) => new()
        {
            FileName = fileName,
            BytesTransferred = 1,
            TotalBytes = 1,
            Percentage = 100,
            SpeedBytesPerSecond = 1,
            EstimatedTimeRemaining = TimeSpan.Zero
        };

        private static RemoteFileInfo CreateRemoteFileInfo(string remotePath) => new()
        {
            Name = Path.GetFileName(remotePath),
            FullPath = remotePath,
            Size = 0,
            Permissions = "-rw-r--r--",
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Owner = "owner",
            Group = "group"
        };
    }
}
