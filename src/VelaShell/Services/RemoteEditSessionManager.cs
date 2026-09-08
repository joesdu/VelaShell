using System.Diagnostics;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Services;

/// <summary>本地副本下载完成后由谁把它打开。</summary>
public enum RemoteEditOpenWith
{
    /// <summary>谁也不打开:调用方自己弹窗(内置编辑器),会话只负责监视与回传。</summary>
    Nothing,

    /// <summary>设置 → 文件传输 → 默认编辑器 里配置的程序。</summary>
    ConfiguredEditor,

    /// <summary>平台默认处理程序(由宿主回调执行,它才拿得到 TopLevel.Launcher)。</summary>
    SystemDefault,
}

/// <summary>一个远程编辑会话此刻的处境。</summary>
public enum RemoteEditState
{
    /// <summary>正在监视本地副本,等下一次保存。</summary>
    Watching,

    /// <summary>正在把本地副本传回远端。</summary>
    Uploading,

    /// <summary>最近一次回传失败,本地副本是这份改动唯一的存身之处。</summary>
    Failed,
}

/// <summary>供界面展示的一次状态快照。</summary>
/// <remarks>
/// 会话状态在线程池上(watcher 回调、上传任务)变,界面在 UI 线程读。
/// 用整体快照替换而不是逐字段读写,免掉一圈只为显示服务的锁。
/// </remarks>
/// <param name="Id">会话标识(界面按它下发命令)。</param>
/// <param name="SessionId">所属的远程会话标识。</param>
/// <param name="FileName">文件名(界面主行)。</param>
/// <param name="RemotePath">远端完整路径。</param>
/// <param name="LocalPath">本地临时副本路径。</param>
/// <param name="ServerName">服务器显示名,空串表示未知。</param>
/// <param name="State">此刻的处境。</param>
/// <param name="HasPendingChange">有改动还没落到远端。</param>
/// <param name="UploadCount">迄今成功回传的次数。</param>
/// <param name="LastUploadedAt">最近一次成功回传的时刻;从未成功则为 null。</param>
/// <param name="LastError">最近一次失败原因;没失败过则为 null。</param>
/// <param name="AutoUpload">保存后是否自动回传。</param>
public sealed record RemoteEditSnapshot(
    Guid Id,
    Guid SessionId,
    string FileName,
    string RemotePath,
    string LocalPath,
    string ServerName,
    RemoteEditState State,
    bool HasPendingChange,
    int UploadCount,
    DateTime? LastUploadedAt,
    string? LastError,
    bool AutoUpload);

/// <summary>开一个远程编辑会话所需要的一切。</summary>
public sealed class RemoteEditRequest
{
    /// <summary>承载下载与回传的远程文件服务。</summary>
    public required ISftpService SftpService { get; init; }

    /// <summary>远程会话标识。</summary>
    public required Guid SessionId { get; init; }

    /// <summary>远端完整路径。</summary>
    public required string RemotePath { get; init; }

    /// <summary>远端文件名(会被当作本地副本的文件名,先过安全校验)。</summary>
    public required string FileName { get; init; }

    /// <summary>服务器显示名,只用于界面。</summary>
    public string ServerName { get; init; } = "";

    /// <summary>本地副本下载完成后由谁打开。</summary>
    public RemoteEditOpenWith OpenWith { get; init; } = RemoteEditOpenWith.Nothing;

    /// <summary><see cref="RemoteEditOpenWith.ConfiguredEditor" /> 时的编辑器命令。</summary>
    public string? EditorCommand { get; init; }

    /// <summary><see cref="RemoteEditOpenWith.SystemDefault" /> 时由宿主执行的打开动作。</summary>
    public Func<string, Task>? OpenLocalAsync { get; init; }

    /// <summary>把话说给用户听的通道(失败、草稿保留位置)。</summary>
    public Action<string>? OnError { get; init; }

