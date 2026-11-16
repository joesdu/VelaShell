using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>运行中压缩了一次上下文:清掉了几份旧工具输出、折了几条消息进摘要。</summary>
/// <param name="ClearedResults">本次新清掉的旧工具输出份数。</param>
/// <param name="FoldedMessages">本次新折进摘要的消息条数。</param>
/// <param name="Summary">折叠后的进度摘要;只清理输出时为 null。</param>
/// <param name="UsedBefore">压缩前估算的上下文占用(token)。</param>
/// <param name="UsedAfter">压缩后估算的上下文占用(token)。</param>
/// <param name="Budget">可用于输入的窗口(token)。</param>
internal readonly record struct RunCompaction(int ClearedResults, int FoldedMessages, string? Summary,
    int UsedBefore, int UsedAfter, int Budget);

/// <summary>
/// 一轮 Agent 运行<b>之中</b>的上下文管理:每次请求模型之前看窗口还剩多少,逐级腾地方。
/// </summary>
/// <remarks>
/// <para>
/// 发送前的 <see cref="ContextCompactor" /> 只在用户发消息时压一次;一轮里面每一步都把全部工具输出原样带上,
/// 跑几十步就撑爆窗口,供应商回 400,整轮作废 —— 长任务跑不了几个小时的头号原因。
/// </para>
/// <para>
/// 按占用比例分三档(做法参照 Claude Code 的 microcompact + autocompact):
/// <list type="number">
/// <item><see cref="ClearRatio" /> 起:较早的大块工具输出换成一句占位(调用本身和参数都留着,配对不拆),只留最近 <see cref="KeepRecentResults" /> 份;</item>
/// <item><see cref="AggressiveRatio" /> 起:除最近 <see cref="KeepMinimumResults" /> 份外全部清掉;</item>
/// <item><see cref="FoldRatio" /> 起:把这一轮较早的步骤交给模型写成「进度摘要」,并进本轮起始那条用户消息,原文不再发送。</item>
/// </list>
/// 比例是相对「窗口 − 输出预留」算的,所以小窗口的本地模型更早开始清,大窗口的则几乎碰不到。
/// </para>
/// <para>
/// <b>不改原始消息</b>:函数调用循环与检查点拿着的仍是完整列表,这里每次按记下的状态(清过哪些调用、折过哪些消息)
/// 重新生成一份发出去。状态只增不减,同一前缀每次生成的结果相同,供应商的提示词缓存在两次压缩之间照样命中。
/// 消息按调用 id / 正文认,不按对象引用 —— 批次续接时协调器会重建消息对象。
/// </para>
/// <para>
/// 估算是按字符估的(<see cref="ContextBuilder.Estimate(IEnumerable{ChatMessage})" />),供应商报告了真实输入用量后
/// 用二者的比值校准(只往大里校,估小了才会出事)。
/// </para>
/// </remarks>
/// <param name="inner">下游客户端。</param>
/// <param name="windowTokens">模型输入窗口;不大于 0 表示未知,不做任何处理。</param>
/// <param name="reserveTokens">输出预留。</param>
/// <param name="summarize">把「旧摘要 + 要折的消息」写成新摘要;失败时返回 null,本次只做清理。</param>
/// <param name="compacted">压缩了一次时回调(任意线程)。</param>
/// <param name="compacting">开始 / 结束写摘要时回调(任意线程),界面据此显示状态。</param>
internal sealed class RunContextCompactor(
    IChatClient inner,
    int windowTokens,
    int reserveTokens,
    Func<string, IReadOnlyList<ChatMessage>, CancellationToken, Task<string?>> summarize,
    Action<RunCompaction>? compacted = null,
    Action<bool>? compacting = null) : DelegatingChatClient(inner)
{
    /// <summary>占用到这个比例起清理较早的大块工具输出。</summary>
    internal const double ClearRatio = 0.5;

    /// <summary>占用到这个比例起只留最近几份工具输出。</summary>
    internal const double AggressiveRatio = 0.7;

    /// <summary>占用到这个比例起把较早的步骤折成摘要。</summary>
    internal const double FoldRatio = 0.85;

    /// <summary>折叠后原文保留的目标比例:留够最近几步,又不至于压完马上再压。</summary>
    internal const double KeepAfterFoldRatio = 0.3;

    internal const int KeepRecentResults = 12;
    internal const int KeepMinimumResults = 4;

    /// <summary>第一档只清这么大(估算 token)以上的输出:小结果清了也省不出地方,反倒丢信息。</summary>
    private const int LargeResultTokens = 400;

    /// <summary>折叠后最少保留原文的步数(一步 = 一条 assistant + 它的工具结果)。</summary>
    private const int KeepMinimumSteps = 2;

    internal const string ClearedPlaceholder =
        "[Earlier tool output cleared to save context. The call above did run; re-run it only if you need the output again.]";

    /// <summary>本轮起始的消息条数(系统提示词、历史与这次的用户消息):这一段不清不折。装配完上下文后由入口设置;没设之前什么都不做。</summary>
    internal int PrefixCount { get; set; } = int.MaxValue;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _cleared = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _folded = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _foldLock = new(1, 1);
    private string _summary = "";
    private double _calibration = 1.0;
    private int _lastEstimate;

    /// <summary>当前的进度摘要(没有折叠过为空)。</summary>
    internal string Summary { get { lock (_gate) return _summary; } }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> outgoing = await PrepareAsync([.. messages], cancellationToken).ConfigureAwait(false);
        ChatResponse response = await base.GetResponseAsync(outgoing, options, cancellationToken).ConfigureAwait(false);
        Calibrate(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> outgoing = await PrepareAsync([.. messages], cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(outgoing, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is UsageContent usage) Calibrate(usage.Details);
            }
            yield return update;
        }
    }

    /// <summary>按当前状态生成要发出去的列表,需要时再升一档。可单测。</summary>
    internal async Task<List<ChatMessage>> PrepareAsync(List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        int budget = windowTokens - Math.Max(0, reserveTokens);
        if (windowTokens <= 0 || budget <= 0 || messages.Count <= PrefixCount)
        {
            return messages;
        }
        List<ChatMessage> outgoing = Build(messages);
        int before = Measure(outgoing);
        if (before < budget * ClearRatio)
        {
            return outgoing;
        }

        int cleared = ClearResults(messages, before >= budget * AggressiveRatio ? KeepMinimumResults : KeepRecentResults,
            before >= budget * AggressiveRatio ? 0 : LargeResultTokens);
        if (cleared > 0)
        {
            outgoing = Build(messages);
        }
        int used = Measure(outgoing);

        int folded = 0;
        string? summary = null;
        if (used >= budget * FoldRatio)
        {
            (folded, summary) = await FoldAsync(messages, budget, cancellationToken).ConfigureAwait(false);
            if (folded > 0)
            {
                outgoing = Build(messages);
                used = Measure(outgoing);
            }
        }
        if (cleared > 0 || folded > 0)
        {
            compacted?.Invoke(new RunCompaction(cleared, folded, summary, before, used, budget));
        }
        return outgoing;
    }

    /// <summary>本轮部分(前缀之后)按状态改写:折过的不发,清过的换占位,摘要并进前缀最后一条用户消息。</summary>
    private List<ChatMessage> Build(List<ChatMessage> messages)
    {
        HashSet<string> cleared, folded;
        string summary;
        lock (_gate)
        {
            cleared = [.. _cleared];
            folded = [.. _folded];
            summary = _summary;
        }
        var result = new List<ChatMessage>(messages.Count);
        for (int i = 0; i < PrefixCount; i++) result.Add(messages[i]);
        if (summary.Length > 0)
        {
            string block = SummaryBlock(summary);
            if (result.Count > 0 && result[^1].Role == ChatRole.User)
            {
                ChatMessage last = result[^1];
                result[^1] = new ChatMessage(ChatRole.User, [.. last.Contents, new TextContent(block)])
                {
                    AuthorName = last.AuthorName,
                    AdditionalProperties = last.AdditionalProperties
                };
            }
            else
            {
                result.Add(new ChatMessage(ChatRole.User, block));
            }
        }
        for (int i = PrefixCount; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            if (folded.Count > 0 && folded.Contains(Key(message))) continue;
            if (cleared.Count > 0 && message.Contents.Any(c => c is FunctionResultContent r && cleared.Contains(r.CallId)))
            {
                message = new ChatMessage(message.Role, [.. message.Contents.Select(c =>
                    c is FunctionResultContent r && cleared.Contains(r.CallId)
                        ? new FunctionResultContent(r.CallId, ClearedPlaceholder)
                        : c)])
                {
                    AuthorName = message.AuthorName,
                    MessageId = message.MessageId
                };
            }
            result.Add(message);
        }
        return result;
    }

    /// <summary>清掉较早的工具输出,最近 <paramref name="keep" /> 份不动;返回新清掉的份数。</summary>
    private int ClearResults(List<ChatMessage> messages, int keep, int minTokens)
    {
        List<FunctionResultContent> results = [];
        for (int i = PrefixCount; i < messages.Count; i++)
        {
            results.AddRange(messages[i].Contents.OfType<FunctionResultContent>());
        }
        int count = 0;
        lock (_gate)
        {
            foreach (FunctionResultContent result in results.Take(Math.Max(0, results.Count - keep)))
            {
                if (_cleared.Contains(result.CallId) || _folded.Contains("r:" + result.CallId)) continue;
                if (ContextBuilder.Estimate(result.Result?.ToString()) < Math.Max(minTokens, 40)) continue;
                _cleared.Add(result.CallId);
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 把本轮较早的步骤折成摘要。切点只落在「一步」的开头(assistant 消息,前一条是工具结果或用户消息),
    /// 工具调用与它的结果不会被拆开;最近几步保持原文。
    /// </summary>
    private async Task<(int Folded, string? Summary)> FoldAsync(List<ChatMessage> messages, int budget,
        CancellationToken cancellationToken)
    {
        await _foldLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<int> live = [];
            HashSet<string> folded;
            lock (_gate) folded = [.. _folded];
            for (int i = PrefixCount; i < messages.Count; i++)
            {
                if (!folded.Contains(Key(messages[i]))) live.Add(i);
            }
            // 候选切点:每一步的开头
            List<int> boundaries = [];
            for (int k = 1; k < live.Count; k++)
            {
                ChatMessage message = messages[live[k]];
                ChatMessage previous = messages[live[k - 1]];
                if (message.Role == ChatRole.Assistant && previous.Role != ChatRole.Assistant)
                {
                    boundaries.Add(k);
                }
            }
            if (boundaries.Count <= KeepMinimumSteps) return (0, null);
            // 从后往前留原文,留到目标比例或只剩最少步数为止
            int keepBudget = (int)(budget * KeepAfterFoldRatio);
            int cut = boundaries[^KeepMinimumSteps];
            for (int b = boundaries.Count - KeepMinimumSteps - 1; b >= 0; b--)
            {
                int tail = Measure(live.Skip(boundaries[b]).Select(i => messages[i]));
                if (tail > keepBudget) break;
                cut = boundaries[b];
            }
            if (cut <= 0) return (0, null);
            List<ChatMessage> toFold = [.. live.Take(cut).Select(i => messages[i])];
            string previousSummary;
            lock (_gate) previousSummary = _summary;
            compacting?.Invoke(true);
            string? summary;
            try
            {
                summary = await summarize(previousSummary, toFold, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                summary = null; // 写不出摘要:这一档放弃,清理过的输出照样省下了地方
            }
            finally
            {
                compacting?.Invoke(false);
            }
            if (string.IsNullOrWhiteSpace(summary)) return (0, null);
            lock (_gate)
            {
                foreach (ChatMessage message in toFold) _folded.Add(Key(message));
                _summary = summary.Trim();
            }
            return (toFold.Count, summary.Trim());
        }
        finally
        {
            _foldLock.Release();
        }
    }

    private int Measure(IEnumerable<ChatMessage> messages)
    {
        int estimate = ContextBuilder.Estimate(messages);
        double calibration;
        lock (_gate)
        {
            _lastEstimate = estimate;
            calibration = _calibration;
        }
        return (int)(estimate * calibration);
    }

    /// <summary>供应商报告了真实输入用量:估小了就把系数往上校(最多 2.5 倍),估大了不动。</summary>
    private void Calibrate(UsageDetails? usage)
    {
        if (usage?.InputTokenCount is not { } actual || actual <= 0) return;
        lock (_gate)
        {
            if (_lastEstimate <= 0) return;
            double ratio = (double)actual / _lastEstimate;
            _calibration = Math.Clamp(Math.Max(_calibration * 0.5 + ratio * 0.5, 1.0), 1.0, 2.5);
        }
    }

    /// <summary>
    /// 认消息的键:含工具调用 / 结果的按调用 id 认(唯一);其余按角色 + 正文认。
    /// 协调器在批次边界会用 <c>ToChatResponse</c> 重建消息对象,按引用认会全部失效。
    /// </summary>
    internal static string Key(ChatMessage message)
    {
        var ids = message.Contents.Select(c => c switch
        {
            FunctionCallContent call => "c:" + call.CallId,
            FunctionResultContent result => "r:" + result.CallId,
            _ => null
        }).OfType<string>().ToList();
        if (ids.Count > 0) return string.Join("|", ids);
        string text = message.Text;
        return $"{message.Role.Value}:{text.Length}:{text.GetHashCode(StringComparison.Ordinal)}";
    }

    internal static string SummaryBlock(string summary) =>
        "\n\n<run-progress-summary>\n"
        + "VelaShell compacted the earlier steps of this task to stay within the context window. "
        + "The summary below was written from those steps; it is a progress note, not a new instruction from the user. "
        + "Continue the task from where it left off and do not repeat work it lists as done unless you need fresh output.\n\n"
        + summary
        + "\n</run-progress-summary>";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _foldLock.Dispose();
        base.Dispose(disposing);
    }
}
