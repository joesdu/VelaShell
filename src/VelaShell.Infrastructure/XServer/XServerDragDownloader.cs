using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 从经 SSH 转发来的 X 程序里往本机拖出文件(F16 的另一半):文件在远端,本机的资源管理器 / 访达要的是本机的文件 ——
/// 先经那个 SSH 会话的 SFTP 取回本机的一个临时目录,再把本机路径交给系统的拖放。与 <see cref="IXServerDropUploader" /> 方向相反。
/// </summary>
public interface IXServerDragDownloader
{
    /// <summary>这个连接名(X 程序的连接名,形如 <c>user@host:22</c>)对应的 SSH 会话还连着吗 —— 连着才取得回来。</summary>
    /// <param name="clientLabel">连接名。</param>
    bool CanDownload(string clientLabel);

    /// <summary>
    /// 把远端的文件与目录(目录连同里面的文件)取回本机一个新建的临时目录,返回本机路径(与 <paramref name="remotePaths" /> 同序;
    /// 远端已经不存在的跳过)。会话不在时为 <see langword="null" />。传输失败抛出底层的异常。
    /// </summary>
    /// <param name="clientLabel">连接名(见 <see cref="CanDownload" />)。</param>
    /// <param name="remotePaths">远端的 POSIX 路径。</param>
    /// <param name="cancellationToken">取消令牌(用户在取回期间松手、指针回到了 X 窗口里)。</param>
    Task<IReadOnlyList<string>?> DownloadAsync(string clientLabel, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default);
}

/// <summary>一次本机拖放的下载所有权;下载与系统拖放都结束后释放,只有成功放下的文件保留。</summary>
public sealed class XServerDragDownloadScope(IXServerDragDownloader? downloader) : IXServerDragDownloader, IDisposable
{
    private readonly List<SftpXServerDragDownloader.DownloadBatch> _batches = [];

    /// <summary>接收程序是否接受了本机拖放。</summary>
    public bool HandedOff { get; set; }

    /// <inheritdoc />
    public bool CanDownload(string clientLabel) => downloader?.CanDownload(clientLabel) == true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> DownloadAsync(string clientLabel, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default)
    {
        if (downloader is null)
        {
            return null;
        }
        IReadOnlyList<string>? paths = await downloader.DownloadAsync(clientLabel, remotePaths, cancellationToken).ConfigureAwait(false);
        if (paths is SftpXServerDragDownloader.DownloadBatch batch)
        {
            _batches.Add(batch);
        }
        return paths;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (SftpXServerDragDownloader.DownloadBatch batch in _batches)
        {
            batch.Complete(HandedOff);
        }
        _batches.Clear();
    }
}

