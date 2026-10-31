using System.Text;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 插件往消息中心发的通知(SDK 2.1.3 的 <see cref="IUiApi.PostNotificationAsync" /> 与隔离模式的 <c>ui/notify</c>)
/// 在宿主这边的唯一入口:校验、去重、按插件静音、限频,再映射成 <see cref="NotificationItem" /> 交给消息中心。
/// 进程内(<c>PluginUiApi</c>)与隔离(<c>PluginCapabilityRouter</c> 经插件上下文的界面能力)走的是同一个实例。
/// </summary>
/// <remarks>
/// <para>
/// 顺序是 SDK 定的口径(<c>FakeUi</c> 与这里一致):先 <see cref="PluginNotification.Validate" />(发送方 id 由宿主给,
/// 不取自消息,冒充不了别的插件),再去重(同 id 已在列表里 → <see cref="PluginNotificationOutcome.AlreadyPosted" />,
/// 不算频率),再看静音(<see cref="PluginNotificationOutcome.Muted" />,同样不算频率),最后限频 ——
/// 每插件 60 秒滑动窗口里至多 <see cref="MaxPerMinute" /> 条新消息(蓝图 07 §7),超出 <see cref="PluginNotificationOutcome.RateLimited" />,不抛异常。
/// 进了列表之后,每插件至多留 <see cref="MaxRetainedPerPlugin" /> 条,超出丢它自己最旧的;总上限满了时消息中心也先丢插件消息,
/// 插件消息挤不掉宿主自己的。
/// </para>
/// <para>
/// 消息中心只收「要留存、可回看」的东西,插件消息也一样:列表里标「插件」徽标和「来自 插件名」,
/// 文案按纯文本显示、超长截断,控制字符与双向文本控制符去掉(否则一条消息能把来源那一行伪装成别人);
/// 动作只能是这个插件自己的命令(<see cref="PluginNotification.Validate" /> 已核过前缀),没有网址字段。
/// </para>
/// <para>
/// 静音按插件记、跨重启保留(<see cref="IAppDataStore" /> 的 <c>app_config/plugin_notification_mutes</c>);卸载插件时
/// <see cref="ForgetPluginAsync" /> 删掉它发过的消息和静音记录。
/// </para>
/// </remarks>
public sealed class PluginNotificationService(
    INotificationCenter center,
    IAppDataStore? store = null,
    Func<string, string?>? displayNameOf = null,
    TimeProvider? time = null)
{
    /// <summary>每插件每分钟最多几条<b>新</b>消息(SDK 契约的上限,宿主可以收得更紧,这里取同值)。</summary>
    public const int MaxPerMinute = 6;

    /// <summary>
    /// 每个插件在消息中心里至多留几条;超出时丢<b>它自己</b>最旧的那条。
    /// <para>
    /// 限频只管"发得多快",管不住"攒得多":一个插件每分钟换 6 个新 id 地发,半小时就能把消息中心
    /// <c>NotificationCenter.MaxItems</c>(200 条)占满,把别的插件的消息全挤掉。宿主自己的消息另由
    /// 消息中心的淘汰顺序护着(插件消息先丢),这一条管的是插件之间。
    /// </para>
    /// <para>
    /// 取 20:SDK 让插件把同类事件合并成一条摘要、"正常的插件一天也发不了几条",20 条够一个插件把最近几天的
    /// 几类提醒都留着回看;又只是总上限的十分之一,装十个插件全发满也挤不到总上限。
    /// </para>
    /// </summary>
    public const int MaxRetainedPerPlugin = 20;

    /// <summary>「来自 …」那一行的长度上限:清单的显示名本身不限长,列表里一行放不下。</summary>
    private const int MaxSourceLength = 64;

    private const string Collection = "app_config";
    private const string MutesDocument = "plugin_notification_mutes";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _recent = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _mutesLoad = new(1, 1);
    private HashSet<string>? _muted;

    private sealed record MutesDoc(List<string> Plugins);

    /// <summary>某插件发出的消息在消息中心里的 id 前缀(<c>plugin:&lt;插件 id&gt;:</c>);插件 id 里不会有冒号,前缀不会撞。</summary>
    public static string IdPrefix(string pluginId) => $"plugin:{pluginId}:";

    /// <summary>静音状态变了(插件管理页据此刷新那一行)。</summary>
    public event Action<string>? MutedChanged;

    /// <summary>投递一条插件消息;规则见类型说明。</summary>
    /// <param name="pluginId">宿主确认的发送方插件 id(实例 / 连接绑定的那个)。</param>
    /// <param name="notification">插件给的消息。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <exception cref="ArgumentException">消息不合格(见 <see cref="PluginNotification.Validate" />)。</exception>
    public async Task<PluginNotificationOutcome> PostAsync(string pluginId, PluginNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        notification.Validate(pluginId);
        string id = IdPrefix(pluginId) + notification.Id;
        if (center.Items.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
        {
            return PluginNotificationOutcome.AlreadyPosted;
        }
        if (await IsMutedAsync(pluginId, cancellationToken).ConfigureAwait(false))
        {
            return PluginNotificationOutcome.Muted;
        }
        DateTimeOffset now = _time.GetUtcNow();
        if (!TryReserve(pluginId, now))
        {
            return PluginNotificationOutcome.RateLimited;
        }
        var item = new NotificationItem
        {
            Id = id,
            Kind = NotificationKind.Plugin,
            Severity = notification.Level switch
            {
                PluginNotificationLevel.Warning => NotificationSeverity.Warning,
                PluginNotificationLevel.Critical => NotificationSeverity.Critical,
                _ => NotificationSeverity.Info
            },
            Title = Clean(notification.Title, PluginNotification.MaxTitleLength, keepLineBreaks: false),
            Body = notification.Body is { } body && Clean(body, PluginNotification.MaxBodyLength, keepLineBreaks: true) is { Length: > 0 } cleaned
                ? cleaned
                : null,
            PublishedAt = now.UtcDateTime,
            Source = Clean(displayNameOf?.Invoke(pluginId) is { Length: > 0 } name ? name : pluginId, MaxSourceLength, keepLineBreaks: false),
            // 动作走命令注册表:清单里声明过的命令有占位,插件没激活时点一下也会惰性激活再执行。
            Link = notification.ActionCommandId is { } command
                ? new NotificationLink
                {
                    Label = notification.ActionLabel is { } label ? Clean(label, 40, keepLineBreaks: false) : "",
                    CommandId = command
                }
                : null
        };
        int added;
        try
        {
            added = await center.PublishAsync([item], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(pluginId, now);
            throw;
        }
        if (added == 0)
        {
            // 与另一条同 id 的并发投递撞上了,对方先进了列表:不算这一条的频率。
            Release(pluginId, now);
            return PluginNotificationOutcome.AlreadyPosted;
        }
        // 这一条已经进了列表:再按每插件的留存上限丢它自己最旧的。不用调用方的令牌 ——
        // 投递已经成了,半路取消只会留下超额的几条,结果却报成失败。
        await TrimToRetainedCapAsync(pluginId).ConfigureAwait(false);
        return PluginNotificationOutcome.Posted;
    }

    /// <summary>这个插件留在消息中心里的消息超过 <see cref="MaxRetainedPerPlugin" /> 条时,从它自己最旧的丢起。</summary>
    private async Task TrimToRetainedCapAsync(string pluginId)
    {
        string prefix = IdPrefix(pluginId);
        string[] excess = [.. center.Items
            .Where(item => item.Id.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(item => item.PublishedAt)
            .Skip(MaxRetainedPerPlugin)
            .Select(item => item.Id)];
        foreach (string id in excess)
        {
            await center.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>这个插件的通知是否被用户静音了。</summary>
    public async Task<bool> IsMutedAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        HashSet<string> muted = await LoadMutesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return muted.Contains(pluginId);
        }
    }

    /// <summary>消息中心里此刻有没有这个插件发的消息(插件管理页只在有消息或已静音时摆出静音开关)。</summary>
    public bool HasMessages(string pluginId)
    {
        string prefix = IdPrefix(pluginId);
        return center.Items.Any(item => item.Id.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>静音或恢复某插件的通知;已经发进列表的消息不受影响(要删在消息中心里删)。</summary>
    public async Task SetMutedAsync(string pluginId, bool muted, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        HashSet<string> set = await LoadMutesAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        lock (_gate)
        {
            changed = muted ? set.Add(pluginId) : set.Remove(pluginId);
        }
        if (!changed)
        {
            return;
        }
        await SaveMutesAsync(cancellationToken).ConfigureAwait(false);
        MutedChanged?.Invoke(pluginId);
    }

    /// <summary>插件被卸载:删掉它发过的消息、静音记录与频率窗口。</summary>
    public async Task ForgetPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await center.RemoveByIdPrefixAsync(IdPrefix(pluginId), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _recent.Remove(pluginId);
        }
        await SetMutedAsync(pluginId, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动清扫:插件目录在宿主没运行时被删掉(手动删、换机器拷配置)也算卸载 —— 消息中心里不在
    /// <paramref name="installed" /> 中的插件发的消息一并删掉(与插件数据的卸载清扫同一口径)。
    /// </summary>
    public async Task SweepUninstalledAsync(IReadOnlySet<string> installed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installed);
        const string prefix = "plugin:";
        string[] orphans = [.. center.Items
            .Select(item => item.Id)
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal) && id.IndexOf(':', prefix.Length) > prefix.Length)
            .Select(id => id[prefix.Length..id.IndexOf(':', prefix.Length)])
            .Where(pluginId => !installed.Contains(pluginId))
            .Distinct(StringComparer.Ordinal)];
        foreach (string pluginId in orphans)
        {
            await ForgetPluginAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>占一个频率名额;窗口里已满时返回 false。名额在投递没成时用 <see cref="Release" /> 还回去。</summary>
    private bool TryReserve(string pluginId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_recent.TryGetValue(pluginId, out List<DateTimeOffset>? sent))
            {
                _recent.Add(pluginId, sent = []);
            }
            sent.RemoveAll(at => now - at >= Window);
            if (sent.Count >= MaxPerMinute)
            {
                return false;
            }
            sent.Add(now);
            return true;
        }
    }

    private void Release(string pluginId, DateTimeOffset reservedAt)
    {
        lock (_gate)
        {
            if (_recent.TryGetValue(pluginId, out List<DateTimeOffset>? sent))
            {
                int at = sent.LastIndexOf(reservedAt);
                if (at >= 0)
                {
                    sent.RemoveAt(at);
                }
            }
        }
    }

    private async Task<HashSet<string>> LoadMutesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _muted) is { } loaded)
        {
            return loaded;
        }
        await _mutesLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_muted is { } raced)
            {
                return raced;
            }
            HashSet<string> set = [with(StringComparer.Ordinal)];
            if (store is not null)
            {
                try
                {
                    if (await store.GetAsync<MutesDoc>(Collection, MutesDocument, cancellationToken).ConfigureAwait(false) is { Plugins: { } saved })
                    {
                        set.UnionWith(saved);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 读不出来就当没人静音:宁可多一条消息,也别因为存储坏了把用户想看的插件消息全吞掉。
                }
            }
            Volatile.Write(ref _muted, set);
            return set;
        }
        finally
        {
            _mutesLoad.Release();
        }
    }

    private async Task SaveMutesAsync(CancellationToken cancellationToken)
    {
        if (store is null || _muted is not { } set)
        {
            return;
        }
        List<string> snapshot;
        lock (_gate)
        {
            snapshot = [.. set.Order(StringComparer.Ordinal)];
        }
        try
        {
            await store.UpsertAsync(Collection, MutesDocument, new MutesDoc(snapshot), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 落盘失败只影响下次启动;本次运行里静音照样生效。
        }
    }

    /// <summary>
    /// 纯文本清洗:去掉控制字符(正文保留换行)与双向文本控制符,收拢首尾空白,超长截断。
    /// 列表按纯文本显示,这里只防排版被搅乱、来源被伪装,不做别的转义。
    /// </summary>
    internal static string Clean(string text, int maxLength, bool keepLineBreaks)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (char ch in text)
        {
            if (ch == '\n' && keepLineBreaks)
            {
                builder.Append(ch);
            }
            else if (ch == '\r' && keepLineBreaks)
            {
                // CRLF 收成 LF;单独的 CR 也当换行之外的空白丢掉。
            }
            else if (char.IsControl(ch) || IsBidiControl(ch))
            {
                if (ch is '\t' or '\n' or '\r' && builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(ch);
            }
        }
        string cleaned = builder.ToString().Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd() + "…";
    }

    /// <summary>U+061C、U+200E/F、U+202A–U+202E、U+2066–U+2069:能把一行字的显示顺序倒过来的那几个。</summary>
    private static bool IsBidiControl(char ch) =>
        ch is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
}
