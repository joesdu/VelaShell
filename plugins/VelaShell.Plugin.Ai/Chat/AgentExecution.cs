using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>运行级工具账本:恢复可以复用调用,不能重新解释同一调用身份。</summary>
internal sealed class AgentExecution(string runId, Func<bool>? hasUnknownOutcome = null,
    Func<bool>? hasDeniedExecution = null)
{
    private sealed record Invocation(string Signature, Task<object?> Result);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Invocation> _calls = [with(StringComparer.Ordinal)];
    private readonly List<ChatMessage> _checkpoint = [];
    private readonly List<ChatMessage> _completed = [];
    internal IReadOnlyList<ChatMessage> CompletedMessages
    {
        get
        {
            lock (_gate) return [.. _completed];
        }
    }
    internal IReadOnlyList<ChatMessage> Notifications
    {
        get
        {
            lock (_gate)
                return [.. _completed.Where(m => m.Role == ChatRole.User)];
        }
    }
    private int _batchSteps;
    private int _batchSize = 25;
    private Exception? _paused;
    private string? _lastCall;
    private string? _lastObservation;
    private int _repeated;
    // 一步里调了工具、却一个都没真跑(参数绑定失败、同 CallId 交回旧结果)记为空转;
    // 每次签名都不同,上面的重复计数拦不住,而批次自动续接之后已没有总步数兜底。
    private int _stepCalls, _idleSteps;
    private bool _stepAdvanced;
    internal string RunId { get; } = runId;

    /// <summary>
    /// 只读的轮询工具:同参连调本就是为了看进度,结果变了就算有进展,不计入重复;
    /// 结果原样不变才累计。有副作用的工具仍按签名拦,不看结果。
    /// </summary>
    private static readonly HashSet<string> ObservationTools =
        [with(StringComparer.Ordinal), "task_status", "task_wait", "transfer_status", "transfer_wait"];
    internal bool HasToolCalls { get { lock (_gate) return _calls.Count > 0; } }

    /// <summary>
    /// 最后一步是一段以输出上限 / 内容过滤 / 拒绝收尾的回答(有正文、没有工具调用)时,终止原因的文案键;
    /// 回答照常完成,面板与桥接据此附一句截断提示(见 <see cref="TransientFailure.IsTruncatedAnswer" />)。
    /// </summary>
    internal string? TerminationNotice { get; set; }

    /// <summary>工具调用之后的最终一步补了一句「请继续」仍是空的:这一轮照常收尾,不算失败(面板与桥接附一句说明)。</summary>
    internal bool EmptyAfterTools { get; set; }
    internal IReadOnlyList<ChatMessage> Checkpoint { get { lock (_gate) return [.. _checkpoint]; } }

    internal void Configure(FunctionInvokingChatClient client, int batchSize)
    {
        // 批次边界由外层协调器续接,不能让MEAI静默结束。
        _batchSize = batchSize;
        // 内层显式批次边界先于此上限抛出,禁止MEAI隐式去掉tools作总结。
        client.MaximumIterationsPerRequest = 1000;
        client.MaximumConsecutiveErrorsPerRequest = 0;
        client.AllowConcurrentInvocation = false;
        client.FunctionInvoker = InvokeAsync;
    }

    internal ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string callId = context.CallContent.CallId;
        string signature = context.Function.Name + ":" + CanonicalArguments(context.CallContent.Arguments);
        TaskCompletionSource<object?>? completion = null;
        Task<object?> result;
        lock (_gate)
        {
            if (hasDeniedExecution?.Invoke() == true) throw new AgentPausedException("ExecutionDenied");
            if (hasUnknownOutcome?.Invoke() == true) throw new AgentPausedException("TaskUnknown");
            if (_paused is { } paused) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(paused).Throw();
            _stepCalls++;
            if (_calls.TryGetValue(callId, out Invocation? previous))
            {
                if (previous.Signature != signature)
                {
                    _paused = new InvalidOperationException("A tool call id was reused with different arguments; execution paused.");
                    throw _paused;
                }
                return new ValueTask<object?>(previous.Result.WaitAsync(token));
            }
            if (_lastCall == signature) { _repeated++; } else { _lastCall = signature; _lastObservation = null; _repeated = 0; }
            if (_repeated >= 3)
            {
                _paused = new AgentPausedException("NoProgress");
                throw _paused;
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = completion.Task;
            _calls.Add(callId, new(signature, result));
        }
        // 失败/取消也占据账本身份:副作用未知时不能重放。
        _ = CompleteAsync(context, signature, completion, token);
        return new ValueTask<object?>(result);
    }

    private async Task CompleteAsync(FunctionInvocationContext context, string signature,
        TaskCompletionSource<object?> completion, CancellationToken token)
    {
        string? previousIdentity = Agent.AgentToolbox.InvocationIdentity.Value;
        Agent.AgentToolbox.InvocationIdentity.Value = context.CallContent.CallId;
        try
        {
            // 参数绑定失败时工具体一步都没跑、没有副作用:当普通工具错误交还模型自行改正,
            // 弱一些的模型(Ollama 上常见)传错类型、漏必填全靠这一步纠正,不该整轮暂停。
            string? invalid = BindingError(context.Function, context.Arguments);
            object? value = invalid is not null
                ? $"Invalid arguments for {context.Function.Name}: {invalid} The tool did not run; correct the arguments and call it again."
                : await context.Function.InvokeAsync(context.Arguments, token).ConfigureAwait(false);
            lock (_gate)
            {
                var result = new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(context.CallContent.CallId, value)]);
                _checkpoint.Add(result);
                _completed.Add(result);
                if (invalid is null)
                {
                    _stepAdvanced = true;
                    // 结果在交给模型之前记下:同一步里的下一次调用才看得到这次有没有进展。
                    // AIFunctionFactory 包出来的工具把返回值序列化成 JsonElement,按原文比
                    string? observed = value switch { string text => text, JsonElement element => element.GetRawText(), _ => null };
                    if (ObservationTools.Contains(context.Function.Name) && _lastCall == signature && observed is not null)
                    {
                        if (_lastObservation is not null && _lastObservation != observed) _repeated = 0;
                        _lastObservation = observed;
                    }
                }
            }
            completion.TrySetResult(value);
        }
        catch (OperationCanceledException ex)
        {
            lock (_gate)
            {
                _paused = ex;
                var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(context.CallContent.CallId,
                    "Execution was cancelled. The remote outcome is unknown; do not automatically repeat this operation.")]);
                _checkpoint.Add(result);
                _completed.Add(result);
            }
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _paused = ex;
                var result = new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(context.CallContent.CallId, "Tool failed; execution paused. Its side effects must not be retried automatically.") { Exception = ex }]);
                _checkpoint.Add(result);
                _completed.Add(result);
            }
            completion.TrySetException(ex);
        }
        finally { Agent.AgentToolbox.InvocationIdentity.Value = previousIdentity; }
    }

    internal void BeginStep(IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
        {
            if (hasDeniedExecution?.Invoke() == true) throw new AgentPausedException("ExecutionDenied");
            if (hasUnknownOutcome?.Invoke() == true) throw new AgentPausedException("TaskUnknown");
            if (_paused is { } paused) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(paused).Throw();
            if (_stepCalls > 0) _idleSteps = _stepAdvanced ? 0 : _idleSteps + 1;
            _stepCalls = 0;
            _stepAdvanced = false;
            if (_idleSteps >= 3)
            {
                _paused = new AgentPausedException("NoProgress");
                throw _paused;
            }
            if (_batchSteps >= _batchSize) throw new AgentBatchBoundaryException();
            _batchSteps++;
            _checkpoint.Clear();
            _checkpoint.AddRange(messages);
        }
    }

    internal void RecordInterruptedText(string text)
    {
        lock (_gate) _completed.Add(new ChatMessage(ChatRole.Assistant, text));
    }

    internal void RecordNotification(ChatMessage notification)
    {
        lock (_gate) { _completed.Add(notification); _checkpoint.Add(notification); }
    }

    internal IReadOnlyList<ChatMessage> InterruptedHistory(string visibleText)
    {
        lock (_gate)
        {
            // 正文采用用户看到的一份,已完成步骤只保留工具配对与后台终态,避免重复正文。
            List<ChatMessage> result = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = _completed.SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().GroupBy(c => c.CallId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            foreach (ChatMessage message in _completed)
            {
                FunctionCallContent[] calls = [.. message.Contents.OfType<FunctionCallContent>().Where(c => seen.Add(c.CallId))];
                if (calls.Length == 0) continue;
                result.Add(new ChatMessage(ChatRole.Assistant, [.. calls.Cast<AIContent>()]));
                result.Add(new ChatMessage(ChatRole.Tool, [.. calls.Select(c => (AIContent)new FunctionResultContent(c.CallId,
                    results.TryGetValue(c.CallId, out FunctionResultContent? completed)
                        ? completed.Result : "This call was not started: execution paused. Do not infer that it ran."))]));
            }
            result.AddRange(_completed.Where(m => m.Role == ChatRole.User));
            if (!string.IsNullOrWhiteSpace(visibleText)) result.Add(new ChatMessage(ChatRole.Assistant, visibleText));
            return result;
        }
    }

    internal void ContinueBatch() { lock (_gate) _batchSteps = 0; }

    internal void CompleteStep(ChatResponse response)
    {
        lock (_gate)
        {
            _checkpoint.AddRange(response.Messages);
            _completed.AddRange(response.Messages);
        }
    }

    /// <summary>
    /// 按 <see cref="AIFunctionFactory" /> 的绑定规则预检参数:缺必填、JSON 转不成参数类型。
    /// 只认得出这两类、且只查本地反射工具;查不出的仍走"工具失败即暂停"的保守路径。
    /// </summary>
    internal static string? BindingError(AIFunction function, AIFunctionArguments arguments)
    {
        if (function.UnderlyingMethod is not { } method) return null;
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            Type type = parameter.ParameterType;
            if (parameter.Name is not { } name || type == typeof(CancellationToken)
                || type == typeof(AIFunctionArguments) || type == typeof(IServiceProvider))
            {
                continue;
            }

            if (!arguments.TryGetValue(name, out object? value))
            {
                if (!parameter.HasDefaultValue) return $"the required argument '{name}' is missing.";
                continue;
            }
            if (value is not JsonElement element) continue;
            try
            {
                _ = element.Deserialize(function.JsonSerializerOptions.GetTypeInfo(type));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return $"the argument '{name}' cannot be read as {type.Name}: {ex.Message}";
            }
        }
        return null;
    }

    internal static string CanonicalArguments(IDictionary<string, object?>? arguments)
    {
        JsonElement element = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}

