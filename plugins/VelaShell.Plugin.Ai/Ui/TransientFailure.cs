using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using AnthropicErrorType = Anthropic.Models.ErrorType;

namespace VelaShell.Plugin.Ai.Ui;
/// <summary>发送准备期间配置已失效;只可换用其他已验证接入,不能原地重试。</summary>
internal sealed class ProviderConfigurationChangedException() : InvalidOperationException(
    "Provider configuration changed; refusing to send an unverified request.");

internal sealed class ModelResponseTerminatedException(string reasonKey)
    : InvalidOperationException($"The model ended the response: {reasonKey}.")
{
    internal string ReasonKey { get; } = reasonKey;
}

/// <summary>
/// 一次失败值不值得自动重来。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>ChatPanelView</c> 拆出来的一簇(Q-01)。这是**行为决策**,不是格式化:
/// 判错了的两种后果都不轻 —— 把参数错当成瞬时故障会白白多打一次(还多花一次钱),
/// 把网络抖动当成永久失败则让用户在明明能成的时候看到一条红字。
/// </para>
/// <para>
/// 只认"再试一次可能就好了"的那些:网络层故障、超时、服务端的 408 / 429 / 5xx,
/// 以及还没开口就结束的空回复。参数错、鉴权失败重试一万次也一样。
/// </para>
/// </remarks>
public static class TransientFailure
{
    internal static bool HasMeaningfulOutput(IEnumerable<ChatResponseUpdate> updates)
        => updates.Any(update => update.Contents.Any(content => content switch
        {
            TextContent text => !string.IsNullOrWhiteSpace(text.Text),
            TextReasoningContent reasoning => !string.IsNullOrWhiteSpace(reasoning.Text),
            FunctionCallContent or FunctionResultContent or ErrorContent => true,
            DataContent data => data.HasTopLevelMediaType("image") && !data.Data.IsEmpty,
            _ => false
        }) || (ReasoningPeek.IsBlank(update)
            && ReasoningPeek.TryRead(update.RawRepresentation, out string peeked)
            && !string.IsNullOrWhiteSpace(peeked)));

