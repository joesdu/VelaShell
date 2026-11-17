using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>消息里一段内容属于哪一类(用量明细「消息」一组下面的分行)。</summary>
public enum ContextPart
{
    /// <summary>用户发的(含随消息附上的文件正文)。</summary>
    User,

    /// <summary>助手的回复与思考。</summary>
    Assistant,

    /// <summary>助手发起的工具调用(名字 + 参数)。</summary>
    ToolCall,

    /// <summary>工具返回的结果。</summary>
    ToolResult,

    /// <summary>图片输入(按固定额度估)。</summary>
    Image
}

/// <summary>一个有名字的占用项(一个工具定义)。</summary>
public sealed record ContextEntry(string Name, int Tokens);

/// <summary>占用最多的单段内容。</summary>
/// <param name="Part">哪一类。</param>
/// <param name="Label">给人看的简述(工具名 · 首个参数 / 消息首行)。</param>
/// <param name="Tokens">估算 token。</param>
public sealed record ContextLargeItem(ContextPart Part, string Label, int Tokens);

/// <summary>一台 MCP 服务器带进请求的工具定义。</summary>
/// <param name="Name">服务器显示名。</param>
/// <param name="Tools">它的每个工具(按占用从大到小)。</param>
/// <param name="Calls">请求里出现过几次对它的工具调用。</param>
public sealed record McpServerShare(string Name, IReadOnlyList<ContextEntry> Tools, int Calls)
{
    /// <summary>这台服务器的工具定义合计。</summary>
    public int Tokens { get; } = Tools.Sum(t => t.Tokens);
}

