using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Services.Update;

/// <summary>
/// 便携式原地升级的文件操作核心:把更新包解压为 <c>*.new</c> 暂存文件,再以
/// "现文件→<c>*.old</c>、<c>*.new</c>→现文件" 的两段重命名完成换版(Windows 允许对
/// 正在运行的 exe/dll 改名,只是不许删除/覆盖,Unix 更宽松,三平台同一套逻辑)。
/// 全程只触碰更新包内列出的文件,应用目录里用户自己的文件绝不改名或删除;
/// 应用数据目录(%LocalAppData%/VelaShell)与本流程无关,永不触碰。
/// 换版进度记录在暂存目录的日志文件里,中途崩溃由下次启动的
/// <see cref="TryFinalizeStartup" /> 依据日志回滚;换版成功后同样由它清理 <c>*.old</c>。
/// </summary>
public sealed class UpdateApplier(string applicationDirectory)
{
    /// <summary>暂存目录名(位于应用目录下,保证与目标文件同卷,重命名是纯元数据操作)。</summary>
    public const string StagingDirectoryName = ".velashell-update";

    private const string JournalFileName = "apply.json";

    /// <summary>应用程序所在目录(更新的目标目录)。</summary>
    public string ApplicationDirectory { get; } =
        Path.GetFullPath(applicationDirectory ?? throw new ArgumentNullException(nameof(applicationDirectory)));

    /// <summary>下载与换版日志的暂存目录。</summary>
    public string StagingDirectory => Path.Combine(ApplicationDirectory, StagingDirectoryName);

    private string JournalPath => Path.Combine(StagingDirectory, JournalFileName);

