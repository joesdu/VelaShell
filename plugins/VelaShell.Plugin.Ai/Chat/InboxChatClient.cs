using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 团队成员的收件箱:每次请求模型之前取走队友 / 主控发来的新消息,追加到这一次要发的上下文末尾。
/// </summary>
/// <remarks>
/// 与 <see cref="SteeringChatClient" /> 同一个道理:它垫在函数调用循环<b>里面</b>,循环每一步整份重发上下文,
/// 而那份历史是循环自己攒的、不知道这里加过东西 —— 所以送达过的消息每一步都要跟着重发,否则下一步就丢了。
/// 末条也是 user 时并进去(Anthropic 要求角色交替,且 tool_result 块必须排在内容最前面,所以追加在后)。
/// </remarks>
/// <param name="inner">真正发请求的客户端。</param>
/// <param name="drain">取走新消息(已经框好的文本)。</param>
internal sealed class InboxChatClient(IChatClient inner, Func<IReadOnlyList<string>> drain) : DelegatingChatClient(inner)
{
    private readonly Lock _gate = new();
    private readonly List<string> _delivered = [];

    /// <summary>送达过的消息条数(测试用)。</summary>
    internal int DeliveredCount { get { lock (_gate) return _delivered.Count; } }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Compose(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(Compose(messages), options, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    internal List<ChatMessage> Compose(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> outgoing = [.. messages];
        string[] carried;
        lock (_gate)
        {
            _delivered.AddRange(drain());
            carried = [.. _delivered];
        }
        foreach (string text in carried)
        {
            if (outgoing.Count > 0 && outgoing[^1].Role == ChatRole.User)
            {
                outgoing[^1] = new ChatMessage(ChatRole.User, [.. outgoing[^1].Contents, new TextContent(text)]);
            }
            else
            {
                outgoing.Add(new ChatMessage(ChatRole.User, text));
            }
        }
        return outgoing;
    }
}
