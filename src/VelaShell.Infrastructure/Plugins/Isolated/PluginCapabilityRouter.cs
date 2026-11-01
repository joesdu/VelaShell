using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>
/// 隔离插件的宿主侧能力路由:把 RPC 请求分发到该插件的 <see cref="PluginContext" />
/// 能力实现(与进程内插件同一套 —— 权限/节流/纪律单点生效),并把宿主事件、
/// 命令触发与面板交互作为通知推回插件进程。
/// 握手完成前除 <see cref="PluginRpc.Handshake" /> 外一切调用拒绝。
/// </summary>
internal sealed class PluginCapabilityRouter : IDisposable
{
    private readonly PluginContext _context;
    private readonly RpcConnection _rpc;
    private readonly string _expectedToken;
    private readonly TaskCompletionSource _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, IDisposable> _commandRegistrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Stream> _openStreams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginSdk.TimeSeries.ITimeSeries> _openSeries = new(StringComparer.Ordinal);
    private readonly string _hostVersion;
    private readonly ExecutionOperations _executions;
    private readonly CancellationTokenRegistration _shutdownRegistration;

    /// <summary>有执行操作时不允许空闲回收;显式停用仍无条件取消。</summary>
    internal int ActiveExecutions => _executions.ActiveCount
        + (_context.RemoteFs is Capabilities.RemoteFsCapability transfers ? transfers.ActiveTransfers : 0);
    private volatile bool _handshakeDone;
    private bool _disposed;

    private readonly Func<Task<IReadOnlyList<ThemeTokenDto>>>? _themeTokens;
    private readonly IPluginEmbedHost? _embedHost;
    private readonly ConcurrentDictionary<string, PluginSdk.Ui.IPluginPanel> _embeddedPanels = new(StringComparer.Ordinal);

