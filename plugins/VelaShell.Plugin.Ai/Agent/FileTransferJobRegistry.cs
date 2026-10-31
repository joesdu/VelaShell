using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>后台线程发出的、只属于一个对话轮次的上传进度；撤权后不再包含路径或快照。</summary>
/// <param name="OwnerId">入口持有的稳定对话所有者。</param>
/// <param name="RunId">入口生成的运行代际。</param>
/// <param name="TransferId">不透明的宿主传输标识。</param>
/// <param name="LocalPath">已批准的本地源路径；撤权时为空。</param>
/// <param name="RemotePath">最终远端目标路径；撤权时为空。</param>
/// <param name="Snapshot">宿主权威快照；撤权时为空。</param>
/// <param name="Withheld">授权已撤销，必须隐藏旧元数据。</param>
/// <param name="MonitoringUnavailable">本地进度观察暂不可确认，不修改宿主状态或序号。</param>
public sealed record FileTransferJobUpdate(string OwnerId, string RunId, string TransferId,
    string LocalPath, string RemotePath, RemoteTransferSnapshot? Snapshot, bool Withheld = false,
    bool MonitoringUnavailable = false);

/// <summary>
/// 插件自己在交给宿主<b>之前</b>就拒绝了这次启动(回执额度用尽、操作身份冲突):宿主什么都没收到,
/// 结论是确定的"没开始",不能当成回执丢失去标记不确定、把整轮停成 TaskUnknown。
/// </summary>
internal sealed class FileTransferRejectedException(string message) : InvalidOperationException(message);

/// <summary>
/// 插件生命周期内共享的 AI 上传登记与本地进度监听。请求、模型与 run 取消不取消宿主任务；
/// 用户必须显式取消传输。插件停用由宿主及 context.Shutdown 收尾。
/// </summary>
public sealed class FileTransferJobRegistry
{
    private static readonly ConditionalWeakTable<IPluginContext, FileTransferJobRegistry> Shared = [];
    private readonly IRemoteFsApi _remoteFs;
    private readonly CancellationToken _lifetime;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = [with(StringComparer.Ordinal)];
    private sealed record StartReceipt(FileTransferRunScope Scope, string Signature, Task<RemoteTransferSnapshot> Result);
    private readonly Dictionary<string, StartReceipt> _operations = [with(StringComparer.Ordinal)];
    // 同一本机目标(规范化全路径;不分大小写,宁严勿松)的下载按启动先后排,send_file 只认最近一次已成功提交的那次。
    private readonly Dictionary<string, List<StartReceipt>> _downloadTargets = [with(StringComparer.OrdinalIgnoreCase)];

    private FileTransferJobRegistry(IPluginContext context)
    {
        _remoteFs = context.RemoteFs;
        _lifetime = context.Shutdown;
    }

    /// <summary>Bridge 每轮释放工具箱不会销毁这里的监听或取消已批准上传。</summary>
    public static FileTransferJobRegistry For(IPluginContext context)
        => Shared.GetValue(context, static key => new FileTransferJobRegistry(key));

    /// <summary>传输身份能力由入口生成，不接受模型指定 owner/run。</summary>
    public FileTransferRunScope BeginRun(string ownerId, string runId,
        Func<string, CancellationToken, Task<bool>>? authorize = null, bool denialEndsRun = false)
        => new(this, ownerId, runId, authorize, denialEndsRun);

    internal Task<RemoteTransferSnapshot> StartAsync(FileTransferRunScope scope, string sessionId,
        string localPath, string remotePath, string operationId, bool overwrite, CancellationToken token)
        => StartCoreAsync(scope, RemoteTransferDirection.Upload, sessionId, localPath, remotePath, operationId, overwrite, token);

    internal Task<RemoteTransferSnapshot> StartDownloadAsync(FileTransferRunScope scope, string sessionId,
        string remotePath, string localPath, string operationId, bool overwrite, CancellationToken token)
        => StartCoreAsync(scope, RemoteTransferDirection.Download, sessionId, localPath, remotePath, operationId, overwrite, token);

