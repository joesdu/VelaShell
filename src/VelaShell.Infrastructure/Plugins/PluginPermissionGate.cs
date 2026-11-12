using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>用户对一次敏感能力请求的裁决。</summary>
public enum PluginPermissionDecision
{
    /// <summary>拒绝本次。</summary>
    Deny,

    /// <summary>仅允许本次。</summary>
    AllowOnce,

    /// <summary>本次运行期间允许(应用退出即失效)。</summary>
    AllowSession,

    /// <summary>始终允许(按插件持久化,可在插件管理页撤销)。</summary>
    AllowAlways
}

/// <summary>授权对话框 SPI(由 UI 层实现:弹窗展示插件、目标与内容预览,给出四种选择)。</summary>
public interface IPluginPermissionPrompt
{
    /// <summary>请求用户裁决一次终端回写。</summary>
    Task<PluginPermissionDecision> RequestTerminalWriteAsync(string pluginId, string sessionLabel, string inputPreview,
        CancellationToken cancellationToken);

    /// <summary>
    /// 请求用户裁决一次「按已保存配置开会话」。
    /// </summary>
    /// <param name="pluginId">发起请求的插件 id。</param>
    /// <param name="target">要连的那台机器(会话树上的名字 + user@host:port)。</param>
    /// <param name="reason">插件给的理由,<b>原样显示</b> —— 用户是照着它决定点不点同意的。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<PluginPermissionDecision> RequestSessionOpenAsync(string pluginId, string target, string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// 逐任务审批具体上传;仅 AllowOnce 有效,不授予其它文件或终端能力。<paramref name="sizeBytes" /> 是已打开源句柄的长度,
    /// 确认框照实显示 —— 不设固定大小上限之后,用户只能从这里看出这次要传多大(Linux / macOS 上还要同等大小的本机快照空间)。
    /// </summary>
    Task<PluginPermissionDecision> RequestFileUploadAsync(string pluginId, string sessionLabel, string localPath,
        string remotePath, bool overwrite, long sizeBytes, CancellationToken cancellationToken)
        => Task.FromResult(PluginPermissionDecision.Deny);

    /// <summary>
    /// 逐任务审批具体下载;仅 AllowOnce 有效,不授予其它文件或终端能力。<paramref name="sizeBytes" /> 是审批之前对远端源
    /// stat 到的大小,确认框与上传一样照实显示(维护者 2026-10-25 定,第二轮第 27 条)。
    /// </summary>
    Task<PluginPermissionDecision> RequestFileDownloadAsync(string pluginId, string sessionLabel, string remotePath,
        string localPath, bool overwrite, long sizeBytes, CancellationToken cancellationToken)
        => Task.FromResult(PluginPermissionDecision.Deny);
}

/// <summary>上传专用的单任务授权租约;撤销立即生效,与终端/开会话授权账本无关。</summary>
public sealed class UploadPermissionLease : IDisposable
{
    private readonly CancellationTokenSource _revoked = new();
    private readonly Action<UploadPermissionLease> _release;
    internal UploadPermissionLease(Action<UploadPermissionLease> release)
    {
        _release = release;
        Revocation = _revoked.Token;
    }
    /// <summary>撤销信号;只影响该任务,不影响其它能力或其它插件。</summary>
    public CancellationToken Revocation { get; }
    internal bool Approved { get; set; }
    internal volatile bool _active = true;
    private int _revokedFlag;
    internal bool IsRevoked => Volatile.Read(ref _revokedFlag) != 0;
    internal void MarkRevoked() => Interlocked.Exchange(ref _revokedFlag, 1);
    internal void SignalRevoked()
    {
        try { _revoked.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* 单个接收方的取消回调失败不能阻止其它上传租约撤销。 */ }
    }
    private int _disposed;
    /// <summary>解除账本登记并释放租约。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        MarkRevoked();
        _release(this);
        SignalRevoked();
        _revoked.Dispose();
    }
}

/// <summary>下载专用的单任务授权租约;不复用上传授权。</summary>
public sealed class DownloadPermissionLease : IDisposable
{
    private readonly CancellationTokenSource _revoked = new();
    private readonly Action<DownloadPermissionLease> _release;
    internal DownloadPermissionLease(Action<DownloadPermissionLease> release) { _release = release; Revocation = _revoked.Token; }
    /// <summary>本任务的撤销信号;待审批与执行中的下载都必须观察。</summary>
    public CancellationToken Revocation { get; }
    internal bool Approved { get; set; }
    internal volatile bool _active = true;
    private int _revokedFlag;
    internal bool IsRevoked => Volatile.Read(ref _revokedFlag) != 0;
    internal void MarkRevoked() => Interlocked.Exchange(ref _revokedFlag, 1);
    internal void SignalRevoked() { try { _revoked.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }
    private int _disposed;
    /// <summary>撤销本任务并解除下载授权账本登记。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        MarkRevoked(); _release(this); SignalRevoked(); _revoked.Dispose();
    }
}

