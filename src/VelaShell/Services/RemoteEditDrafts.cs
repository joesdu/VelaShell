using System.Diagnostics;
using System.Text.Json;

namespace VelaShell.Services;

/// <summary>一个外部编辑会话落在磁盘上的状态标记(<c>remote-edit/&lt;8 位十六进制&gt;/.vela-edit.json</c>)。</summary>
/// <param name="State"><see cref="RemoteEditDrafts.Clean" /> 或 <see cref="RemoteEditDrafts.Dirty" />。</param>
/// <param name="FileName">本地副本的文件名(同目录下)。</param>
/// <param name="RemotePath">远端完整路径。</param>
/// <param name="Server">哪台服务器(界面上的名字);不知道时为 null。</param>
/// <param name="ProfileId">连接配置;不知道(快速连接一类)时为 null。消息中心的「重新上传」按它找连接。</param>
/// <param name="Length">干净时:与远端一致的那份副本的长度。</param>
/// <param name="LastWriteUtc">干净时:那份副本的修改时间。</param>
/// <param name="RemoteLength">
/// 本地副本最近一次与远端一致时(下载落地、干净地重新下载、传上去之后)远端文件的长度;查不到、旧版本的标记为 null。
/// 从消息中心重传草稿时拿它比远端此刻的样子:对不上就是草稿之后被别人改过,要先问。
/// </param>
/// <param name="RemoteModifiedUtc">同上,远端文件那时的修改时间(UTC)。</param>
public sealed record RemoteEditMarker(
    string State,
    string FileName,
    string RemotePath,
    string? Server,
    Guid? ProfileId,
    long Length,
    DateTime LastWriteUtc,
    long? RemoteLength = null,
    DateTime? RemoteModifiedUtc = null);

/// <summary>启动时找到的一份外部编辑草稿:有(或可能有)没传回服务器的改动。</summary>
/// <param name="Directory">草稿所在的子目录。</param>
/// <param name="LocalPath">本地副本;子目录里找不到时是子目录本身。</param>
/// <param name="FileName">文件名(不知道时取子目录里那个文件的名字)。</param>
/// <param name="RemotePath">远端完整路径;没有标记(旧版本留下的)时为 null。</param>
/// <param name="Server">哪台服务器;不知道时为 null。</param>
/// <param name="LastWriteUtc">子目录里最晚的修改时间。</param>
/// <param name="ProfileId">标记里记着的连接配置;没有标记、快速连接一类不知道时为 null(这种草稿没法从消息中心直接重传)。</param>
public sealed record RemoteEditDraft(
    string Directory,
    string LocalPath,
    string FileName,
    string? RemotePath,
    string? Server,
    DateTime LastWriteUtc,
    Guid? ProfileId = null)
{
    /// <summary>草稿子目录的名字(8 位小写十六进制):消息 id 与「重新上传」的参数都用它。</summary>
    public string Key => Path.GetFileName(Directory);

    /// <summary>能不能从消息中心直接重传:标记里记着远端路径与连接配置(本地副本是扫描时找到的那个文件)。</summary>
    public bool CanReupload => RemotePath is { Length: > 0 } && ProfileId is { } profile && profile != Guid.Empty;
}

/// <summary>
/// 外部编辑副本(<see cref="RemoteEditSessionManager.TempRoot" />)的崩溃兜底:每个编辑会话在自己的子目录里落一个状态标记,
/// 启动时按它分清「干净的副本」与「带着没传上去的改动的草稿」。
/// </summary>
/// <remarks>
/// <para>
/// 以前 <c>remote-edit</c> 一个都不能按时间删:回传失败时刻意保留的草稿,与崩溃时正开着的会话(同样可能带着没传上去的改动)
/// 在磁盘上分不开。现在:<b>干净</b>的(内容与远端一致)与 <c>builtin-edit</c> 同一口径,7 天没碰过就在启动后台清掉;
/// <b>有未传改动</b>的、没有标记的(旧版本留下的)、标记读不出来的,一律当草稿 —— 永不自动删,在消息中心列出来让用户处理。
/// (维护者 2026-10-25 定。)
/// </para>
/// <para>
/// 「干净」不只信标记:标记记着那份与远端一致的副本的长度与修改时间,启动时副本对不上(watcher 漏了一次保存、
/// 自动上传关着时改过、标记写完之后又存了一次)就按草稿算。宁可多列一份,不能删掉一份改动。
/// </para>
/// </remarks>
public static class RemoteEditDrafts
{
    /// <summary>标记文件名:与被监视的副本不同名,watcher 只盯副本那一个名字,写标记不会触发回传。</summary>
    public const string MarkerFileName = ".vela-edit.json";

