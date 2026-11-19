using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 插话通道:把 <see cref="SteeringQueue" /> 里排着的用户消息,赶在模型<b>下一步</b>之前
/// 追加到即将发出的那份上下文末尾。
/// </summary>
/// <remarks>
/// <para>
/// 它必须垫在函数调用循环(<c>FunctionInvokingChatClient</c>)<b>里面</b>:那个循环一轮里会向模型
/// 发起多次请求(每调用一次工具就再问一次),每一次都要穿过这一层。于是用户在 Agent 干活时补的
/// 一句"顺便把日志也看一下",能在它读完当前工具结果、决定下一步<b>之前</b>进到上下文里 ——
/// 这正是 ClaudeCode 那种"边跑边补"的手感。垫在循环外面则只有整轮开头那一次机会,补充信息
/// 要等下一轮才生效。
/// </para>
/// <para>
/// 不打断进行中的那一步:已经发出去的请求照常读完,工具照常执行完。插话是"下一步的输入",
/// 不是中断信号 —— 中断有停止按钮。
/// </para>
/// </remarks>
/// <param name="innerClient">被包住的客户端(真正发请求的那个)。</param>
/// <param name="queue">面板级的插话队列。</param>
/// <param name="onDelivered">
/// 有插话真的被送进请求时回调(<b>在发请求的那个线程上</b>,通常不是 UI 线程)。
/// 面板据此把它补进对话历史、在回复里挂一张卡、并把输入框上方那枚排队芯片撤掉。
/// </param>
/// <param name="beforeRequest">每次真实请求(含工具结果续流)前复验本轮固定凭据;失败不出境。</param>
/// <param name="onRequest">
/// 每次真实请求组装好、即将发出时回调(插话已并入;<b>在发请求的那个线程上</b>)。
/// 用量明细据此拆分上下文;回调抛出的异常不影响请求。
/// </param>
internal sealed class SteeringChatClient(
    IChatClient innerClient,
    SteeringQueue queue,
    Action? onDelivered = null,
    Func<CancellationToken, Task>? beforeRequest = null,
    Action<IReadOnlyList<ChatMessage>, ChatOptions?>? onRequest = null) : IChatClient
{
    private readonly Lock _gate = new();
    private readonly record struct Delivery(int Boundary, IReadOnlyList<SteeringMessage> Items);

    private readonly List<Delivery> _deliveries = [];
    private string?[] _previousSteps = [];

    internal IChatClient RequestClient { get; private set; } = innerClient;

    /// <summary>同账号续期只替换真实请求客户端,保留工具循环与已送达插话,不重放副作用。</summary>
    internal void ReplaceInnerClient(IChatClient client)
    {
        IChatClient previous = RequestClient;
        RequestClient = client;
        previous.Dispose();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : RequestClient.GetService(serviceType, serviceKey);
    }

    public void Dispose() => RequestClient.Dispose();

    /// <summary>本轮已经送进请求的插话(按送达先后)。</summary>
    public IReadOnlyList<SteeringMessage> Delivered
    {
        get
        {
            lock (_gate)
            {
                return [.. _deliveries.SelectMany(delivery => delivery.Items)];
            }
        }
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (beforeRequest is not null) await beforeRequest(cancellationToken).ConfigureAwait(false);
        return await RequestClient.GetResponseAsync(Observe(Compose(messages), options), options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (beforeRequest is not null) await beforeRequest(cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in RequestClient.GetStreamingResponseAsync(Observe(Compose(messages), options), options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>把即将发出的这一份交给观察者;观察者出错不连累请求(那只是统计)。</summary>
    private List<ChatMessage> Observe(List<ChatMessage> outgoing, ChatOptions? options)
    {
        try
        {
            onRequest?.Invoke(outgoing, options);
        }
        catch (Exception)
        {
            // 用量明细算不出来就不显示,请求照发
        }
        return outgoing;
    }

    /// <summary>把队列里的插话并进这一次要发的消息序列。</summary>
    private List<ChatMessage> Compose(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> history = [.. messages];
        IReadOnlyList<SteeringMessage> fresh;
        Delivery[] carried;
        lock (_gate)
        {
            fresh = queue.DrainAll();
            if (_deliveries.Count == 0 && fresh.Count == 0) return history;
            RebaseDeliveries([.. history.Select(StepKey)]);
            if (fresh.Count > 0) _deliveries.Add(new Delivery(history.Count, fresh));
            carried = [.. _deliveries];
        }

        List<ChatMessage> outgoing = [];
        int deliveryIndex = 0;
        for (int i = 0; i <= history.Count; i++)
        {
            bool hasNotes = deliveryIndex < carried.Length && carried[deliveryIndex].Boundary == i;
            while (deliveryIndex < carried.Length && carried[deliveryIndex].Boundary == i)
            {
                foreach (SteeringMessage item in carried[deliveryIndex++].Items) Append(outgoing, item.Message);
            }
            if (i == history.Count) break;
            if (hasNotes && history[i].Role == ChatRole.User) Append(outgoing, history[i]);
            else outgoing.Add(history[i]);
        }
        if (fresh.Count > 0) onDelivered?.Invoke();
        return outgoing;
    }

    // 批次续接会重建消息;工具结果还可能清理或重新分组。结果永远跟在发起调用的步骤后面。
    // 压缩会把进度摘要追加到起始 user 的末尾,它仍是同一条用户消息。
    private static string? StepKey(ChatMessage message)
    {
        if (message.Role == ChatRole.Tool || message.Contents.Any(c => c is FunctionResultContent)) return null;
        if (message.Role == ChatRole.User)
            return "user:" + (message.Contents.FirstOrDefault() as TextContent)?.Text;
        return RunContextCompactor.Key(message);
    }

    // 找到送达时还没有的第一步。被折进摘要的旧步骤可以跳过;因此即使原来的工具结果已消失,
    // 插话也只会放在保留下来的新步骤之前,不会重新落到末尾变成模型眼里的新指令。
    private void RebaseDeliveries(string?[] current)
    {
        int previousIndex = 0, currentIndex = 0;
        for (int i = 0; i < _deliveries.Count; i++)
        {
            Delivery delivery = _deliveries[i];
            while (currentIndex < current.Length)
            {
                if (current[currentIndex] is { } key)
                {
                    int match = Array.IndexOf(_previousSteps, key, previousIndex, delivery.Boundary - previousIndex);
                    if (match < 0) break;
                    previousIndex = match + 1;
                }
                currentIndex++;
            }
            _deliveries[i] = delivery with { Boundary = currentIndex };
        }
        _previousSteps = current;
    }

    /// <summary>
    /// 追加一条插话。末条<b>也是</b> user 时并进去而不是另起一条 ——
    /// Anthropic 协议要求角色交替,而适配器不会替你合并(与 <c>ContextBuilder</c> 同一个理由)。
    /// </summary>
    /// <remarks>
    /// 并进末条时插话排在原有内容<b>之后</b>:那条 user 往往装的是工具结果,
    /// 而 Anthropic 要求 <c>tool_result</c> 块排在消息内容的最前面,插到前面会直接被拒。
    /// </remarks>
    private static void Append(List<ChatMessage> sink, ChatMessage message)
    {
        if (sink.Count > 0 && sink[^1].Role == ChatRole.User)
        {
            sink[^1] = new ChatMessage(ChatRole.User, [.. sink[^1].Contents, .. message.Contents]);
            return;
        }
        sink.Add(message);
    }
}
