using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>一次请求的上下文装配结果。</summary>
/// <param name="Messages">最终发出去的消息(系统提示词 + 摘要 + 裁剪并规整过的历史)。</param>
/// <param name="DroppedMessages">
/// 为放进窗口而<b>直接丢掉</b>的早期消息条数。这是压缩失败时的兜底手段 ——
/// 正常路径上早期内容会被折成摘要而不是丢弃(见 <see cref="ContextCompactor" />)。
/// </param>
/// <param name="EstimatedTokens">装配后的输入 token 估算值。</param>
public readonly record struct RequestContext(
    List<ChatMessage> Messages,
    int DroppedMessages,
    int EstimatedTokens);

/// <summary>
/// 把"系统提示词 + 对话历史"装配成一次请求真正要发的消息序列:先按上下文窗口裁掉最早的几轮,
/// 再把相邻同角色的消息并成一条。纯函数、不碰 UI,所以能直接单测。
/// </summary>
public static class ContextBuilder
{
    /// <summary>每条消息的固定开销(角色、分隔符之类),各家协议都有,量级一致。</summary>
    private const int PerMessageOverhead = 4;

    /// <summary>
    /// 装配请求。窗口未知时不裁剪历史；已知时按预算裁剪并规整工具轮次。
    /// </summary>

    public static RequestContext Build(string systemPrompt, IReadOnlyList<ChatMessage> history,
        int windowTokens, int reserveTokens, string summary = "", int summarizedThrough = 0)
    {
        ArgumentNullException.ThrowIfNull(history);
        ContextCompilationInput<ChatMessage> input = new(
            systemPrompt, history, windowTokens, reserveTokens, summary, summarizedThrough);
        ContextCompilationResult<ChatMessage> result = PromptContextCompiler.CompileContext(input, ChatMessageAdapter.Instance);
        return new RequestContext([.. result.Messages], result.DroppedMessages, result.EstimatedTokens);
    }

    private sealed class ChatMessageAdapter : IContextMessageAdapter<ChatMessage>
    {
        public static ChatMessageAdapter Instance { get; } = new();

        public ContextMessageInfo Describe(ChatMessage message)
        {
            List<ContextToolPart> tools = [];
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        tools.Add(new(ContextToolPartKind.Call, call.CallId));
                        break;
                    case FunctionResultContent result:
                        tools.Add(new(ContextToolPartKind.Result, result.CallId));
                        break;
                }
            }

            ContextMessageRole role = message.Role == ChatRole.System ? ContextMessageRole.System
                : message.Role == ChatRole.User ? ContextMessageRole.User
                : message.Role == ChatRole.Assistant ? ContextMessageRole.Assistant
                : message.Role == ChatRole.Tool ? ContextMessageRole.Tool
                : ContextMessageRole.Other;
            return new ContextMessageInfo(role, message.Role.Value, Estimate(message), tools);
        }

        public ChatMessage CreateSystem(string text) => new(ChatRole.System, text);

        public ChatMessage CreateUser(string text) => new(ChatRole.User, text);

        public ChatMessage Merge(ChatMessage first, ChatMessage second)
            => new(first.Role, [.. first.Contents, .. second.Contents]);

        public ContextMessageNormalization<ChatMessage> RemoveOrphanedToolParts(ChatMessage message,
            IReadOnlySet<string> callIds, IReadOnlySet<string> resultIds)
        {
            int removed = 0;
            List<AIContent> retained = [];
            foreach (AIContent content in message.Contents)
            {
                bool orphaned = content switch
                {
                    FunctionCallContent call => !resultIds.Contains(call.CallId),
                    FunctionResultContent result => !callIds.Contains(result.CallId),
                    _ => false
                };
                if (orphaned)
                {
                    removed++;
                }
                else
                {
                    retained.Add(content);
                }
            }

            return removed == 0
                ? new ContextMessageNormalization<ChatMessage>(message, true, 0)
                : new ContextMessageNormalization<ChatMessage>(
                    new ChatMessage(message.Role, retained), retained.Count > 0, removed);
        }
    }

    /// <summary>
    /// 把打头那条 <c>system</c> 消息从列表里摘出来,返回它的文本。
    /// </summary>
    /// <remarks>
    /// 给"不收 system 角色"的端点用(ChatGPT 的 Codex 后端就是,照发会 400)。
    /// 摘出来的内容由调用方放进 <c>ChatOptions.Instructions</c> —— 一个字都不会少,只是换了位置。
    /// <para>
    /// <b>只摘打头那条</b>:装配出来的列表里 system 必定在最前(见 <see cref="Build" />),
    /// 而后面若还有 system,那是历史里带来的、模型自己也认得的内容,不该被悄悄吞掉。
    /// </para>
    /// </remarks>
    /// <param name="messages">装配好的消息列表;命中时<b>就地修改</b>。</param>
    /// <returns>系统提示词;列表不以 system 打头时返回 null。</returns>
    public static string? MoveSystemPromptOut(List<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
        {
            return null;
        }
        string text = messages[0].Text;
        messages.RemoveAt(0);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>整段消息的 token 估算。</summary>
    public static int Estimate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int total = 0;
        foreach (ChatMessage message in messages)
        {
            total += Estimate(message);
        }
        return total;
    }

    private static int Estimate(IReadOnlyList<ChatMessage> messages, int from)
    {
        int total = 0;
        for (int i = from; i < messages.Count; i++)
        {
            total += Estimate(messages[i]);
        }
        return total;
    }

    /// <summary>单条消息的 token 估算(含固定开销)。</summary>
    public static int Estimate(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int total = PerMessageOverhead;
        foreach (AIContent content in message.Contents)
        {
            total += Estimate(content);
        }
        return total;
    }

    /// <summary>每条消息的固定开销(供按内容拆分的统计把它记回某一类)。</summary>
    internal static int MessageOverhead => PerMessageOverhead;

    /// <summary>单段内容的 token 估算(不含消息的固定开销)。</summary>
    internal static int Estimate(AIContent content) => content switch
    {
        TextContent text => Estimate(text.Text),
        TextReasoningContent reasoning => Estimate(reasoning.Text),
        FunctionCallContent call => Estimate(call.Name) + EstimateArguments(call),
        FunctionResultContent result => Estimate(result.Result?.ToString()),
        // 图片按一张中等尺寸图的常见量级算;精确值各家不同,这里只求量级对
        DataContent => 800,
        _ => 8
    };

    private static int EstimateArguments(FunctionCallContent call)
    {
        if (call.Arguments is not { Count: > 0 } arguments)
        {
            return 0;
        }
        int total = 0;
        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            total += Estimate(pair.Key) + Estimate(pair.Value?.ToString());
        }
        return total;
    }

    /// <summary>
    /// 文本的 token 估算。没有 tokenizer,按字符类别近似:
    /// ASCII 约 4 字符 1 token,中日韩等非 ASCII 约 1.5 字符 1 token。
    /// 只用来决定"要不要裁",偏差一两成不影响判断。
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int ascii = 0, wide = 0;
        foreach (char c in text)
        {
            if (char.IsAscii(c))
            {
                ascii++;
            }
            else
            {
                wide++;
            }
        }
        return (int)((ascii / 4.0) + (wide / 1.5)) + 1;
    }
}