/// <summary>
/// 一次真实请求的上下文拆分:系统提示词、工具定义(内置 / MCP)、压缩摘要、消息各占多少。
/// </summary>
/// <remarks>
/// <para>
/// 全部是本地估算(<see cref="ContextBuilder.Estimate(string)" /> 的口径),只求量级与比例对。
/// 面板拿供应商报告的输入总量按比例缩放这些数,所以合计与报告值一致、分项是估的。
/// </para>
/// <para>
/// 工具定义也算进来:各家协议都把工具的名字、说明与参数 JSON Schema 计入输入 token,
/// 而它们往往是开场就占掉十几 k 的那一块 —— 用户最想知道的正是这个。
/// </para>
/// </remarks>
public sealed record ContextBreakdown(
    int System,
    int Summary,
    IReadOnlyList<ContextEntry> BuiltinTools,
    IReadOnlyList<McpServerShare> McpServers,
    IReadOnlyDictionary<ContextPart, int> Parts,
    int MessageCount,
    IReadOnlyList<ContextLargeItem> Largest)
{
    /// <summary>每个工具定义的固定开销(类型、包裹结构),与消息的固定开销同一量级。</summary>
    internal const int ToolOverhead = 8;

    /// <summary>运行内压缩折出的进度摘要块的标记(见 <see cref="RunContextCompactor.SummaryBlock" />)。</summary>
    private const string RunSummaryTag = "<run-progress-summary>";

    /// <summary>内置工具定义合计。</summary>
    public int Tools => BuiltinTools.Sum(t => t.Tokens);

    /// <summary>MCP 工具定义合计。</summary>
    public int Mcp => McpServers.Sum(s => s.Tokens);

    /// <summary>消息合计。</summary>
    public int Messages => Parts.Values.Sum();

    /// <summary>估算总量。</summary>
    public int Total => System + Summary + Tools + Mcp + Messages;

    /// <summary>拆一次请求。纯函数,不改动入参。</summary>
    /// <param name="messages">真正要发出去的消息序列(系统提示词在内)。</param>
    /// <param name="options">请求参数;工具定义与挪出去的系统提示词(<see cref="ChatOptions.Instructions" />)从这里取。</param>
    /// <param name="summary">对话级压缩摘要;请求里与它同文的那条 user 消息记作摘要。</param>
    /// <param name="mcpServerOf">某个工具来自哪台 MCP 服务器;内置工具返回 null。</param>
    /// <param name="largest">保留占用最大的几段内容。</param>
    public static ContextBreakdown Analyze(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string summary,
        Func<AITool, string?> mcpServerOf, int largest = 6)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(mcpServerOf);

        // ── 工具定义 ──
        var builtin = new List<ContextEntry>();
        var servers = new Dictionary<string, List<ContextEntry>>(StringComparer.Ordinal);
        var serverOfTool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AITool tool in options?.Tools ?? [])
        {
            var entry = new ContextEntry(tool.Name, EstimateTool(tool));
            if (mcpServerOf(tool) is { } server)
            {
                if (!servers.TryGetValue(server, out List<ContextEntry>? list)) servers[server] = list = [];
                list.Add(entry);
                serverOfTool.TryAdd(tool.Name, server);
            }
            else
            {
                builtin.Add(entry);
            }
        }

        // ── 消息 ──
        int system = ContextBuilder.Estimate(options?.Instructions);
        int summaryTokens = 0;
        int messageCount = 0;
        var parts = new Dictionary<ContextPart, int>();
        var items = new List<ContextLargeItem>();
        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var callsPerServer = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ChatMessage message in messages)
        {
            foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!string.IsNullOrEmpty(call.CallId)) calls[call.CallId] = call;
                if (serverOfTool.TryGetValue(call.Name, out string? server))
                    callsPerServer[server] = callsPerServer.GetValueOrDefault(server) + 1;
            }
        }
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                system += ContextBuilder.Estimate(message);
                continue;
            }
            if (summary.Length > 0 && message.Role == ChatRole.User && message.Text == summary)
            {
                summaryTokens += ContextBuilder.Estimate(message);
                continue;
            }
            messageCount++;
            int overhead = ContextBuilder.MessageOverhead;
            foreach (AIContent content in message.Contents)
            {
                int tokens = ContextBuilder.Estimate(content);
                if (content is TextContent text && text.Text.TrimStart().StartsWith(RunSummaryTag, StringComparison.Ordinal))
                {
                    summaryTokens += tokens;
                    continue;
                }
                ContextPart part = PartOf(message.Role, content);
                parts[part] = parts.GetValueOrDefault(part) + tokens + overhead;
                overhead = 0; // 消息的固定开销只记一次,记在它的第一段上
                items.Add(new ContextLargeItem(part, Describe(content, calls), tokens));
            }
            if (overhead > 0)
            {
                // 没有内容的空消息:开销仍按角色记一份
                ContextPart part = message.Role == ChatRole.User ? ContextPart.User : ContextPart.Assistant;
                parts[part] = parts.GetValueOrDefault(part) + overhead;
            }
        }

        return new ContextBreakdown(
            system,
            summaryTokens,
            [.. builtin.OrderByDescending(t => t.Tokens).ThenBy(t => t.Name, StringComparer.Ordinal)],
            [
                .. servers.Select(s => new McpServerShare(s.Key,
                        [.. s.Value.OrderByDescending(t => t.Tokens).ThenBy(t => t.Name, StringComparer.Ordinal)],
                        callsPerServer.GetValueOrDefault(s.Key)))
                    .OrderByDescending(s => s.Tokens)
            ],
            parts,
            messageCount,
            [.. items.Where(i => i.Tokens > 0).OrderByDescending(i => i.Tokens).Take(Math.Max(0, largest))]);
    }

    /// <summary>一个工具定义的 token 估算:名字 + 说明 + 参数 Schema。</summary>
    internal static int EstimateTool(AITool tool)
    {
        int total = ToolOverhead + ContextBuilder.Estimate(tool.Name) + ContextBuilder.Estimate(tool.Description);
        if (tool is AIFunctionDeclaration function && function.JsonSchema.ValueKind != JsonValueKind.Undefined)
        {
            total += ContextBuilder.Estimate(function.JsonSchema.GetRawText());
        }
        return total;
    }

    private static ContextPart PartOf(ChatRole role, AIContent content) => content switch
    {
        FunctionResultContent => ContextPart.ToolResult,
        FunctionCallContent => ContextPart.ToolCall,
        DataContent => ContextPart.Image,
        _ when role == ChatRole.User => ContextPart.User,
        _ when role == ChatRole.Tool => ContextPart.ToolResult,
        _ => ContextPart.Assistant
    };

    /// <summary>给一段内容一句简述:工具结果写「工具名 · 第一个参数」,文字取首个非空行。</summary>
    private static string Describe(AIContent content, Dictionary<string, FunctionCallContent> calls) => content switch
    {
        FunctionResultContent result when calls.TryGetValue(result.CallId, out FunctionCallContent? call) => DescribeCall(call),
        FunctionResultContent result => result.CallId,
        FunctionCallContent call => DescribeCall(call),
        DataContent data => data.Name ?? data.MediaType,
        TextContent text => FirstLine(text.Text),
        TextReasoningContent reasoning => FirstLine(reasoning.Text),
        _ => content.GetType().Name
    };

    private static string DescribeCall(FunctionCallContent call)
    {
        string? first = call.Arguments?.Values.Select(v => v?.ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return first is null ? call.Name : $"{call.Name} · {FirstLine(first)}";
    }

    private static string FirstLine(string? text)
    {
        const int max = 60;
        string line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
