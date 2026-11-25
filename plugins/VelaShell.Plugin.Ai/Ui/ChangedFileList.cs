using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>「已修改文件」列表里的一个文件:同一文件的多次改动合并成一条。</summary>
internal sealed class ChangedFileEntry
{
    internal ChangedFileList Owner { get; }
    internal int Revision { get; private set; }
    internal bool CanRestore { get; set; } = true;
    internal string? ReviewError { get; set; }
    private bool? BeforeHasBom { get; }
    internal FileChange Snapshot() => new("review", Location, Path, Kind, Before, After, OldPath) { BeforeHasBom = BeforeHasBom };

    internal ChangedFileEntry(ChangedFileList owner, FileChange first)
    {
        Owner = owner;
        Location = first.Location;
        Path = first.Path;
        OldPath = first.Kind == FileChangeKind.Renamed ? first.OldPath : null;
        Kind = first.Kind;
        Before = first.Before;
        BeforeHasBom = first.BeforeHasBom;
        After = first.After;
        Recompute();
    }

    /// <summary>位置(<c>local:…</c> / <c>remote:…</c>)。</summary>
    public string Location { get; }

    /// <summary>当前路径(重命名后为新路径)。</summary>
    public string Path { get; internal set; }

    /// <summary>重命名前的原路径;没改过名为 null。</summary>
    public string? OldPath { get; internal set; }

    /// <summary>合并后的改动种类。</summary>
    public FileChangeKind Kind { get; internal set; }

    /// <summary>第一次改动之前的全文。</summary>
    public string? Before { get; internal set; }

    /// <summary>最后一次改动之后的全文。</summary>
    public string? After { get; internal set; }

    /// <summary>首尾全文之间的差异;拿不到文本时为 null。</summary>
    public ToolFileDiff? Diff { get; private set; }

    /// <summary>新增行数(拿不到差异为 0)。</summary>
    public int Added => Diff?.Added ?? 0;

    /// <summary>删除行数(拿不到差异为 0)。</summary>
    public int Removed => Diff?.Removed ?? 0;

    /// <summary>列表里那枚种类字母:A / M / D / R。</summary>
    public string Badge => Kind switch
    {
        FileChangeKind.Created => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        _ => "M"
    };

    internal void Recompute()
    {
        Revision++;
        ReviewError = null;
        Diff = ToolDiff.FromTexts(Path, Before, After, Kind);
    }
}

/// <summary>
/// 一段对话里 Agent 改过的文件:按(位置, 路径)合并,Before 取第一次、After 取最后一次。
/// </summary>
/// <remarks>
/// <para>合并规则:</para>
/// <list type="bullet">
/// <item>先新建后删除 = 什么都没发生,条目移除;先新建后修改仍算新建。</item>
/// <item>先删后又建回来算修改;已有文件被删掉算删除。</item>
/// <item>重命名把原路径那一条挪到新路径上,记下最初的原路径;新建的文件改名仍算新建。</item>
/// </list>
/// 只活在内存里,不写进历史:换对话、新建对话时清空(见 <c>ChatPanelView.ChangedFiles.cs</c>)。
/// </remarks>
internal sealed class ChangedFileList
{
    private readonly List<ChangedFileEntry> _entries = [];

    /// <summary>按首次改动的先后排列。</summary>
    public IReadOnlyList<ChangedFileEntry> Entries => _entries;

    /// <summary>新增行合计。</summary>
    public int TotalAdded => _entries.Sum(e => e.Added);

    /// <summary>删除行合计。</summary>
    public int TotalRemoved => _entries.Sum(e => e.Removed);

    /// <summary>(位置, 路径)的字典键,停靠文档的去重也用它。</summary>
    public static string KeyOf(string location, string path) => location + "\n" + path.Replace('\\', '/');

    /// <summary>按键找条目。</summary>
    public ChangedFileEntry? Find(string location, string path)
        => _entries.Find(e => Same(e, location, path));

    /// <summary>并入一条改动;返回它落在的条目(合并后被移除时为 null)。</summary>
    public ChangedFileEntry? Add(FileChange change)
    {
        if (change.Kind == FileChangeKind.Renamed && change.OldPath is { Length: > 0 } oldPath)
        {
            return Rename(change, oldPath);
        }
        ChangedFileEntry? entry = Find(change.Location, change.Path);
        if (entry is null)
        {
            entry = new ChangedFileEntry(this, change);
            _entries.Add(entry);
            return entry;
        }
        switch (change.Kind)
        {
            case FileChangeKind.Deleted:
                if (entry.Kind == FileChangeKind.Created)
                {
                    _entries.Remove(entry);
                    return null;
                }
                entry.Kind = FileChangeKind.Deleted;
                entry.After = null;
                break;
            case FileChangeKind.Created:
                entry.Kind = entry.Kind == FileChangeKind.Deleted ? FileChangeKind.Modified : entry.Kind;
                entry.After = change.After;
                break;
            default:
                if (entry.Kind == FileChangeKind.Deleted) entry.Kind = FileChangeKind.Modified;
                entry.After = change.After;
                break;
        }
        entry.Recompute();
        return entry;
    }

    private ChangedFileEntry Rename(FileChange change, string oldPath)
    {
        ChangedFileEntry? source = Find(change.Location, oldPath);
        ChangedFileEntry? target = Find(change.Location, change.Path);
        if (target is not null && !ReferenceEquals(target, source))
        {
            // 改名覆盖了一个也改过的文件:那一条并进来,不留两条指向同一路径
            _entries.Remove(target);
            if (source is not null) source.CanRestore = false;
        }
        if (source is null)
        {
            var entry = new ChangedFileEntry(this, change) { CanRestore = target is null };
            _entries.Add(entry);
            return entry;
        }
        source.Path = change.Path;
        if (source.Kind != FileChangeKind.Created)
        {
            source.OldPath ??= oldPath;
            source.Kind = FileChangeKind.Renamed;
        }
        source.After = change.After ?? source.After;
        source.Recompute();
        return source;
    }

    internal bool Remove(ChangedFileEntry entry) => _entries.Remove(entry);

    /// <summary>清空。</summary>
    public void Clear() => _entries.Clear();

    private static bool Same(ChangedFileEntry entry, string location, string path)
        => string.Equals(entry.Location, location, StringComparison.Ordinal)
           && string.Equals(entry.Path.Replace('\\', '/'), path.Replace('\\', '/'),
               location.StartsWith("local:", StringComparison.Ordinal) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
