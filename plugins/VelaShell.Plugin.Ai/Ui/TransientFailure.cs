namespace VelaShell.Plugin.Ai.Ui;

using System.Net;
using Anthropic.Exceptions;
using VelaShell.Plugin.Ai.Configuration;

/// <summary>发送准备期间配置已失效;只可换用其他已验证接入,不能原地重试。</summary>
internal sealed class ProviderConfigurationChangedException() : InvalidOperationException(
    "Provider configuration changed; refusing to send an unverified request.");

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
                case AnthropicApiException { StatusCode: { } apiStatus } when (int)apiStatus != 0:
                    return (int)apiStatus is 401 or 403 or 429;
                case System.ClientModel.ClientResultException { Status: > 0 } result:
                    return result.Status is 401 or 403 or 429;
            }
        }
        return false;
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
            if (current is AnthropicApiException { StatusCode: { } apiStatus } && (int)apiStatus != 0)
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
