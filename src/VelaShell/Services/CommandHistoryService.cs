using VelaShell.Core.Data;

namespace VelaShell.Services;

/// <summary>
/// 本地命令历史(命令补全建议的主数据源,plan.md #16):MRU 序、去重、封顶 500 条,
/// 经 <see cref="IAppDataStore" /> 持久化。仅记录经调用方回显校验过的提交
/// (密码等无回显输入不会到达这里)。所有成员都在 UI 线程访问。
/// </summary>
public sealed class CommandHistoryService(IAppDataStore? dataStore)
{
    private const string Collection = "command_history";
    private const string DocumentId = "history";
    private const int MaxEntries = 500;
    private const int MaxCommandLength = 500;

    private readonly List<string> _entries = [];

    /// <summary>最近优先的历史快照。</summary>
    public IReadOnlyList<string> Entries => _entries;

    public async Task LoadAsync()
    {
        if (dataStore is null)
        {
            return;
        }
        try
        {
            CommandHistoryData? data = await dataStore.GetAsync<CommandHistoryData>(Collection, DocumentId);
            if (data?.Commands is { Count: > 0 } commands)
            {
                _entries.Clear();
                _entries.AddRange(commands.Take(MaxEntries));
            }
        }
        catch
        {
            // 历史读不出来就从空开始,不影响主流程。
        }
    }

    /// <summary>记录一次已执行的命令(MRU 去重;过短/过长的忽略)。</summary>
    public void Record(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length is < 2 or > MaxCommandLength)
        {
            return;
        }
        _entries.Remove(trimmed);
        _entries.Insert(0, trimmed);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        _ = SaveAsync();
    }

    /// <summary>清空全部历史(设置页可挂此入口)。</summary>
    public void Clear()
    {
        _entries.Clear();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (dataStore is null)
        {
            return;
        }
        try
        {
            await dataStore.UpsertAsync(Collection, DocumentId, new CommandHistoryData { Commands = [.. _entries] });
        }
        catch
        {
            // 历史落盘尽力而为。
        }
    }

    public sealed class CommandHistoryData
    {
        public List<string> Commands { get; set; } = [];
    }
}