    private async Task<RemoteTransferSnapshot> StartCoreAsync(FileTransferRunScope scope, RemoteTransferDirection direction,
        string sessionId, string localPath, string remotePath, string operationId, bool overwrite, CancellationToken token)
    {
        if (!_remoteFs.SupportsTransferOperations || (direction == RemoteTransferDirection.Download && !_remoteFs.SupportsDownloadOperations))
            throw new NotSupportedException("This host does not support independent transfers; synchronous fallback is disabled.");
        await AuthorizeSessionAsync(scope, sessionId, token).ConfigureAwait(false);
        string signature = JsonSerializer.Serialize(new { direction, sessionId, localPath, remotePath, overwrite });
        // 规范化放在登记之前:路径不合法就什么都不记,当作启动前被拒。
        string? target = direction == RemoteTransferDirection.Download ? Path.GetFullPath(localPath) : null;
        TaskCompletionSource<RemoteTransferSnapshot>? starter = null;
        Task<RemoteTransferSnapshot> receipt;
        lock (_gate)
        {
            if (_operations.TryGetValue(operationId, out StartReceipt? prior))
            {
                if (!ReferenceEquals(prior.Scope, scope) || prior.Signature != signature)
                    throw new FileTransferRejectedException("Transfer operation identity conflict; do not replay this operation.");
                receipt = prior.Result;
            }
            else
            {
                if (_operations.Count >= 1024) throw new FileTransferRejectedException("The transfer task budget is exhausted.");
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                receipt = starter.Task;
                var started = new StartReceipt(scope, signature, receipt);
                _operations.Add(operationId, started);
                if (target is not null)
                {
                    if (!_downloadTargets.TryGetValue(target, out List<StartReceipt>? attempts))
                        _downloadTargets.Add(target, attempts = []);
                    attempts.Add(started);
                }
                scope._pendingStarts++;
            }
        }
        if (starter is not null) _ = CompleteStartAsync(scope, direction, sessionId, localPath, remotePath, operationId, overwrite, starter);
        RemoteTransferSnapshot result = await receipt.WaitAsync(token).ConfigureAwait(false);
        return Deliver(Find(scope, result.TransferId));
    }

    private async Task CompleteStartAsync(FileTransferRunScope scope, RemoteTransferDirection direction, string sessionId, string localPath,
        string remotePath, string operationId, bool overwrite, TaskCompletionSource<RemoteTransferSnapshot> completion)
    {
        RemoteTransferSnapshot? result = null;
        Exception? failure = null;
        try
        {
            result = await ReceiveStartAsync(scope, direction, sessionId, localPath, remotePath, operationId, overwrite).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (!IsDefiniteRejection(ex))
                scope._uncertainStart = true;
        }
        finally { lock (_gate) scope._pendingStarts--; }
        if (failure is null)
        {
            completion.TrySetResult(result!);
        }
        else
        {
            completion.TrySetException(failure);
            // 没有等待者的迟到错误也必须被观察；保留账本身份，禁止自动新建上传。
            _ = completion.Task.Exception;
        }
    }

    private async Task<RemoteTransferSnapshot> StartRemoteAsync(RemoteTransferDirection direction, string sessionId,
        string localPath, string remotePath, string operationId, bool overwrite)
        => direction == RemoteTransferDirection.Download
            ? await _remoteFs.StartDownloadAsync(sessionId, remotePath, localPath,
                new RemoteDownloadOptions { OperationId = operationId, Overwrite = overwrite }, _lifetime).ConfigureAwait(false)
            : await _remoteFs.StartUploadAsync(sessionId, localPath, remotePath,
                new RemoteUploadOptions { OperationId = operationId, Overwrite = overwrite }, _lifetime).ConfigureAwait(false);

