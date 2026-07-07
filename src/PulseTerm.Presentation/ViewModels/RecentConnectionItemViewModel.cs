using PulseTerm.Core.Models;

namespace PulseTerm.Presentation.ViewModels;

/// <summary>
/// 侧边栏“最近连接”单项:第一行显示“名称 - 分组”,第二行显示相对时间(设计稿 rc1/rc2/rc3)。
/// </summary>
public sealed class RecentConnectionItemViewModel
{
    public RecentConnectionItemViewModel(RecentConnectionEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        DisplayName = string.IsNullOrWhiteSpace(entry.GroupName)
            ? entry.Name
            : $"{entry.Name} - {entry.GroupName}";
        RelativeTime = FormatRelativeTime(entry.ConnectedAt);
        Tooltip = $"{entry.Username}@{entry.Host}:{entry.Port}\n双击连接";
    }

    public RecentConnectionEntry Entry { get; }

    /// <summary>“名称 - 分组”;无分组时仅名称。</summary>
    public string DisplayName { get; }

    /// <summary>“刚刚 / N 分钟前 / N 小时前 / 昨天 / N 天前 / yyyy-MM-dd”。</summary>
    public string RelativeTime { get; }

    /// <summary>悬停提示保留 user@host:port,列表本身不再显示该形式。</summary>
    public string Tooltip { get; }

    internal static string FormatRelativeTime(DateTimeOffset timestamp, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.Now;
        var elapsed = reference - timestamp;

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes} 分钟前";
        }

        if (elapsed < TimeSpan.FromHours(24))
        {
            return $"{(int)elapsed.TotalHours} 小时前";
        }

        if (timestamp.ToLocalTime().Date == reference.ToLocalTime().Date.AddDays(-1))
        {
            return "昨天";
        }

        if (elapsed < TimeSpan.FromDays(7))
        {
            return $"{(int)elapsed.TotalDays} 天前";
        }

        return timestamp.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