    /// <summary>
    /// 把远端内容取到指定本地路径;返回是否真的落地了。
    /// </summary>
    /// <remarks>
    /// 为空时直接走 <see cref="ISftpService.DownloadFileAsync" />。调用方提供它,是为了让这次
    /// 下载走自己那套(进传输浮窗、按设置限速、可取消)—— 双击一个大文件时,
    /// 「什么都没发生」和「浮窗里有一行在跑」是两种体验。
    /// </remarks>
    public Func<string, CancellationToken, Task<bool>>? DownloadAsync { get; init; }

    /// <summary>回传实现;为空时直接走 <see cref="ISftpService.UploadFileAsync" />(不进传输浮窗)。</summary>
    public Func<string, string, Task>? UploadAsync { get; init; }

    /// <summary>保存后是否自动回传(设置 → 文件传输 → 编辑后自动上传)。</summary>
    public bool AutoUpload { get; init; } = true;
}

/// <summary>
/// 远程文件的本地编辑会话(WinSCP / electerm 式):远程文件下载到本地 temp 的独立子目录,
/// 交给内置编辑器、配置的编辑器或系统默认程序;<see cref="FileSystemWatcher" /> 侦听保存
/// (600ms 防抖)后自动回传。
/// </summary>
/// <remarks>
/// <para>
/// <b>三个「打开」入口共用这一套</b>(#396)。此前只有右键「使用默认编辑器打开」挂了监视,
/// 而双击走的是"下载到本地 + 交给系统默认程序",全程没有 watcher —— 用户看到的是
/// 「第一次保存有效(那次用的是右键菜单),后面再存就没反应」,而两条路径在界面上长得一样。
/// 现在入口只决定"谁来打开",要不要回传是会话本身的事。
/// </para>
/// <para>
/// <b>会话生命周期不再赌编辑器进程。</b>旧实现用「进程 3 秒内退出就当作是单实例编辑器的引导进程」
/// 来判断编辑器是否还开着 —— 带标签页的单实例编辑器(Notepad--、Notepad++、VS Code)冷启动
/// 慢一点就会误判成"编辑器已关闭",于是停 watcher、删临时目录,此后所有保存无声丢失。
/// 现在进程退出只触发一次补传,会话只由三件事结束:用户在传输浮窗里点结束、所属远程会话关闭、
/// 应用退出(<see cref="CleanupAll" />)。
/// </para>
/// </remarks>
public static class RemoteEditSessionManager
{
    private static readonly List<RemoteEditSession> Sessions = [];

    private static readonly Lock Gate = new();