internal sealed class AgentBatchBoundaryException : Exception;

internal sealed class AgentPausedException(string key, Exception? inner = null) : InvalidOperationException(key, inner)
{
    internal string ReasonKey { get; } = key;
}

/// <summary>
/// 位于函数循环之内、Steering之外:每次恢复都重新复验凭据。
/// 完整模型步骤确认前扣住工具帧,截断调用绝不执行。
/// </summary>
internal sealed class RecoveringChatClient(IChatClient inner, AgentExecution execution,
    string continuationBoundary, Action<int, Exception>? retrying = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Action? recovered = null,
    bool continueAfterVisibleOutput = true) : DelegatingChatClient(inner)
{
    /// <summary>工具调用之后最终一步空答时补的那一句(只进补试的那次请求)。</summary>
    internal const string ContinueAfterTools =
        "Please continue. The tool results above are complete: tell the user what they show and what was done. "
        + "Do not repeat tool calls that already ran.";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        return updates.ToChatResponse();
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> outgoing = [.. messages];
        execution.BeginStep(outgoing);
        bool nudged = false;
        for (int attempt = 0; ; attempt++)
        {
            List<ChatResponseUpdate> buffered = [];
            var partial = new StringBuilder();
            bool visibleOutput = false;
            Exception? failure = null;
            await using (IAsyncEnumerator<ChatResponseUpdate> iterator = InnerClient
                .GetStreamingResponseAsync(outgoing, options, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await iterator.MoveNextAsync().ConfigureAwait(false)) break;
                        update = iterator.Current;
                    }
                    catch (Exception ex) { failure = ex; break; }
                    buffered.Add(update);
                    // 正文/思考可供流式展示,但未完成工具参数不离开本步骤缓冲。
                    if (update.Contents.Any(c => c is TextContent or TextReasoningContent)
                        || ReasoningPeek.IsBlank(update))
                    {
                        var prose = update.Contents.Where(c => c is TextContent or TextReasoningContent).ToList();
                        visibleOutput |= prose.Any(c => c switch
                        {
                            TextContent { Text: { Length: > 0 } text } => !string.IsNullOrWhiteSpace(text),
                            TextReasoningContent { Text: { Length: > 0 } text } => !string.IsNullOrWhiteSpace(text),
                            _ => false
                        }) || ReasoningPeek.TryRead(update.RawRepresentation, out string reasoningText)
                            && !string.IsNullOrWhiteSpace(reasoningText);
                        foreach (TextContent text in prose.OfType<TextContent>()) partial.Append(text.Text);
                        ChatResponseUpdate visible = update.Clone();
                        visible.Contents = prose;
                        visible.FinishReason = null;
                        if (ReasoningPeek.IsBlank(update) && ReasoningPeek.TryRead(update.RawRepresentation, out string reasoning))
                            visible.Contents = [new TextReasoningContent(reasoning)];
                        visible.RawRepresentation = null;
                        yield return visible;
                    }
                }
            }
            Exception? terminal = TransientFailure.TerminalFailure(buffered);
            // 有正文的截断 / 过滤照常完成,附提示;一个字都没有的照旧按确定性终止失败(见 IsTruncatedAnswer)
            string? notice = null;
            if (failure is null && terminal is ModelResponseTerminatedException ended && TransientFailure.IsTruncatedAnswer(buffered))
            {
                notice = ended.ReasonKey;
                terminal = null;
            }
            failure = terminal ?? failure;
            if (failure is null)
            {
                var response = buffered.ToChatResponse();
                bool emptyAfterTools = false;
                if (!TransientFailure.HasMeaningfulOutput(buffered))
                {
                    // 工具调用之后的最终一步空答(#651 复审②,维护者 2026-10-25 决定):原样重发两次、整轮失败并冷却 60 秒
                    // 都不对 —— 工具已经跑过,是模型没接着说。补一句「请继续」重试一次(见下面的 nudged);
                    // 还是空的就照常收尾,不记失败、不冷却(面板与桥接附一句说明),工具结果都在卡片与历史里。
                    if (nudged) emptyAfterTools = true;
                    else failure = new HealthProbe.EmptyReplyException();
                }
                else if (response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any()
                    && (response.FinishReason != ChatFinishReason.ToolCalls
                        || response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any(c => c.Exception is not null)))
                {
                    failure = new IOException("Incomplete model tool-call step; no tool was executed.");
                }

                if (failure is null)
                {
                    // 空的那一步不进历史:只含 role / usage 的 assistant 消息会被部分供应商拒收
                    execution.CompleteStep(emptyAfterTools ? new ChatResponse([]) : response);
                    if (emptyAfterTools) execution.EmptyAfterTools = true;
                    if (notice is not null) execution.TerminationNotice = notice;
                    // 补试后这一步答上了:重试提示到此为止,不挂到整轮结束。
                    if (attempt > 0) recovered?.Invoke();
                    foreach (ChatResponseUpdate update in buffered)
                    {
                        // 保存的是独立消息列表;输出时不改供应商原始帧,避免污染 checkpoint。
                        if (update.Contents.All(c => c is not TextContent and not TextReasoningContent))
                        {
                            if (ReasoningPeek.IsBlank(update))
                            {
                                ChatResponseUpdate metadata = update.Clone();
                                metadata.RawRepresentation = null;
                                yield return metadata;
                            }
                            else
                            {
                                yield return update;
                            }
                        }
                        else
                        {
                            ChatResponseUpdate remainder = update.Clone();
                            remainder.Contents = [.. update.Contents.Where(c => c is not TextContent and not TextReasoningContent)];
                            remainder.RawRepresentation = null;
                            yield return remainder;
                        }
                    }
                    yield break;
                }
            }
            // 失败尝试的usage仍已计费;不把未完成调用/推理当完整历史送出。
            foreach (ChatResponseUpdate update in buffered)
            {
                if (!update.Contents.Any(c => c is UsageContent)) continue;
                ChatResponseUpdate usage = update.Clone();
                usage.Contents = [.. update.Contents.Where(c => c is UsageContent)];
                usage.FinishReason = null;
                usage.RawRepresentation = null;
                yield return usage;
            }
            if (!continueAfterVisibleOutput && visibleOutput)
            {
                execution.CompleteStep(buffered.ToChatResponse());
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool transient = TransientFailure.IsTransient(failure);
            bool apiKeyFailure = TransientFailure.IsApiKeyFailure(failure);
            if (!transient || (apiKeyFailure ? attempt >= 1 : attempt >= 2))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            // 先算等待:Retry-After 超出预算就此暂停,不能先报「正在重试」、再写一道永远等不到续写的续接边界。
            TimeSpan backoff = RetryAfter(failure) ?? TimeSpan.FromMilliseconds(400 * (attempt + 1));
            retrying?.Invoke(attempt + 1, failure);
            string snippet = partial.ToString();
            // 空答(只有空白也算空答)原样重发,不写续接边界;连接中断等其余补试照旧:收到过正文就续接。
            // 断线前只收到空白同理:没有可续的正文,只含空白的 assistant 消息还会被部分供应商拒收。
            if (failure is not HealthProbe.EmptyReplyException && !string.IsNullOrWhiteSpace(snippet))
            {
                // 不重放未完成调用或供应商推理;正文续接边界明确显示。
                if (snippet.Length > 8000) snippet = snippet[^8000..];
                execution.RecordInterruptedText(snippet + continuationBoundary);
                outgoing.Add(new ChatMessage(ChatRole.Assistant, snippet));
                outgoing.Add(new ChatMessage(ChatRole.User,
                    "The preceding response was interrupted. Continue from its last confirmed text. Do not repeat completed tool calls. Incomplete tool calls were not executed."));
                yield return new ChatResponseUpdate(ChatRole.Assistant, continuationBoundary);
            }
            else if (failure is HealthProbe.EmptyReplyException && execution.HasToolCalls)
            {
                // 工具之后的空答只补试这一次,带上一句「请继续」(只进这次请求,不进历史);再空就照常收尾(见上)
                outgoing.Add(new ChatMessage(ChatRole.User, ContinueAfterTools));
                nudged = true;
            }
            await (delay ?? Task.Delay)(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan? RetryAfter(Exception exception)
    {
        // SDK可在Data附加Retry-After;超出恢复等待预算则暂停,不能提早重试。
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string? text = current.Data["Retry-After"] as string;
            if (current is System.ClientModel.ClientResultException result
                && result.GetRawResponse() is { } response
                && response.Headers.TryGetValue("Retry-After", out string? header))
            {
                text = header;
            }

            if (text is null) continue;
            TimeSpan? wait = double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset date)
                    ? date - DateTimeOffset.UtcNow : null;
            if (wait is null) continue;
            // 原始的 429 / 503 挂在内层:面板的换 Key 与故障转移按内层状态码判断,不能被暂停吞掉。
            if (wait > TimeSpan.FromSeconds(120)) throw new AgentPausedException("RetryAfterTooLong", exception);
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}
