using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 基于 SSH 会话的 SFTP 文件操作服务:按会话缓存并复用 SFTP 客户端,提供目录浏览、
/// 上传/下载、删除、重命名、权限设置等远端文件系统操作。
/// </summary>
public class SftpService(
    ISshConnectionService connectionService,
    Func<SshSession, ISftpClientWrapper>? sftpClientFactory = null,
    ISettingsService? settingsService = null) : ISftpService
{
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();

    /// <summary>属主/属组的数字 id → 名称翻译(按会话缓存,见 RemoteIdentityResolver)。</summary>
    private readonly RemoteIdentityResolver _identities = new(connectionService);

    /// <summary>SFTP 客户端创建的按会话单飞闸(见 GetOrCreateSftpClientAsync)。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _clientGates = new();

    /// <summary>列出指定会话下远端目录的条目(自动剔除 "." 与 ".." )。</summary>
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        // 属主/属组名要查远端 passwd 库(SFTP 只报数字 id):每会话查一次,查不到回退数字。
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return [.. files.Where(f => f.Name is not "." and not "..").Select(f => MapToRemoteFileInfo(f, identities))];
    }

    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持取消。</summary>
    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持断点续传(resumeOffset > 0 时追加上传)。</summary>
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(localPath);
        long totalBytes = fileInfo.Length;
        string fileName = Path.GetFileName(localPath);
        var stopwatch = Stopwatch.StartNew();
        (long uploadBps, _, _) = await GetTransferTuningAsync().ConfigureAwait(false);

        if (resumeOffset > 0)
        {
            // 断点续传:使用新的支持偏移的上传路径。
            Stream fileStream = uploadBps > 0
                                    ? new ThrottledStream(File.OpenRead(localPath), uploadBps)
                                    : File.OpenRead(localPath);
            await using (cancellationToken.Register(() => SafeDispose(fileStream)))
            {
                try
                {
                    await client.UploadAsync(fileStream, remotePath, resumeOffset, bytesTransferred => { ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch); }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                finally
                {
                    await fileStream.DisposeAsync();
                }
            }
        }
        else
        {
            // 全新上传:原始的截断并写入路径。
            Stream fileStream = uploadBps > 0
                                    ? new ThrottledStream(File.OpenRead(localPath), uploadBps)
                                    : File.OpenRead(localPath);
            await using (cancellationToken.Register(() => SafeDispose(fileStream)))
            {
                try
                {
                    await client.UploadAsync(fileStream, remotePath, bytesTransferred => { ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch); }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                finally
                {
                    await fileStream.DisposeAsync();
                }
            }
        }
    }

    /// <summary>将远端文件下载到本地路径,可选限速与进度回报;按设置可保留文件修改时间戳。</summary>
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string fileName = GetUnixFileName(remotePath);
        RemoteFileInfo fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        long totalBytes = fileInfo.Size;
        var stopwatch = Stopwatch.StartNew();
        (_, long downloadBps, bool preserveTimestamps) = await GetTransferTuningAsync().ConfigureAwait(false);

        if (resumeOffset > 0)
        {
            // 断点续传下载:向不完整的本地文件追加。
            await using Stream localStream = new FileStream(localPath, FileMode.Append, FileAccess.Write, FileShare.None);
            // 从偏移处分块拷贝下载远端内容。
            using Stream remoteStream = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            remoteStream.Seek(resumeOffset, SeekOrigin.Begin);

            byte[] buffer = new byte[32 * 1024];
            long bytesRead = 0;
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesRead += read;
                ReportProgress(progress, fileName, resumeOffset + bytesRead, totalBytes, stopwatch);
            }
        }
        else
        {
            Stream fileStream = downloadBps > 0
                                    ? new ThrottledStream(File.Create(localPath), downloadBps)
                                    : File.Create(localPath);

            // 参见 UploadFileAsync:回调运行在脱离的线程池线程上,因此我们通过
            // 释放自己的流(使工作线程的写入失败)来取消,而非从中抛出异常。
            await using (cancellationToken.Register(() => SafeDispose(fileStream)))
            {
                try
                {
                    await client.DownloadAsync(remotePath, fileStream, bytesTransferred => { ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch); }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                finally
                {
                    await fileStream.DisposeAsync();
                }
            }
        }

        // 保留文件时间戳(设置 → 文件传输):下载完成后把远端修改时间写到本地副本。
        if (preserveTimestamps && fileInfo.LastModified != default)
        {
            try
            {
                File.SetLastWriteTime(localPath, fileInfo.LastModified);
            }
            catch
            {
                // 时间戳只是尽力而为。
            }
        }
    }

    /// <summary>删除远端文件或目录;目录按深度优先递归删除子项,并逐条回报删除进度。</summary>
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            throw new FileNotFoundException($"Remote path not found: {remotePath}");
        }
        string parentDir = GetUnixParentDirectory(remotePath);
        string name = GetUnixFileName(remotePath);
        IEnumerable<SftpEntry> siblings = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        SftpEntry? entry = siblings.FirstOrDefault(f => f.Name == name);
        bool isDirectory = entry is { IsDirectory: true };
        int total = await CountEntriesAsync(client, remotePath, isDirectory, cancellationToken).ConfigureAwait(false);

        // 先发出一个 "0 / total" 的进度点,使 UI 能立即切换到确定型进度。
        progress?.Report(new(0, total, remotePath));
        var counter = new DeleteCounter();
        await DeleteEntryAsync(client, remotePath, isDirectory, total, counter, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在远端创建指定路径的目录。</summary>
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.CreateDirectoryAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>在远端创建一个空文件。</summary>
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream();
        await client.UploadAsync(empty, remotePath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>幂等地确保远端目录存在:直接创建,若因已存在而失败则视为成功。</summary>
    public async Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 直接创建而非先 Exists 探测:目录已存在时 Tmds.Ssh 的 CreateDirectory 本身不报错,
        // 上传新文件夹树时逐目录只有一次网络往返。
        // 创建失败且目录确实已存在 → 幂等成功;其余失败(权限/父目录缺失)照常抛出。
        try
        {
            await client.CreateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (VelaSshClientException)
        {
            if (!await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>重命名或移动远端文件/目录;普通 rename 被服务器拒绝时回退到 posix-rename 扩展。</summary>
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.RenameFileAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VelaSftpOperationException or NotSupportedException)
        {
            // 部分 SFTP 服务器以 SSH_FX_BAD_MESSAGE(表现为"bad message")拒绝普通的 SSH_FXP_RENAME,
            // 跨目录移动时常见。改用被广泛支持的 posix-rename@openssh.com 扩展重试;若该路径也不可用,
            // 则抛出原本更具信息量的错误。
            try
            {
                await client.PosixRenameFileAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                throw ex;
            }
        }
    }

    /// <summary>
    /// 将远端文件或目录复制到同一服务器的另一路径。
    /// 单个文件经由临时本地文件复制(不在内存中缓冲大文件)。
    /// 目录则递归遍历并做循环检测。
    /// </summary>
    public async Task CopyAsync(Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);

        // 通过 stat 判断源是否为目录。
        string parentDir = GetUnixParentDirectory(sourcePath);
        string name = GetUnixFileName(sourcePath);
        IEnumerable<SftpEntry> siblings = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        SftpEntry? entry = siblings.FirstOrDefault(f => f.Name == name);
        bool isDir = entry is { IsDirectory: true };

        if (!isDir)
        {
            await CopySingleFileAsync(sessionId, sourcePath, destPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await CopyDirectoryAsync(sessionId, sourcePath, destPath, [with(StringComparer.Ordinal)], 0, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 经由临时本地文件复制单个远端文件,绝不在内存中缓冲整个文件。
    /// 复用 DownloadFileAsync/UploadFileAsync 以进行限速与取消。
    /// </summary>
    private async Task CopySingleFileAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VelaShell", "copy");
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));

        try
        {
            // 远端下载 → 临时文件
            await DownloadFileAsync(sessionId, sourcePath, tempPath, progress, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 临时文件上传 → 远端目标
            await UploadFileAsync(sessionId, tempPath, destPath, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// 递归复制远端目录,通过已访问路径集合做循环检测,
    /// 并设置深度上限以防止符号链接环导致的栈溢出。
    /// </summary>
    private async Task CopyDirectoryAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        HashSet<string> visited,
        int depth,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxDepth = 64;
        if (depth > maxDepth)
        {
            throw new InvalidOperationException($"Copy depth exceeded {maxDepth} levels — possible symlink cycle at {sourcePath}");
        }

        string canonical = sourcePath.TrimEnd('/');
        if (!visited.Add(canonical))
        {
            throw new InvalidOperationException($"Cycle detected copying {sourcePath} — a directory contains a link to itself.");
        }

        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await EnsureDirectoryAsync(sessionId, destPath, cancellationToken).ConfigureAwait(false);

        IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        foreach (SftpEntry child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Name is "." or "..") continue;

            string childSource = CombineUnixPath(sourcePath, child.Name);
            string childDest = CombineUnixPath(destPath, child.Name);

            if (child.IsDirectory)
            {
                await CopyDirectoryAsync(sessionId, childSource, childDest, visited, depth + 1, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await CopySingleFileAsync(sessionId, childSource, childDest, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>以三位八进制模式(000-777)设置远端文件/目录的权限。</summary>
    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        if (octalMode < 0 || octalMode > 777 || octalMode % 10 > 7 || (octalMode / 10) % 10 > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(octalMode), octalMode, @"Mode must be three octal digits (000-777).");
        }
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.ChangePermissionsAsync(remotePath, octalMode, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>获取远端指定路径文件/目录的详细信息;路径不存在时抛出 <see cref="FileNotFoundException" />。</summary>
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string parentDir = GetUnixParentDirectory(remotePath);
        string fileName = GetUnixFileName(remotePath);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        SftpEntry? file = files.FirstOrDefault(f => f.Name == fileName) ?? throw new FileNotFoundException($"File not found: {remotePath}");
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return MapToRemoteFileInfo(file, identities);
    }

    /// <summary>判断远端路径是否存在。</summary>
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回该会话 SFTP 客户端的当前工作目录。</summary>
    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return client.WorkingDirectory;
    }

    /// <summary>关闭并清理指定会话缓存的 SFTP 客户端与单飞闸,断开连接并释放资源。</summary>
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_clientGates.TryRemove(sessionId, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }

        // 在下面的早退之前丢弃:即使本会话从未建过 SFTP 客户端,查表缓存也可能已存在。
        _identities.Invalidate(sessionId);
        if (!_sftpClients.TryRemove(sessionId, out ISftpClientWrapper? client))
        {
            return;
        }
        await Task.Run(() =>
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
                client.Dispose();
            }
            catch
            {
                // 尽力拆解;标签页已经不在了。
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>断开并释放所有缓存的 SFTP 客户端,尽力清理全部会话资源。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (KeyValuePair<Guid, ISftpClientWrapper> kvp in _sftpClients)
        {
            try
            {
                if (kvp.Value.IsConnected)
                {
                    kvp.Value.Disconnect();
                }
                kvp.Value.Dispose();
            }
            catch
            {
                // 释放期间的尽力清理
            }
        }
        _sftpClients.Clear();
        await ValueTask.CompletedTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>带宽限制(设置 → 文件传输):返回字节/秒,0 = 不限速。</summary>
    private async Task<(long UploadBps, long DownloadBps, bool PreserveTimestamps)> GetTransferTuningAsync()
    {
        if (settingsService is null)
        {
            return (0, 0, true);
        }
        try
        {
            TransferOptions t = (await settingsService.GetSettingsAsync().ConfigureAwait(false)).Transfer;
            long up = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.UploadLimitMBps) * 1024 * 1024 : 0;
            long down = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.DownloadLimitMBps) * 1024 * 1024 : 0;
            return (up, down, t.PreserveTimestamps);
        }
        catch
        {
            return (0, 0, true);
        }
    }

    /// <summary>异步递归的已删除计数(async 方法不允许 ref 参数)。</summary>
    private sealed class DeleteCounter
    {
        public int Deleted;
    }

    /// <summary>
    /// 深度优先删除:先移除目录的子项再移除目录自身,因为 SFTP 的 <c>rmdir</c> 仅对空目录成功。
    /// 每移除一个条目回报一次进度。
    /// </summary>
    private static async Task<int> CountEntriesAsync(ISftpClientWrapper client, string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isDirectory)
        {
            return 1;
        }
        int total = 1;
        IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (SftpEntry child in children)
        {
            if (child.Name is "." or "..")
            {
                continue;
            }
            total += await CountEntriesAsync(client, child.FullName, child.IsDirectory, cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task DeleteEntryAsync(ISftpClientWrapper client,
        string path,
        bool isDirectory,
        int total,
        DeleteCounter counter,
        IProgress<SftpDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDirectory)
        {
            IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (SftpEntry child in children)
            {
                if (child.Name is "." or "..")
                {
                    continue;
                }
                await DeleteEntryAsync(client, child.FullName, child.IsDirectory, total, counter, progress, cancellationToken).ConfigureAwait(false);
            }
            await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        counter.Deleted++;
        progress?.Report(new(counter.Deleted, total, path));
    }

    private async Task<ISftpClientWrapper> GetOrCreateSftpClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (TryGetUsableClient(sessionId, out ISftpClientWrapper? existingClient))
        {
            return existingClient;
        }

        // 单飞:面板加载与文件操作可能并发首次触达同一会话,不加闸时会各自握手出
        // 一条 SFTP 连接(SSH 握手 × N),后到者还会把先到者从字典里顶掉造成泄漏。
        SemaphoreSlim gate = _clientGates.GetOrAdd(sessionId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetUsableClient(sessionId, out existingClient))
            {
                return existingClient;
            }
            SshSession? session = _connectionService.GetSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} not found");
            if (session.Status != SessionStatus.Connected)
            {
                throw new InvalidOperationException($"Session {sessionId} is not connected");
            }
            if (sftpClientFactory == null)
            {
                throw new InvalidOperationException("SFTP client factory not configured");
            }
            ISftpClientWrapper client = sftpClientFactory(session);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _sftpClients[sessionId] = client;
            return client;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // CloseSessionAsync 与首次创建赛跑时会把闸释放掉;创建结果本身已无所谓。
            }
        }
    }

    private bool TryGetUsableClient(Guid sessionId, [NotNullWhen(true)] out ISftpClientWrapper? client)
    {
        if (_sftpClients.TryGetValue(sessionId, out client))
        {
            try
            {
                return client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                // 会话关闭把客户端释放了,当作缺失重建。
            }
        }
        client = null;
        return false;
    }

    private static RemoteFileInfo MapToRemoteFileInfo(SftpEntry file, RemoteIdentityMap identities)
    {
        return new()
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            LastModified = file.LastWriteTime,
            Owner = identities.UserName(file.UserId),
            Group = identities.GroupName(file.GroupId)
        };
    }

    private static void ReportProgress(IProgress<TransferProgress>? progress, string fileName, long bytesTransferred, long totalBytes, Stopwatch stopwatch)
    {
        if (progress == null)
        {
            return;
        }
        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        double speed = elapsedSeconds > 0 ? bytesTransferred / elapsedSeconds : 0;
        long remainingBytes = totalBytes - bytesTransferred;
        TimeSpan estimatedTimeRemaining = speed > 0
                                              ? TimeSpan.FromSeconds(remainingBytes / speed)
                                              : TimeSpan.Zero;
        var transferProgress = new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            Percentage = totalBytes > 0 ? (int)((bytesTransferred * 100) / totalBytes) : 0,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = estimatedTimeRemaining
        };
        progress.Report(transferProgress);
    }

    /// <summary>
    /// 取消时释放流以中止进行中的传输。运行于 <see cref="CancellationToken" /> 回调中,
    /// 因此绝不能抛出异常,否则 <see cref="CancellationTokenSource.Cancel()" /> 会把聚合异常
    /// 抛给发起取消的一方。
    /// </summary>
    private static void SafeDispose(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch
        {
            // 尽力而为:传输仍会失败并以已取消的形式上报。
        }
    }

    private static string GetUnixParentDirectory(string remotePath)
    {
        int lastSlash = remotePath.LastIndexOf('/');
        return lastSlash > 0 ? remotePath[..lastSlash] : "/";
    }

    private static string CombineUnixPath(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    private static string GetUnixFileName(string remotePath)
    {
        int lastSlash = remotePath.LastIndexOf('/');
        return lastSlash >= 0 ? remotePath[(lastSlash + 1)..] : remotePath;
    }

    private static string FormatPermissions(SftpEntry file)
    {
        string perms = file.IsDirectory ? "d" : "-";
        perms += file.OwnerCanRead ? "r" : "-";
        perms += file.OwnerCanWrite ? "w" : "-";
        perms += file.OwnerCanExecute ? "x" : "-";
        perms += file.GroupCanRead ? "r" : "-";
        perms += file.GroupCanWrite ? "w" : "-";
        perms += file.GroupCanExecute ? "x" : "-";
        perms += file.OthersCanRead ? "r" : "-";
        perms += file.OthersCanWrite ? "w" : "-";
        perms += file.OthersCanExecute ? "x" : "-";
        return perms;
    }
}
