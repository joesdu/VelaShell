using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 侧边栏“最近连接”单项:第一行显示“名称 - 分组”,第二行显示相对时间(设计稿 rc1/rc2/rc3)。
/// </summary>
public sealed class RecentConnectionItemViewModel(RecentConnectionEntry entry)
{
    public RecentConnectionEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));

    /// <summary>“名称 - 分组”;无分组时仅名称。</summary>
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(entry.GroupName)
                                             ? entry.Name
                                             : $"{entry.Name} - {entry.GroupName}";

    /// <summary>“刚刚 / N 分钟前 / N 小时前 / 昨天 / N 天前 / yyyy-MM-dd”。</summary>
    public string RelativeTime { get; } = FormatRelativeTime(entry.ConnectedAt);

    /// <summary>悬停提示保留 user@host:port,列表本身不再显示该形式。</summary>
    public string Tooltip { get; } = $"{entry.Username}@{entry.Host}:{entry.Port}\n{Strings.Get("Svc_DoubleClickToConnect")}";

    private static string FormatRelativeTime(DateTimeOffset timestamp, DateTimeOffset? now = null)
    {
        DateTimeOffset reference = now ?? DateTimeOffset.Now;
        TimeSpan elapsed = reference - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Strings.Get("Svc_JustNow");
        }
        if (elapsed < TimeSpan.FromHours(1))
        {
            return Strings.Format("Svc_MinutesAgo", (int)elapsed.TotalMinutes);
        }
        if (elapsed < TimeSpan.FromHours(24))
        {
            return Strings.Format("Svc_HoursAgo", (int)elapsed.TotalHours);
        }
        if (timestamp.ToLocalTime().Date == reference.ToLocalTime().Date.AddDays(-1))
        {
            return Strings.Get("Svc_Yesterday");
        }
        if (elapsed < TimeSpan.FromDays(7))
        {
            return Strings.Format("Svc_DaysAgo", (int)elapsed.TotalDays);
        }
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