    /// <summary>应用目录是否可写(装进 Program Files 之类的位置时为 false,只能手动更新)。</summary>
    public bool IsApplicationDirectoryWritable()
    {
        try
        {
            string probe = Path.Combine(ApplicationDirectory, $".velashell-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建(或清空后重建)暂存目录,返回其路径。下载前调用。</summary>
    public string PrepareStagingDirectory()
    {
        if (Directory.Exists(StagingDirectory))
        {
            Directory.Delete(StagingDirectory, true);
        }
        DirectoryInfo dir = Directory.CreateDirectory(StagingDirectory);
        if (OperatingSystem.IsWindows())
        {
            dir.Attributes |= FileAttributes.Hidden;
        }
        return StagingDirectory;
    }

    /// <summary>
    /// 应用更新包:校验条目路径 → 预清理陈旧 <c>*.old/*.new</c> → 解压为 <c>*.new</c> →
    /// 两段重命名换版。任何一步失败都会就地回滚到原状后重新抛出异常。
    /// 成功返回后旧版文件以 <c>*.old</c> 留存,由重启后的 <see cref="TryFinalizeStartup" /> 清理。
    /// </summary>
    public void Apply(string archivePath)
    {
        List<PackageEntry> entries = ReadPackageEntries(archivePath);
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"Update package contains no files: {archivePath}");
        }

        // 预清理:陈旧的 .old/.new(上次清理失败的残留)必须先删掉,否则回滚时无法
        // 区分 “本次换版产生的 .old” 与 “历史残留”,可能把旧版本误还原回去。
        // 删不掉(被占用)就在动任何现有文件之前放弃,应用保持原状。
        foreach (PackageEntry entry in entries)
        {
            string target = Path.Combine(ApplicationDirectory, entry.RelativePath);
            // File.Exists 先判一道:File.Delete 对"父目录不存在"会抛异常(新增子目录的场景)。
            if (File.Exists(target + ".old"))
            {
                File.Delete(target + ".old");
            }
            if (File.Exists(target + ".new"))
            {
                File.Delete(target + ".new");
            }
        }

        UpdateJournal journal = new()
        {
            Phase = UpdateJournal.PhaseStaged,
            Files = entries
                .Select(e => new UpdateJournalFile
                {
                    Path = e.RelativePath,
                    Existed = File.Exists(Path.Combine(ApplicationDirectory, e.RelativePath))
                })
                .ToList()
        };
        WriteJournal(journal);
        try
        {
            ExtractAsNew(archivePath, entries);

            journal.Phase = UpdateJournal.PhaseApplying;
            WriteJournal(journal);
            foreach (PackageEntry entry in entries)
            {
                string target = Path.Combine(ApplicationDirectory, entry.RelativePath);
                if (File.Exists(target))
                {
                    File.Move(target, target + ".old");
                }
                File.Move(target + ".new", target);
                if (!OperatingSystem.IsWindows() && entry.UnixMode is { } mode)
                {
                    File.SetUnixFileMode(target, mode);
                }
            }

            journal.Phase = UpdateJournal.PhaseDone;
            WriteJournal(journal);
        }
        catch
        {
            Rollback(journal);
            TryDeleteStaging();
            throw;
        }
    }

    /// <summary>
    /// 启动期收尾:按日志清理换版成功后的 <c>*.old</c>(旧进程可能尚未完全退出,删不掉的
    /// 留待重试),或回滚上次中途崩溃的换版。没有待办或全部处理完返回 true;
    /// 返回 false 表示还有残留,调用方可稍后重试。绝不抛出异常。
    /// </summary>
    public bool TryFinalizeStartup()
    {
        try
        {
            UpdateJournal? journal = ReadJournal();
            if (journal == null)
            {
                // 无日志:只剩(可能存在的)下载残留,直接清掉暂存目录。
                return TryDeleteStaging();
            }
            bool clean = journal.Phase == UpdateJournal.PhaseDone
                ? TryDeleteOldFiles(journal)
                : Rollback(journal);
            return clean && TryDeleteStaging();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update finalize failed: {ex}");
            return false;
        }
    }

    /// <summary>删除换版成功后残留的 <c>*.old</c>;被占用的跳过并返回 false。</summary>
    private bool TryDeleteOldFiles(UpdateJournal journal)
    {
        bool allDeleted = true;
        foreach (UpdateJournalFile file in journal.Files)
        {
            string old = Path.Combine(ApplicationDirectory, file.Path) + ".old";
            try
            {
                if (File.Exists(old))
                {
                    File.Delete(old);
                }
            }
            catch
            {
                allDeleted = false;
            }
        }
        return allDeleted;
    }

    /// <summary>
    /// 依据日志回滚:凡存在 <c>f.old</c> 的,删掉换入的新 <c>f</c> 并把 <c>f.old</c> 还原为
    /// <c>f</c>;换版前不存在的新增文件直接删除;所有 <c>f.new</c> 清掉。幂等,可重复执行。
    /// </summary>
    private bool Rollback(UpdateJournal journal)
    {
        bool clean = true;
        foreach (UpdateJournalFile file in journal.Files)
        {
            string target = Path.Combine(ApplicationDirectory, file.Path);
            try
            {
                if (File.Exists(target + ".old"))
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    File.Move(target + ".old", target);
                }
                else if (!file.Existed && File.Exists(target))
                {
                    File.Delete(target);
                }
                if (File.Exists(target + ".new"))
                {
                    File.Delete(target + ".new");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VelaShell] Update rollback failed for {file.Path}: {ex}");
                clean = false;
            }
        }
        return clean;
    }