    /// <summary>所有编辑副本的临时根目录:%TEMP%/VelaShell/remote-edit。</summary>
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "VelaShell", "remote-edit");

    /// <summary>会话增删或状态变化;界面据此刷新「正在编辑」列表。可能在任意线程触发。</summary>
    public static event Action? SessionsChanged;

    /// <summary>当前存活的会话(拷贝,可安全遍历)。</summary>
    public static IReadOnlyList<RemoteEditSession> ActiveSessions
    {
        get
        {
            lock (Gate)
            {
                return [.. Sessions];
            }
        }
    }

    /// <summary>按会话标识找一个存活会话;找不到返回 <see langword="null" />。</summary>
    /// <param name="id">会话标识。</param>
    public static RemoteEditSession? Find(Guid id)
    {
        lock (Gate)
        {
            return Sessions.FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>
    /// 打开(或复用)一个远程文件的本地编辑会话:下载到独占临时目录 → 按
    /// <see cref="RemoteEditRequest.OpenWith" /> 打开 → 侦听保存并回传。
    /// </summary>
    /// <param name="request">会话参数。</param>
    /// <param name="cancellationToken">取消下载。</param>
    /// <returns>新建或复用的会话;下载没能落地(取消/失败)时为 <see langword="null" />。</returns>
    /// <exception cref="InvalidOperationException">远端文件名不能安全地落到本地。</exception>
    public static async Task<RemoteEditSession?> OpenAsync(
        RemoteEditRequest request,
        CancellationToken cancellationToken = default)
    {
        // 同一个远程文件重复打开就复用:再下一份等于开第二个 watcher 对着第二份副本,
        // 两边各存各的,最后谁覆盖谁看运气。
        // 打开是 UI 线程发起的动作,而 LaunchAsync 可能要回到 UI 线程(系统默认程序走的是
        // TopLevel.Launcher)。所以本方法这条链<b>刻意不写 ConfigureAwait(false)</b> ——
        // 让续体回到调用者的上下文,别把一个必须在 UI 线程上做的事甩到线程池去。
        RemoteEditSession? existing = FindByTarget(request.SessionId, request.RemotePath);
        if (existing is not null)
        {
            RemoteEditLog.Write("open", $"reuse {request.RemotePath} -> {existing.LocalPath}");
            await existing.RefreshFromRemoteAsync(cancellationToken);
            await existing.LaunchAsync();
            return existing;
        }

        // 每次编辑独占一个子目录,避免同名文件互相覆盖。
        string directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8]);
        if (!LocalPathSafety.TryResolveDestination(directory, request.FileName, out string localPath))
        {
            throw new InvalidOperationException(Strings.Get("KeySvc_InvalidName"));
        }
        Directory.CreateDirectory(directory);
        if (!await FetchAsync(request, localPath, cancellationToken))
        {
            // 下载被取消或失败:别留一个对着半截文件的 watcher(调用方已经报过原因了)。
            TryDeleteDirectory(directory);
            RemoteEditLog.Write("open", $"download did not land {request.RemotePath}");
            return null;
        }
        var session = new RemoteEditSession(request, localPath);
        lock (Gate)
        {
            Sessions.Add(session);
        }
        RemoteEditLog.Write("open",
                            $"new {request.RemotePath} -> {localPath} (openWith={request.OpenWith}, autoUpload={request.AutoUpload})");
        RaiseSessionsChanged();
        await session.LaunchAsync();
        return session;
    }

    /// <summary>
    /// 结束一个会话(用户在「正在编辑」里点结束):先把没传完的传掉,再按结果决定
    /// 删不删本地副本。
    /// </summary>
    /// <param name="id">会话标识。</param>
    public static async Task CloseAsync(Guid id)
    {
        RemoteEditSession? session = Find(id);
        if (session is null)
        {
            return;
        }
        await session.ShutdownAsync(RemoteEditSession.ShutdownUploadTimeout).ConfigureAwait(false);
        Remove(session);
        session.Dispose();
    }

    /// <summary>
    /// 所属远程会话(SFTP 标签 / 终端侧栏)关闭时,连带结束它名下的编辑会话。
    /// </summary>
    /// <remarks>
    /// 不做这一步的话,watcher 会一直挂到应用退出,而它对着的连接早就没了 ——
    /// 用户之后的保存只会换来一串上传失败。
    /// </remarks>
    /// <param name="sessionId">远程会话标识。</param>
    public static async Task CloseScopeAsync(Guid sessionId)
    {
        RemoteEditSession[] owned;
        lock (Gate)
        {
            owned = [.. Sessions.Where(s => s.SessionId == sessionId)];
        }
        if (owned.Length == 0)
        {
            return;
        }
        RemoteEditLog.Write("close", $"scope {sessionId:N}: {owned.Length} session(s)");
        foreach (RemoteEditSession session in owned)
        {
            await session.ShutdownAsync(RemoteEditSession.ShutdownUploadTimeout).ConfigureAwait(false);
            Remove(session);
            session.Dispose();
        }
    }

    /// <summary>
    /// 应用退出时的同步入口(<c>desktop.Exit</c> 是同步事件)。
    /// </summary>
    /// <remarks>
    /// <b>收尾在线程池上跑,这里只限时等它。</b>上传回调可能要回 UI 线程(传输浮窗),
    /// 而退出事件本身就在 UI 线程上 —— 直接 <c>GetResult()</c> 会把两边锁死。
    /// 等不到就当作没落地:草稿保留、提示路径,退出照常继续,绝不让一条烂链路把关闭卡住。
    /// </remarks>
    public static void CleanupAll()
    {
        // 5 秒是"退出别卡住"和"局域网上一次小文件上传"之间的折中;超时不丢东西,只是留草稿。
        var budget = TimeSpan.FromSeconds(5);
        try
        {
            Task.Run(() => CleanupAllAsync(budget)).Wait(budget + TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 收尾本身不该阻止进程退出。
        }
    }

    /// <summary>
    /// 应用退出时调用:先把各会话未落地的改动传完,再删 remote-edit 临时树。
    /// </summary>
    /// <remarks>
    /// 退出路径和用户主动结束走同一套收尾规则,否则会出现"关编辑器安全、关应用丢改动"
    /// 这种只有特定顺序才复现的丢数据。传不完的那些会保留自己的临时子目录并提示路径,
    /// 所以这里<b>不再无条件删整棵树</b>。
    /// </remarks>
    /// <param name="timeout">等待全部收尾的总时限;退出流程不能被一条烂链路无限拖住。</param>
    public static async Task CleanupAllAsync(TimeSpan timeout)
    {
        RemoteEditSession[] pending;
        lock (Gate)
        {
            pending = [.. Sessions];
            Sessions.Clear();
        }
        try
        {
            await Task.WhenAll(pending.Select(s => s.ShutdownAsync(timeout))).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 超时的那些会在下面的 Dispose 里保留本地副本并提示路径。
        }
        foreach (RemoteEditSession session in pending)
        {
            session.Dispose();
        }
        if (pending.Length > 0)
        {
            RaiseSessionsChanged();
        }
        // 只清空壳:还留着草稿的子目录由 Dispose 决定保不保,这里不能一把全删。
        TryDeleteEmptyTree(TempRoot);
    }

    /// <summary>删掉临时树里的空目录,留下仍有草稿的那些。</summary>
    private static void TryDeleteEmptyTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为:清不掉留给下次启动。
        }
    }

    /// <summary>
    /// 取一次远端内容到本地副本:调用方给了下载实现就用它(进传输浮窗/可取消),
    /// 否则直接走服务。返回本地副本是否真的落地了。
    /// </summary>
    internal static async Task<bool> FetchAsync(RemoteEditRequest request, string localPath, CancellationToken cancellationToken)
    {
        if (request.DownloadAsync is not null)
        {
            // 回调说"成功"还不够,得地上真有这个文件:调用方那套下载会按「文件已存在时」的
            // 冲突策略跳过某些文件,跳过在它看来是正常收场,而对我们来说没有可监视的东西。
            return await request.DownloadAsync(localPath, cancellationToken).ConfigureAwait(false)
                   && File.Exists(localPath);
        }
        await request.SftpService
                     .DownloadFileAsync(request.SessionId, request.RemotePath, localPath, null, cancellationToken: cancellationToken)
                     .ConfigureAwait(false);
        return File.Exists(localPath);
    }

    private static RemoteEditSession? FindByTarget(Guid sessionId, string remotePath)
    {
        lock (Gate)
        {
            return Sessions.FirstOrDefault(s =>
                s.SessionId == sessionId && string.Equals(s.RemotePath, remotePath, StringComparison.Ordinal));
        }
    }

    internal static void Remove(RemoteEditSession session)
    {
        bool removed;
        lock (Gate)
        {
            removed = Sessions.Remove(session);
        }
        if (removed)
        {
            RaiseSessionsChanged();
        }
    }

    /// <summary>
    /// 广播会话变化。<b>订阅者炸了不能连累编辑会话本身。</b>
    /// </summary>
    /// <remarks>
    /// 这个事件是从 watcher 线程和上传任务里发出来的,而订阅者是界面。让一个界面异常
    /// 沿着调用栈冒回来,结果是"打开文件"或"保存回传"整条链路当场失败 —— 实测过一次:
    /// 一个跨线程的集合更新异常,把 <see cref="OpenAsync" /> 里紧随其后的"启动编辑器"
    /// 整个跳过了,文件下下来了却没人打开。
    /// </remarks>
    internal static void RaiseSessionsChanged()
    {
        if (SessionsChanged is not { } handlers)
        {
            return;
        }
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                RemoteEditLog.Write("ui", $"sessions-changed subscriber threw: {ex.Message}");
            }
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 临时文件清理是尽力而为:被占用就留给下次启动/系统清理。
        }
    }
}

