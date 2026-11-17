using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 上下文明细的拆分:一次真实请求里,系统提示词、工具定义(内置 / MCP)、压缩摘要、消息各占多少。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ContextBreakdownTests
{
    /// <summary>约 n 个 token 的 ASCII 文本(估算是 4 字符 1 token)。</summary>
    private static string Bulk(int tokens) => new('x', tokens * 4);

    private static AIFunction Tool(string name, string description)
        => AIFunctionFactory.Create((string path) => path, name, description);

    /// <summary>名字以 <c>github_</c> 开头的算 github 服务器的 MCP 工具,其余是内置。</summary>
    private static string? ServerOf(AITool tool) => tool.Name.StartsWith("github_", StringComparison.Ordinal) ? "github" : null;

    [TestMethod]
    public void Analyze_SplitsSystemToolsMcpSummaryAndMessages()
    {
        const string summary = "早先确认过:nginx 的 upstream 超时是 60s";
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Bulk(300)),
            new(ChatRole.User, summary),
            new(ChatRole.User, "看一下错误日志"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_remote_file", new Dictionary<string, object?> { ["path"] = "/var/log/app/error.log" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", Bulk(2000))]),
            new(ChatRole.Assistant, "找到了,是连接池耗尽。")
        ];
        var options = new ChatOptions
        {
            Tools = [Tool("read_remote_file", "Read a remote file"), Tool("github_search_code", "Search code"), Tool("github_list_issues", "List issues")]
        };

        var b = ContextBreakdown.Analyze(messages, options, summary, ServerOf);

        Assert.IsGreaterThanOrEqualTo(300, b.System, "系统提示词按 system 消息算");
        Assert.IsGreaterThan(0, b.Summary, "与对话摘要同文的那条 user 消息记作摘要,不算消息");
        Assert.HasCount(1, b.BuiltinTools);
        Assert.AreEqual("read_remote_file", b.BuiltinTools[0].Name);
        Assert.HasCount(1, b.McpServers);
        Assert.AreEqual("github", b.McpServers[0].Name);
        Assert.HasCount(2, b.McpServers[0].Tools);
        Assert.AreEqual(0, b.McpServers[0].Calls, "请求里没调用过 github 的工具");
        Assert.AreEqual(4, b.MessageCount, "system 与摘要那条不计入消息条数");
        Assert.IsGreaterThanOrEqualTo(2000, b.Parts[ContextPart.ToolResult]);
        Assert.IsTrue(b.Parts.ContainsKey(ContextPart.User));
        Assert.IsTrue(b.Parts.ContainsKey(ContextPart.Assistant));
        Assert.IsTrue(b.Parts.ContainsKey(ContextPart.ToolCall));
        Assert.AreEqual(b.System + b.Summary + b.Tools + b.Mcp + b.Messages, b.Total);
        Assert.AreEqual(ContextBuilder.Estimate(messages), b.System + b.Summary + b.Messages,
            "消息部分的合计与装配器的估算同一口径(含每条的固定开销)");
    }

    /// <summary>占用最大的那段要说清楚是什么:工具结果写成「工具名 · 第一个参数」,不是一个调用 id。</summary>
    [TestMethod]
    public void Largest_NamesAToolResult_ByItsCallAndFirstArgument()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "查一下"),
            new(ChatRole.Assistant, [new FunctionCallContent("c9", "run_command", new Dictionary<string, object?> { ["command"] = "journalctl -u nginx -n 400\nextra" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c9", Bulk(5000))])
        ];

        var b = ContextBreakdown.Analyze(messages, null, "", _ => null);

        Assert.AreEqual(ContextPart.ToolResult, b.Largest[0].Part);
        Assert.AreEqual("run_command · journalctl -u nginx -n 400", b.Largest[0].Label, "参数只取第一行");
    }

    [TestMethod]
    public void McpCalls_AreCountedPerServer()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "搜一下"),
            new(ChatRole.Assistant, [new FunctionCallContent("a", "github_search_code", null), new FunctionCallContent("b", "github_search_code", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("a", "1"), new FunctionResultContent("b", "2")])
        ];
        var options = new ChatOptions { Tools = [Tool("github_search_code", "Search code")] };

        var b = ContextBreakdown.Analyze(messages, options, "", ServerOf);

        Assert.AreEqual(2, b.McpServers.Single().Calls);
    }

    /// <summary>运行内压缩折出来的进度摘要块并在最后一条 user 消息里,也要记作摘要。</summary>
    [TestMethod]
    public void RunProgressSummary_CountsAsSummary()
    {
        string block = RunContextCompactor.SummaryBlock(Bulk(400));
        List<ChatMessage> messages = [new(ChatRole.User, [new TextContent("继续"), new TextContent(block)])];

        var b = ContextBreakdown.Analyze(messages, null, "", _ => null);

        Assert.IsGreaterThanOrEqualTo(400, b.Summary);
        Assert.IsLessThan(50, b.Parts.GetValueOrDefault(ContextPart.User));
    }

    /// <summary>系统提示词被挪到 Instructions(不收 system 角色的端点)时照样算进系统提示词。</summary>
    [TestMethod]
    public void Instructions_CountAsSystemPrompt()
    {
        var b = ContextBreakdown.Analyze([new(ChatRole.User, "hi")], new ChatOptions { Instructions = Bulk(500) }, "", _ => null);

        Assert.IsGreaterThanOrEqualTo(500, b.System);
    }

    /// <summary>工具定义按名字 + 说明 + 参数 Schema 估:参数越多越占。</summary>
    [TestMethod]
    public void ToolEstimate_IncludesTheParameterSchema()
    {
        AIFunction small = AIFunctionFactory.Create(() => "", "t", "d");
        AIFunction large = AIFunctionFactory.Create((string path, int offset, int limit, bool follow) => "", "t", "d");

        Assert.IsGreaterThan(ContextBreakdown.EstimateTool(small), ContextBreakdown.EstimateTool(large));
    }

    /// <summary>
    /// 面板按报告值缩放各类:合计必须等于报告值,而只有几个 token 的消息不能被舍入误差抹掉
    /// (工具定义很大、消息只有一句 "hi" 时,缩放系数很小,曾把消息那一行整行吃掉)。
    /// </summary>
    [TestMethod]
    public void Scaling_KeepsTinyMessages_AndSumsToTheReportedTotal()
    {
        List<ChatMessage> messages = [new(ChatRole.System, Bulk(900)), new(ChatRole.User, "hi")];
        var options = new ChatOptions { Tools = [.. Enumerable.Range(0, 30).Select(i => Tool($"tool_{i}", Bulk(120)))] };
        var b = ContextBreakdown.Analyze(messages, options, "", _ => null);

        var usage = new Ui.ContextUsage(Figures(contextTokens: 1000, window: 10_000, b));

        Assert.AreEqual(1000, usage.Slices.Sum(s => s.Tokens));
        Assert.Contains(s => s.Slice == Ui.ContextSlice.Messages && s.Tokens > 0, usage.Slices);
    }

    /// <summary>生成中:拆分不缩放,多出来的(正在写的回复)全记进消息。</summary>
    [TestMethod]
    public void Streaming_PutsTheInFlightReplyIntoMessages()
    {
        List<ChatMessage> messages = [new(ChatRole.System, Bulk(100)), new(ChatRole.User, Bulk(100))];
        var b = ContextBreakdown.Analyze(messages, null, "", _ => null);

        var usage = new Ui.ContextUsage(Figures(contextTokens: b.Total + 500, window: 10_000, b, streaming: true));

        Assert.AreEqual(b.System, usage.Slices.Single(s => s.Slice == Ui.ContextSlice.System).Tokens);
        Assert.AreEqual(b.Messages + 500, usage.Slices.Single(s => s.Slice == Ui.ContextSlice.Messages).Tokens);
    }

    private static Ui.UsageFigures Figures(long contextTokens, int window, ContextBreakdown b, bool streaming = false)
        => new(streaming, contextTokens, window, "", contextTokens, 0, 0, contextTokens, 0, 0, 0, 0, 0, null, 0, false, b);

    [TestMethod]
    public void TriggerTokens_MatchesShouldCompact()
    {
        Assert.AreEqual(138_000, ContextCompactor.TriggerTokens(200_000, 16_000));
        Assert.AreEqual(0, ContextCompactor.TriggerTokens(0, 16_000), "窗口未知就没有压缩线");
    }
}