    private bool TryDeleteStaging()
    {
        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把包内所有文件解压为目标路径加 <c>.new</c> 后缀的暂存文件。</summary>
    private void ExtractAsNew(string archivePath, List<PackageEntry> entries)
    {
        Dictionary<string, PackageEntry> byPath = entries.ToDictionary(
            e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive zip = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (NormalizeEntryPath(entry.FullName) is not { } rel || !byPath.ContainsKey(rel))
                {
                    continue;
                }
                string newPath = PrepareNewPath(rel);
                entry.ExtractToFile(newPath, true);
            }
        }
        else
        {
            using FileStream file = File.OpenRead(archivePath);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using TarReader tar = new(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || NormalizeEntryPath(entry.Name) is not { } rel
                    || !byPath.ContainsKey(rel))
                {
                    continue;
                }
                string newPath = PrepareNewPath(rel);
                entry.ExtractToFile(newPath, true);
            }
        }
        // 校验齐全:包里列出的每个文件都必须已解出,缺一个都不进入换版阶段。
        foreach (PackageEntry entry in entries)
        {
            if (!File.Exists(Path.Combine(ApplicationDirectory, entry.RelativePath) + ".new"))
            {
                throw new InvalidDataException($"Update package entry missing after extraction: {entry.RelativePath}");
            }
        }
    }

    private string PrepareNewPath(string relativePath)
    {
        string newPath = Path.Combine(ApplicationDirectory, relativePath) + ".new";
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        return newPath;
    }

    /// <summary>枚举包内文件条目,并对每个条目做路径安全校验(防 zip-slip)。</summary>
    private List<PackageEntry> ReadPackageEntries(string archivePath)
    {
        List<PackageEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        void Add(string rawPath, UnixFileMode? mode)
        {
            if (NormalizeEntryPath(rawPath) is not { } rel)
            {
                return;
            }
            EnsureInsideApplicationDirectory(rel, rawPath);
            if (seen.Add(rel))
            {
                entries.Add(new(rel, mode));
            }
        }
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive zip = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                Add(entry.FullName, null);
            }
        }
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream file = File.OpenRead(archivePath);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using TarReader tar = new(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    Add(entry.Name, entry.Mode);
                }
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported update package format: {archivePath}");
        }
        return entries;
    }

    /// <summary>
    /// 归一化包内条目路径:统一斜杠、去掉引导 "./";目录条目、空路径、绝对路径、
    /// 含 ".." 的路径,以及落在暂存目录内的条目返回 null(目录/空)或抛异常(可疑路径)。
    /// </summary>
    private string? NormalizeEntryPath(string rawPath)
    {
        string path = rawPath.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        if (path.Length == 0 || path.EndsWith('/'))
        {
            return null;
        }
        string[] segments = path.Split('/');
        if (Path.IsPathRooted(path) || segments.Any(s => s is "" or "." or ".."))
        {
            throw new InvalidDataException($"Update package contains a suspicious entry path: {rawPath}");
        }
        // 更新器自己的暂存目录不属于应用文件,包里出现同名路径直接忽略。
        if (segments[0].Equals(StagingDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.Combine(segments);
    }

    private void EnsureInsideApplicationDirectory(string relativePath, string rawPath)
    {
        string full = Path.GetFullPath(Path.Combine(ApplicationDirectory, relativePath));
        string root = ApplicationDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? ApplicationDirectory
            : ApplicationDirectory + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison))
        {
            throw new InvalidDataException($"Update package entry escapes the application directory: {rawPath}");
        }
    }

    private void WriteJournal(UpdateJournal journal)
    {
        Directory.CreateDirectory(StagingDirectory);
        string tmp = JournalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, UpdateJournalContext.Default.UpdateJournal));
        File.Move(tmp, JournalPath, true);
    }

    private UpdateJournal? ReadJournal()
    {
        if (!File.Exists(JournalPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(JournalPath), UpdateJournalContext.Default.UpdateJournal);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update journal unreadable, discarding: {ex.Message}");
            return null;
        }
    }

    private sealed record PackageEntry(string RelativePath, UnixFileMode? UnixMode);
}

/// <summary>换版日志:阶段 + 涉及的文件清单,崩溃后据此回滚或完成清理。</summary>
public sealed class UpdateJournal
{
    /// <summary>阶段:已解压暂存(<c>.new</c> 就绪,尚未动现有文件)。</summary>
    public const string PhaseStaged = "staged";

    /// <summary>阶段:换版重命名进行中。</summary>
    public const string PhaseApplying = "applying";

    /// <summary>阶段:换版完成,待清理 <c>*.old</c>。</summary>
    public const string PhaseDone = "done";

    /// <summary>当前阶段。</summary>
    public string Phase { get; set; } = PhaseStaged;

    /// <summary>本次更新涉及的文件。</summary>
    public List<UpdateJournalFile> Files { get; set; } = [];
}

/// <summary>换版日志中的单个文件:应用目录内的相对路径,及换版前是否已存在。</summary>
public sealed class UpdateJournalFile
{
    /// <summary>应用目录内的相对路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>换版前该文件是否已存在(不存在的是新增文件,回滚时直接删除)。</summary>
    public bool Existed { get; set; }
}

/// <summary>换版日志的 System.Text.Json 源生成上下文(单文件发布下不依赖反射)。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateJournal))]
internal sealed partial class UpdateJournalContext : JsonSerializerContext;