/// <summary>
/// 一个远程文件的本地编辑会话:盯住本地副本,保存后把它传回远端。
/// </summary>
public sealed class RemoteEditSession : IDisposable
{
    /// <summary>
    /// 收尾时最多再等这么久让末次保存落到远端。
    /// </summary>
    /// <remarks>
    /// 这里以前是<b>无条件等 1.5 秒然后删目录</b>。1.5 秒要同时装下:600ms 防抖 +
    /// 等文件解锁(最多 3 次 × 300ms)+ 一次真实网络上传 —— 后者在慢链路或大文件上
    /// 根本不是 1.5 秒能完成的事。超时的后果不是"慢一点",而是把用户刚存的内容连同
    /// 本地副本一起删掉,远端还是旧的,且不报错。
    /// <para>
    /// 现在改成:等真实的上传结果;等不到就<b>保留本地副本</b>并告诉用户它在哪儿。
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan ShutdownUploadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>编辑器保存往往触发多个事件(写入 + 改名 + 属性),攒这么久再传一次。</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(600);

    private readonly Action<string>? _onError;
    private readonly RemoteEditRequest _request;
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly FileSystemWatcher _watcher;
    private Timer? _debounce;
    private bool _disposed;

    /// <summary>已进入收尾:不再接受新的文件变化(但正在跑的收尾还要把最后一次改动传完)。</summary>
    private bool _closing;

