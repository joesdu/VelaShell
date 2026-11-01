using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>消息列表里的一条:包装共享的 <see cref="NotificationItem" />,向界面暴露展示属性。</summary>
public class NotificationItemViewModel(NotificationItem item) : ReactiveObject
{
    private readonly NotificationItem _item = item ?? throw new ArgumentNullException(nameof(item));

    /// <summary>消息标识(去重与已读状态的键)。</summary>
    public string Id => _item.Id;

    /// <summary>标题。</summary>
    public string Title => _item.Title;

    /// <summary>正文;无正文时为空。</summary>
    public string? Body => _item.Body;

    /// <summary>是否有正文(控制正文行显隐)。</summary>
    public bool HasBody => !string.IsNullOrWhiteSpace(_item.Body);

    /// <summary>消息种类。</summary>
    public NotificationKind Kind => _item.Kind;

    /// <summary>是否已读。</summary>
    public bool IsRead
    {
        get => _item.IsRead;
        set
        {
            if (_item.IsRead == value)
            {
                return;
            }
            _item.IsRead = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsUnread));
        }
    }

    /// <summary>是否未读(未读行左侧有强调竖条、标题用主文本色)。</summary>
    public bool IsUnread => !_item.IsRead;

    /// <summary>种类徽标文字(资讯 / 更新 / 安全 / 推广)。</summary>
    public string KindBadge => Kind switch
    {
        NotificationKind.Update => Strings.Get("Notify_KindUpdate"),
        NotificationKind.Security => Strings.Get("Notify_KindSecurity"),
        NotificationKind.Promotion => Strings.Get("Notify_KindPromotion"),
        NotificationKind.System => Strings.Get("Notify_KindSystem"),
        NotificationKind.Plugin => Strings.Get("Notify_KindPlugin"),
        _ => Strings.Get("Notify_KindNews")
    };

    /// <summary>来源一行(「来自 插件名」);宿主自己的消息没有来源,为空。</summary>
    public string SourceText => _item.Source is { Length: > 0 } source ? Strings.Format("Notify_Source", source) : string.Empty;

    /// <summary>是否显示来源(插件消息才有)。</summary>
    public bool HasSource => _item.Source is { Length: > 0 };

    /// <summary>是否为需要留意的消息(警告/严重),用于把徽标换成警示色。</summary>
    public bool IsHighlighted => _item.Severity is NotificationSeverity.Warning or NotificationSeverity.Critical;

    /// <summary>这条消息有没有可去的地方。</summary>
    public bool HasLink => _item.Link is not null;

    /// <summary>动作文案;源里没给就按去处兜底(站内「查看」/ 站外「打开链接」)。</summary>
    public string LinkLabel =>
        _item.Link is not { } link
            ? string.Empty
            : !string.IsNullOrWhiteSpace(link.Label)
                ? link.Label
                : Strings.Get(link.CommandId is { Length: > 0 } ? "Notify_LinkOpen" : "Notify_LinkExternal");

    /// <summary>
    /// 外链的主机名,显示在动作旁边。远端源指过来的链接,**要让用户在点之前就看见去哪** ——
    /// 一个只显示「阅读全文」的按钮,点下去到哪只有投递方知道。
    /// </summary>
    public string? LinkHost =>
        _item.Link?.CommandId is { Length: > 0 }
            ? null
            : _item.Link?.Url is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                ? parsed.Host
                : null;

    /// <summary>是否显示外链主机名。</summary>
    public bool HasLinkHost => LinkHost is { Length: > 0 };

    /// <summary>底层的去处(由面板执行跳转)。</summary>
    public NotificationLink? Link => _item.Link;

    /// <summary>
    /// 第二个动作(摆在去处那一行的左端)。只认站内命令,且要有主动作、主动作不是外链:
    /// 外链那一行的左端摆着它的主机名,两样挤在一处会叠在一起。
    /// </summary>
    public NotificationLink? SecondaryLink =>
        _item.SecondaryLink is { CommandId.Length: > 0 } secondary && HasLink && !HasLinkHost ? secondary : null;

    /// <summary>有没有第二个动作。</summary>
    public bool HasSecondaryLink => SecondaryLink is not null;

    /// <summary>第二个动作的文案;没给就兜底成「查看」。</summary>
    public string SecondaryLinkLabel =>
        SecondaryLink is not { } secondary
            ? string.Empty
            : !string.IsNullOrWhiteSpace(secondary.Label)
                ? secondary.Label
                : Strings.Get("Notify_LinkOpen");

    /// <summary>相对时间(刚刚 / 5 分钟前 / 3 小时前 / 2 天前)。</summary>
    public string RelativeTime => FormatRelative(DateTime.UtcNow - _item.PublishedAt);

    /// <summary>由面板的时钟调用,刷新相对时间。</summary>
    public void RefreshRelativeTime() => this.RaisePropertyChanged(nameof(RelativeTime));

    /// <summary>换语言后让绑定重取 C# 侧拼的文案(类别徽标、链接按钮、相对时间)。</summary>
    public void RefreshLocalizedText()
    {
        this.RaisePropertyChanged(nameof(KindBadge));
        this.RaisePropertyChanged(nameof(SourceText));
        this.RaisePropertyChanged(nameof(LinkLabel));
        this.RaisePropertyChanged(nameof(SecondaryLinkLabel));
        RefreshRelativeTime();
    }

    /// <summary>把时间差说成人话;未来时间(源端时钟偏了)一律当作「刚刚」。</summary>
    public static string FormatRelative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return Strings.Get("Notify_TimeJustNow");
        }
        if (age < TimeSpan.FromHours(1))
        {
            return Strings.Format("Notify_TimeMinutes", (int)age.TotalMinutes);
        }
        if (age < TimeSpan.FromDays(1))
        {
            return Strings.Format("Notify_TimeHours", (int)age.TotalHours);
        }
        return age < TimeSpan.FromDays(30)
                   ? Strings.Format("Notify_TimeDays", (int)age.TotalDays)
                   : Strings.Format("Notify_TimeMonths", (int)(age.TotalDays / 30));
    }
}