/// <summary>
/// 敏感能力的授权闸(蓝图 06 的最小可用形态):
/// 始终允许 → SonnetDB 持久化(按插件);本次运行允许 → 内存;仅本次 → 放行一次;
/// 拒绝 → 本次拒绝。同插件的并发请求串行化,不弹对话框风暴。无提示器的宿主一律拒绝。
/// </summary>
/// <remarks>
/// 两类能力(终端回写 / 开会话)各记各的账:落库分两个文档,内存也分两套。
/// 合成一本账就意味着"允许它替我敲一行命令"顺带把"允许它自己连生产机"也批了 ——
/// 这两件事的分量差得远,用户在确认框上点的也不是同一个"是"。
/// </remarks>
public sealed class PluginPermissionGate(IAppDataStore? store, IPluginPermissionPrompt? prompt)
{
    private const string Collection = "app_config";

    private sealed record PermissionsDoc(List<string> AlwaysAllow);

    /// <summary>一类能力的授权账本(持久 + 本次运行)。</summary>
    private sealed class Grants(string documentId)
    {
        public string DocumentId { get; } = documentId;
        public HashSet<string> SessionAllowed { get; } = [with(StringComparer.Ordinal)];
        public HashSet<string>? AlwaysAllowed { get; set; }
    }

    /// <summary>终端回写。文档 id 是历史名字,不改 —— 改了等于把用户已有的授权丢掉。</summary>
    private readonly Grants _terminalWrite = new("plugin_terminal_write_permissions");

    /// <summary>按已保存配置开会话。</summary>
    private readonly Grants _sessionOpen = new("plugin_session_open_permissions");

    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private readonly Lock _uploadGate = new();
    private readonly Dictionary<string, HashSet<UploadPermissionLease>> _uploads = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, HashSet<DownloadPermissionLease>> _downloads = [with(StringComparer.Ordinal)];

