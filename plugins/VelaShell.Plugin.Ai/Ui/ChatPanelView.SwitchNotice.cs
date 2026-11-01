using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 聊天记录里的切换提示卡:故障转移换了一站、或同一模型换了一把 Key 时,在这条回复里说清「换到了谁、为什么换」。
/// </summary>
/// <remarks>
/// <para>
/// 起因(#582 复审时记下的一条):切换会粘住并落盘,之后的请求都发往新那一站、记在新账号上 ——
/// 以前只在状态行闪一句「已切换到 X」,下一轮发送就被清掉,用户事后无从知道计费归属换了。
/// </para>
/// <para>
/// <b>不进模型上下文。</b>卡片只挂在气泡的可视树上:<c>History</c>(发给模型的消息)、复制整段用的正文、
/// 存进库里的回复正文都不含它;存档时与思考、工具调用一样记在这条回复的附加信息(<see cref="ChatTurnMeta.Switch" />)里,
/// 重开会话时恢复成卡片,上下文仍只由正文装配。导出 Markdown 时写成一段引用,与思考、工具调用同属排查记录。
/// </para>
/// <para>
/// <b>一轮一张卡。</b>A → B → C 连着换,或者一整个 Key 池挨个限流,都只更新同一张卡:第一行是最后一跳(现在用的是谁),
/// 下面按顺序列出每一站为什么被换掉 —— 只留最后一跳会丢掉「最初那一站为什么不能用」,每跳一张卡又会把回答挤到屏幕外。
/// 超过 <see cref="MaxListedHops" /> 跳时只列头一跳与最后两跳,中间合成一行计数。
/// </para>
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>卡上最多逐条列几跳;再多就「头一跳 + 中间计数 + 最后两跳」。</summary>
    private const int MaxListedHops = 4;

    /// <summary>一站的显示名:与模型下拉同一种写法(「供应商·模型」)。</summary>
    private static string StationLabel(ResolvedModel model)
        => string.IsNullOrWhiteSpace(model.ProviderName) ? model.Name : $"{model.ProviderName}·{model.Name}";

    /// <summary>
    /// 这一站用的那把 Key 怎么称呼:只在填 Key 的供应商有不止一把 Key 时点名(只有一把时,供应商名已经说清了是谁),
    /// 写法与设置页 Key 行一致 —— 序号 + 末四位,不带 Key 本身。
    /// </summary>
    private ChatKeyLabel? SwitchKeyLabel(ResolvedModel model, string? keyId, ProviderCredential credential)
    {
        if (keyId is null || model.Provider.Auth != AuthMethod.ApiKey || credential.IsBearerToken)
        {
            return null;
        }
        string[] ids = [.. _store.ProviderApiKeyIds(model.Provider)];
        int index = Array.IndexOf(ids, keyId);
        if (ids.Length < 2 || index < 0)
        {
            return null;
        }
        string value = credential.Value ?? "";
        return new ChatKeyLabel(index + 1, value.Length > 4 ? value[^4..] : "");
    }

    /// <summary>记下这一轮的一跳,并就地更新这条回复里的那张切换提示卡。</summary>
    private void RecordSwitch(AssistantBubble? bubble, List<ChatProviderSwitch> switches,
        ResolvedModel from, ChatKeyLabel? fromKey, ResolvedModel to, ChatKeyLabel? toKey, Exception reason)
    {
        (string code, int status) = TransientFailure.SwitchReason(reason);
        switches.Add(new ChatProviderSwitch(StationLabel(from), StationLabel(to), code, status, fromKey, toKey));
        if (bubble is null)
        {
            return;
        }
        bubble.ShowSwitchNotice(new ChatSwitchNotice([.. switches], Sticky: false));
        RequestAutoScroll();
    }

    /// <summary>切换提示卡的各行(界面与导出共用):现在用谁、一路为什么换、是否粘住。</summary>
    internal IReadOnlyList<string> SwitchNoticeLines(ChatSwitchNotice notice)
    {
        IReadOnlyList<ChatProviderSwitch> hops = notice.Hops;
        var lines = new List<string>(MaxListedHops + 2);
        if (hops.Count == 0)
        {
            return lines;
        }
        ChatProviderSwitch last = hops[^1];
        lines.Add(_loc.F("SwitchNoticeTo", KeyedStation(last.To, last.ToKey)));
        if (hops.Count <= MaxListedHops)
        {
            lines.AddRange(hops.Select(HopLine));
        }
        else
        {
            lines.Add(HopLine(hops[0]));
            lines.Add(_loc.F("SwitchNoticeMore", hops.Count - 3));
            lines.Add(HopLine(hops[^2]));
            lines.Add(HopLine(last));
        }
        if (notice.Sticky)
        {
            lines.Add(_loc["SwitchNoticeSticky"]);
        }
        return lines;
    }

    private string HopLine(ChatProviderSwitch hop)
    {
        string reason = _loc[hop.Reason switch
        {
            TransientFailure.SwitchRateLimit => "SwitchReasonRateLimit",
            TransientFailure.SwitchAuth => "SwitchReasonAuth",
            TransientFailure.SwitchTimeout => "SwitchReasonTimeout",
            TransientFailure.SwitchServer => "SwitchReasonServer",
            TransientFailure.SwitchNetwork => "SwitchReasonNetwork",
            TransientFailure.SwitchEmpty => "SwitchReasonEmpty",
            TransientFailure.SwitchConfig => "SwitchReasonConfig",
            _ => "SwitchReasonOther"
        }];
        if (hop.Status > 0)
        {
            reason = _loc.F("SwitchReasonWithStatus", reason, hop.Status);
        }
        return _loc.F("SwitchNoticeHop", KeyedStation(hop.From, hop.FromKey), reason);
    }

    private string KeyedStation(string station, ChatKeyLabel? key)
        => key is null ? station : $"{station} · {_loc.F("SetupApiKeyOrdinal", key.Ordinal)} · ●●●●{key.Tail}";

    internal sealed partial class AssistantBubble
    {
        private Border? _switchCard;
        private StackPanel? _switchLines;

        /// <summary>这一轮的切换记录(存档用);没切过为 null。</summary>
        public ChatSwitchNotice? SwitchNotice { get; private set; }

        /// <summary>思考区插在哪儿:紧跟头行;有切换提示卡时排在卡后面(那份思考属于换过去的那一站)。</summary>
        private int ThinkingSlot => _switchCard is null ? 1 : 2;

        /// <summary>
        /// 摆出或更新这条回复里的切换提示卡。一轮只有一张:第一次切换时紧跟头行插进去,之后每一跳都改这一张。
        /// 它不是模型说的话,与提示卡、错误卡一样不混进正文。
        /// </summary>
        public void ShowSwitchNotice(ChatSwitchNotice notice)
        {
            SwitchNotice = notice;
            if (_switchCard is null)
            {
                var stack = new StackPanel { Spacing = 6 };
                stack.Children.Add(_owner.BuildCardHeader("Icon.arrow-right-left", "VelaWarning", _owner._loc["SwitchNoticeTitle"], null));
                _switchLines = new StackPanel { Spacing = 2 };
                stack.Children.Add(_switchLines);
                _switchCard = new Border { Name = "SwitchNoticeCard", Classes = { "noticeCard" }, Child = stack };
                // 失败那一站留下的空白正文 / 思考稍后由 ResetUnopenedContent 清掉;卡不在清理之列
                _stack.Children.Insert(1, _switchCard);
            }
            _switchLines!.Children.Clear();
            bool first = true;
            foreach (string line in _owner.SwitchNoticeLines(notice))
            {
                var text = new TextBlock { Name = "SwitchNoticeText", Text = line, TextWrapping = TextWrapping.Wrap };
                if (!first) text.Classes.Add("dim");
                first = false;
                _switchLines.Children.Add(text);
            }
        }
    }
}
