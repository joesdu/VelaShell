using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VelaShell.Plugin.Ai.Chat.Memory;

/// <summary>一条记忆。</summary>
/// <param name="Id">短 id(给模型引用、给界面定位)。</param>
/// <param name="Title">一句话标题。</param>
/// <param name="Content">内容。</param>
/// <param name="Origin">谁写的:<c>model</c>(模型经工具保存)或 <c>user</c>(在「记忆」页手动添加 / 改过)。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">最后修改时间。</param>
internal sealed record MemoryEntry(string Id, string Title, string Content, string Origin, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// AI 插件的记忆:跨对话保留的少量事实(用户的偏好、环境里的事实、被纠正过的做法)。
/// </summary>
/// <remarks>
/// <para>
/// <b>只在桌面面板生效、全局一份</b>(维护者 2026-11-19 定):IM 机器人与对外 MCP 既不读也不写 —— 记忆里可能有个人偏好与环境信息,
/// 不该带进群聊,也不该让群里的人往里写东西。
/// </para>
/// <para>
/// 存在插件数据目录的 <c>memory.json</c>,只在本机,不随设置同步。写入先写临时文件再替换,中途失败不会留下半份。
/// 文件读不出来(手改坏了)时按空处理并保留原文件为 <c>memory.json.bad</c>,不让一份坏文件拦住整个面板。
/// 同一数据目录只有一个实例(<see cref="For" />):面板与「配置工具」窗口看的是同一份,任何一边改了都经 <see cref="Changed" /> 通知另一边。
/// </para>
/// <para>
/// <b>不存机密</b>:内容像私钥、API Key、令牌的拒绝写入(<see cref="LooksLikeSecret" />),工具说明也写明不要存。
/// 这是兜底的粗筛,不是保证 —— 用户可以在「记忆」页随时查看与删除。
/// </para>
/// </remarks>
internal sealed partial class MemoryStore
{
    /// <summary>最多保留几条。</summary>
    public const int MaxEntries = 200;

    /// <summary>标题上限(字符)。</summary>
    public const int MaxTitleLength = 80;

    /// <summary>内容上限(字符)。</summary>
    public const int MaxContentLength = 1000;

    private static readonly ConcurrentDictionary<string, MemoryStore> Instances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<MemoryEntry>? _entries;

    private MemoryStore(string dataDirectory) => _path = Path.Combine(dataDirectory, "memory.json");

    /// <summary>某个插件数据目录的记忆(同一目录同一实例)。</summary>
    public static MemoryStore For(string dataDirectory) => Instances.GetOrAdd(Path.GetFullPath(dataDirectory), dir => new MemoryStore(dir));

    /// <summary>记忆有增删改(可能不在 UI 线程)。</summary>
    public event Action? Changed;

    /// <summary>全部记忆,最近改过的在前。</summary>
    public IReadOnlyList<MemoryEntry> List()
    {
        lock (_gate) return [.. Load().OrderByDescending(e => e.UpdatedAt)];
    }

    /// <summary>按 id 找。</summary>
    public MemoryEntry? Find(string id)
    {
        lock (_gate) return Load().FirstOrDefault(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>新增一条;不合规时抛 <see cref="ArgumentException" />(消息可直接给模型或界面看)。</summary>
    public MemoryEntry Add(string title, string content, string origin)
    {
        (string t, string c) = Validate(title, content);
        MemoryEntry entry;
        lock (_gate)
        {
            List<MemoryEntry> entries = Load();
            if (entries.Count >= MaxEntries)
                throw new ArgumentException($"Memory is full ({MaxEntries} entries). Update or delete an existing memory instead.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            entry = new MemoryEntry(NewId(entries), t, c, origin, now, now);
            entries.Add(entry);
            Save(entries);
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>修改一条(标题或内容为 null 时保留原值);找不到抛 <see cref="KeyNotFoundException" />。</summary>
    public MemoryEntry Update(string id, string? title, string? content, string origin)
    {
        MemoryEntry updated;
        lock (_gate)
        {
            List<MemoryEntry> entries = Load();
            int index = entries.FindIndex(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new KeyNotFoundException($"No memory with id '{id}'.");
            MemoryEntry old = entries[index];
            (string t, string c) = Validate(title ?? old.Title, content ?? old.Content);
            updated = old with { Title = t, Content = c, Origin = origin, UpdatedAt = DateTimeOffset.UtcNow };
            entries[index] = updated;
            Save(entries);
        }
        Changed?.Invoke();
        return updated;
    }

    /// <summary>删除一条;找不到返回 false。</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            List<MemoryEntry> entries = Load();
            if (entries.RemoveAll(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Save(entries);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>全部清除。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            List<MemoryEntry> entries = Load();
            if (entries.Count == 0) return;
            entries.Clear();
            Save(entries);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 内容像不像机密:PEM 私钥、常见云厂商 / 平台的令牌前缀、<c>password=</c> 一类的赋值。粗筛,宁可放过也不要误拦正常的笔记。
    /// </summary>
    public static bool LooksLikeSecret(string text) => SecretPattern().IsMatch(text);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(sk-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[abp]-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,})\b|\b(password|passwd|pwd|secret|api[_-]?key|token)\s*[:=]\s*\S{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    private static (string Title, string Content) Validate(string? title, string? content)
    {
        string t = (title ?? "").Trim();
        string c = (content ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException("A memory needs a short title.");
        if (c.Length == 0) throw new ArgumentException("A memory needs content.");
        if (t.Length > MaxTitleLength) throw new ArgumentException($"The title is longer than {MaxTitleLength} characters.");
        if (c.Length > MaxContentLength) throw new ArgumentException($"The content is longer than {MaxContentLength} characters; keep memories short.");
        if (LooksLikeSecret(t) || LooksLikeSecret(c))
            throw new ArgumentException("This looks like a password, key or token. Secrets must not be stored in memory.");
        return (t, c);
    }

    private static string NewId(List<MemoryEntry> entries)
    {
        // 短 id:模型在提示词里看到、在工具里引用,太长容易抄错
        string id;
        do id = "m" + Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 3).ToLowerInvariant();
        while (entries.Any(e => e.Id == id));
        return id;
    }

    private List<MemoryEntry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<MemoryEntry>>(File.ReadAllText(_path, Encoding.UTF8), JsonOptions) ?? []
                : [];
        }
        catch (JsonException)
        {
            // 手改坏了:留一份原样的,按空处理
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch (IOException) { }
            _entries = [];
        }
        return _entries;
    }

    private void Save(List<MemoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions), Encoding.UTF8);
        File.Move(temp, _path, overwrite: true);
        _entries = entries;
    }

    /// <summary>测试用:丢掉缓存的实例(各用例的数据目录不同,但保险起见)。</summary>
    internal static void ResetForTests() => Instances.Clear();
}