    /// <summary>
    /// 一步回复里有可见正文、又没有工具调用 —— 这时以输出上限 / 内容过滤 / 拒绝收尾的,是一段说到一半的回答,不是失败。
    /// </summary>
    /// <remarks>
    /// §223 起这三种收尾一律整轮失败:桥接不再送出被截断的回答,面板故障转移后又切回失败的那一站(#651 复审①)。
    /// 维护者 2026-10-25 决定:有正文就照常完成,附一句截断 / 过滤提示;一个字都没有的照旧报错、不重试、不换站。
    /// 带工具调用的步骤不在此列:被截断的工具参数绝不能执行(那条仍按"工具调用步骤不完整"失败)。
    /// </remarks>
    internal static bool IsTruncatedAnswer(IEnumerable<ChatResponseUpdate> step)
    {
        bool text = false;
        foreach (ChatResponseUpdate update in step)
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is FunctionCallContent) return false;
                if (content is TextContent { Text: { } value } && !string.IsNullOrWhiteSpace(value)) text = true;
            }
        }
        return text;
    }

    /// <summary>截断 / 过滤收尾时附在回答后面的那句提示的文案键(由 <see cref="TerminalFailure" /> 给出的终止原因换算)。</summary>
    internal static string TruncationNoticeKey(string reasonKey)
        => reasonKey == "ErrorOutputLimit" ? "NoticeOutputLimit" : "NoticeFiltered";

    // 确定性终止不能当作空流重发,尤其不能借故障转移绕过拒绝。
    internal static Exception? TerminalFailure(IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (ChatResponseUpdate update in updates)
        {
            if (update.FinishReason is { } reason
                && (reason == ChatFinishReason.Length || reason == ChatFinishReason.ContentFilter
                    || reason.Value == "refusal"))
            {
                return new ModelResponseTerminatedException(reason == ChatFinishReason.Length ? "ErrorOutputLimit" : "ErrorModelRefusal");
            }
#pragma warning disable OPENAI001 // Responses API 在 OpenAI SDK 中标记为实验性
            if (update.RawRepresentation is OpenAI.Responses.StreamingResponseIncompleteUpdate incomplete)
            {
                return new ModelResponseTerminatedException(incomplete.Response.IncompleteStatusDetails?.Reason
                    == OpenAI.Responses.ResponseIncompleteStatusReason.MaxOutputTokens ? "ErrorOutputLimit" : "ErrorModelRefusal");
            }

            if (update.RawRepresentation is OpenAI.Responses.StreamingResponseRefusalDeltaUpdate
                or OpenAI.Responses.StreamingResponseRefusalDoneUpdate
                or OpenAI.Chat.StreamingChatCompletionUpdate { RefusalUpdate.Length: > 0 })
            {
                return new ModelResponseTerminatedException("ErrorModelRefusal");
            }
            // response.failed:M.E.AI 的适配器把它转成一帧没有任何内容的更新(不给 ErrorContent、也不抛)。
            // 不在这里认出来,先吐了半截正文再失败的流会被当成答完了 —— 记成功、解冻冷却;
            // 一个字没吐就失败的又被当成空回复,原样重试、报"空回复",服务端的原话丢了(#582 复审④)
            if (update.RawRepresentation is OpenAI.Responses.StreamingResponseFailedUpdate failed)
            {
                return new InvalidOperationException(failed.Response?.Error?.Message is { Length: > 0 } message
                    ? message : "The model response failed.");
            }
#pragma warning restore OPENAI001
            if (update.Contents.OfType<ErrorContent>().FirstOrDefault() is { } error)
                return new InvalidOperationException(error.Message);
        }
        return null;
    }

    /// <summary>这个异常值不值得重来一次。</summary>
    /// <remarks>
    /// <b>逐层看 InnerException</b>:HTTP 客户端与 SDK 会把真实原因包上一两层,
    /// 只看最外层那个的话,绝大多数可重试的失败都会被判成永久失败。
    /// 带状态码的 <see cref="HttpRequestException" /> 按状态码判 —— 服务端已经答复过了,
    /// 400/404 这类换到哪儿都一样,不能再当成"网络抖了"放行。
    /// </remarks>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>可重试时为 true。</returns>
    public static bool IsTransient(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // 正式流只有 role/usage/结束帧:没有开口,可安全重试并切下一站
                case HealthProbe.EmptyReplyException:
                    return true;
                // 带状态码 = 服务端答复过了:只认 408/429/5xx,其余(400/401/404…)一律不瞬时
                case HttpRequestException { StatusCode: { } status }:
                    int code = (int)status; // 枚举不能打关系模式,先落到整型
                    return code is 408 or 429 or >= 500 and < 600;
                // Anthropic SDK 自己的异常也带状态码,同样按码判(review⑥#1:
                // 只认 HttpRequestException 时,401 鉴权失败既不瞬时也不值得换一家,
                // 整条链原地不动)。码为 0(网络层失败被它包了进来)则继续走
                // InnerException 链 —— 那时真实原因在里面那层。
                case AnthropicApiException { StatusCode: { } apiStatus }:
                    int apiCode = (int)apiStatus;
                    if (apiCode is >= 400 and < 600)
                    {
                        return apiCode is 408 or 429 or >= 500 and < 600;
                    }
                    continue;
                // 流式中途的 error 事件:HTTP 200 早已回过,overloaded_error 正是 529 的流式形态。
                // SDK 抛的是不带状态码的 AnthropicSseException,只能按错误类型判 ——
                // 不认的话,故障转移最该接手的"过载"反而原地报错
                case AnthropicSseException
                {
                    ErrorType: AnthropicErrorType.OverloadedError or AnthropicErrorType.ApiError
                    or AnthropicErrorType.RateLimitError or AnthropicErrorType.TimeoutError
                }:
                    return true;
                // 没状态码 = 压根没连上(DNS/拒绝/断连),抖一下就好
                case HttpRequestException:
                case IOException:
                case TimeoutException:
                    return true;
                case System.ClientModel.ClientResultException { Status: > 0 } result:
                    return result.Status is 408 or 429 or >= 500 and < 600;
            }
        }
        return false;
    }

    /// <summary>只把服务端 401/403/429 当作某一把 Key 的鉴权或额度失败。</summary>
    public static bool IsApiKeyFailure(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AiSettingsStore.ApiKeySlotChangedException:
                case ProviderConfigurationChangedException:
                case OperationCanceledException:
                    return false;
                case HttpRequestException { StatusCode: { } status }:
                    return (int)status is 401 or 403 or 429;
                case AnthropicApiException { StatusCode: { } apiStatus } when apiStatus != 0:
                    return (int)apiStatus is 401 or 403 or 429;
                case System.ClientModel.ClientResultException { Status: > 0 } result:
                    return result.Status is 401 or 403 or 429;
            }
        }
        return false;
    }

    /// <summary>
    /// 服务端回 401:这把 Key 本身被拒(无效、吊销),换哪个模型都一样 —— 冷整把 Key。
    /// 403 / 429 不算:可能只是这把 Key 用不了这一个型号(没开通)或这个型号单独限流,
    /// 只冷这把 Key 用在这个模型上(见 <see cref="ProviderHealth.RecordKeyFailure" />),兄弟模型照常探。
    /// </summary>
    public static bool IsKeyRejected(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AiSettingsStore.ApiKeySlotChangedException:
                case ProviderConfigurationChangedException:
                case OperationCanceledException:
                    return false;
                case HttpRequestException { StatusCode: { } status }:
                    return (int)status == 401;
                case AnthropicApiException { StatusCode: { } apiStatus } when apiStatus != 0:
                    return (int)apiStatus == 401;
                case System.ClientModel.ClientResultException { Status: > 0 } result:
                    return result.Status == 401;
            }
        }
        return false;
    }

    // 切换提示里的原因代码(会落进会话历史的附加信息,改名就是改存量数据的口径)
    internal const string SwitchRateLimit = "rate_limit", SwitchAuth = "auth", SwitchTimeout = "timeout",
        SwitchServer = "server", SwitchNetwork = "network", SwitchEmpty = "empty", SwitchConfig = "config", SwitchOther = "other";

    /// <summary>
    /// 故障转移 / 换 Key 那一跳<b>因为什么</b>,给聊天记录里的切换提示用 —— 只管怎么说,切不切仍由
    /// <see cref="IsWorthSwitching" /> / <see cref="IsApiKeyFailure" /> 定。
    /// </summary>
    /// <remarks>
    /// 与上面几个判定同一口径:逐层看 InnerException;带状态码的按码(429 限流、401 / 403 鉴权、408 超时、5xx 服务端);
    /// Anthropic 流式 <c>event: error</c> 按错误类型;没有状态码的网络层故障算「连不上」(<see cref="ApiErrorText.IsUnreachable" />)。
    /// 长 Retry-After 的暂停把原始 429 / 503 挂在内层,照样按内层的码说。
    /// </remarks>
    /// <returns>原因代码与 HTTP 状态码(没有时为 0)。</returns>
    internal static (string Reason, int Status) SwitchReason(Exception? exception)
    {
        bool network = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            int status = current switch
            {
                HttpRequestException { StatusCode: { } code } => (int)code,
                AnthropicApiException { StatusCode: { } apiCode } when apiCode != 0 => (int)apiCode,
                System.ClientModel.ClientResultException { Status: > 0 } result => result.Status,
                _ => 0
            };
            if (status > 0)
            {
                return (status switch
                {
                    429 => SwitchRateLimit,
                    401 or 403 => SwitchAuth,
                    408 => SwitchTimeout,
                    >= 500 and < 600 => SwitchServer,
                    _ => SwitchOther
                }, status);
            }
            switch (current)
            {
                case HealthProbe.EmptyReplyException:
                    return (SwitchEmpty, 0);
                case ProviderConfigurationChangedException or AiSettingsStore.ApiKeySlotChangedException:
                    return (SwitchConfig, 0);
                // 内置 OAuth 凭据不能发往这个地址:凭据这一侧的问题
                case AiSettingsStore.BuiltinOAuthHostMismatchException:
                    return (SwitchAuth, 0);
                case AnthropicSseException { ErrorType: AnthropicErrorType.RateLimitError }:
                    return (SwitchRateLimit, 0);
                case AnthropicSseException { ErrorType: AnthropicErrorType.TimeoutError }:
                case TimeoutException:
                    return (SwitchTimeout, 0);
                case AnthropicSseException { ErrorType: AnthropicErrorType.OverloadedError or AnthropicErrorType.ApiError }:
                    return (SwitchServer, 0);
                case IOException:
                    network = true;
                    break;
            }
        }
        return (network || ApiErrorText.IsUnreachable(exception) ? SwitchNetwork : SwitchOther, 0);
    }

    /// <summary>
    /// 这个异常值不值得把请求<b>换一家</b>再发。
    /// </summary>
    /// <remarks>
    /// 瞬时故障(网络/超时/408/429/5xx)、空回复,与鉴权失败(401/403,SDK 的
    /// <see cref="System.ClientModel.ClientResultException" /> 与带状态码的
    /// <see cref="HttpRequestException" /> 两种形态都认):前两者换个端点可能就好,
    /// 后者是这家的凭据或配额出了问题,原地重试一万次也一样。
    /// 内置 OAuth 凭据的目标地址不匹配也只能换用另一家的独立凭据,不能原地重试。
    /// 发送准备期间配置失效同样只能跳过当前站,不能沿用未验证的地址或凭据。
    /// 其余(400 参数错、404 模型不存在…)换到哪儿都还是那个错 ——
    /// 那时该把真原因端给用户,而不是拖着整条链挨个撞(还多花一遍探测的钱)。
    /// </remarks>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>值得换一家时为 true。</returns>
    public static bool IsWorthSwitching(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
                return (int)status is 401 or 403 or 408 or 429 or >= 500 and < 600;
            if (current is AnthropicApiException { StatusCode: { } apiStatus } && apiStatus != 0)
                return (int)apiStatus is 401 or 403 or 408 or 429 or >= 500 and < 600;
            if (current is System.ClientModel.ClientResultException { Status: > 0 } result)
                return result.Status is 401 or 403 or 408 or 429 or >= 500 and < 600;
            if (current is AiSettingsStore.BuiltinOAuthHostMismatchException
                or AiSettingsStore.ApiKeySlotChangedException
                or ProviderConfigurationChangedException || IsTransient(current))
            {
                return true;
            }
        }
        return false;
    }
}
