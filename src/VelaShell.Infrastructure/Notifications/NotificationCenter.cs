using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;

namespace VelaShell.Infrastructure.Notifications;

/// <summary>
/// <see cref="INotificationCenter" /> 的默认实现:内存列表 + SonnetDB 单文档持久化。
/// <para>
/// 消息**跨重启留存**(与文件传输面板相反):一条"有新版本"或者一篇公告,
/// 关掉应用第二天再看仍然成立,而一次传输的进度隔天已经没有意义。
/// </para>
/// </summary>
public sealed class NotificationCenter(IAppDataStore? store = null) : INotificationCenter
{
    /// <summary>持久化落点:文档集合 <c>notifications</c> 下的单份文档。</summary>
    private const string Collection = "notifications";

    private const string DocumentId = "inbox";

    /// <summary>
    /// 保留上限。资讯源可以一直发,而消息中心不是归档系统 —— 超出的按发布时间丢最旧的,
    /// 插件消息先丢(见 <see cref="Prune" />)。
    /// </summary>
    public const int MaxItems = 200;

    private readonly List<NotificationItem> _items = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public IReadOnlyList<NotificationItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <inheritdoc />
    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(item => !item.IsRead);
            }
        }
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return;
        }
        List<NotificationItem>? saved;
        try
        {
            saved = await store.GetAsync<List<NotificationItem>>(Collection, DocumentId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 存储损坏或旧格式:当作没有历史消息,后续保存会覆盖成新格式。
            return;
        }
        if (saved is not { Count: > 0 })
        {
            return;
        }
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(saved);
            Prune();
        }
        Changed?.Invoke();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(IEnumerable<NotificationItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        int added = 0;
        lock (_gate)
        {
            foreach (NotificationItem item in items)
            {
                // 同 id 的已经在了就跳过 —— 每次启动都重投同一条"有新版本"时,
                // 覆盖会把用户已经读过的又变回未读。
                if (_items.Exists(existing => existing.Id == item.Id))
                {
                    continue;
                }
                _items.Add(item);
                added++;
            }
            if (added == 0)
            {
                return 0;
            }
            Prune();
        }
        Changed?.Invoke();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <inheritdoc />
    public Task MarkReadAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(items =>
        {
            NotificationItem? target = items.Find(item => item.Id == id);
            if (target is null || target.IsRead)
            {
                return false;
            }
            target.IsRead = true;
            return true;
        }, cancellationToken);

    /// <inheritdoc />
    public Task MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(items =>
        {
            bool changed = false;
            foreach (NotificationItem item in items.Where(item => !item.IsRead))
            {
                item.IsRead = true;
                changed = true;
            }
            return changed;
        }, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(items => items.RemoveAll(item => item.Id == id) > 0, cancellationToken);

    /// <inheritdoc />
    public async Task<int> RemoveByIdPrefixAsync(string idPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idPrefix);
        int removed = 0;
        await MutateAsync(items =>
        {
            removed = items.RemoveAll(item => item.Id.StartsWith(idPrefix, StringComparison.Ordinal));
            return removed > 0;
        }, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(items =>
        {
            if (items.Count == 0)
            {
                return false;
            }
            items.Clear();
            return true;
        }, cancellationToken);

    private async Task MutateAsync(Func<List<NotificationItem>, bool> mutate, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!mutate(_items))
            {
                return;
            }
        }
        Changed?.Invoke();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 丢掉过期条目,按发布时间倒序,并把超出上限的最旧那些截掉。调用方须持锁。
    /// <para>
    /// <b>超出上限时先丢插件消息(最旧的先丢),丢完了才轮到宿主自己的</b>:插件消息再多也挤不掉宿主的。
    /// 宿主有几条提示只发一次 —— 本机加密密钥找不到 / 已换新(master-key:*)、拉到的设置开着 Webhook 告警却没有地址
    /// (sync:webhook-url-missing:*,按指纹去重)—— 被挤掉就再也不会回来,而它们恰恰是「密码怎么解不开」
    /// 「告警怎么不响」的唯一线索。按插件限频(每分钟 6 条)管不住"攒得多":一个插件不停换 id 地发,
    /// 半小时就能把 200 条占满。每个插件自己留几条另由 <c>PluginNotificationService</c> 管。
    /// </para>
    /// </summary>
    private void Prune()
    {
        DateTime now = DateTime.UtcNow;
        _items.RemoveAll(item => item.ExpiresAt is { } expiry && expiry <= now);
        _items.Sort((left, right) => right.PublishedAt.CompareTo(left.PublishedAt));
        int excess = _items.Count - MaxItems;
        // 从最旧的(列表尾部)往前丢插件消息。
        for (int i = _items.Count - 1; i >= 0 && excess > 0; i--)
        {
            if (_items[i].Kind == NotificationKind.Plugin)
            {
                _items.RemoveAt(i);
                excess--;
            }
        }
        if (excess > 0)
        {
            _items.RemoveRange(_items.Count - excess, excess);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }
        List<NotificationItem> snapshot;
        lock (_gate)
        {
            snapshot = [.. _items];
        }
        try
        {
            await store.UpsertAsync(Collection, DocumentId, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 落盘失败不影响本次运行里看到的消息;下次变更会再试。
        }
    }
}