    /// <summary>登记待审批的单任务下载租约,使撤权也能取消等待用户裁决的请求。</summary>
    public DownloadPermissionLease CreateDownloadLease(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_uploadGate)
        {
            if (!_downloads.TryGetValue(pluginId, out HashSet<DownloadPermissionLease>? leases)) _downloads.Add(pluginId, leases = []);
            var lease = new DownloadPermissionLease(value =>
            {
                lock (_uploadGate)
                {
                    if (_downloads.TryGetValue(pluginId, out HashSet<DownloadPermissionLease>? current))
                    {
                        current.Remove(value);
                        if (current.Count == 0) _downloads.Remove(pluginId);
                    }
                }
            });
            leases.Add(lease); return lease;
        }
    }

    /// <summary>默认逐任务请求可信下载审批,不复用其它能力授权；宿主策略可自动批准当前任务。</summary>
    /// <remarks><c>onApproved</c> 与 <see cref="CheckUploadAsync" /> 的同名参数一样:批准生效时在登记批准的同一把锁里调用。</remarks>
    public async Task<bool> CheckDownloadAsync(DownloadPermissionLease lease, string pluginId, string sessionLabel,
        string remotePath, string localPath, bool overwrite, long sizeBytes, CancellationToken cancellationToken,
        Action? onApproved = null, bool autoApprove = false)
    {
        if (autoApprove)
        {
            lock (_uploadGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lease.Revocation.ThrowIfCancellationRequested();
                if (lease.IsRevoked || !_downloads.TryGetValue(pluginId, out HashSet<DownloadPermissionLease>? leases)
                    || !leases.Contains(lease))
                {
                    return false;
                }

                lease.Approved = true;
                onApproved?.Invoke();
                return true;
            }
        }
        if (prompt is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Revocation);
        await _promptGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_uploadGate) if (!_downloads.TryGetValue(pluginId, out HashSet<DownloadPermissionLease>? leases) || !leases.Contains(lease)) return false;
            Task<PluginPermissionDecision> pending = prompt.RequestFileDownloadAsync(pluginId, sessionLabel, remotePath, localPath,
                overwrite, sizeBytes, linked.Token);
            PluginPermissionDecision decision;
            try { decision = await pending.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            linked.Token.ThrowIfCancellationRequested();
            lock (_uploadGate)
            {
                linked.Token.ThrowIfCancellationRequested();
                lease.Approved = !lease.IsRevoked && decision == PluginPermissionDecision.AllowOnce;
                if (lease.Approved) onApproved?.Invoke();
                return lease.Approved;
            }
        }
        finally { _promptGate.Release(); }
    }

    /// <summary>登记尚待审批的单任务租约;因此撤销也能取消正在等用户裁决的请求。</summary>
    public UploadPermissionLease CreateUploadLease(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_uploadGate)
        {
            if (!_uploads.TryGetValue(pluginId, out HashSet<UploadPermissionLease>? leases)) _uploads.Add(pluginId, leases = []);
            var lease = new UploadPermissionLease(value =>
            {
                lock (_uploadGate)
                {
                    if (_uploads.TryGetValue(pluginId, out HashSet<UploadPermissionLease>? current))
                    {
                        current.Remove(value);
                        if (current.Count == 0) _uploads.Remove(pluginId);
                    }
                }
            });
            leases.Add(lease);
            return lease;
        }
    }

    /// <summary>默认逐次请求可信上传审批,不复用其它能力授权；宿主策略可自动批准当前任务。</summary>
    /// <remarks>
    /// <c>onApproved</c> 在批准生效时、登记批准的同一把锁里调用,让调用方把任务状态与「已批准」一起推进:插件此刻读到的快照
    /// 要么还在等确认、要么已经批准,不会有「其实批了、快照还说在等」的空当(插件停止时按快照撤等确认的任务,
    /// 那段空当里读到的旧阶段会把用户刚批准的传输撤掉)。回调只许做不阻塞的内存操作:可以取任务自己的锁
    /// (锁序固定为本闸 → 任务),不许回头调用本闸。
    /// </remarks>
    public async Task<bool> CheckUploadAsync(UploadPermissionLease lease, string pluginId, string sessionLabel,
        string localPath, string remotePath, bool overwrite, long sizeBytes, CancellationToken cancellationToken,
        Action? onApproved = null, bool autoApprove = false)
    {
        if (autoApprove)
        {
            lock (_uploadGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lease.Revocation.ThrowIfCancellationRequested();
                if (lease.IsRevoked || !_uploads.TryGetValue(pluginId, out HashSet<UploadPermissionLease>? leases)
                    || !leases.Contains(lease))
                {
                    return false;
                }

                lease.Approved = true;
                onApproved?.Invoke();
                return true;
            }
        }
        if (prompt is null) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Revocation);
        await _promptGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_uploadGate)
            {
                if (!_uploads.TryGetValue(pluginId, out HashSet<UploadPermissionLease>? leases) || !leases.Contains(lease)) return false;
            }
            Task<PluginPermissionDecision> pending = prompt.RequestFileUploadAsync(pluginId, sessionLabel, localPath,
                remotePath, overwrite, sizeBytes, linked.Token);
            PluginPermissionDecision decision;
            try { decision = await pending.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            linked.Token.ThrowIfCancellationRequested();
            lock (_uploadGate)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (lease.IsRevoked) return false;
                lease.Approved = decision == PluginPermissionDecision.AllowOnce;
                if (lease.Approved) onApproved?.Invoke();
                return lease.Approved;
            }
        }
        finally { _promptGate.Release(); }
    }

    /// <summary>是否放行本次终端回写;必要时弹授权对话框。</summary>
    public Task<bool> CheckTerminalWriteAsync(string pluginId, string sessionLabel, string inputPreview,
        CancellationToken cancellationToken)
        => CheckAsync(_terminalWrite, pluginId,
            ct => prompt!.RequestTerminalWriteAsync(pluginId, sessionLabel, inputPreview, ct), cancellationToken);

    /// <summary>
    /// 是否放行本次「按已保存配置开会话」;必要时弹授权对话框。
    /// </summary>
    /// <remarks>
    /// 授权按插件计,而不是按 (插件, 机器) 计:后者更精确,但也意味着一个 IM 桥接插件
    /// 要为运维手上每一台机器各弹一次框 —— 用户会在第三台上开始盲点。分寸取在
    /// "这个插件可以替我连机器"这一层,想收回就去插件管理页撤销。
    /// </remarks>
    public Task<bool> CheckSessionOpenAsync(string pluginId, string target, string reason,
        CancellationToken cancellationToken)
        => CheckAsync(_sessionOpen, pluginId,
            ct => prompt!.RequestSessionOpenAsync(pluginId, target, reason, ct), cancellationToken);

    /// <summary>撤销某插件的**全部**授权(持久 + 会话内),插件管理页使用。</summary>
    public async Task RevokeAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        UploadPermissionLease[] uploads;
        // 锁内只同步标记撤销,锁外才执行 CancellationToken 回调,避免回调修改账本或锁倒置。
        lock (_uploadGate)
        {
            uploads = _uploads.TryGetValue(pluginId, out HashSet<UploadPermissionLease>? live) ? [.. live] : [];
            foreach (UploadPermissionLease lease in uploads) lease.MarkRevoked();
        }
        foreach (UploadPermissionLease lease in uploads) lease.SignalRevoked();
        DownloadPermissionLease[] downloads;
        lock (_uploadGate)
        {
            downloads = _downloads.TryGetValue(pluginId, out HashSet<DownloadPermissionLease>? live) ? [.. live] : [];
            foreach (DownloadPermissionLease lease in downloads) lease.MarkRevoked();
        }
        foreach (DownloadPermissionLease lease in downloads) lease.SignalRevoked();
        foreach (Grants grants in Ledgers)
        {
            lock (grants.SessionAllowed)
            {
                grants.SessionAllowed.Remove(pluginId);
            }
            HashSet<string> always = await LoadAlwaysAsync(grants, cancellationToken).ConfigureAwait(false);
            if (always.Remove(pluginId) && store is not null)
            {
                await store.UpsertAsync(Collection, grants.DocumentId, new PermissionsDoc([.. always]), cancellationToken)
                           .ConfigureAwait(false);
            }
        }
    }

    /// <summary>某插件当前是否持有**任何**授权(持久或会话内、任一类能力),插件管理页展示用。</summary>
    public async Task<bool> HasGrantAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        lock (_uploadGate)
            if (_uploads.TryGetValue(pluginId, out HashSet<UploadPermissionLease>? uploads) && uploads.Any(lease => lease._active && !lease.IsRevoked)) return true;
        lock (_uploadGate)
            if (_downloads.TryGetValue(pluginId, out HashSet<DownloadPermissionLease>? downloads) && downloads.Any(lease => lease._active && !lease.IsRevoked)) return true;
        foreach (Grants grants in Ledgers)
        {
            if (await IsAlwaysAllowedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            lock (grants.SessionAllowed)
            {
                if (grants.SessionAllowed.Contains(pluginId))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private IEnumerable<Grants> Ledgers
    {
        get
        {
            yield return _terminalWrite;
            yield return _sessionOpen;
        }
    }

    private async Task<bool> CheckAsync(Grants grants, string pluginId,
        Func<CancellationToken, Task<PluginPermissionDecision>> ask, CancellationToken cancellationToken)
    {
        if (await IsGrantedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (prompt is null)
        {
            return false; // 无 UI 可问 = 不放行(绝不静默授权)
        }
        // 串行化提问:并发请求只弹一个框;等待期间别人拿到的授权对后续请求同样生效。
        await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsGrantedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            PluginPermissionDecision decision = await ask(cancellationToken).ConfigureAwait(false);
            switch (decision)
            {
                case PluginPermissionDecision.AllowOnce:
                    return true;
                case PluginPermissionDecision.AllowSession:
                    lock (grants.SessionAllowed)
                    {
                        grants.SessionAllowed.Add(pluginId);
                    }
                    return true;
                case PluginPermissionDecision.AllowAlways:
                    await PersistAlwaysAsync(grants, pluginId, cancellationToken).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private async Task<bool> IsGrantedAsync(Grants grants, string pluginId, CancellationToken cancellationToken)
    {
        if (await IsAlwaysAllowedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        lock (grants.SessionAllowed)
        {
            return grants.SessionAllowed.Contains(pluginId);
        }
    }

    private async Task<bool> IsAlwaysAllowedAsync(Grants grants, string pluginId, CancellationToken cancellationToken) =>
        (await LoadAlwaysAsync(grants, cancellationToken).ConfigureAwait(false)).Contains(pluginId);

    private async Task<HashSet<string>> LoadAlwaysAsync(Grants grants, CancellationToken cancellationToken)
    {
        if (grants.AlwaysAllowed is not null)
        {
            return grants.AlwaysAllowed;
        }
        PermissionsDoc? doc = store is null
            ? null
            : await store.GetAsync<PermissionsDoc>(Collection, grants.DocumentId, cancellationToken).ConfigureAwait(false);
        return grants.AlwaysAllowed = new(doc?.AlwaysAllow ?? [], StringComparer.Ordinal);
    }

    private async Task PersistAlwaysAsync(Grants grants, string pluginId, CancellationToken cancellationToken)
    {
        HashSet<string> always = await LoadAlwaysAsync(grants, cancellationToken).ConfigureAwait(false);
        always.Add(pluginId);
        if (store is not null)
        {
            await store.UpsertAsync(Collection, grants.DocumentId, new PermissionsDoc([.. always]), cancellationToken)
                       .ConfigureAwait(false);
        }
    }
}
