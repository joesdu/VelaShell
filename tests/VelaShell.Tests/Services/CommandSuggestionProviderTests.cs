using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
[TestCategory("CommandSuggestions")]
public class CommandSuggestionProviderTests
{
    /// <summary>
    /// 取一条内置命令与它的前缀当样本。刻意不写死某条具体命令(旧版写死 htop,内置目录
    /// 一改这些测试就集体失效):要测的是“前缀能命中内置目录”,不是目录里有什么。
    /// </summary>
    private static QuickCommand SampleBuiltIn => QuickCommandCatalog.BuiltIns[0];

    private static string SamplePrefix => SampleBuiltIn.CommandText[..3];

    private static CommandSuggestionProvider CreateProvider(out CommandHistoryService history)
    {
        history = new(null);
        return new(history, null);
    }

    [TestMethod]
    public async Task Prefix_MatchesBuiltInQuickCommand()
    {
        CommandSuggestionProvider provider = CreateProvider(out _);
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync(SamplePrefix, 8);
        Assert.Contains(s => s.Text == SampleBuiltIn.CommandText, items, $"内置快捷命令应命中前缀 {SamplePrefix}");
    }

    [TestMethod]
    public async Task Prefix_IsCaseInsensitive()
    {
        CommandSuggestionProvider provider = CreateProvider(out _);
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync(SamplePrefix.ToUpperInvariant(), 8);
        Assert.Contains(s => s.Text == SampleBuiltIn.CommandText, items);
    }

    [TestMethod]
    public async Task History_PrefixMatch_RanksFirst()
    {
        CommandSuggestionProvider provider = CreateProvider(out CommandHistoryService history);
        history.Record("sudo apt update");
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("sudo ", 8);
        Assert.IsNotEmpty(items);
        Assert.AreEqual("sudo apt update", items[0].Text);
        Assert.AreEqual(Strings.Get("Svc_History"), items[0].Source);
    }

    [TestMethod]
    public async Task History_MostRecentFirst_AndDeduplicated()
    {
        CommandSuggestionProvider provider = CreateProvider(out CommandHistoryService history);
        history.Record("sudo apt update");
        history.Record("sudo systemctl restart nginx");
        history.Record("sudo apt update"); // 重复执行 → 移到最前,不重复。
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("sudo", 8);
        Assert.AreEqual("sudo apt update", items[0].Text);
        Assert.AreEqual("sudo systemctl restart nginx", items[1].Text);
        Assert.ContainsSingle(s => s.Text == "sudo apt update", items);
    }

    [TestMethod]
    public async Task QuickCommand_DescriptionContains_AlsoMatches()
    {
        CommandSuggestionProvider provider = CreateProvider(out _);
        // 内置 journalctl -f 的描述是 "Follow system journal"。
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("journal", 8);
        Assert.Contains(s => s.Text == "journalctl -f", items);
    }

    [TestMethod]
    public async Task EmptyPrefix_ReturnsQuickCommandsAndRecentHistory()
    {
        CommandSuggestionProvider provider = CreateProvider(out CommandHistoryService history);
        history.Record("tail -f /var/log/syslog");
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync(string.Empty, 20);
        Assert.Contains(s => s.Source == Strings.Get("QuickCommands"), items);
        Assert.Contains(s => s.Text == "tail -f /var/log/syslog", items);
    }

    [TestMethod]
    public async Task SameTextInHistoryAndQuickCommands_AppearsOnce()
    {
        CommandSuggestionProvider provider = CreateProvider(out CommandHistoryService history);

        // 同一条命令既在历史里又在内置目录里 —— 两个来源都会产出它,结果里只该留一条。
        history.Record(SampleBuiltIn.CommandText);
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync(SamplePrefix, 8);
        Assert.ContainsSingle(s => s.Text == SampleBuiltIn.CommandText, items);
    }
}