    private async Task<RemoteTransferSnapshot> ReceiveStartAsync(FileTransferRunScope scope, RemoteTransferDirection direction, string sessionId,
        string localPath, string remotePath, string operationId, bool overwrite)
    {
        RemoteTransferSnapshot snapshot;
        try
        {
            snapshot = await StartRemoteAsync(direction, sessionId, localPath, remotePath, operationId, overwrite).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // 短请求回执丢失时只用原 OperationId 重取一次；从不另造 id 重传。
            snapshot = await StartRemoteAsync(direction, sessionId, localPath, remotePath, operationId, overwrite).ConfigureAwait(false);
        }
        Job job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(snapshot.TransferId, out job!))
            {
                job = new Job(scope, snapshot);
                _jobs.Add(snapshot.TransferId, job);
            }
            else if (!ReferenceEquals(job._scope, scope))
            {
                throw new InvalidOperationException("Transfer identity conflict.");
            }
        }
        try
        {
            await PublishAsync(job, snapshot).ConfigureAwait(false);
            bool cancelLate;
            lock (_gate) cancelLate = scope._cancelAllRequested;
            if (cancelLate && NeedsCleanup(snapshot))
            {
                RemoteTransferSnapshot cancelled = await _remoteFs.CancelTransferAsync(snapshot.TransferId,
                    snapshot.Generation, _lifetime).ConfigureAwait(false);
                await PublishAsync(job, cancelled).ConfigureAwait(false);
            }
        }
        finally
        {
            // 任务已经登记:发布或迟到取消哪一步抛了(授权回调读会话失败、取消请求断网),都得有人盯着它走到终态。
            // 没有观察者,卡片冻住、清理与 send_file 守卫一直以为它还在跑。
            StartWatcher(job);
        }
        // 后台收据登记不是模型读取；迟到终态仍必须由协调器消费一次。
        return snapshot;
    }

    internal async Task<RemoteTransferSnapshot> ReadAsync(FileTransferRunScope scope, string id, CancellationToken token,
        bool consume = true)
    {
        Job job = Find(scope, id);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        RemoteTransferSnapshot snapshot;
        try { snapshot = await _remoteFs.GetTransferAsync(id, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PluginPermissionDeniedException)
        {
            await WithholdAsync(job).ConfigureAwait(false);
            throw new UnauthorizedAccessException("Transfer metadata is withheld.");
        }
        await ObservedAsync(job, snapshot).ConfigureAwait(false);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        StartWatcher(job);
        if (consume) return Deliver(job);
        lock (_gate) return job._snapshot;
    }

    internal async Task<RemoteTransferSnapshot> WaitAsync(FileTransferRunScope scope, string id, TimeSpan wait,
        CancellationToken token)
    {
        if (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(IRemoteFsApi.MaxTransferWaitSeconds))
            throw new ArgumentOutOfRangeException(nameof(wait));
        Job job = Find(scope, id);
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        try { await job._completion.Task.WaitAsync(wait, token).ConfigureAwait(false); }
        catch (TimeoutException) { /* 只是本地等待窗口，不是传输执行死线。 */ }
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        return Deliver(job);
    }

    internal async Task CancelAsync(FileTransferRunScope scope, string id, long generation, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Job job = Find(scope, id);
        // 清理不交出返回的快照，且不受读取授权撤销阻挡。
        RemoteTransferSnapshot snapshot = await _remoteFs.CancelTransferAsync(id, generation, token).ConfigureAwait(false);
        await PublishAsync(job, snapshot).ConfigureAwait(false);
    }

    internal async Task<RemoteTransferSnapshot> ResumeAsync(FileTransferRunScope scope, string id,
        RemoteTransferResumeOptions options, CancellationToken token)
    {
        Job job = Find(scope, id);
        if (job._snapshot.Direction == RemoteTransferDirection.Download)
            throw new NotSupportedException("Downloaded files cannot be resumed by the upload resume operation.");
        await AuthorizeAsync(job, token).ConfigureAwait(false);
        await AuthorizeSessionAsync(scope, options.TargetSessionId, token).ConfigureAwait(false);
        RemoteTransferSnapshot snapshot = await _remoteFs.ResumeTransferAsync(id, options, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (snapshot.Generation > job._snapshot.Generation)
            {
                job._completion = Signal();
                job._delivered = false;
            }
        }
        await PublishAsync(job, snapshot).ConfigureAwait(false);
        StartWatcher(job);
        return Deliver(job);
    }

    private Job Find(FileTransferRunScope scope, string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? job) || !ReferenceEquals(job._scope, scope))
                throw new FileNotFoundException("No transfer task with this id belongs to this run.");
            return job;
        }
    }

    private static async Task AuthorizeSessionAsync(FileTransferRunScope scope, string sessionId, CancellationToken token)
    {
        if (scope._authorize is { } authorize && !await authorize(sessionId, token).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Authorization for this transfer was revoked; metadata is withheld.");
    }

    private async Task AuthorizeAsync(Job job, CancellationToken token)
    {
        try { await AuthorizeSessionAsync(job._scope, job._snapshot.SessionId, token).ConfigureAwait(false); }
        catch (UnauthorizedAccessException)
        {
            await WithholdAsync(job).ConfigureAwait(false);
            throw;
        }
        lock (_gate)
            if (job._withheld) throw new UnauthorizedAccessException("Transfer metadata is withheld.");
    }

    private async Task WithholdAsync(Job job)
    {
        bool first;
        lock (_gate) { first = !job._withheld; job._withheld = true; job._completion.TrySetResult(); }
        if (!first) return;
        job._scope.Publish(new(job._scope.OwnerId, job._scope.RunId, job._snapshot.TransferId, "", "", null, true));
        try
        {
            await _remoteFs.CancelTransferAsync(job._snapshot.TransferId, job._snapshot.Generation, _lifetime).ConfigureAwait(false);
        }
        catch { /* 尽力清理；撤权后的元数据始终隐藏。 */ }
    }

    private async Task PublishAsync(Job job, RemoteTransferSnapshot snapshot, bool forceObservationUpdate = false)
    {
        try
        {
            await AuthorizeAsync(job, _lifetime).ConfigureAwait(false);
            await AuthorizeSessionAsync(job._scope, snapshot.SessionId, _lifetime).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { await WithholdAsync(job).ConfigureAwait(false); return; }
        bool cancelThis, cancelOthers;
        lock (_gate)
        {
            if (snapshot.Sequence < job._snapshot.Sequence || job._withheld) return;
            if (job._publishedSequence >= snapshot.Sequence && !forceObservationUpdate) return;
            job._publishedSequence = snapshot.Sequence;
            job._snapshot = snapshot;
            if (snapshot.IsTerminal) job._completion.TrySetResult();
            // 本轮已停止或被拒之后才进入确认阶段的(启动回执迟到、或宿主晚一拍才报阶段):一到就撤
            cancelThis = job._scope._cancelUnapprovedRequested && AwaitsApproval(snapshot);
            // 用户在宿主确认框里拒了这一项:整轮算被拒(与 HasDeniedExecution 同一判据),同一轮排着的确认一并撤掉
            cancelOthers = job._scope.DenialEndsRun && !job._scope._cancelUnapprovedRequested
                && snapshot.State == RemoteTransferState.Failed && snapshot.ErrorCode == "permission-denied";
        }
        job._scope.Publish(new(job._scope.OwnerId, job._scope.RunId, snapshot.TransferId,
            snapshot.LocalPath, snapshot.RemotePath, snapshot));
        if (cancelThis) _ = CancelUnapprovedAsync(job);
        if (cancelOthers) _ = CancelUnapprovedAsync(job._scope);
    }

    /// <summary>
    /// 从宿主拿到了一份新鲜快照(显式查询或观察者恢复):先撤下"进度不可确认",再发布。
    /// 宿主 Sequence 没变也照发一次,否则界面会一直挂着"不可确认"。
    /// </summary>
    private async Task ObservedAsync(Job job, RemoteTransferSnapshot snapshot)
    {
        bool recovered;
        lock (_gate)
        {
            recovered = job._monitoringFailed && !job._withheld;
            if (recovered)
            {
                job._monitoringFailed = false;
                if (!snapshot.IsTerminal) job._completion = Signal();
            }
        }
        await PublishAsync(job, snapshot, forceObservationUpdate: recovered).ConfigureAwait(false);
    }

    private void StartWatcher(Job job)
    {
        lock (_gate)
        {
            if (job._watching || job._withheld || job._snapshot.IsTerminal) return;
            job._watching = true;
        }
        _ = WatchAsync(job);
    }

    private async Task WatchAsync(Job job)
    {
        int failures = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                RemoteTransferSnapshot current;
                lock (_gate)
                {
                    current = job._snapshot;
                    if (current.IsTerminal || job._withheld) return;
                }
                try
                {
                    await AuthorizeAsync(job, _lifetime).ConfigureAwait(false);
                    RemoteTransferSnapshot next;
                    try
                    {
                        next = await _remoteFs.WaitTransferAsync(current.TransferId, current.Sequence,
                            TimeSpan.FromSeconds(IRemoteFsApi.MaxTransferWaitSeconds), _lifetime).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException or PluginPermissionDeniedException))
                    {
                        // 丢失进度等待不代表任务失败；回读成功后继续监听，不能悄悄停止并让完成等待永远挂起。
                        next = await _remoteFs.GetTransferAsync(current.TransferId, _lifetime).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetime).ConfigureAwait(false);
                    }
                    await ObservedAsync(job, next).ConfigureAwait(false);
                    failures = 0;
                    if (next.Sequence <= current.Sequence && !next.IsTerminal)
                        await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PluginPermissionDeniedException)
                { await WithholdAsync(job).ConfigureAwait(false); return; }
                catch (Exception)
                {
                    // 不取消宿主任务，也不把网络故障伪装成撤权。
                    if (++failures == 3)
                    {
                        lock (_gate)
                        {
                            // 本地观察失败独立于宿主 Sequence/终态，不能伪造版本或让清理漏掉仍运行的上传。
                            // 只记在这一项上(不碰整轮的 _uncertainStart):拿回新鲜快照时就能撤掉。
                            job._monitoringFailed = true;
                            job._completion.TrySetResult();
                        }
                        job._scope.Publish(new(job._scope.OwnerId, job._scope.RunId, current.TransferId,
                            current.LocalPath, current.RemotePath, current, MonitoringUnavailable: true));
                    }
                    // 报了"不可确认"也不收手:观察一停,卡片冻住、IM 永远等不到终态、send_file 被这次下载一直挡着,
                    // 订阅与清理登记也都释放不掉。退避着一直盯到宿主报终态、撤权或插件停用。
                    await Task.Delay(RetryDelay(failures), _lifetime).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            bool restart;
            lock (_gate)
            {
                job._watching = false;
                restart = !_lifetime.IsCancellationRequested && !job._withheld && !job._snapshot.IsTerminal;
            }
            // Resume/显式查询可能与旧观察者收尾竞态；不能因旧代次仍持 Watching 而丢失新代次监听。
            if (restart) StartWatcher(job);
        }
    }

    /// <summary>观察失败后的退避:前两次 250 / 500 ms,第三次起(已报"不可确认")1 s 起翻倍,封顶 30 s。</summary>
    internal static TimeSpan RetryDelay(int failures) => failures < 3
        ? TimeSpan.FromMilliseconds(250 * failures)
        : TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures - 3, 5)));

    private RemoteTransferSnapshot Deliver(Job job)
    {
        lock (_gate)
        {
            if (job._withheld) throw new UnauthorizedAccessException("Transfer metadata is withheld.");
            if (job._snapshot.IsTerminal || job._monitoringFailed) job._delivered = true;
            return job._monitoringFailed ? MonitoringUnknown(job) : job._snapshot;
        }
    }

    private static RemoteTransferSnapshot MonitoringUnknown(Job job) => job._snapshot with
    {
        State = RemoteTransferState.Unknown,
        RemoteStopConfirmed = false,
        ErrorCode = "ProgressUnavailable",
        ErrorMessage = "Transfer progress cannot be confirmed. Do not automatically replay this operation."
    };

    private static bool NeedsCleanup(RemoteTransferSnapshot snapshot) => !snapshot.IsTerminal
        || (snapshot.State is RemoteTransferState.Unknown or RemoteTransferState.TimedOut && !snapshot.RemoteStopConfirmed);

    internal bool HasRunningTransfers(FileTransferRunScope scope)
    {
        lock (_gate)
        {
            return scope._pendingStarts > 0
            || _jobs.Values.Any(j => ReferenceEquals(j._scope, scope) && NeedsCleanup(j._snapshot));
        }
    }

    /// <summary>
    /// 仍在启动或宿主尚未报告终态的任务。MCP 闲置回收只为它们留着会话:已终结、停止未确认的
    /// Unknown/TimedOut 等多久都不会变,留着只会把会话名额占满;回收时释放会话照样 CancelAll。
    /// 撤权后扣下的任务也不算:扣下时已向宿主发过取消,快照再也读不到,只会停在扣下前那份。
    /// </summary>
    internal bool HasUnfinishedTransfers(FileTransferRunScope scope)
    {
        lock (_gate)
        {
            return scope._pendingStarts > 0
            || _jobs.Values.Any(j => ReferenceEquals(j._scope, scope) && !j._withheld && !j._snapshot.IsTerminal);
        }
    }

    /// <summary>
    /// 本机路径上是否有尚未成功提交的下载:任一次仍可能在写(回执未到或丢失、宿主还没报终态),
    /// 或者最近一次真正开始过的下载没有以 Succeeded 收尾。宿主写随机临时文件、成功才原子改名,
    /// 在那之前目标处要么没有文件、要么是同名旧文件 —— send_file 两种都不该发。整个插件共享,链接下载也算。
    /// </summary>
    internal bool HasUncommittedDownload(string localPath)
    {
        string target = Path.GetFullPath(localPath);
        lock (_gate)
        {
            if (!_downloadTargets.TryGetValue(target, out List<StartReceipt>? attempts)) return false;
            if (attempts.Any(MayStillWrite)) return true;
            // 走到这里,每一次要么已明确被拒(什么都没写),要么已拿到回执且宿主已报终态;被拒的不改变结论
            StartReceipt? latest = attempts.LastOrDefault(a => a.Result.IsCompletedSuccessfully);
            return latest is not null && !(_jobs.TryGetValue(latest.Result.Result.TransferId, out Job? job)
                && job._snapshot.IsSuccess);
        }
    }

    // 调用方持有 _gate。明确的启动拒绝什么都没开始;其余启动失败与未终结一样,当作宿主可能还在写。
    // 宿主报了终态就不会再写目标:只有成功才提交,收尾只删自己的临时文件 —— 停止未确认的 Unknown/TimedOut
    // 也一样(后端仍在写的只是它自己的临时文件)。它留下的同名旧文件由「最近一次须 Succeeded」挡住。
    private bool MayStillWrite(StartReceipt attempt)
    {
        Task<RemoteTransferSnapshot> result = attempt.Result;
        if (!result.IsCompleted) return true;
        if (!result.IsCompletedSuccessfully)
            return result.Exception?.InnerException is not { } failure || !IsDefiniteRejection(failure);
        return !_jobs.TryGetValue(result.Result.TransferId, out Job? job) || !job._snapshot.IsTerminal;
    }

    // 宿主的「启动前被拒」(PluginTransferRejectedException,SDK 2.1.3:源找不到、被占用、额度满)是宿主给的保证 ——
    // 没登记、没生成 transferId、没读写任何内容。它刻意不是 IOException,ReceiveStartAsync 不会按「回执丢了」去重取。
    private static bool IsDefiniteRejection(Exception ex)
        => ex is ArgumentException or NotSupportedException or UnauthorizedAccessException or PluginPermissionDeniedException
            or PluginTransferRejectedException;

    /// <summary>
    /// 宿主「启动前被拒」的原因,写给模型看:说清是什么、该不该再试。不带路径 —— 路径模型自己知道,
    /// 宿主的说明也刻意不带。
    /// </summary>
    internal static string DescribeHostRejection(RemoteTransferRejectReason reason) => reason switch
    {
        RemoteTransferRejectReason.SourceNotFound => "the source file does not exist (it may have been moved or deleted). Check the path with the user before trying again.",
        RemoteTransferRejectReason.SourceInUse => "the source file is open or locked by another program. Ask the user to close it; only then may you try again.",
        RemoteTransferRejectReason.QuotaExceeded => "too many transfers are already queued or running for this plugin. Wait for them to finish before starting another.",
        RemoteTransferRejectReason.InsufficientSpace => "there is not enough local disk space to prepare the transfer. Tell the user how much space is needed.",
        _ => "the host gave no specific reason. Tell the user; do not retry automatically."
    };

    internal bool HasActiveJobs(FileTransferRunScope scope)
    {
        lock (_gate) return _jobs.Values.Any(j => ReferenceEquals(j._scope, scope) && !j._delivered);
    }

    internal async Task CancelAllAsync(FileTransferRunScope scope)
    {
        Job[] jobs;
        lock (_gate)
        {
            scope._cancelAllRequested = true;
            jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j._scope, scope) && NeedsCleanup(j._snapshot))];
        }
        foreach (Job job in jobs)
        {
            try { await CancelAsync(scope, job._snapshot.TransferId, job._snapshot.Generation, _lifetime).ConfigureAwait(false); }
            catch { /* 宿主关闭或撤权可能已经收尾了任务。 */ }
        }
    }

    /// <summary>
    /// 本轮停止或被拒:撤掉还在等宿主确认的传输。
    /// </summary>
    /// <remarks>
    /// 宿主的逐任务确认框一次只弹一个(其余排在闸后)。模型一步里发起了几次上传、用户点了停止或拒绝了其中一个,
    /// 剩下的确认框以前仍一个接一个弹出来(AI 上传③)。停止只停模型、不停已批准的传输(那是用户点过头的),
    /// 所以这里只撤 <see cref="RemoteTransferPhase.Approval" /> 阶段的:宿主那边排队的确认随取消一起撤下,
    /// 已经弹出来的那个也随之关掉,任务以 Cancelled 收尾、什么都没写。
    /// 之后才拿到回执的(启动请求还在路上)由 <see cref="FileTransferRunScope._cancelUnapprovedRequested" /> 接住。
    /// 宿主宣告了 <see cref="IRemoteFsApi.SupportsTransferWithdraw" /> 时逐项用条件撤回(判定与撤下在宿主同一步),
    /// 否则先取最新快照、仍在确认阶段才按代次取消。
    /// 旧宿主的上传不报阶段(Unknown),这里认不出就不撤,退回原来的行为。
    /// </remarks>
    internal async Task CancelUnapprovedAsync(FileTransferRunScope scope)
    {
        Job[] jobs;
        lock (_gate)
        {
            scope._cancelUnapprovedRequested = true;
            jobs = [.. _jobs.Values.Where(j => ReferenceEquals(j._scope, scope) && AwaitsApproval(j._snapshot))];
        }
        foreach (Job job in jobs) await CancelUnapprovedAsync(job).ConfigureAwait(false);
    }

    private async Task CancelUnapprovedAsync(Job job)
    {
        RemoteTransferSnapshot snapshot;
        lock (_gate) snapshot = job._snapshot;
        try
        {
            if (_remoteFs.SupportsTransferWithdraw)
            {
                // 宿主的条件撤回(SDK 2.1.4):「还在等确认吗」与「撤下」在宿主那边是同一步,和批准生效在同一个串行点上判先后。
                // 手里的快照落后一拍也不要紧 —— 宿主已经批准的答 PastApproval、什么都不动(那是用户点过头的,照常传);
                // Withdrawn 是撤下了(返回时可能还是 CancelRequested,观察者照常盯到 Cancelled);AlreadyEnded 是批准之前就因为
                // 别的原因收尾了(确认框里被拒、断线……)。三种都只是把宿主给的快照发布出去;Unknown(更新的宿主)也不当成撤下了、
                // 更不改发无条件取消,同样只发布,由观察者盯到终态。
                RemoteTransferWithdrawResult withdrawal = await _remoteFs.WithdrawPendingTransferAsync(snapshot.TransferId,
                    snapshot.Generation, _lifetime).ConfigureAwait(false);
                await PublishAsync(job, withdrawal.Snapshot).ConfigureAwait(false);
                return;
            }
            // 不支持条件撤回的宿主走老路。手里的快照可能落后一拍:宿主已经批准、观察者还没把 Transfer 阶段发布过来。按旧快照撤,
            // 就把用户刚点过头的那一个也撤了 —— 先向宿主取一次最新的,已经过了确认的不撤。
            // 取与撤之间仍有一个往返的空当(宿主恰在这一拍批准,刚批准的照样被撤);合上它要靠上面的条件撤回。
            RemoteTransferSnapshot current = await _remoteFs.GetTransferAsync(snapshot.TransferId, _lifetime).ConfigureAwait(false);
            if (!AwaitsApproval(current))
            {
                await PublishAsync(job, current).ConfigureAwait(false);
                return;
            }
            RemoteTransferSnapshot cancelled = await _remoteFs.CancelTransferAsync(current.TransferId,
                current.Generation, _lifetime).ConfigureAwait(false);
            await PublishAsync(job, cancelled).ConfigureAwait(false);
        }
        catch { /* 宿主可能已经收尾或撤权;观察者照常盯到终态。 */ }
    }

    /// <summary>还在等宿主确认、也还没被要求取消。</summary>
    private static bool AwaitsApproval(RemoteTransferSnapshot snapshot)
        => !snapshot.IsTerminal && !snapshot.CancellationRequested && snapshot.Phase == RemoteTransferPhase.Approval;

    internal bool HasDeniedExecution(FileTransferRunScope scope)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j._scope, scope)
            && j._snapshot.State == RemoteTransferState.Failed && j._snapshot.ErrorCode == "permission-denied");
        }
    }

    internal bool HasUnknownOutcome(FileTransferRunScope scope)
    {
        if (scope._uncertainStart) return true;
        // 观察暂不可确认只记在那一项上:观察者退避重试拿回新鲜快照就撤掉,结论回到宿主的权威状态。
        lock (_gate)
        {
            return _jobs.Values.Any(j => ReferenceEquals(j._scope, scope)
            && (j._withheld || j._monitoringFailed || j._snapshot.State == RemoteTransferState.Unknown
                || (j._snapshot.State == RemoteTransferState.TimedOut && !j._snapshot.RemoteStopConfirmed)));
        }
    }

    internal async Task<IReadOnlyList<RemoteTransferSnapshot>> WaitForCompletionAsync(FileTransferRunScope scope,
        CancellationToken token)
    {
        while (true)
        {
            Job[] pending;
            lock (_gate) pending = [.. _jobs.Values.Where(j => ReferenceEquals(j._scope, scope) && !j._delivered)];
            if (pending.Length == 0) return [];
            await Task.WhenAny(pending.Select(j => j._completion.Task)).WaitAsync(token).ConfigureAwait(false);
            List<RemoteTransferSnapshot> results = [];
            foreach (Job job in pending.Where(j => j._completion.Task.IsCompleted))
            {
                try { await AuthorizeAsync(job, token).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { }
                lock (_gate)
                {
                    // 观察恢复会换上新的完成信号:那一刻起它又是未完成任务,不能把运行中的快照当终态交出去。
                    if (job._delivered || !job._completion.Task.IsCompleted) continue;
                    job._delivered = true;
                    results.Add(job._withheld ? new RemoteTransferSnapshot
                    {
                        TransferId = job._snapshot.TransferId,
                        OperationId = "",
                        SessionId = "",
                        LocalPath = "",
                        RemotePath = "",
                        State = RemoteTransferState.Unknown,
                        ErrorCode = "ScopeRevoked",
                        ErrorMessage = "Transfer result is withheld. Do not automatically replay this operation."
                    } : job._monitoringFailed ? MonitoringUnknown(job) : job._snapshot);
                }
            }
            if (results.Count > 0) return results;
        }
    }

    internal static string StableOperationId(string ownerId, string runId, string callIdentity)
        => "ai-transfer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { ownerId, runId, callIdentity })))).ToLowerInvariant();

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Job(FileTransferRunScope scope, RemoteTransferSnapshot snapshot)
    {
        internal readonly FileTransferRunScope _scope = scope;
        internal RemoteTransferSnapshot _snapshot = snapshot;
        internal TaskCompletionSource _completion = Signal();
        internal bool _delivered;
        internal bool _withheld;
        internal bool _watching;
        internal bool _monitoringFailed;
        internal long _publishedSequence;
    }
}

