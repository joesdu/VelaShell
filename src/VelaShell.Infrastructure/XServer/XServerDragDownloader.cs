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

/// <summary>
/// <see cref="IXServerDragDownloader" /> 的实现:按连接名找连着的 SSH 会话(与上传同一个写法,见 <see cref="SftpXServerDropUploader.LabelOf" />),
/// 经 <see cref="ISftpService" /> 取到 <c>临时目录/velashell-xdrag/随机串/</c>。文件留在那里给放下的程序拷走(资源管理器拷大文件是在拖放结束后
/// 慢慢拷的,不能马上删);每次取之前清掉一天以前留下的。
/// </summary>
public sealed class SftpXServerDragDownloader(ISshConnectionService connections, ISftpService sftp, string? root = null) : IXServerDragDownloader
{
    private readonly ISshConnectionService _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly ISftpService _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));

    /// <summary>取回的文件放在这个目录下面(每次一个子目录)。</summary>
    public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "velashell-xdrag");

    /// <summary>留下的子目录过了这么久就在下一次取之前删掉。</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(1);

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
        PruneOld();
        string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        List<string> local = [];
        HashSet<string> names = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string remote in remotePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _sftp.ExistsAsync(id, remote, cancellationToken).ConfigureAwait(false))
            {
                continue;   // 拖着的时候还在,放下时已经没了
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
            local.Add(target);
        }
        return local;
    }

    /// <summary>目录连同里面的文件与子目录一起取回。指向目录的符号链接不跟(可能绕回自己);指向文件的照常取(取的是它指的内容)。</summary>
    private async Task DownloadDirectoryAsync(Guid id, string remote, string local, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(local);
        HashSet<string> names = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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

    /// <summary>删掉一天以前留下的子目录(删不掉的 —— 程序还开着里面的文件 —— 留到下次)。</summary>
    private void PruneOld()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }
        DateTime cutoff = DateTime.UtcNow - Retention;
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 还被占着:下次再删
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