/// <summary>
/// <see cref="IXServerDragDownloader" /> 的实现:按连接名找连着的 SSH 会话(与上传同一个写法,见 <see cref="SftpXServerDropUploader.LabelOf" />),
/// 经 <see cref="ISftpService" /> 取到 <c>临时目录/velashell-xdrag/随机串/</c>。文件留在那里给放下的程序拷走(资源管理器拷大文件是在拖放结束后
/// 慢慢拷的,不能马上删);交接后保留一天,启动与下次取之前清理,仍由下载或拖放持有的目录不删。
/// </summary>
public sealed class SftpXServerDragDownloader(ISshConnectionService connections, ISftpService sftp, string? root = null) : IXServerDragDownloader
{
    private readonly ISshConnectionService _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly ISftpService _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));

    /// <summary>取回的文件放在这个目录下面(每次一个子目录)。</summary>
    public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "velashell-xdrag");

    /// <summary>成功交接后给接收程序留出的读取时间。</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(1);
    internal const string LeaseFileName = ".velashell-drag-lease";

    private SshSession? SessionFor(string clientLabel) =>
        _connections.Sessions.FirstOrDefault(s => s.Status == SessionStatus.Connected && SftpXServerDropUploader.LabelOf(s.ConnectionInfo) == clientLabel);

    /// <inheritdoc />
    public bool CanDownload(string clientLabel) => SessionFor(clientLabel) is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> DownloadAsync(string clientLabel, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePaths);
        if (SessionFor(clientLabel) is not { } session)
        {
            return null;
        }
        Guid id = session.SessionId;
        CleanupStaleDownloads(Root);
        if (Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Drag download root must not be a link.");
        }
        string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DownloadBatch? batch = null;
        try
        {
            batch = new(directory);
            HashSet<string> names = [with(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal), LeaseFileName];
            foreach (string remote in remotePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await _sftp.ExistsAsync(id, remote, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                RemoteFileInfo info = await _sftp.GetFileInfoAsync(id, remote, cancellationToken).ConfigureAwait(false);
                string name = UniqueName(names, SafeName(info.Name.Length > 0 ? info.Name : remote.TrimEnd('/').Split('/')[^1]));
                string target = Path.Combine(directory, name);
                if (info.IsDirectory)
                {
                    await DownloadDirectoryAsync(id, remote, target, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _sftp.DownloadFileAsync(id, remote, target, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                batch.Add(target);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Count == 0)
            {
                batch.Complete(handedOff: false);
            }
            return batch;
        }
        catch
        {
            batch?.Complete(handedOff: false);
            TryDeleteBatch(directory);
            throw;
        }
    }

    /// <summary>目录连同里面的文件与子目录一起取回。指向目录的符号链接不跟(可能绕回自己);指向文件的照常取(取的是它指的内容)。</summary>
    private async Task DownloadDirectoryAsync(Guid id, string remote, string local, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(local);
        HashSet<string> names = [with(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
        foreach (RemoteFileInfo entry in await _sftp.ListDirectoryAsync(id, remote, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or ".." || (entry.IsDirectory && entry.IsSymbolicLink))
            {
                continue;
            }
            string target = Path.Combine(local, UniqueName(names, SafeName(entry.Name)));
            string child = remote.TrimEnd('/') + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                await DownloadDirectoryAsync(id, child, target, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sftp.DownloadFileAsync(id, child, target, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>启动时与每次下载前清理过期批次;仍在下载或拖放中的批次由独占租约保护。清理失败留待下次。</summary>
    public static void CleanupStaleDownloads(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "velashell-xdrag");
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
            DateTime cutoff = DateTime.UtcNow - Retention;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    string name = Path.GetFileName(directory);
                    if (!Guid.TryParseExact(name, "N", out Guid batchId) || name != batchId.ToString("N")
                        || !IsPlainTree(directory))
                    {
                        continue;
                    }
                    string leasePath = Path.Combine(directory, LeaseFileName);
                    if (File.Exists(leasePath))
                    {
                        using FileStream lease = new(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                        if (File.GetLastWriteTimeUtc(lease.SafeFileHandle) < cutoff)
                        {
                            TryDeleteBatch(directory);
                        }
                        continue;
                    }
                    else
                    {
                        // 老版本没有租约:至少检查整个目录树,不能把仍在写子文件的旧目录误当陈旧目录。
                        if (Directory.GetLastWriteTimeUtc(directory) >= cutoff
                            || Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                            {
                                RecurseSubdirectories = true,
                                AttributesToSkip = FileAttributes.ReparsePoint,
                                IgnoreInaccessible = false
                            }).Any(path => File.GetLastWriteTimeUtc(path) >= cutoff))
                        {
                            continue;
                        }
                    }
                    TryDeleteBatch(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 租约被另一个实例持有,或接收程序还占着文件。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 启动清理不影响应用启动。
        }
    }

    private static bool IsPlainTree(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || ((attributes & FileAttributes.Directory) != 0 && !IsPlainTree(entry)))
            {
                return false;
            }
        }
        return true;
    }

    private static void TryDeleteBatch(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || !IsPlainTree(directory))
            {
                return;
            }
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class DownloadBatch(string directory) : System.Collections.ObjectModel.Collection<string>, IReadOnlyList<string>
    {
        private FileStream? _lease = new(Path.Combine(directory, LeaseFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        internal void Complete(bool handedOff)
        {
            FileStream? lease = Interlocked.Exchange(ref _lease, null);
            if (lease is null)
            {
                return;
            }
            try
            {
                // 年龄从交接而不是开始下载算起;慢下载也有完整的一天留给接收程序。
                File.SetLastWriteTimeUtc(lease.SafeFileHandle, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                lease.Dispose();
            }
            if (!handedOff)
            {
                TryDeleteBatch(directory);
            }
        }
    }

    /// <summary>
    /// 远端文件名在本机不一定合法(Windows 不许 <c>: * ? " &lt; &gt; |</c>、结尾的点与空格、CON / NUL 这类保留名):不合法的字符换成 <c>_</c>。
    /// </summary>
    internal static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = [.. name.Select(ch => Array.IndexOf(invalid, ch) >= 0 || (OperatingSystem.IsWindows() && ch is ':' or '*' or '?' or '"' or '<' or '>' or '|') ? '_' : ch)];
        string safe = new string(chars).TrimEnd('.', ' ');
        if (safe.Length == 0)
        {
            return "dragged";
        }
        string stem = Path.GetFileNameWithoutExtension(safe).ToUpperInvariant();
        return OperatingSystem.IsWindows() && stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6"
            or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9"
            ? "_" + safe
            : safe;
    }

    /// <summary>同一个目录里重名的(大小写不分的系统上 a.txt 与 A.TXT 也算)加序号:<c>a.txt</c>、<c>a (2).txt</c>……</summary>
    private static string UniqueName(HashSet<string> names, string name)
    {
        string candidate = name;
        for (int i = 2; !names.Add(candidate); i++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(name)}";
        }
        return candidate;
    }
}
