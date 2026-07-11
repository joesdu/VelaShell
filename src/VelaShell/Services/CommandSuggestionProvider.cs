using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>一条补全建议:插入文本 + 补充说明(快捷命令的名称/描述)+ 来源标签。</summary>
public sealed record CommandSuggestion(string Text, string? Detail, string Source);

/// <summary>
/// 命令补全建议提供器(plan.md #16):合并本地命令历史(MRU、前缀匹配优先)与
/// 快捷命令(内置目录 + 设置里自定义的 quick_commands)。自定义命令带 10 秒 TTL
/// 缓存——设置页保存后最迟 10 秒生效,避免每次按键都读库。
/// </summary>
public sealed class CommandSuggestionProvider(CommandHistoryService history, IAppDataStore? dataStore)
{
    private const string Collection = "quick_commands";
    private const string DocumentId = "commands";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private List<QuickCommand> _customCommands = [];
    private DateTime _customLoadedAt = DateTime.MinValue;

    /// <summary>
    /// 返回按相关度排序的建议:历史前缀匹配 → 快捷命令(名称/命令文本匹配)→ 历史包含匹配。
    /// 空前缀(Alt+Enter 面板)返回快捷命令全量 + 最近历史。
    /// </summary>
    public async Task<IReadOnlyList<CommandSuggestion>> GetSuggestionsAsync(string prefix, int max)
    {
        List<QuickCommand> quick = await GetQuickCommandsAsync();
        var results = new List<CommandSuggestion>(max);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string text, string? detail, string source)
        {
            if (results.Count < max && !string.IsNullOrWhiteSpace(text) && seen.Add(text))
            {
                results.Add(new(text, detail, source));
            }
        }

        if (string.IsNullOrEmpty(prefix))
        {
            foreach (QuickCommand cmd in quick)
            {
                Add(cmd.CommandText, DescribeQuickCommand(cmd), "快捷命令");
            }
            foreach (string entry in history.Entries)
            {
                Add(entry, null, "历史");
            }
            return results;
        }

        // 排序:历史前缀命中 → 快捷命令前缀命中 → 快捷命令包含命中(名称/描述也算)
        // → 历史包含命中。前缀匹配一律忽略大小写("Sudo"/"sudo" 不应错过)。
        foreach (string entry in history.Entries.Where(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Length > prefix.Length))
        {
            Add(entry, null, "历史");
        }
        foreach (QuickCommand cmd in quick.Where(c => c.CommandText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            Add(cmd.CommandText, DescribeQuickCommand(cmd), "快捷命令");
        }
        foreach (QuickCommand cmd in quick.Where(c => MatchesLoosely(c, prefix)))
        {
            Add(cmd.CommandText, DescribeQuickCommand(cmd), "快捷命令");
        }
        foreach (string entry in history.Entries.Where(e => e.Contains(prefix, StringComparison.OrdinalIgnoreCase) && e.Length > prefix.Length))
        {
            Add(entry, null, "历史");
        }
        return results;
    }

    private static bool MatchesLoosely(QuickCommand cmd, string prefix) =>
        cmd.CommandText.Contains(prefix, StringComparison.OrdinalIgnoreCase) ||
        cmd.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase) ||
        cmd.Description.Contains(prefix, StringComparison.OrdinalIgnoreCase);

    private static string? DescribeQuickCommand(QuickCommand cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.Description))
        {
            return cmd.Description;
        }
        return cmd.Name == cmd.CommandText ? null : cmd.Name;
    }

    private async Task<List<QuickCommand>> GetQuickCommandsAsync()
    {
        if (DateTime.UtcNow - _customLoadedAt < CacheTtl)
        {
            return _customCommands;
        }
        var merged = new List<QuickCommand>();
        if (dataStore is not null)
        {
            try
            {
                QuickCommandData? data = await dataStore.GetAsync<QuickCommandData>(Collection, DocumentId);
                if (data?.Commands is { Count: > 0 } custom)
                {
                    // 自定义命令排在内置前(用户自己配的更常用)。
                    merged.AddRange(custom);
                }
            }
            catch
            {
                // 读不出自定义命令就只用内置目录。
            }
        }
        merged.AddRange(QuickCommandCatalog.BuiltIns);
        _customCommands = merged;
        _customLoadedAt = DateTime.UtcNow;
        return merged;
    }
}
