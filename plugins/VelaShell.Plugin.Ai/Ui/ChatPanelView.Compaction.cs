using Avalonia.Controls;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 上下文压缩的面板侧接线:发送前检查是否该压,压完在消息流里留一道可展开的分隔条。
/// 算法本身在 <see cref="ContextCompactor" />(不碰 UI,可单测)。
/// </summary>
public partial class ChatPanelView
{
    // 滚动摘要与它覆盖到的下标按对话各持一份(见 Conversation):_contextSummary / _summarizedThrough。

    private void ResetCompaction()
    {
        ContextSummary = "";
        SummarizedThrough = 0;
    }

    /// <summary>
    /// 发送前调用:接近窗口就先把早期对话折成摘要。
    /// 网络失败仍回落裁剪;固定槽或身份失效、取消则抛给整轮收尾,禁止继续发送历史。
    /// </summary>
    private async Task CompactIfNeededAsync(ResolvedModel provider, CancellationToken cancellationToken,
        Func<CancellationToken, Task<IChatClient>> getVerifiedClient)
    {
        if (!_settings.CompactContext
            || !ContextCompactor.ShouldCompact(History, SummarizedThrough, ContextSummary,
                provider.MaxInputTokens, provider.MaxTokens))
        {
            return;
        }
        int cut = ContextCompactor.PlanCutPoint(History, SummarizedThrough, provider.MaxInputTokens, provider.MaxTokens);
        if (cut <= SummarizedThrough)
        {
            return;
        }
        try
        {
            SetStatus(_loc["Compacting"]);
            // 真正摘要请求前复验固定槽与身份,取得最新裸客户端;生命周期由工具/插话通道拥有。
            CompactionResult? result = await Task.Run(async () =>
            {
                IChatClient client = await getVerifiedClient(cancellationToken);
                return await ContextCompactor.CompactAsync(client, History, SummarizedThrough, ContextSummary, cut,
                    _context.Host.Locale, cancellationToken,
                    tuneOptions: o => AiSettingsStore.ApplyEndpointQuirks(o, provider));
            }, cancellationToken);
            if (result is not { } compaction)
            {
                return;
            }

            ContextSummary = compaction.Summary;
            SummarizedThrough = compaction.Through;
            // 压缩自身的用量也算进累计(是真花的钱),但不动"上一轮上下文"那个读数
            if (compaction.Usage is { } usage)
            {
                TotalInputTokens += usage.InputTokenCount ?? 0;
                TotalOutputTokens += usage.OutputTokenCount ?? 0;
            }
            await _historyStore.SaveSummaryAsync(ConversationId, ContextSummary, SummarizedThrough, cancellationToken);
            ShowCompactionMarker(compaction.FoldedMessages);
            UpdateUsageText();
        }
        catch (OperationCanceledException)
        {
            throw; // 用户按了停止,交给外层统一收尾
        }
        catch (Exception ex) when (ex is not ProviderConfigurationChangedException and not AiSettingsStore.ApiKeySlotChangedException)
        {
            // 压不动就照常发:装配那一步会按窗口丢掉最早几条,不至于卡住对话
            _context.Log.Warn($"Context compaction failed, falling back to trimming: {ex.Message}");
        }
        finally
        {
            SetStatus("");
        }
    }

    /// <summary>
    /// 在消息流里放一道"已压缩"分隔条,点开能读到摘要原文。
    /// 压缩必须看得见 —— 模型从此刻起看到的是摘要而不是原文,用户有权知道这件事。
    /// </summary>
    private void ShowCompactionMarker(int foldedMessages)
    {
        var collapsible = new Collapsible(_loc.F("Compacted", foldedMessages),
            iconKey: "AiIcon.scissors", iconBrushKey: "VelaAccent");
        collapsible.SetBody(ContextSummary);
        var host = new Border { Classes = { "compactionMarker" }, Child = collapsible.Root };
        MessagesPanel.Children.Add(host);
        RequestAutoScroll(force: true);
    }

    /// <summary>载入历史会话时把上次压缩的结果一并恢复,免得一进来就重压一次。</summary>
    private async Task RestoreSummaryAsync()
    {
        (string summary, int through) = await _historyStore.LoadSummaryAsync(ConversationId);
        // 摘要覆盖范围不能超过实际条数(会话被编辑/删除截断过就会这样),对不上就整个作废
        ContextSummary = through > 0 && through <= History.Count ? summary : "";
        SummarizedThrough = ContextSummary.Length > 0 ? through : 0;
    }
}
