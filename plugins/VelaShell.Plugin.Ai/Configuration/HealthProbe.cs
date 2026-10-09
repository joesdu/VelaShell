using Microsoft.Extensions.AI;
using System.Text;
using System.Globalization;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 极小真请求探活:要求一个不触发正式停止词的短回答,输出额度与正常聊天一致,答得上来就算通。
/// </summary>
/// <remarks>
/// 不拿 <c>/models</c> 这类目录接口探:FetchAsync 对 401 与 404 都返回空列表,
/// 判不出"活着但没这个模型"和"根本连不上"(见 EndpointModelCatalog)。
/// 探活要回答的是"现在能不能拿它答一句话",那就直接拿它答一句话 ——
/// 设置页的「测试」、全局设置里的「全部检测」与故障转移的切换前探测走的是同一条路。
/// </remarks>
public static class HealthProbe
{
    /// <summary>
    /// 单次探测的时限。探活是给人等的,15 秒还没答上来就当不通 ——
    /// 再长,用户只会以为界面卡死了。
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>探一次。</summary>
    /// <returns>
    /// <c>Error</c> 为 null 即通,<c>Text</c> 是模型的回文(设置页直接拿来显示);
    /// 否则 <c>Error</c> 是判定为不通的原因。用户主动取消原样抛出 —— 那不是"端点死了"。
    /// </returns>
    public static Task<(Exception? Error, string Text)> ProbeAsync(
        AiSettingsStore store, ResolvedModel model, string? apiKeyOverride = null,
        CancellationToken cancellationToken = default)
        => ProbeCoreAsync(store, model, null, apiKeyOverride, cancellationToken, null);

    /// <summary>故障转移使用已固定的凭据探活;已收到的用量在调用上下文收尾时结算一次(包括失败和取消)。</summary>
    internal static Task<(Exception? Error, string Text)> ProbeAsync(
        AiSettingsStore store, ResolvedModel model, ProviderCredential credential,
        CancellationToken cancellationToken = default, Action<UsageDetails>? usageCallback = null)
        => ProbeCoreAsync(store, model, credential, null, cancellationToken, usageCallback);

    private static async Task<(Exception? Error, string Text)> ProbeCoreAsync(
        AiSettingsStore store, ResolvedModel model, ProviderCredential? credential,
        string? apiKeyOverride, CancellationToken cancellationToken, Action<UsageDetails>? usageCallback)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(model);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        UsageDetails? usage = null;
        try
        {
            using IChatClient client = credential is { } fixedCredential
                ? store.CreateClient(model, fixedCredential)
                : await store.CreateClientAsync(model, apiKeyOverride, cts.Token);
            var options = new ChatOptions
            {
                MaxOutputTokens = model.MaxTokens,
                Temperature = model.Temperature,
                TopP = model.TopP,
                StopSequences = string.IsNullOrWhiteSpace(model.StopSequences)
                    ? null
                    : model.StopSequences.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };
            // 探活也得用正式请求的有效思考档位:缺掉 reasoning_effort 或 Anthropic
            // thinking/max_tokens 会把不支持该档位的站误标活,或把能答的站误标死。
            AiSettingsStore.ApplyReasoning(options, model);
            // 正式请求走的那套端点怪癖必须一起施加:内置 Codex 后端把 max_output_tokens
            // 列在不支持参数里,多发一个字段就整轮 400 —— 不抹掉它,一个能正常聊天的接入
            // 会被自己的「测试 / 全部检测 / 切换前探测」误判成不可用(见 ApplyEndpointQuirks)。
            AiSettingsStore.ApplyEndpointQuirks(options, model);
            string expectedReply = ProbeReply(options.StopSequences);
            string reply = await Task.Run(async () =>
            {
                var text = new StringBuilder();
                await foreach (ChatResponseUpdate update in client
                    .GetStreamingResponseAsync($"Reply with exactly: {expectedReply}", options, cts.Token)
                    .ConfigureAwait(false))
                {
                    text.Append(update.Text);
                    if (usageCallback is not null)
                    {
                        foreach (AIContent content in update.Contents)
                        {
                            if (content is UsageContent reported)
                            {
                                (usage ??= new UsageDetails()).Add(reported.Details);
                            }
                        }
                    }
                }
                return text.ToString().Trim();
            }, cts.Token);
            if (!HasVisibleText(reply))
            {
                // HTTP 通了、一个字却没答:没答上探测问题,不算活 ——
                // 否则故障转移会把一个答不上来的备用站端上来
                return (new EmptyReplyException(), "");
            }
            return (null, reply);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 用户按了停止:取消照旧往上抛,不记成"端点死了"
        }
        catch (Exception ex)
        {
            // 到点触发的取消也落在这儿(上面那个 when 已把用户取消挑走)——对探活而言超时就是不通
            return (ex, "");
        }
        finally
        {
            // await Task.Run 已回到调用方上下文;SSE 线程池只汇总，不能直接操作聊天 UI。
            if (usage is not null)
            {
                usageCallback?.Invoke(usage);
            }
        }
    }

    private static bool HasVisibleText(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
            if (rune.Value is not (0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0)
                && !Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) is not UnicodeCategory.Control and not UnicodeCategory.Format
                and not UnicodeCategory.NonSpacingMark and not UnicodeCategory.SpacingCombiningMark and not UnicodeCategory.EnclosingMark)
                return true;
        return false;
    }

    private static string ProbeReply(IList<string>? stopSequences)
    {
        if (stopSequences is null || !stopSequences.Any(stop => "OK".Contains(stop, StringComparison.Ordinal)))
        {
            return "OK";
        }
        // 一个可见字符不会包含多字符停止词;逐个枚举 Unicode 标量,选择有界且确定。
        for (int value = '!'; value <= 0x10FFFF; value++)
        {
            if (!Rune.TryCreate(value, out Rune rune)
                || !(Rune.IsLetterOrDigit(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)))
            {
                continue;
            }
            string reply = rune.ToString();
            if (!stopSequences.Contains(reply))
            {
                return reply;
            }
        }
        throw new InvalidOperationException("The stop sequences block every visible probe reply.");
    }

    /// <summary>HTTP 请求成功、但一个字都没答 —— 对探活而言这不是"活的"。</summary>
    /// <remarks>
    /// 界面按类型给它配了本地化文案(见设置页 <c>TestFail</c> 的分派);
    /// 消息本身是给日志看的,保持英文即可。
    /// </remarks>
    public sealed class EmptyReplyException()
        : Exception("The endpoint returned an empty reply to the probe.");
}