/// <summary>所有者隔离的上传能力。模型 Stop/Dispose 不暗中取消上传；界面订阅必须显式释放。</summary>
public sealed class FileTransferRunScope : IAsyncDisposable
{
    private readonly FileTransferJobRegistry _registry;
    private readonly Lock _gate = new();
    private readonly List<Action<FileTransferJobUpdate>> _subscribers = [];
    internal readonly Func<string, CancellationToken, Task<bool>>? _authorize;
    internal volatile bool _uncertainStart;
    internal int _pendingStarts;
    internal bool _cancelAllRequested;
    /// <summary>本轮已停止或被拒:之后才登记上、仍在等宿主确认的任务一到就撤(见 <see cref="CancelUnapprovedAsync" />)。</summary>
    internal bool _cancelUnapprovedRequested;
    /// <summary>
    /// 宿主拒绝其中一项就算整轮被拒(桌面与 IM 的轮次)。对外 MCP 的轮次跨越整条客户端连接,拒绝只是那一次调用的结果,
    /// 不连带撤掉同一连接里别的上传。
    /// </summary>
    internal bool DenialEndsRun { get; }
    /// <summary>入口持有的稳定对话所有者。</summary>
    public string OwnerId { get; }
    /// <summary>入口生成的运行代际。</summary>
    public string RunId { get; }
    /// <summary>仍有尚未投递终态的上传。</summary>
    public bool HasActiveJobs => _registry.HasActiveJobs(this);
    /// <summary>宿主尚未报告终态的真实任务，不受模型通知是否消费影响。</summary>
    public bool HasRunningTransfers => _registry.HasRunningTransfers(this);
    /// <summary>仍在启动或尚未终结的任务;已终结但停止未确认的、撤权扣下的不算(MCP 闲置回收看它)。</summary>
    internal bool HasUnfinishedTransfers => _registry.HasUnfinishedTransfers(this);
    /// <summary>宿主逐任务可信上传审批被拒绝；不是普通传输失败。</summary>
    public bool HasDeniedExecution => _registry.HasDeniedExecution(this);
    /// <summary>存在不可自动重放的不确定结果。</summary>
    public bool HasUnknownOutcome => _registry.HasUnknownOutcome(this);