    /// <summary>本地副本与远端一致。</summary>
    public const string Clean = "clean";

    /// <summary>有改动还没落到远端(防抖中、上传中、上传失败)。</summary>
    public const string Dirty = "dirty";

    /// <summary>多久没碰过的干净副本算遗留(与 <see cref="BuiltInEditTempFiles.StaleAfter" /> 同一口径)。</summary>
    public static readonly TimeSpan StaleAfter = BuiltInEditTempFiles.StaleAfter;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>写(覆盖)一个子目录的状态标记;尽力而为,从不抛。</summary>
    /// <param name="directory">编辑会话的子目录。</param>
    /// <param name="marker">标记内容。</param>
    public static void Write(string directory, RemoteEditMarker marker)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, MarkerFileName), JsonSerializer.Serialize(marker, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不了标记:启动时没有标记的按草稿算,最坏是多列一份,不会删掉改动。
            Trace.WriteLine($"[VelaShell] remote-edit marker not written: {ex.Message}");
        }
    }

    /// <summary>读一个子目录的状态标记;没有、读不出、格式不对时为 null。</summary>
    /// <param name="directory">编辑会话的子目录。</param>
    public static RemoteEditMarker? Read(string directory)
    {
        try
        {
            string path = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(path))
            {
                return null;
            }
            RemoteEditMarker? marker = JsonSerializer.Deserialize<RemoteEditMarker>(File.ReadAllText(path), JsonOptions);
            return marker is { State: Clean or Dirty, FileName.Length: > 0 } ? marker : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 删掉 <paramref name="root" /> 下干净且 <see cref="StaleAfter" /> 没碰过的子目录;草稿与正开着的会话一个都不动。尽力而为,从不抛。
    /// </summary>
    /// <param name="root">外部编辑副本的根目录(测试里换成临时目录)。</param>
    /// <param name="utcNow">当前时刻(UTC)。</param>
    /// <param name="activeDirectories">此刻正开着的编辑会话的子目录(不动)。</param>
    /// <returns>删掉的子目录个数。</returns>
    public static int SweepStaleClean(string root, DateTime utcNow, IReadOnlyCollection<string>? activeDirectories = null)
    {
        int removed = EditDirectoryLease.SweepRetired(root);
        foreach ((DirectoryInfo directory, RemoteEditDraft? draft, DateTime newest, IDisposable lease) in Scan(root, activeDirectories))
        {
            if (draft is not null || utcNow - newest < StaleAfter)
            {
                continue;
            }
            try
            {
                EditDirectoryLease.DeleteOwnedDirectory(directory.FullName, lease);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉(文件被占用、没权限)就留给下一次启动。
            }
        }
        return removed;
    }

    /// <summary>列出 <paramref name="root" /> 下的草稿(有或可能有没传回服务器的改动),按最晚修改时间倒序。从不抛。</summary>
    /// <param name="root">外部编辑副本的根目录。</param>
    /// <param name="activeDirectories">此刻正开着的编辑会话的子目录(它们的处境由会话自己报,不在这里列)。</param>
    public static IReadOnlyList<RemoteEditDraft> ListDrafts(string root, IReadOnlyCollection<string>? activeDirectories = null) =>
        [.. Scan(root, activeDirectories).Select(static entry => entry.Draft).OfType<RemoteEditDraft>()
            .OrderByDescending(static draft => draft.LastWriteUtc)];

    /// <summary>
    /// 按子目录名找一份草稿(消息中心的动作带着这个名字):名字不是本程序起的、子目录不在、是重解析点、正开着、
    /// 已经不是草稿了(干净或空壳),都返回 null。从不抛。
    /// </summary>
    /// <param name="root">外部编辑副本的根目录。</param>
    /// <param name="key">草稿子目录的名字(<see cref="RemoteEditDraft.Key" />)。</param>
    /// <param name="activeDirectories">此刻正开着的编辑会话的子目录。</param>
    public static RemoteEditDraft? FindDraft(string root, string key, IReadOnlyCollection<string>? activeDirectories = null)
    {
        if (!IsOwnDirectoryName(key))
        {
            return null;
        }
        try
        {
            if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }
            var directory = new DirectoryInfo(Path.Combine(root, key));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || (activeDirectories ?? []).Any(active => SameDirectory(active, directory.FullName)))
            {
                return null;
            }
            using IDisposable? lease = EditDirectoryLease.TryAcquireExisting(directory.FullName);
            return lease is null ? null : Classify(directory)?.Draft;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>把一个子目录归类:是草稿就给出草稿,干净就给 null;外加最晚修改时间。</summary>
    private static IEnumerable<(DirectoryInfo Directory, RemoteEditDraft? Draft, DateTime Newest, IDisposable Lease)> Scan(
        string root, IReadOnlyCollection<string>? activeDirectories)
    {
        DirectoryInfo[] directories;
        try
        {
            if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            {
                yield break;
            }
            directories = new DirectoryInfo(root).GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        var active = new HashSet<string>(
            (activeDirectories ?? []).Select(NormalizeDirectory), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DirectoryInfo directory in directories)
        {
            if (!IsOwnDirectoryName(directory.Name)
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || active.Contains(NormalizeDirectory(directory.FullName)))
            {
                continue;
            }
            using IDisposable? lease = EditDirectoryLease.TryAcquireExisting(directory.FullName);
            if (lease is null)
            {
                continue;
            }
            (RemoteEditDraft? Draft, DateTime Newest)? classified = Classify(directory);
            if (classified is { } entry)
            {
                yield return (directory, entry.Draft, entry.Newest, lease);
            }
        }
    }

    private static (RemoteEditDraft? Draft, DateTime Newest)? Classify(DirectoryInfo directory)
    {
        try
        {
            DateTime newest = directory.LastWriteTimeUtc;
            List<FileInfo> copies = [];
            foreach (FileSystemInfo entry in EditDirectoryLease.EnumerateEntries(directory))
            {
                if (entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }
                if (entry is FileInfo file && !string.Equals(file.Name, MarkerFileName, StringComparison.Ordinal))
                {
                    copies.Add(file);
                }
            }
            RemoteEditMarker? marker = Read(directory.FullName);
            FileInfo? copy = marker is null
                ? copies.FirstOrDefault()
                : copies.FirstOrDefault(file => string.Equals(file.Name, marker.FileName, StringComparison.Ordinal));
            if (copies.Count == 0)
            {
                // 一个文件都没有:没有可丢的东西,按干净的算(到点清掉空壳)。
                return (null, newest);
            }
            bool clean = marker is { State: Clean }
                         && copies.Count == 1
                         && copy is not null
                         && copy.Length == marker.Length
                         && copy.LastWriteTimeUtc == marker.LastWriteUtc;
            if (clean)
            {
                return (null, newest);
            }
            return (new RemoteEditDraft(
                directory.FullName,
                copy?.FullName ?? copies[0].FullName,
                marker?.FileName ?? copy?.Name ?? copies[0].Name,
                marker?.RemotePath,
                marker?.Server,
                newest,
                // 标记说的那个文件不在了(只剩别的文件):说不准这一份对应哪个远端文件,不给重传
                copy is null ? null : marker?.ProfileId), newest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了就既不删也不报:下次再看。
            return null;
        }
    }

    /// <summary>
    /// 在系统的文件管理器里打开草稿所在的文件夹:只有一份草稿时直接开它的子目录,否则开根目录(消息正文里写着每一份的完整路径)。
    /// </summary>
    /// <param name="root">外部编辑副本的根目录。</param>
    /// <returns>是否打开了。</returns>
    public static bool OpenDraftsFolder(string root)
    {
        IReadOnlyList<RemoteEditDraft> drafts = ListDrafts(root);
        return OpenFolder(drafts.Count == 1 ? drafts[0].Directory : root);
    }

    /// <summary>
    /// 打开一份草稿的子目录(消息中心里那一条「打开所在文件夹」带着子目录名);那份草稿已经不在时退回根目录(根目录也不在就不开)。
    /// </summary>
    /// <param name="root">外部编辑副本的根目录。</param>
    /// <param name="key">草稿子目录的名字。</param>
    /// <returns>是否打开了。</returns>
    public static bool OpenDraftFolder(string root, string key) =>
        OpenFolder(IsOwnDirectoryName(key) && Directory.Exists(Path.Combine(root, key)) ? Path.Combine(root, key) : root);

    private static bool OpenFolder(string target)
    {
        if (!Directory.Exists(target))
        {
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Opening the remote-edit drafts folder failed: {ex.Message}");
            return false;
        }
    }

    private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>本程序起的子目录名:<c>Guid.ToString("N")</c> 的前 8 位(与 <see cref="BuiltInEditTempFiles" /> 同一规则)。</summary>
    internal static bool IsOwnDirectoryName(string name) =>
        name.Length == 8 && name.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
