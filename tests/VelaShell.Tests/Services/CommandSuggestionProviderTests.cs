using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
[TestCategory("CommandSuggestions")]
public class CommandSuggestionProviderTests
{
    private static CommandSuggestionProvider CreateProvider(out CommandHistoryService history)
    {
        history = new(null);
        return new(history, null);
    }

    [TestMethod]
    public async Task Prefix_MatchesBuiltInQuickCommand()
    {
        CommandSuggestionProvider provider = CreateProvider(out _);
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("ht", 8);
        Assert.Contains(s => s.Text == "htop", items, "内置快捷命令 htop 应命中前缀 ht");
    }

    [TestMethod]
    public async Task Prefix_IsCaseInsensitive()
    {
        CommandSuggestionProvider provider = CreateProvider(out _);
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("HT", 8);
        Assert.Contains(s => s.Text == "htop", items);
    }

    [TestMethod]
    public async Task History_PrefixMatch_RanksFirst()
    {
        CommandSuggestionProvider provider = CreateProvider(out CommandHistoryService history);
        history.Record("sudo apt update");
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("sudo ", 8);
        Assert.IsNotEmpty(items);
        Assert.AreEqual("sudo apt update", items[0].Text);
        Assert.AreEqual("历史", items[0].Source);
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
        Assert.Contains(s => s.Source == "快捷命令", items);
        Assert.Contains(s => s.Text == "tail -f /var/log/syslog", items);
    }

    [TestMethod]
    public async Task SameTextInHistoryAndQuickCommands_AppearsOnce()
    {
        CommandSuggestionProvider provider = CreateProvider(out CommandHistoryService history);
        history.Record("htop -d 10"); // 保证 htop 前缀有历史项;htop 本身来自内置。
        history.Record("htop");
        IReadOnlyList<CommandSuggestion> items = await provider.GetSuggestionsAsync("ht", 8);
        Assert.ContainsSingle(s => s.Text == "htop", items);
    }
}
