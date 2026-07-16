using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Services;

/// <summary>一条补全建议:插入文本 + 补充说明 + 来源标签。</summary>
public sealed record CommandSuggestion(string Text, string? Detail, string Source);

/// <summary>合并本地命令历史与快捷命令的补全建议提供器。</summary>
public sealed class CommandSuggestionProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly CommandHistoryService _history;
    private readonly IQuickCommandRepository? _repository;
    private List<QuickCommand> _customCommands = [];
    private DateTime _customLoadedAt = DateTime.MinValue;

    /// <summary>创建由历史记录和快捷命令仓储共同提供建议的服务。</summary>
    public CommandSuggestionProvider(
        CommandHistoryService history,
        IQuickCommandRepository? repository
    )
    {
        _history = history;
        _repository = repository;
        if (_repository is not null)
        {
            _repository.Changed += (_, _) => _customLoadedAt = DateTime.MinValue;
        }
    }

    /// <summary>返回按相关度排序的建议。</summary>
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

        string quickCommandSource = Strings.Get("QuickCommands");
        string historySource = Strings.Get("Svc_History");
        if (string.IsNullOrEmpty(prefix))
        {
            foreach (QuickCommand command in quick)
            {
                Add(command.CommandText, DescribeQuickCommand(command), quickCommandSource);
            }
            foreach (string entry in _history.Entries)
            {
                Add(entry, null, historySource);
            }
            return results;
        }

        foreach (
            string entry in _history.Entries.Where(entry =>
                entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && entry.Length > prefix.Length
            )
        )
        {
            Add(entry, null, historySource);
        }
        foreach (
            QuickCommand command in quick.Where(command =>
                command.CommandText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            Add(command.CommandText, DescribeQuickCommand(command), quickCommandSource);
        }
        foreach (QuickCommand command in quick.Where(command => MatchesLoosely(command, prefix)))
        {
            Add(command.CommandText, DescribeQuickCommand(command), quickCommandSource);
        }
        foreach (
            string entry in _history.Entries.Where(entry =>
                entry.Contains(prefix, StringComparison.OrdinalIgnoreCase)
                && entry.Length > prefix.Length
            )
        )
        {
            Add(entry, null, historySource);
        }
        return results;
    }

    private static bool MatchesLoosely(QuickCommand command, string prefix) =>
        command.CommandText.Contains(prefix, StringComparison.OrdinalIgnoreCase)
        || command.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)
        || command.Description.Contains(prefix, StringComparison.OrdinalIgnoreCase);

    private static string? DescribeQuickCommand(QuickCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            return command.Description;
        }
        return command.Name == command.CommandText ? null : command.Name;
    }

    private async Task<List<QuickCommand>> GetQuickCommandsAsync()
    {
        if (DateTime.UtcNow - _customLoadedAt < CacheTtl)
        {
            return _customCommands;
        }
        var merged = new List<QuickCommand>();
        if (_repository is not null)
        {
            try
            {
                QuickCommandLoadResult result = await _repository.LoadAsync();
                merged.AddRange(result.Data.Commands);
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
