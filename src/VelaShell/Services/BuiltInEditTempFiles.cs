namespace VelaShell.Services;

/// <summary>
/// 内置编辑器(SFTP「打开」)的本地临时副本:<c>%TEMP%/VelaShell/builtin-edit/&lt;8 位十六进制&gt;/文件名</c>。
/// </summary>
/// <remarks>
/// <para>
/// 每次打开独占一个子目录,编辑器窗口关闭时(<c>RemoteFileEditorView.OnClosed</c>)整个删掉。进程崩溃、被结束时
/// 这一步没机会跑,子目录就一直留在临时目录里 —— 原先没有任何兜底。所以启动后在后台清一遍:
/// <see cref="StaleAfter" /> 之内碰过的不动(可能是另一个实例正开着的编辑器,也给崩溃后想找回改动的人留出余地),
/// 更早的连目录删掉。
/// </para>
/// <para>
/// <b>只清 <c>builtin-edit</c>,不碰旁边的 <c>remote-edit</c>。</b>后者是外部编辑器 / 系统默认程序那条路
/// (<see cref="RemoteEditSessionManager" />)的副本,回传失败或没传完时会<b>刻意保留</b>子目录并把路径告诉用户
/// (「改动保留在 …」)—— 那是这份改动唯一的存身之处。它按每个会话落下的状态标记另外清(<see cref="RemoteEditDrafts" />):
/// 干净的同样 7 天后清,有未传改动的永不自动删、在消息中心列出来。内置编辑器没有这种草稿:回传失败时改动还在窗口里,
/// 关窗要先确认丢弃。
/// </para>
/// </remarks>
public static class BuiltInEditTempFiles
{
    /// <summary>多久没碰过的副本算遗留。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>宿主所有临时副本的公共父目录:%TEMP%/VelaShell。</summary>
    public static string TempBase { get; } = Path.Combine(Path.GetTempPath(), "VelaShell");

    /// <summary>内置编辑器副本的根目录:%TEMP%/VelaShell/builtin-edit。</summary>
    public static string Root { get; } = RootUnder(TempBase);

    private static readonly Dictionary<string, IDisposable> Opening = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly Lock OwnershipGate = new();

    /// <summary>给一次「打开」分一个新的独占子目录(只拼路径,不创建)。</summary>
    public static string NewDirectory() => Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);

    /// <summary>下载与显示窗口期间持有目录;窗口实际打开时才接管,失败则立即清理。</summary>
    internal static async Task OpenOwnedAsync(string directory, Func<Task> openAsync)
    {
        Directory.CreateDirectory(directory);
        IDisposable? lease = null;
        bool registered = false;
        try
        {
            lease = EditDirectoryLease.Acquire(directory);
            lock (OwnershipGate)
            {
                Opening.Add(directory, lease);
                registered = true;
            }
            await openAsync();
        }
        finally
        {
            bool unclaimed;
            lock (OwnershipGate)
            {
                unclaimed = !registered || Opening.Remove(directory);
            }
            if (unclaimed)
            {
                RemoteEditSessionManager.TryDeleteDirectory(directory);
                lease?.Dispose();
            }
        }
    }

    internal static IDisposable? TakeOwnership(string localPath)
    {
        string? directory = Path.GetDirectoryName(localPath);
        lock (OwnershipGate)
        {
            return directory is not null && Opening.Remove(directory, out IDisposable? lease) ? lease : null;
        }
    }

    /// <summary>
    /// 删掉 <paramref name="tempBase" /> 下 <c>builtin-edit</c> 里超过 <see cref="StaleAfter" /> 没碰过的子目录;尽力而为,从不抛。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>「碰过」取子目录与它里面所有条目的最晚修改时间:保存会写文件,而保存不一定改到目录的时间。</item>
    ///   <item>只认 8 位小写十六进制的子目录名(本程序起的名字),链接与别的东西一律不动。</item>
    ///   <item>删不掉(文件被占用、没权限)就留给下一次启动。</item>
    /// </list>
    /// </remarks>
    /// <param name="tempBase">宿主临时目录的公共父目录(测试里换成临时目录)。</param>
    /// <param name="utcNow">当前时刻(UTC)。</param>
    /// <returns>删掉的子目录个数。</returns>
    public static int SweepStale(string tempBase, DateTime utcNow)
    {
        string root = RootUnder(tempBase);
        int removed = 0;
        try
        {
            if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            {
                return 0;
            }
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (TryRemoveIfStale(directory, utcNow))
                {
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 根目录本身读不了:下次再说。
        }
        return removed;
    }

    private static string RootUnder(string tempBase) => Path.Combine(tempBase, "builtin-edit");

    private static bool TryRemoveIfStale(string directory, DateTime utcNow)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!IsOwnDirectoryName(info.Name) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
            using IDisposable? lease = EditDirectoryLease.TryAcquireExisting(directory);
            if (lease is null)
            {
                return false;
            }
            DateTime newest = info.LastWriteTimeUtc;
            foreach (FileSystemInfo entry in EditDirectoryLease.EnumerateEntries(info))
            {
                if (entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }
            }
            if (utcNow - newest < StaleAfter)
            {
                return false;
            }
            info.Delete(recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>本程序起的子目录名:<c>Guid.ToString("N")</c> 的前 8 位。</summary>
    private static bool IsOwnDirectoryName(string name) =>
        name.Length == 8 && name.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>共享系统临时目录的跨进程租约;崩溃会自动释放句柄,租约文件留给下次启动判断。</summary>
internal static class EditDirectoryLease
{
    internal const string FileName = ".vela-edit.lease";

    internal static IDisposable Acquire(string directory) =>
        new FileStream(Path.Combine(directory, FileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Delete);

    internal static IEnumerable<FileSystemInfo> EnumerateEntries(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Linked editor artifacts must be preserved.");
            }
            if (entry.Name == FileName)
            {
                continue;
            }
            yield return entry;
            if (entry is DirectoryInfo child)
            {
                foreach (FileSystemInfo nested in EnumerateEntries(child))
                {
                    yield return nested;
                }
            }
        }
    }

    internal static IDisposable? TryAcquireExisting(string directory)
    {
        try
        {
            string path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                {
                    return null;
                }
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            }
            // Migrate legacy directories without making old copies appear newly modified.
            DateTime lastWrite = Directory.GetLastWriteTimeUtc(directory);
            var lease = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
            try
            {
                Directory.SetLastWriteTimeUtc(directory, lastWrite);
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