    internal FileTransferRunScope(FileTransferJobRegistry registry, string ownerId, string runId,
        Func<string, CancellationToken, Task<bool>>? authorize, bool denialEndsRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _registry = registry; OwnerId = ownerId; RunId = runId; _authorize = authorize; DenialEndsRun = denialEndsRun;
    }

    /// <summary>订阅后台进度；调用者切到 UI 线程并显式释放返回的订阅。</summary>
    public IDisposable Subscribe(Action<FileTransferJobUpdate> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate) _subscribers.Add(callback);
        return new Subscription(() => { lock (_gate) _subscribers.Remove(callback); });
    }

    internal void Publish(FileTransferJobUpdate update)
    {
        Action<FileTransferJobUpdate>[] callbacks;
        lock (_gate) callbacks = [.. _subscribers];
        foreach (Action<FileTransferJobUpdate> callback in callbacks)
            try { callback(update); } catch { /* 界面观察者不能破坏进度监听。 */ }
    }

    internal Task<RemoteTransferSnapshot> StartAsync(string sessionId, string localPath, string remotePath,
        string operationId, bool overwrite, CancellationToken token)
        => _registry.StartAsync(this, sessionId, localPath, remotePath, operationId, overwrite, token);
    internal Task<RemoteTransferSnapshot> StartDownloadAsync(string sessionId, string remotePath, string localPath,
        string operationId, bool overwrite, CancellationToken token)
        => _registry.StartDownloadAsync(this, sessionId, remotePath, localPath, operationId, overwrite, token);
    /// <summary>重验当前授权后读取本轮任务；消费终态以避免重复模型唤醒。</summary>
    public Task<RemoteTransferSnapshot> ReadAsync(string transferId, CancellationToken cancellationToken = default)
        => _registry.ReadAsync(this, transferId, cancellationToken);
    /// <summary>供界面同步快照，不消费模型终态通知。</summary>
    public Task<RemoteTransferSnapshot> PeekAsync(string transferId, CancellationToken cancellationToken = default)
        => _registry.ReadAsync(this, transferId, cancellationToken, consume: false);
    /// <summary>只等本地完成信号，超时或取消本次等待不会取消上传。</summary>
    public Task<RemoteTransferSnapshot> WaitAsync(string transferId, TimeSpan wait,
        CancellationToken cancellationToken = default) => _registry.WaitAsync(this, transferId, wait, cancellationToken);
    /// <summary>显式取消指定执行代次，不返回撤权后的元数据。</summary>
    public Task CancelAsync(string transferId, long expectedGeneration, CancellationToken cancellationToken = default)
        => _registry.CancelAsync(this, transferId, expectedGeneration, cancellationToken);
    internal Task<RemoteTransferSnapshot> ResumeAsync(string transferId, RemoteTransferResumeOptions options,
        CancellationToken token) => _registry.ResumeAsync(this, transferId, options, token);
    /// <summary>原子消费一次终态通知；本地等待不调用模型。</summary>
    public Task<IReadOnlyList<RemoteTransferSnapshot>> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => _registry.WaitForCompletionAsync(this, cancellationToken);

    /// <summary>入口显式销毁/撤销授权时取消该轮次的未完成上传；模型 Stop 不调用它。</summary>
    public Task CancelAllAsync() => _registry.CancelAllAsync(this);

    /// <summary>
    /// 本轮停止或被拒时撤掉还在等宿主确认的传输(已批准的照常传完);之后才登记上的也一到就撤。不抛异常。
    /// </summary>
    public Task CancelUnapprovedAsync() => _registry.CancelUnapprovedAsync(this);

    /// <summary>释放模型观察者不取消宿主任务；UI 订阅由返回的 IDisposable 独立管理。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
