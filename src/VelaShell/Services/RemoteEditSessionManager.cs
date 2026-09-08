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
/// <param name="EditorTracked">能否自动察觉编辑器关闭;否则这一行只能手动结束。</param>
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
    bool AutoUpload,
    bool EditorTracked);

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

    /// <summary>
    /// 同上,但<b>把进程句柄交回来</b>(拿不到就返回 <see langword="null" />,回落到
    /// <see cref="OpenLocalAsync" />)。
    /// </summary>
    /// <remarks>
    /// 没有句柄就不知道用户什么时候关掉了编辑器,那一行会一直挂在「正在编辑」里不走
    /// —— 用户的原话是「我明明编辑器都关掉了,传输列表还显示正在编辑」。
    /// </remarks>
    public Func<string, Task<Process?>>? OpenLocalTrackedAsync { get; init; }

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
/// <b>会话生命周期不拿单个进程句柄下赌注。</b>旧实现用「进程 3 秒内退出就当作是引导进程」
/// 判断编辑器还开不开着 —— 单实例编辑器冷启动慢一点就误判成"已关闭",停 watcher、
/// 删临时目录,此后所有保存无声丢失。现在:进程活了一阵才退 = 用户关了它,收会话;
/// 启动即返回 = 引导进程转交完就走了,改成轮询「这个编辑器还有没有实例活着」
/// (VS Code 那种一个实例底下一堆同名进程的形态,盯任何单个句柄都是错的)。
/// 除此之外,会话由三件事结束:用户在传输浮窗里点结束、所属远程会话关闭、
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

    /// <summary>
    /// 进程活得比这还短就当作"引导进程转交完就退了",去找接手的实例,而不是判会话死刑。
    /// </summary>
    /// <remarks>
    /// 单实例编辑器把文件转交给已有实例通常在 1 秒内完成,但冷启动 + 慢盘能拖到好几秒;
    /// 给得宽一点是刻意的 —— 猜错这一边只是多留一行,猜错另一边是丢用户的改动。
    /// </remarks>
    private static readonly TimeSpan BootstrapExitWindow = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 引导进程退出后,每隔这么久看一眼编辑器还有没有实例活着。
    /// </summary>
    /// <remarks>
    /// 一次 <see cref="Process.GetProcessesByName(string)" /> 而已,几秒一次的开销可以忽略;
    /// 而这是"编辑器关了"唯一靠得住的信号。间隔给 5 秒,是让那一行在用户关掉编辑器之后
    /// 「差不多马上」消失 —— 再快也只是让轮询更吵,慢了用户又会觉得它没反应。
    /// </remarks>
    private static readonly TimeSpan LivenessPollInterval = TimeSpan.FromSeconds(5);

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

    /// <summary>当前盯着的编辑器进程是什么时候开始盯的(判断"启动即返回"用)。</summary>
    private DateTime _trackedSince;

    /// <summary>编辑器进程名,轮询存活用。退出后取不到,所以启动时就记下来。</summary>
    private string? _editorProcessName;

    /// <summary>引导进程退出后的存活轮询。</summary>
    private Timer? _livenessPoll;

    /// <summary>见过至少一次这个编辑器的实例。没见过就永远不判它死(见 StartLivenessPoll)。</summary>
    private bool _sawEditorInstance;

    /// <summary>
    /// 拿到过任何能判断"编辑器关没关"的抓手(进程句柄或进程名)。
    /// </summary>
    /// <remarks>
    /// 为 <see langword="false" /> 时这一行只能由用户手动结束 —— 界面得说出来,
    /// 而不是让用户对着一个永远不消失的"正在编辑"发愣。
    /// </remarks>
    private bool _editorTracked;

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
            AutoUpload,
            _editorTracked);

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
        Interlocked.Exchange(ref _livenessPoll, null)?.Dispose();
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
            case RemoteEditOpenWith.SystemDefault:
                await LaunchWithSystemDefaultAsync().ConfigureAwait(false);
                break;
            case RemoteEditOpenWith.Nothing:
            default:
                // 内置编辑器自己弹窗;会话只负责监视与回传。
                break;
        }
    }

    /// <summary>
    /// 交给系统默认程序。优先走能拿到进程句柄的那条 —— 拿不到句柄,
    /// 就永远不知道用户什么时候把编辑器关了,这一行会一直挂在「正在编辑」里。
    /// </summary>
    private async Task LaunchWithSystemDefaultAsync()
    {
        if (_request.OpenLocalTrackedAsync is not null)
        {
            Process? process = await _request.OpenLocalTrackedAsync(LocalPath).ConfigureAwait(false);
            if (process is not null)
            {
                Track(process);
                return;
            }
        }
        if (_request.OpenLocalAsync is not null)
        {
            RemoteEditLog.Write("open", $"opened untracked (no process handle) {LocalPath}");
            await _request.OpenLocalAsync(LocalPath).ConfigureAwait(false);
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
        if (process is not null)
        {
            Track(process);
        }
    }

    /// <summary>盯住一个编辑器进程:它退出时决定这个会话是接着守还是就此收摊。</summary>
    private void Track(Process process)
    {
        _trackedSince = DateTime.UtcNow;
        _editorTracked = true;
        try
        {
            // 进程名必须在它还活着的时候取 —— 退出之后 ProcessName 直接抛。
            _editorProcessName ??= process.ProcessName;
            _sawEditorInstance = true;
        }
        catch (InvalidOperationException)
        {
            // 已经退了,拿不到名字就没法轮询存活;下面照常按退出处理。
        }
        int[] fired = [0];
        process.Exited += (_, _) =>
        {
            // EnableRaisingEvents 对一个已经退出的进程也会补一次事件,加个闸免得跑两遍。
            if (Interlocked.Exchange(ref fired[0], 1) == 0)
            {
                _ = OnEditorProcessExitedAsync(process, DateTime.UtcNow - _trackedSince);
            }
        };
        process.EnableRaisingEvents = true;
    }

    /// <summary>
    /// 编辑器进程退出了:先把攒着的那次改动传掉,再判断这是"编辑器真的关了"还是
    /// "单实例编辑器的引导进程转交完就退了"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧实现用「3 秒内退出 = 引导进程,否则编辑器关了 → 停 watcher、删临时目录」。
    /// 带标签页的单实例编辑器(Notepad--、Notepad++、VS Code)冷启动一慢就掉进后一支:
    /// 文件还在编辑器里开着,VelaShell 这边已经不听了,之后每一次保存无声丢失 ——
    /// #396 的第二条复现路径。
    /// </para>
    /// <para>
    /// <b>时长现在只决定"要不要去找接手的实例",不再决定生死。</b>两条岔路的代价差着量级:
    /// 收早了 = 用户后面的保存<b>悄悄丢掉</b>;收晚了 = 列表里多挂一行。所以只有
    /// <b>活了一阵才退</b>的进程才算数 —— 那就是编辑器本身,用户把它关了;
    /// 启动即返回的那种一律往"还开着"的方向猜,顶多让用户自己点一下「结束监视」。
    /// </para>
    /// </remarks>
    /// <param name="process">刚退出的那个进程。</param>
    /// <param name="aliveFor">它从被盯上到退出活了多久。</param>
    /// <returns>会话是否就此结束。</returns>
    internal async Task<bool> OnEditorProcessExitedAsync(Process? process, TimeSpan aliveFor)
    {
        RemoteEditLog.Write("watch", $"editor process exited after {aliveFor.TotalSeconds:F1}s ({RemotePath})");
        await FlushAsync().ConfigureAwait(false);
        if (_disposed || _closing)
        {
            return true;
        }
        if (aliveFor < BootstrapExitWindow)
        {
            // 启动即返回:多半是引导进程,文件已经转交给别的实例。改用"这个编辑器还有没有
            // 实例活着"来判断,而不是再挑一个进程句柄盯着(理由见 StartLivenessPoll)。
            RemoteEditLog.Write("watch", $"looks like a bootstrap exit; polling {_editorProcessName ?? "?"} liveness ({RemotePath})");
            StartLivenessPoll();
            return false;
        }
        RemoteEditLog.Write("watch", $"editor closed; ending session ({RemotePath})");
        await RemoteEditSessionManager.CloseAsync(Id).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 改用轮询「这个编辑器还有没有实例活着」来判断它关没关。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上一版是"挑一个同名的存活进程改盯它"。<b>对 VS Code 这类多进程应用是错的</b> ——
    /// 一个 VS Code 实例底下是一堆同名进程(主进程 + GPU + 渲染 + 扩展宿主),
    /// 收养到哪一个都不代表"应用还开着":收养到辅助进程,它随时会自己退;
    /// 而收养到主进程也未必等得到事件。用户反馈的正是这个形态 —— 双击 `.profile`,
    /// VS Code 打开,关掉 VS Code 之后那一行还挂着;而 txt(记事本,单进程、
    /// 我们起的就是真身)一切正常。
    /// </para>
    /// <para>
    /// 问题问对了就简单了:不问"某个进程死了没",问"这个名字还有活的没有"。
    /// 多进程、单实例、启动器转交,三种形态一个答案。
    /// </para>
    /// <para>
    /// <b>没见过它起来就永远不判死。</b>启动器把文件转交出去、真身还没起来的那一瞬,
    /// 名字底下可能一个进程都没有;此时就收摊等于把用户后面的保存悄悄丢掉(#396)。
    /// 所以要先见到过至少一次实例,之后"全没了"才算关闭。
    /// </para>
    /// </remarks>
    private void StartLivenessPoll()
    {
        if (_editorProcessName is null && EditorLivenessProbeForTest is null)
        {
            // 连进程名都没拿到就无从判断;那一行只能由用户手动结束(界面会说明)。
            RemoteEditLog.Write("watch", $"no process name to poll; row must be ended by hand ({RemotePath})");
            _editorTracked = false;
            Publish();
            return;
        }
        Interlocked.Exchange(ref _livenessPoll, null)?.Dispose();
        _livenessPoll = new(_ => _ = PollEditorLivenessAsync(), null, LivenessPollInterval, LivenessPollInterval);
    }

    private async Task PollEditorLivenessAsync()
    {
        if (_disposed || _closing)
        {
            return;
        }
        if (AnyEditorInstanceAlive())
        {
            _sawEditorInstance = true;
            return;
        }
        if (!_sawEditorInstance)
        {
            // 还没见过它起来 —— 可能是启动器刚转交完、真身还在加载。别急着判它死。
            return;
        }
        RemoteEditLog.Write("watch", $"no {_editorProcessName} instance left; ending session ({RemotePath})");
        Interlocked.Exchange(ref _livenessPoll, null)?.Dispose();
        await FlushAsync().ConfigureAwait(false);
        await RemoteEditSessionManager.CloseAsync(Id).ConfigureAwait(false);
    }

    /// <summary>存活探针的替身:回归用例用它把"编辑器还在不在"变成可控输入。</summary>
    /// <remarks>
    /// 不这么做的话用例就得真起一个进程,再拿进程名去数 —— 而进程名是全机器共享的:
    /// CI 上恰好另有一个同名进程(cmd、sh、dotnet 都极可能),用例就随机变红。
    /// </remarks>
    internal Func<bool>? EditorLivenessProbeForTest { get; set; }

    /// <summary>引导进程退出后是否真的挂上了存活轮询(回归用例读它)。</summary>
    internal bool HasLivenessPollForTest => Volatile.Read(ref _livenessPoll) is not null;

    /// <summary>手动推一次存活轮询,免得用例干等定时器(回归用例专用)。</summary>
    internal Task PollEditorLivenessForTestAsync() => PollEditorLivenessAsync();

    /// <summary>这个编辑器名下还有没有活着的进程。</summary>
    private bool AnyEditorInstanceAlive()
    {
        if (EditorLivenessProbeForTest is { } probe)
        {
            return probe();
        }
        if (_editorProcessName is null)
        {
            return false;
        }
        Process[] instances;
        try
        {
            instances = Process.GetProcessesByName(_editorProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or SystemException)
        {
            // 查不了就当它还活着 —— 往"别把会话收早"的方向倒。
            return true;
        }
        try
        {
            return instances.Length > 0;
        }
        finally
        {
            // 每次轮询都会新开一批句柄,不还回去就是稳定的句柄泄漏。
            foreach (Process instance in instances)
            {
                instance.Dispose();
            }
        }
    }

    /// <summary>把攒着的那次改动传掉,会话继续存活。</summary>
    private async Task FlushAsync()
    {
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