    /// <summary>装配路由并接线事件转发。</summary>
    public PluginCapabilityRouter(PluginContext context, RpcConnection rpc, string expectedToken, string hostVersion,
        Func<Task<IReadOnlyList<ThemeTokenDto>>>? themeTokens = null, IPluginEmbedHost? embedHost = null)
    {
        _context = context;
        _rpc = rpc;
        _expectedToken = expectedToken;
        _hostVersion = hostVersion;
        _themeTokens = themeTokens;
        _embedHost = embedHost;
        _executions = new(context.Shutdown);
        _shutdownRegistration = context.Shutdown.Register(_executions.Stop);
        rpc.Disconnected += _executions.Stop;
        rpc.Disconnected += StopTransfers;
        // 宿主事件 → 插件进程通知。订阅挂在 context.Events(PluginEventHub)上,
        // context.Dispose 时统一拆除。
        context.Events.SessionConnected += session =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("sessionConnected", session, null));
        context.Events.SessionDisconnected += session =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("sessionDisconnected", session, null));
        context.Events.ThemeChanged += theme =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("themeChanged", null, theme));
        // 令牌快照跟的是**生效配色**而不是 themeChanged。三条理由,每条都对应一个真实的漏推:
        //   · 换具名主题时 themeChanged 的参数不变(VelaDark→Tokyo Night 都报 "dark"),
        //     它作为信号还在,但插件侧没法拿参数判断;
        //   · "跟随系统"下系统明暗翻转根本不经过 themeChanged —— 主题 id 一直是 "system";
        //   · 用户改强调色同样不经过它。
        // 后两种情况下隔离插件原本会一直停在上一套配色上。IHostThemeApi.Changed 是三者的并集,
        // 而且它在触发前已经把新快照采好了 —— 从前那个"等 100ms 让 UI 线程落定"的权宜也就
        // 不需要了(HostThemeSource 采集时自己会跳 UI 线程,天然排在贴令牌之后)。
        context.Theme.Changed += info => { _ = PushThemeTokensAsync(); };
        context.Events.LocaleChanged += locale =>
            _ = rpc.NotifyAsync(PluginRpc.HostEvent, new HostEventNotification("localeChanged", null, locale));
    }

    /// <summary>
    /// 下发主题状态(整套令牌 + 主题身份)。
    /// <para>
    /// 身份**总是**发,即使宿主没有令牌提供者(headless):隔离插件的
    /// <see cref="PluginSdk.Theming.IHostThemeApi.Current" /> 靠它更新,没有令牌不等于没有主题。
    /// 令牌采集失败时只是发一份空表,插件侧会保留上一份颜色。
    /// </para>
    /// </summary>
    public async Task PushThemeTokensAsync()
    {
        ThemeTokenDto[] tokens = [];
        if (_themeTokens is not null)
        {
            try
            {
                tokens = [.. await _themeTokens().ConfigureAwait(false)];
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"Theme token collection failed: {ex.Message}");
            }
        }
        try
        {
            await _rpc.NotifyAsync(PluginRpc.ThemeTokens,
                new ThemeTokensNotification(tokens, _context.Theme.Current)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Theme state push failed: {ex.Message}");
        }
    }

    /// <summary>握手完成(令牌校验通过)时完成;超时/失败由等待方裁决。</summary>
    public Task HandshakeCompleted => _handshake.Task;

    /// <summary>插件发起了一次 RPC 往来(请求或通知)—— 空闲回收的活跃信号。</summary>
    public event Action? Activity;

    /// <summary>插件进程的打开面板数变化 —— 非零时不做空闲回收。</summary>
    public event Action<int>? SurfacesChanged;

    /// <summary>RPC 请求入口。</summary>
    public async Task<object?> HandleRequestAsync(string method, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (method == PluginRpc.Handshake)
        {
            return Handshake(Get<HandshakeRequest>(payload));
        }
        Activity?.Invoke();
        if (!_handshakeDone)
        {
            throw new InvalidOperationException("Handshake not completed.");
        }
        switch (method)
        {
            case PluginRpc.SessionsList:
                return await _context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.SessionsGet:
                return await _context.Sessions.GetAsync(Get<SessionRef>(payload).SessionId, cancellationToken).ConfigureAwait(false);
            case PluginRpc.SessionsListSaved:
                return await _context.Sessions.ListSavedAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.SessionsOpen:
                {
                    // 这一条可以很慢:中间夹着一个给用户看的确认框,还有真正的建连。
                    // 插件侧的超时按"人要看一眼再点"给(见 RpcSessions.OpenTimeout),
                    // 宿主这边不另设死线 —— 提前判死只会留下一条谁都不认领的连接。
                    SessionOpenRequest request = Get<SessionOpenRequest>(payload);
                    return await _context.Sessions.OpenAsync(request.SavedSessionId,
                        new(request.Reason, request.ReuseConnected), cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.SessionsClose:
                await _context.Sessions.CloseAsync(Get<SessionRef>(payload).SessionId, cancellationToken).ConfigureAwait(false);
                return null;
            case PluginRpc.ExecRun:
                {
                    ExecRunRequest request = Get<ExecRunRequest>(payload);
                    return await _context.RemoteExec.RunAsync(request.SessionId, request.Command,
                        new() { Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds) }, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.ExecStream:
                {
                    ExecStreamRequest request = Get<ExecStreamRequest>(payload);
                    string operationId = request.OperationId ?? request.OutputToken;
                    await using ExecutionOperations.Operation operation = _executions.Register(operationId, cancellationToken);
                    operation.Token.ThrowIfCancellationRequested();
                    var sink = new ExecutionNotificationSink<ExecOutput>((line, sequence) =>
                        _rpc.NotifyExecutionAsync(PluginRpc.ExecOutput,
                            new ExecOutputNotification(request.OutputToken, line.Stream is ExecStream.StandardError, line.Line)
                            { Sequence = sequence }), () => _executions.Cancel(operationId));
                    try
                    {
                        return await _context.RemoteExec.StreamAsync(request.SessionId, request.Command,
                            new()
                            {
                                Timeout = request.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(request.TimeoutSeconds) : null,
                                IncludeStandardError = request.IncludeStandardError
                            }, sink, operation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        // 应答(成功或错误)绝不能越过最后一条已接收的输出。
                        await sink.DrainAsync().ConfigureAwait(false);
                    }
                }
            case PluginRpc.ExecSudoStream:
                {
                    ExecSudoStreamRequest request = Get<ExecSudoStreamRequest>(payload);
                    await using ExecutionOperations.Operation operation = _executions.Register(request.OperationId, cancellationToken);
                    operation.Token.ThrowIfCancellationRequested();
                    var sink = new ExecutionNotificationSink<SudoExecEvent>((value, sequence) =>
                        _rpc.NotifyExecutionAsync(PluginRpc.ExecSudoEvent,
                            new ExecSudoEventNotification(request.OperationId, sequence, value)),
                        () => _executions.Cancel(request.OperationId));
                    try
                    {
                        return await _context.RemoteExec.StreamSudoAsync(request.SessionId, request.Command,
                            new() { Timeout = request.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(request.TimeoutSeconds) : null },
                            sink, operation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await sink.DrainAsync().ConfigureAwait(false);
                    }
                }
            case PluginRpc.FsList:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    return await _context.RemoteFs.ListDirectoryAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsStat:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    return await _context.RemoteFs.StatAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsExists:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    return await _context.RemoteFs.ExistsAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsWorkingDirectory:
                return await _context.RemoteFs.GetWorkingDirectoryAsync(Get<SessionRef>(payload).SessionId, cancellationToken).ConfigureAwait(false);
            case PluginRpc.FsDownload:
                {
                    FsTransferRequest request = Get<FsTransferRequest>(payload);
                    await _context.RemoteFs.DownloadFileAsync(request.SessionId, request.RemotePath, request.LocalPath,
                        ProgressFor(request.ProgressToken), cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsUpload:
                {
                    FsTransferRequest request = Get<FsTransferRequest>(payload);
                    await _context.RemoteFs.UploadFileAsync(request.SessionId, request.LocalPath, request.RemotePath,
                        ProgressFor(request.ProgressToken), cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsTransferStartUpload:
                {
                    FsTransferStartUploadRequest request = Get<FsTransferStartUploadRequest>(payload);
                    return await _context.RemoteFs.StartUploadAsync(request.SessionId, request.LocalPath, request.RemotePath,
                        request.Options, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsTransferStartDownload:
                {
                    FsTransferStartDownloadRequest request = Get<FsTransferStartDownloadRequest>(payload);
                    return await _context.RemoteFs.StartDownloadAsync(request.SessionId, request.RemotePath, request.LocalPath,
                        request.Options, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsTransferGet:
                return await _context.RemoteFs.GetTransferAsync(Get<FsTransferRef>(payload).TransferId, cancellationToken).ConfigureAwait(false);
            case PluginRpc.FsTransferWait:
                {
                    FsTransferWaitRequest request = Get<FsTransferWaitRequest>(payload);
                    return await _context.RemoteFs.WaitTransferAsync(request.TransferId, request.AfterSequence,
                        TimeSpan.FromSeconds(request.WaitSeconds), cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsTransferCancel:
                {
                    FsTransferCancelRequest request = Get<FsTransferCancelRequest>(payload);
                    return await _context.RemoteFs.CancelTransferAsync(request.TransferId, request.ExpectedGeneration, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsTransferWithdraw:
                {
                    // 判定与撤下都在宿主那一步完成;不支持的能力(旧实现、不可用的替身)照原样抛不支持,绝不改走无条件取消。
                    FsTransferWithdrawRequest request = Get<FsTransferWithdrawRequest>(payload);
                    return await _context.RemoteFs.WithdrawPendingTransferAsync(request.TransferId, request.ExpectedGeneration, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsTransferResume:
                {
                    FsTransferResumeRequest request = Get<FsTransferResumeRequest>(payload);
                    return await _context.RemoteFs.ResumeTransferAsync(request.TransferId, request.Options, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.FsOpenRead:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    Stream stream = await _context.RemoteFs.OpenReadAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    string streamId = Guid.NewGuid().ToString("N");
                    _openStreams[streamId] = stream;
                    long length = stream.CanSeek ? stream.Length : -1;
                    return new FsOpenReadResponse(streamId, length);
                }
            case PluginRpc.FsStreamRead:
                {
                    FsStreamReadRequest request = Get<FsStreamReadRequest>(payload);
                    if (!_openStreams.TryGetValue(request.StreamId, out Stream? stream))
                    {
                        throw new InvalidOperationException("Stream is not open (already closed or never opened).");
                    }
                    // 单块上限 512KB:base64 后 ~700KB,远低于帧上限,又不至于块太碎。
                    int count = Math.Clamp(request.MaxBytes, 1, 512 * 1024);

                    // 缓冲走池:512KB 远超 85KB 的大对象堆阈值,按块新开会让插件拉一个大文件的
                    // 全过程都在 LOH 上反复申请/回收(LOH 不压缩,直接喂出碎片与 gen2 回收)。
                    // 池的最大桶是 1MB,512KB 正好落在池内。
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(count);
                    try
                    {
                        int read = 0;
                        while (read < count)
                        {
                            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                            if (n == 0)
                            {
                                break;
                            }
                            read += n;
                        }
                        bool eof = read == 0;
                        if (eof && _openStreams.TryRemove(request.StreamId, out Stream? finished))
                        {
                            await finished.DisposeAsync().ConfigureAwait(false); // 流尽即释放,插件忘关也不泄漏
                        }
                        return new FsStreamReadResponse(Convert.ToBase64String(buffer, 0, read), eof);
                    }
                    finally
                    {
                        // 租来的数组可能比 count 长,且必然带着上一位租客的数据:
                        // 上面一律按 read 长度截取,不会把多余字节读出去。
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            case PluginRpc.FsStreamClose:
                {
                    if (_openStreams.TryRemove(Get<FsStreamRef>(payload).StreamId, out Stream? stream))
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    return null;
                }
            case PluginRpc.FsReadAll:
                {
                    FsReadAllRequest request = Get<FsReadAllRequest>(payload);
                    byte[] content = await _context.RemoteFs.ReadAllBytesAsync(request.SessionId, request.Path, request.MaxBytes,
                        cancellationToken).ConfigureAwait(false);
                    return Convert.ToBase64String(content);
                }
            case PluginRpc.FsWriteAll:
                {
                    FsWriteAllRequest request = Get<FsWriteAllRequest>(payload);
                    await _context.RemoteFs.WriteAllBytesAsync(request.SessionId, request.Path,
                        Convert.FromBase64String(request.ContentBase64), cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsDelete:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    await _context.RemoteFs.DeleteAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsCreateDirectory:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    await _context.RemoteFs.CreateDirectoryAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsEnsureDirectory:
                {
                    FsPathRequest request = Get<FsPathRequest>(payload);
                    await _context.RemoteFs.EnsureDirectoryAsync(request.SessionId, request.Path, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.FsRename:
                {
                    FsRenameRequest request = Get<FsRenameRequest>(payload);
                    await _context.RemoteFs.RenameAsync(request.SessionId, request.OldPath, request.NewPath, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.CommandsRegister:
                {
                    CommandRegistration registration = Get<CommandRegistration>(payload);
                    // 命令体留在插件进程:宿主触发时发通知过去。
                    IDisposable handle = _context.Commands.Register(new(registration.Id, registration.Title, registration.Category,
                        executeToken =>
                        {
                            _ = executeToken;
                            _ = _rpc.NotifyAsync(PluginRpc.CommandExecute, new CommandRef(registration.Id));
                            return Task.CompletedTask;
                        }));
                    if (_commandRegistrations.TryRemove(registration.Id, out IDisposable? replaced))
                    {
                        replaced.Dispose();
                    }
                    _commandRegistrations[registration.Id] = handle;
                    return null;
                }
            case PluginRpc.CommandsTryExecute:
                return _context.Commands.TryExecute(Get<CommandRef>(payload).Id);
            case PluginRpc.StorageGet:
                return await _context.Storage.GetAsync<JsonElement?>(Get<StorageKeyRef>(payload).Key, cancellationToken).ConfigureAwait(false);
            case PluginRpc.StorageSet:
                {
                    StorageSetRequest request = Get<StorageSetRequest>(payload);
                    await _context.Storage.SetAsync(request.Key, request.Value, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.StorageRemove:
                return await _context.Storage.RemoveAsync(Get<StorageKeyRef>(payload).Key, cancellationToken).ConfigureAwait(false);
            case PluginRpc.StorageKeys:
                return await _context.Storage.GetKeysAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.TimeSeriesOpen:
                {
                    TimeSeriesOpenRequest request = Get<TimeSeriesOpenRequest>(payload);
                    PluginSdk.TimeSeries.ITimeSeries series = await _context.TimeSeries
                        .OpenAsync(request.Definition, cancellationToken).ConfigureAwait(false);
                    _openSeries[series.Name] = series;
                    return new TimeSeriesNameRef(series.Name);
                }
            case PluginRpc.TimeSeriesList:
                return await _context.TimeSeries.ListAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.TimeSeriesDrop:
                {
                    string name = Get<TimeSeriesNameRef>(payload).Name;
                    _openSeries.TryRemove(name, out _);
                    return await _context.TimeSeries.DropAsync(name, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.TimeSeriesWrite:
                {
                    TimeSeriesWriteRequest request = Get<TimeSeriesWriteRequest>(payload);
                    await Series(request.Name).WriteManyAsync(request.Points, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.TimeSeriesQuery:
                {
                    TimeSeriesQueryRequest request = Get<TimeSeriesQueryRequest>(payload);
                    IReadOnlyList<PluginSdk.TimeSeries.TimeSeriesPoint> points = await Series(request.Name)
                        .QueryAsync(request.Query, cancellationToken).ConfigureAwait(false);
                    return points.ToArray();
                }
            case PluginRpc.TimeSeriesCount:
                {
                    TimeSeriesCountRequest request = Get<TimeSeriesCountRequest>(payload);
                    return await Series(request.Name).CountAsync(request.Field, request.Query, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.TimeSeriesDistinct:
                {
                    TimeSeriesDistinctRequest request = Get<TimeSeriesDistinctRequest>(payload);
                    return await Series(request.Name).DistinctTagValuesAsync(request.Tag, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.TimeSeriesDelete:
                {
                    TimeSeriesDeleteRequest request = Get<TimeSeriesDeleteRequest>(payload);
                    return await Series(request.Name).DeleteAsync(request.Tags, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.SecretsGet:
                return await _context.Secrets.GetAsync(Get<SecretRef>(payload).Name, cancellationToken).ConfigureAwait(false);
            case PluginRpc.SecretsSet:
                {
                    SecretSetRequest request = Get<SecretSetRequest>(payload);
                    await _context.Secrets.SetAsync(request.Name, request.Value, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.SecretsDelete:
                return await _context.Secrets.DeleteAsync(Get<SecretRef>(payload).Name, cancellationToken).ConfigureAwait(false);
            case PluginRpc.ClipboardGetText:
                return await _context.Clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
            case PluginRpc.ClipboardSetText:
                await _context.Clipboard.SetTextAsync(Get<ClipboardSetRequest>(payload).Text, cancellationToken).ConfigureAwait(false);
                return null;
            case PluginRpc.TerminalGetOutput:
                {
                    TerminalGetOutputRequest request = Get<TerminalGetOutputRequest>(payload);
                    return await _context.Terminal.GetOutputAsync(request.SessionId, request.MaxLines, cancellationToken).ConfigureAwait(false);
                }
            case PluginRpc.TerminalSearch:
                {
                    TerminalSearchRequest request = Get<TerminalSearchRequest>(payload);
                    IReadOnlyList<PluginSdk.Terminal.TerminalMatch> matches = await _context.Terminal
                        .SearchOutputAsync(request.SessionId, request.Pattern, request.IsRegex, request.MaxMatches, cancellationToken)
                        .ConfigureAwait(false);
                    return matches.Select(m => new TerminalMatchDto(m.Line, m.Text)).ToArray();
                }
            case PluginRpc.TerminalWrite:
                {
                    TerminalWriteRequest request = Get<TerminalWriteRequest>(payload);
                    await _context.Terminal.WriteAsync(request.SessionId, request.Input, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            case PluginRpc.UiEmbedPanel:
                {
                    if (_embedHost is not { IsSupported: true } embedHost)
                    {
                        throw new InvalidOperationException("Dock embedding is not supported by this host.");
                    }
                    UiEmbedRequest request = Get<UiEmbedRequest>(payload);
                    PluginSdk.Ui.IPluginPanel panel = await embedHost.EmbedAsync(_context.PluginId, _context.Log,
                        request.Title, (nint)request.Hwnd, cancellationToken).ConfigureAwait(false);
                    _embeddedPanels[panel.PanelId] = panel;
                    panel.Closed += () =>
                    {
                        // 用户关标签/插件停用:摘出记录并通知插件进程关掉它的窗口。
                        _embeddedPanels.TryRemove(panel.PanelId, out _);
                        _ = _rpc.NotifyAsync(PluginRpc.UiPanelClosed, new UiPanelRef(panel.PanelId));
                    };
                    return new UiEmbedResponse(panel.PanelId);
                }
            case PluginRpc.UiNotify:
                {
                    // 发送方是这条连接绑定的插件(_context 的界面能力就是它的),不取自消息本身。
                    // 宿主不支持时界面能力自己抛 NotSupportedException,握手里也没宣告,PluginHost 本不该发来。
                    UiNotifyRequest request = Get<UiNotifyRequest>(payload);
                    PluginSdk.Ui.PluginNotificationOutcome outcome = await _context.Ui
                        .PostNotificationAsync(request.Notification ?? throw new ArgumentException("Missing notification."), cancellationToken)
                        .ConfigureAwait(false);
                    return new UiNotifyResponse(outcome);
                }
            case PluginRpc.UiClosePanel:
                {
                    if (_embeddedPanels.TryRemove(Get<UiPanelRef>(payload).PanelId, out PluginSdk.Ui.IPluginPanel? panel))
                    {
                        await panel.CloseAsync().ConfigureAwait(false);
                    }
                    return null;
                }
            // 注:界面不走 RPC —— 隔离插件的窗口由插件进程自带的 Avalonia 直接呈现。
            default:
                throw new InvalidOperationException($"Unknown method '{method}'.");
        }
    }

    /// <summary>RPC 通知入口(日志、命令注销、面板数)。</summary>
    public void HandleNotification(string method, JsonElement? payload)
    {
        if (!_handshakeDone)
        {
            return;
        }
        Activity?.Invoke();
        switch (method)
        {
            case PluginRpc.ExecCancel:
                _executions.Cancel(Get<ExecCancelNotification>(payload).OperationId);
                break;
            case PluginRpc.UiSurfaces:
                {
                    SurfacesChanged?.Invoke(Get<UiSurfacesNotification>(payload).Count);
                    break;
                }
            case PluginRpc.LogWrite:
                {
                    LogNotification log = Get<LogNotification>(payload);
                    _context.Log.Write(log.Level, log.Exception is null ? log.Message : $"{log.Message} — {log.Exception}");
                    break;
                }
            case PluginRpc.CommandsUnregister:
                {
                    if (_commandRegistrations.TryRemove(Get<CommandRef>(payload).Id, out IDisposable? handle))
                    {
                        handle.Dispose();
                    }
                    break;
                }
        }
    }

    /// <summary>停机后异步等待执行方法及其输出排空;不得在同步 Dispose 中等待网络。</summary>
    internal ValueTask DrainExecutionsAsync() => _executions.DisposeAsync();

    private HandshakeResponse Handshake(HandshakeRequest request)
    {
        if (!string.Equals(request.Token, _expectedToken, StringComparison.Ordinal)
            || !string.Equals(request.PluginId, _context.PluginId, StringComparison.Ordinal))
        {
            var rejected = new InvalidOperationException("Handshake rejected: token or plugin id mismatch.");
            _handshake.TrySetException(rejected);
            throw rejected;
        }
        if (!request.ApiLevels.Contains(VelaPluginApi.Level))
        {
            var incompatible = new InvalidOperationException(
                $"Handshake rejected: plugin host supports apiLevels [{string.Join(",", request.ApiLevels)}], " +
                $"this host requires {VelaPluginApi.Level}.");
            _handshake.TrySetException(incompatible);
            throw incompatible;
        }
        _handshakeDone = true;
        _handshake.TrySetResult();
        return new(VelaPluginApi.Level, _hostVersion, _context.Host.Locale, _context.Host.Theme,
            SupportsEmbedding: _embedHost is { IsSupported: true },
            // 主题身份握手就给:插件在 Activate 里建界面时就要知道自己长在哪套主题上,
            // 等第一条 theme/tokens 通知就晚了。
            ThemeInfo: _context.Theme.Current)
        {
            SupportsExecutionOperations = true,
            SupportsTransferOperations = _context.RemoteFs.SupportsTransferOperations,
            SupportsDownloadOperations = _context.RemoteFs.SupportsDownloadOperations,
            // 条件撤回按实际能力宣告:插件进程据此决定发 fs/transfer/withdraw 还是在本地直接抛不支持。
            SupportsTransferWithdraw = _context.RemoteFs.SupportsTransferWithdraw,
            SupportsNotifications = _context.Ui.SupportsNotifications
        };
    }

    /// <summary>取已打开的 measurement 句柄;插件必须先 <see cref="PluginRpc.TimeSeriesOpen" />。</summary>
    private PluginSdk.TimeSeries.ITimeSeries Series(string name)
        => _openSeries.TryGetValue(name, out PluginSdk.TimeSeries.ITimeSeries? series)
            ? series
            : throw new InvalidOperationException($"Time series '{name}' is not open (call OpenAsync first).");

    private Progress<RemoteTransferProgress>? ProgressFor(string? progressToken)
        => progressToken is null
            ? null
            : new Progress<RemoteTransferProgress>(p =>
                _ = _rpc.NotifyAsync(PluginRpc.FsProgress,
                    new FsProgressNotification(progressToken, p.TransferredBytes, p.TotalBytes)));

    private static T Get<T>(JsonElement? payload) where T : class
        => payload is { } element
            ? element.Deserialize<T>() ?? throw new ArgumentException("Malformed payload.")
            : throw new ArgumentException("Missing payload.");

    private static async Task DisposeStreamQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 释放尽力而为。
        }
    }

    private void StopTransfers() => (_context.RemoteFs as IDisposable)?.Dispose();

    /// <summary>拆除命令/面板注册(context 本体由 PluginManager 统一释放)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _executions.Stop();
        _shutdownRegistration.Dispose();
        _rpc.Disconnected -= _executions.Stop;
        _rpc.Disconnected -= StopTransfers;
        StopTransfers();
        foreach (IDisposable handle in _commandRegistrations.Values)
        {
            handle.Dispose();
        }
        _commandRegistrations.Clear();
        // 在途的流式读取随插件停用释放。
        // 走异步释放、不等它:远端文件流的关闭是一次 SSH_FXP_CLOSE 往返,同步 Dispose
        // 只能阻塞着等网络(sync-over-async),而这里在插件停用 / 崩溃的路径上。
        foreach (Stream stream in _openStreams.Values.ToArray())
        {
            _ = DisposeStreamQuietlyAsync(stream);
        }
        _openStreams.Clear();
        // 插件停用/崩溃:嵌入的停靠标签一并撤下(面板 Closed 会尝试通知插件进程,断连时静默)。
        foreach (PluginSdk.Ui.IPluginPanel panel in _embeddedPanels.Values.ToArray())
        {
            _ = panel.CloseAsync();
        }
        _embeddedPanels.Clear();
        _handshake.TrySetException(new RpcDisconnectedException("Router disposed before handshake."));
    }
}