    /// <summary>有一次改动还没落到远端(防抖还没到点,或者刚攒下)。0/1,用 Interlocked 读写。</summary>
    private int _pendingSave;

    /// <summary>最近一次上传是否失败(失败就保留本地副本,不删临时目录)。</summary>
    private bool _lastUploadFailed;

    private RemoteEditState _state = RemoteEditState.Watching;
    private int _uploadCount;
    private DateTime? _lastUploadedAt;
    private string? _lastError;
    private bool _autoUpload;

    internal RemoteEditSession(RemoteEditRequest request, string localPath)
    {
        _request = request;
        _onError = request.OnError;
        _autoUpload = request.AutoUpload;
        LocalPath = localPath;
        _watcher = new(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
        {
            // FileName 不能少:很多编辑器的"安全保存"是写临时文件再改名顶上去
            // (Notepad--、VS Code、vim 的 writebackup 都是这个套路),
            // 只订 LastWrite/Size 的话这类保存一次都看不见。
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += (_, e) => OnFileEvent("changed", e.Name);
        _watcher.Created += (_, e) => OnFileEvent("created", e.Name);
        _watcher.Renamed += (_, e) => OnFileEvent("renamed", e.Name);
        _watcher.Error += (_, e) => RemoteEditLog.Write("watch", $"error {RemotePath}: {e.GetException().Message}");
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>会话标识(界面按它下发命令)。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>所属远程会话标识。</summary>
    public Guid SessionId => _request.SessionId;

    /// <summary>远端完整路径。</summary>
    public string RemotePath => _request.RemotePath;

    /// <summary>本地临时副本路径。</summary>
    public string LocalPath { get; }

    /// <summary>文件名。</summary>
    public string FileName => Path.GetFileName(RemotePath);

    /// <summary>保存后是否自动回传。设置改了之后由宿主刷新。</summary>
    public bool AutoUpload
    {
        get => Volatile.Read(ref _autoUpload);
        set
        {
            if (Volatile.Read(ref _autoUpload) == value)
            {
                return;
            }
            Volatile.Write(ref _autoUpload, value);
            // 从关到开:把攒着的那次改动立刻兑现,否则用户打开开关后还得再存一次。
            if (value && Volatile.Read(ref _pendingSave) == 1)
            {
                ScheduleUpload();
            }
            Publish();
        }
    }

    /// <summary>有改动还没落到远端。</summary>
    public bool HasPendingChange => Volatile.Read(ref _pendingSave) == 1;

    /// <summary>取一份当前状态的快照。</summary>
    public RemoteEditSnapshot Snapshot() =>
        new(Id,
            SessionId,
            FileName,
            RemotePath,
            LocalPath,
            _request.ServerName,
            _state,
            HasPendingChange,
            _uploadCount,
            _lastUploadedAt,
            _lastError,
            AutoUpload);

    /// <summary>拆除会话。上传没能落地时<b>保留</b>本地副本,不删临时目录。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _closing = true;
        _watcher.EnableRaisingEvents = false;
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        _watcher.Dispose();
        if (_lastUploadFailed || Volatile.Read(ref _pendingSave) == 1)
        {
            // 远端没拿到这份内容 —— 本地副本是它唯一的存身之处,删了就真没了。
            RemoteEditLog.Write("close", $"draft kept {RemotePath} -> {LocalPath}");
            _onError?.Invoke(Strings.Format("Svc_RemoteEditDraftKept", FileName, LocalPath));
            return;
        }
        RemoteEditLog.Write("close", $"done {RemotePath} (uploads={_uploadCount})");
        RemoteEditSessionManager.TryDeleteDirectory(Path.GetDirectoryName(LocalPath)!);
    }

    /// <summary>
    /// 收尾:停收新变化 → 把还没传的那一次传完 → 等在途上传结束。
    /// </summary>
    /// <param name="timeout">等待上传的时限。</param>
    /// <returns>全部内容都已落到远端时为 <see langword="true" />。</returns>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        _closing = true;
        _watcher.EnableRaisingEvents = false;
        // 防抖还没到点的那一次不能就这么丢掉 —— 那正是"改完立刻关编辑器"的常见情形。
        // 计时器停掉,但 _pendingSave 保持置位,下面的 UploadAsync 会把它兑现。
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        try
        {
            // UploadAsync 要先拿上传闸,所以 await 它同时也等掉了正在跑的那一次上传。
            await UploadAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        return !_lastUploadFailed && Volatile.Read(ref _pendingSave) == 0;
    }

    /// <summary>
    /// 立即回传一次(界面上的「立即上传」/失败后的重试;自动上传关掉时的唯一出口)。
    /// </summary>
    public async Task UploadNowAsync()
    {
        if (_disposed)
        {
            return;
        }
        Interlocked.Exchange(ref _pendingSave, 1);
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        RemoteEditLog.Write("upload", $"manual {RemotePath}");
        await UploadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 复用会话时把远端的最新内容取回本地副本。
    /// </summary>
    /// <remarks>
    /// <b>有未落地的改动时一步都不做</b>,只提示一句 —— 那份本地副本是用户改了还没传上去的
    /// 内容,盖掉它就是直接丢数据;宁可让用户看到旧内容,也不能把他刚写的东西冲掉。
    /// 干净时才刷新:期间关掉 watcher,回来前把这次下载本身触发的事件清掉,
    /// 否则"打开文件"会凭空换来一次把刚下下来的内容原样传回去的上传。
    /// </remarks>
    /// <param name="cancellationToken">取消下载。</param>
    public async Task RefreshFromRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _closing)
        {
            return;
        }
        if (HasPendingChange || _lastUploadFailed)
        {
            RemoteEditLog.Write("open", $"skip refresh (pending changes) {RemotePath}");
            _onError?.Invoke(Strings.Format("Svc_RemoteEditReusedDraft", FileName, LocalPath));
            return;
        }
        _watcher.EnableRaisingEvents = false;
        try
        {
            // 先删掉旧副本再取。下载走的是调用方那套(带"文件已存在时"冲突策略),
            // 留着它会让用户对着一个自己根本没见过的临时文件回答"要不要覆盖"。
            // 这一步只在<b>干净</b>时才走到:每一处本地改动都已经落到远端了,删掉不丢东西。
            File.Delete(LocalPath);

            // 不写 ConfigureAwait(false):调用方(OpenAsync)之后还要在 UI 线程上打开文件。
            await RemoteEditSessionManager.FetchAsync(_request, LocalPath, cancellationToken);
        }
        catch (Exception ex)
        {
            RemoteEditLog.Write("open", $"refresh failed {RemotePath}: {ex.Message}");
            _onError?.Invoke(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _pendingSave, 0);
            Interlocked.Exchange(ref _debounce, null)?.Dispose();
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>把本地副本交给配置的编辑器 / 系统默认程序打开。</summary>
    internal async Task LaunchAsync()
    {
        switch (_request.OpenWith)
        {
            case RemoteEditOpenWith.ConfiguredEditor when !string.IsNullOrWhiteSpace(_request.EditorCommand):
                LaunchEditor(_request.EditorCommand);
                break;
            case RemoteEditOpenWith.SystemDefault when _request.OpenLocalAsync is not null:
                await _request.OpenLocalAsync(LocalPath).ConfigureAwait(false);
                break;
            case RemoteEditOpenWith.Nothing:
            default:
                // 内置编辑器自己弹窗;会话只负责监视与回传。
                break;
        }
    }

    private void LaunchEditor(string editorCommand)
    {
        ProcessStartInfo startInfo = BuildEditorStartInfo(editorCommand.Trim().Trim('"'), LocalPath);
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            RemoteEditLog.Write("open", $"launch failed {editorCommand}: {ex.Message}");
            _onError?.Invoke(ex.Message);
            return;
        }
        if (process is null)
        {
            return;
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = NotifyEditorExitedAsync();
    }

    /// <summary>
    /// 编辑器进程退出了:把攒着的那次改动传掉,<b>会话继续存活</b>。
    /// </summary>
    /// <remarks>
    /// 旧实现在这里做过一次要命的判断:退出得早(&lt;3s)就认为是单实例编辑器的引导进程、
    /// 保留监听,否则认为编辑器真关了 —— 于是停 watcher、删临时目录。带标签页的单实例编辑器
    /// (Notepad--、Notepad++、VS Code)冷启动一慢就掉进后一支:文件还在编辑器里开着,
    /// VelaShell 这边已经不听了,之后每一次保存都无声丢失,这正是 #396 的第二条复现路径。
    /// 进程状态根本判断不出"用户还要不要编辑这个文件",别再拿它当依据。
    /// </remarks>
    internal async Task NotifyEditorExitedAsync()
    {
        RemoteEditLog.Write("watch", $"editor process exited {RemotePath}; flushing");
        if (_disposed || _closing || !HasPendingChange)
        {
            return;
        }
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        await UploadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 按平台组装编辑器启动方式:
    /// Windows — ShellExecute(支持 exe 完整路径、PATH 命令名与 App Paths 注册名,如 notepad++);
    /// macOS — 配置的不是现存可执行文件时按应用名/.app 包走 `open -a`(GUI 应用的正规启动方式);
    /// Linux — 直接 exec,命令名经 PATH 解析(如 gedit、kate、code)。
    /// </summary>
    private static ProcessStartInfo BuildEditorStartInfo(string editor, string filePath)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsMacOS() && !File.Exists(editor))
        {
            startInfo = new() { FileName = "open", UseShellExecute = false };
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add(editor);
            startInfo.ArgumentList.Add(filePath);
            return startInfo;
        }
        startInfo = new()
        {
            FileName = editor,
            UseShellExecute = OperatingSystem.IsWindows(),
            // 显式指定工作目录 = 被编辑文件所在的临时目录。不指定的话子进程继承 VelaShell 的
            // 工作目录(应用安装目录),编辑器的相对路径操作、swap/备份文件就会落到那儿 ——
            // 商店版的安装目录还是只读的,直接写失败(#120)。
            WorkingDirectory = Path.GetDirectoryName(filePath)
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        startInfo.ArgumentList.Add(filePath);
        return startInfo;
    }

    private void OnFileEvent(string kind, string? name)
    {
        RemoteEditLog.Write("watch", $"{kind} {name} ({RemotePath})");
        ScheduleUpload();
    }

    private void ScheduleUpload()
    {
        if (_disposed || _closing)
        {
            return;
        }
        // 先记账再排防抖:计时器可能被收尾拆掉,而"有一次改动还没传"这件事必须留下来,
        // 否则改完立刻关编辑器就会把那次保存丢在防抖窗口里。
        Interlocked.Exchange(ref _pendingSave, 1);
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        if (!AutoUpload)
        {
            // 自动上传关掉了:改动仍然记账并在界面上显示成"待上传",等用户点「立即上传」。
            Publish();
            return;
        }
        // ReSharper disable once RedundantAssignment
        // ReSharper disable once AllUnderscoreLocalParameterName
        _debounce = new(_ => _ = UploadAsync(), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        Publish();
    }

    /// <summary>
    /// 把待传的改动送上去。<b>拿到闸就说明在途的那一次已经结束</b>,所以 await 本方法
    /// 同时也是"等上传完成"。传完之后再看一眼:期间又存了就再传一轮,不留尾巴。
    /// </summary>
    private async Task UploadAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _uploadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (Interlocked.Exchange(ref _pendingSave, 0) == 1)
            {
                if (!File.Exists(LocalPath))
                {
                    // 文件没了(编辑器改名保存后又删了?)—— 没有可传的东西。
                    RemoteEditLog.Write("upload", $"skip, local copy missing {LocalPath}");
                    return;
                }
                _state = RemoteEditState.Uploading;
                Publish();
                try
                {
                    // 编辑器保存后可能短暂持锁:先等到文件可读再上传,保证传输浮窗里只出现一行。
                    await WaitUntilReadableAsync().ConfigureAwait(false);
                    if (_request.UploadAsync is not null)
                    {
                        await _request.UploadAsync(LocalPath, RemotePath).ConfigureAwait(false);
                    }
                    else
                    {
                        await _request.SftpService.UploadFileAsync(SessionId, LocalPath, RemotePath).ConfigureAwait(false);
                    }
                    _lastUploadFailed = false;
                    _lastError = null;
                    _uploadCount++;
                    _lastUploadedAt = DateTime.Now;
                    _state = RemoteEditState.Watching;
                    RemoteEditLog.Write("upload", $"ok #{_uploadCount} {RemotePath}");
                    Publish();
                }
                catch (Exception ex)
                {
                    // 失败要留痕:本地副本是这份改动唯一的存身之处,Dispose 据此决定不删目录。
                    _lastUploadFailed = true;
                    _lastError = ex.Message;
                    _state = RemoteEditState.Failed;
                    Interlocked.Exchange(ref _pendingSave, 1);
                    RemoteEditLog.Write("upload", $"failed {RemotePath}: {ex.Message}");
                    Publish();
                    _onError?.Invoke(Strings.Format("Svc_RemoteUpdateFailed", FileName, ex.Message));
                    return;
                }
            }
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    private async Task WaitUntilReadableAsync()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await using FileStream _ = File.Open(LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
    }

    private static void Publish() => RemoteEditSessionManager.RaiseSessionsChanged();
}
