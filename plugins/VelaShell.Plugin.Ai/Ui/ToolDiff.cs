using System.Text.Json;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 一次改文件的工具调用(或一条 <see cref="FileChange" />)换算出的差异:一段或几段行级比对。
/// </summary>
/// <param name="Path">改的是哪个文件(参数里的 path;取不到为 null)。</param>
/// <param name="Sections">
/// 各段比对。整份前后文比对只有一段;<c>edits</c> 数组形状的补丁一段对应一处替换。
/// </param>
/// <param name="HasLineNumbers">
/// 行号是不是文件里的真实行号。只从补丁参数算出来的片段比对不知道片段在文件里的位置,不显示行号。
/// </param>
public sealed record ToolFileDiff(string? Path, IReadOnlyList<LineDiffResult> Sections, bool HasLineNumbers)
{
    /// <summary>新增行合计。</summary>
    public int Added => Sections.Sum(s => s.Added);

    /// <summary>删除行合计。</summary>
    public int Removed => Sections.Sum(s => s.Removed);

    /// <summary>补丁的各处替换原文(旧 / 新片段);整份比对为空。「载入完整文件」时用它从当前全文反推改动前的内容。</summary>
    public IReadOnlyList<(string Old, string New)> Edits { get; init; } = [];
}

/// <summary>
/// 从工具参数或 <see cref="FileChange" /> 算差异,供工具卡片展开区与「已修改文件」列表使用。
/// </summary>
/// <remarks>
/// 补丁工具的参数有两种形状,都认:顶层 <c>oldText</c> / <c>newText</c>(可带 <c>replaceAll</c>),
/// 或 <c>edits</c> 数组(每项各带一对 <c>oldText</c> / <c>newText</c>;模型偶尔把数组写成 JSON 字符串,也解开)。
/// 历史回放时工具卡片只有持久化的参数,补丁的差异照样从这里算出来。
/// </remarks>
public static class ToolDiff
{
    private static readonly string[] OldKeys = ["oldText", "old_text", "oldString", "old_string"];
    private static readonly string[] NewKeys = ["newText", "new_text", "newString", "new_string"];
    private static readonly string[] PathKeys = ["path", "remotePath", "localPath", "filePath", "file_path"];

    /// <summary>参数里装着补丁或整份内容、展开时要画差异的工具。</summary>
    /// <param name="toolName">工具名。</param>
    public static bool IsFileEditTool(string toolName)
        => toolName is "patch_local_file" or "patch_remote_file" or "write_local_file" or "write_remote_file";

    /// <summary>展开详情时不必再原样列出的大段参数(差异视图已经画出来了)。</summary>
    /// <param name="key">参数名。</param>
    public static bool IsDiffPayloadKey(string key)
        => key is "content" or "edits" || OldKeys.Contains(key) || NewKeys.Contains(key);

