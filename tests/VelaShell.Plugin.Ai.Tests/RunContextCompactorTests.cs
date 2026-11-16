using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 运行中的上下文管理:按窗口余量逐级清理旧工具输出、折叠早期步骤,
/// 且始终不拆开工具调用与结果、不碰本轮起始的那一段。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RunContextCompactorTests
{
    /// <summary>约 n 个 token 的 ASCII 文本(估算是 4 字符 1 token)。</summary>
    private static string Bulk(int tokens) => new('x', tokens * 4);

    private static List<ChatMessage> Run(int steps, int resultTokens, int proseTokens = 0)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "检查磁盘并清理日志")
        ];
        for (int i = 0; i < steps; i++)
        {
            string id = $"call-{i}";
            List<AIContent> step = [];
            // 每步的过渡正文:清理工具输出省不掉它,只有折叠能腾出地方
            if (proseTokens > 0) step.Add(new TextContent($"step {i} " + Bulk(proseTokens)));
            step.Add(new FunctionCallContent(id, "run_command", new Dictionary<string, object?> { ["command"] = $"step {i}" }));
            messages.Add(new ChatMessage(ChatRole.Assistant, step));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(id, $"out {i} " + Bulk(resultTokens))]));
        }
        return messages;
    }

    private static RunContextCompactor Create(int window, Func<string, IReadOnlyList<ChatMessage>, CancellationToken, Task<string?>>? summarize = null,
        List<RunCompaction>? events = null)
        => new(new NullClient(), window, 0, summarize ?? ((_, _, _) => Task.FromResult<string?>(null)),
            c => events?.Add(c))
        {
            PrefixCount = 2
        };

    private static IEnumerable<FunctionResultContent> Results(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>();

    private static void AssertPairsIntact(List<ChatMessage> messages)
    {
        HashSet<string> calls = [.. messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId)];
        HashSet<string> results = [.. Results(messages).Select(r => r.CallId)];
        Assert.AreSequenceEqual(calls.ToList(), [.. results], SequenceOrder.InAnyOrder, "工具调用与结果必须成对发出");
    }

    [TestMethod]
    public async Task BelowThreshold_SendsMessagesUntouched()
    {
        List<ChatMessage> messages = Run(steps: 4, resultTokens: 100);
        RunContextCompactor compactor = Create(window: 100_000);

        List<ChatMessage> outgoing = await compactor.PrepareAsync(messages, CancellationToken.None);

        Assert.HasCount(messages.Count, outgoing);
        Assert.DoesNotContain(r => (string)r.Result! == RunContextCompactor.ClearedPlaceholder, Results(outgoing));
    }

    [TestMethod]
    public async Task OverHalfTheWindow_ClearsOlderLargeOutputsButKeepsRecentOnes()
    {
        // 20 步 × 约 600 token ≈ 12k,窗口 20k:过了 50% 但不到 70%
        List<ChatMessage> messages = Run(steps: 20, resultTokens: 600);
        var events = new List<RunCompaction>();
        RunContextCompactor compactor = Create(window: 20_000, events: events);

        List<ChatMessage> outgoing = await compactor.PrepareAsync(messages, CancellationToken.None);

        FunctionResultContent[] results = [.. Results(outgoing)];
        Assert.HasCount(20, results);
        Assert.IsTrue(results.Take(8).All(r => (string)r.Result! == RunContextCompactor.ClearedPlaceholder));
        Assert.IsTrue(results.Skip(8).All(r => (string)r.Result! != RunContextCompactor.ClearedPlaceholder),
            $"最近 {RunContextCompactor.KeepRecentResults} 份保持原文");
        AssertPairsIntact(outgoing);
        Assert.HasCount(1, events);
        Assert.AreEqual(8, events[0].ClearedResults);
        Assert.IsLessThan(events[0].UsedBefore, events[0].UsedAfter);
        // 原始列表一个字没动:检查点与补试拿的仍是完整消息
        Assert.IsTrue(Results(messages).All(r => ((string)r.Result!).StartsWith("out ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task StateIsStable_SecondPassClearsNothingNew()
    {
        List<ChatMessage> messages = Run(steps: 20, resultTokens: 600);
        var events = new List<RunCompaction>();
        RunContextCompactor compactor = Create(window: 20_000, events: events);

        List<ChatMessage> first = await compactor.PrepareAsync(messages, CancellationToken.None);
        // 批次续接时协调器重建消息对象:按调用 id 认,结果应完全相同
        List<ChatMessage> rebuilt = [.. messages.Select(m => new ChatMessage(m.Role, [.. m.Contents]))];
        List<ChatMessage> second = await compactor.PrepareAsync(rebuilt, CancellationToken.None);

        Assert.AreSequenceEqual(Results(first).Select(r => r.Result).ToList(), [.. Results(second).Select(r => r.Result)]);
    }

    [TestMethod]
    public async Task NearlyFull_FoldsEarlyStepsIntoSummaryAndKeepsTheTaskMessage()
    {
        // 40 步 × 约 500 token 正文 ≈ 20k,窗口 20k:清掉工具输出之后仍超 85%,需要折叠
        List<ChatMessage> messages = Run(steps: 40, resultTokens: 100, proseTokens: 500);
        IReadOnlyList<ChatMessage>? folded = null;
        var events = new List<RunCompaction>();
        RunContextCompactor compactor = Create(window: 20_000, (previous, steps, _) =>
        {
            folded = steps;
            return Task.FromResult<string?>("已完成:step 0..N");
        }, events);

        List<ChatMessage> outgoing = await compactor.PrepareAsync(messages, CancellationToken.None);

        Assert.IsNotNull(folded);
        Assert.AreEqual(ChatRole.System, outgoing[0].Role);
        Assert.AreEqual(ChatRole.User, outgoing[1].Role);
        Assert.Contains("检查磁盘并清理日志", outgoing[1].Text);
        Assert.Contains("<run-progress-summary>", outgoing[1].Text);
        Assert.Contains("已完成:step 0..N", outgoing[1].Text);
        Assert.AreEqual(ChatRole.Assistant, outgoing[2].Role, "折叠后紧接着的必须是一步的开头");
        AssertPairsIntact(outgoing);
        Assert.IsLessThan(messages.Count, outgoing.Count);
        Assert.Contains(e => e.FoldedMessages > 0, events);
        Assert.IsLessThan(20_000 * RunContextCompactor.FoldRatio, events[^1].UsedAfter);
    }

    [TestMethod]
    public async Task SummaryFailure_FallsBackToClearingOnly()
    {
        List<ChatMessage> messages = Run(steps: 40, resultTokens: 100, proseTokens: 500);
        RunContextCompactor compactor = Create(window: 20_000,
            (_, _, _) => throw new HttpRequestException("boom"));

        List<ChatMessage> outgoing = await compactor.PrepareAsync(messages, CancellationToken.None);

        Assert.HasCount(messages.Count, outgoing);
        Assert.AreEqual("", compactor.Summary);
        AssertPairsIntact(outgoing);
    }

    [TestMethod]
    public async Task PrefixIsNeverTouched()
    {
        List<ChatMessage> messages = Run(steps: 40, resultTokens: 600);
        // 前缀里放一份很大的旧工具结果:属于历史,不归本轮清理
        messages.Insert(1, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("old", Bulk(800))]));
        messages.Insert(1, new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("old", "read_file")]));
        RunContextCompactor compactor = Create(window: 20_000);
        compactor.PrefixCount = 4;

        List<ChatMessage> outgoing = await compactor.PrepareAsync(messages, CancellationToken.None);

        Assert.AreEqual(Bulk(800), (string)Results(outgoing).First(r => r.CallId == "old").Result!);
    }

    [TestMethod]
    public async Task UnknownWindow_DoesNothing()
    {
        List<ChatMessage> messages = Run(steps: 40, resultTokens: 600);
        RunContextCompactor compactor = Create(window: 0);

        List<ChatMessage> outgoing = await compactor.PrepareAsync(messages, CancellationToken.None);

        Assert.AreSame(messages, outgoing);
    }

    private sealed class NullClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse([]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