    /// <summary>从工具参数算差异;不是改文件的工具、参数解不开或里面没有可比的内容时返回 null。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">参数 JSON。</param>
    public static ToolFileDiff? FromArguments(string toolName, string? argumentsJson)
    {
        if (!IsFileEditTool(toolName) || string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? path = FirstString(root, PathKeys);
            if (toolName.StartsWith("write_", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                // 只有新内容、不知道原文:整份按新增算,行号就是新文件的真实行号
                return new ToolFileDiff(path, [LineDiff.Compute(null, content.GetString())], true);
            }

            var sections = new List<(string Old, string New)>();
            AddPair(root, sections);
            if (root.TryGetProperty("edits", out JsonElement edits))
            {
                AddEdits(edits, sections);
            }
            return sections.Count == 0 ? null : new ToolFileDiff(path, [.. sections.Select(p => LineDiff.Compute(p.Old, p.New))], false) { Edits = sections };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从改动后的当前全文反推改动前的内容:按相反顺序把每处 New 换回 Old。
    /// 某处 New 为空(纯删除,定位不到)、在全文里找不到或不止一处时反推不出,返回 null。
    /// </summary>
    /// <param name="current">当前全文。</param>
    /// <param name="edits">补丁的各处替换。</param>
    public static string? ReverseApply(string current, IReadOnlyList<(string Old, string New)> edits)
    {
        if (edits.Count == 0) return null;
        string text = current.ReplaceLineEndings("\n");
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            string @new = edits[i].New.ReplaceLineEndings("\n");
            if (@new.Length == 0) return null;
            int at = text.IndexOf(@new, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(@new, at + 1, StringComparison.Ordinal) >= 0) return null;
            text = string.Concat(text.AsSpan(0, at), edits[i].Old.ReplaceLineEndings("\n"), text.AsSpan(at + @new.Length));
        }
        return text;
    }

    /// <summary>参数里的各个路径值(path / newPath / from / to 等),用来把一条文件改动对回发起它的工具卡片。</summary>
    /// <param name="argumentsJson">参数 JSON。</param>
    public static IReadOnlyList<string> PathsOf(string? argumentsJson)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(argumentsJson)) return paths;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return paths;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && (property.Name.Contains("path", StringComparison.OrdinalIgnoreCase)
                        || property.Name is "from" or "to" or "source" or "destination" or "target")
                    && property.Value.GetString() is { Length: > 0 } value)
                {
                    paths.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // 解不开就当没有路径
        }
        return paths;
    }

    /// <summary>两个给人看的路径是否指同一个文件:分隔符归一后相等,或一个是另一个的尾段(相对 vs 绝对)。</summary>
    /// <param name="a">路径一。</param>
    /// <param name="b">路径二。</param>
    public static bool SamePath(string a, string b)
    {
        string x = a.Replace('\\', '/').Trim().TrimStart('.', '/');
        string y = b.Replace('\\', '/').Trim().TrimStart('.', '/');
        if (x.Length == 0 || y.Length == 0) return false;
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase)
               || x.EndsWith("/" + y, StringComparison.OrdinalIgnoreCase)
               || y.EndsWith("/" + x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从一条文件改动的前后全文算差异;前后都拿不到(二进制、超出上限)时返回 null。</summary>
    /// <param name="change">文件改动。</param>
    public static ToolFileDiff? FromChange(FileChange change)
        => FromTexts(change.Path, change.Before, change.After, change.Kind);

    /// <summary>从前后全文算差异;该有的那一侧文本拿不到(新建缺 After、删除缺 Before、其余缺任一侧)时返回 null。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="before">改动前全文。</param>
    /// <param name="after">改动后全文。</param>
    /// <param name="kind">改动种类。</param>
    public static ToolFileDiff? FromTexts(string? path, string? before, string? after, FileChangeKind kind)
    {
        bool available = kind switch
        {
            FileChangeKind.Created => after is not null,
            FileChangeKind.Deleted => before is not null,
            _ => before is not null && after is not null
        };
        return available ? new ToolFileDiff(path, [LineDiff.Compute(before, after)], true) : null;
    }

    private static void AddEdits(JsonElement edits, List<(string Old, string New)> sections)
    {
        if (edits.ValueKind == JsonValueKind.String && edits.GetString() is { Length: > 0 } text)
        {
            try
            {
                using var inner = JsonDocument.Parse(text);
                AddEdits(inner.RootElement.Clone(), sections);
            }
            catch (JsonException)
            {
                // 不是 JSON 的字符串:没有可比的东西
            }
            return;
        }
        if (edits.ValueKind == JsonValueKind.Object)
        {
            AddPair(edits, sections);
            return;
        }
        if (edits.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item in edits.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                AddPair(item, sections);
            }
        }
    }

    private static void AddPair(JsonElement element, List<(string Old, string New)> sections)
    {
        string? oldText = FirstString(element, OldKeys);
        string? newText = FirstString(element, NewKeys);
        if (oldText is null && newText is null)
        {
            return;
        }
        sections.Add((oldText ?? "", newText ?? ""));
    }

    private static string? FirstString(JsonElement element, string[] keys)
    {
        foreach (string key in keys)
        {
            if (element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }
}
