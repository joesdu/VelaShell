using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.PluginSdk.Versioning;
using VelaShell.Services.Update;

namespace VelaShell.Services;

/// <summary>
/// 便携式自更新服务:从 GitHub Releases 读取 CI 生成的 latest.json 清单,下载与当前
/// 平台匹配的压缩包到应用目录下的暂存目录,SHA-256 校验后由 <see cref="UpdateApplier" />
/// 解包,再交由本进程退出后才动手的外置换版进程完成换版与重启(见 <see cref="UpdateRunner" />)。
/// 应用装在哪里就更新哪里,不强制安装位置,也绝不触碰 ~/.velashell 数据目录。
/// Linux 仅检查版本,由关于页按安装方式打开对应安装包的下载地址(认不出时打开发布页)供用户手动安装。
/// </summary>
public class UpdateService : IUpdateService
{
    private readonly IUpdateSource _source;
    private readonly Func<Task<string>> _channelProvider;
    private readonly UpdateApplier _applier;
    private readonly Action _shutdownForRestart;
    private readonly Lock _stateLock = new();
    private UpdateManifest? _manifest;
    private UpdateAsset? _asset;
    private UpdateManifest? _readyUpdate;
    private UpdateAsset? _readyAsset;
    private string? _downloadedArchivePath;
    private bool _operationInProgress;
    private long _checkGeneration;

    /// <summary>
    /// 以 GitHub 仓库地址构造:更新源即该仓库的 Releases,无需自己架服务器。
    /// <paramref name="channelProvider" /> 返回更新通道("preview" 时预发布版也纳入,
    /// 其余走稳定通道;beta 阶段没有正式版时稳定通道自动放宽到最新预发布)。
    /// </summary>
    public UpdateService(string repositoryUrl, Func<Task<string>>? channelProvider = null)
        : this(new GitHubReleaseSource(repositoryUrl), channelProvider)
    { }

    /// <summary>核心构造,测试可注入更新源、应用目录、版本号、"关闭应用"动作与商店版标志。</summary>
    public UpdateService(
        IUpdateSource source,
        Func<Task<string>>? channelProvider = null,
        string? applicationDirectory = null,
        string? currentVersionOverride = null,
        Action? shutdownForRestart = null,
        bool? storeManagedOverride = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _channelProvider = channelProvider ?? (static () => Task.FromResult("stable"));
        _applier = new(applicationDirectory
            ?? Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory);
        CurrentVersion = currentVersionOverride;
        _shutdownForRestart = shutdownForRestart ?? DefaultShutdown;
        IsStoreManaged = storeManagedOverride ?? AppPackaging.IsPackaged;
    }

    /// <inheritdoc />
    public bool IsStoreManaged { get; }

    /// <summary>当前运行版本:程序集 InformationalVersion(含预发布后缀),读不到退回四段数字版。</summary>
    public string? CurrentVersion
    {
        get
        {
            if (field != null)
            {
                return field;
            }
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString();
        }
    }

    /// <summary>最近一次检查发现的可用更新版本;无可用更新时为 null。</summary>
    public string? AvailableVersion
    {
        get { lock (_stateLock) return _manifest?.Version; }
    }

    /// <inheritdoc />
    public UpdateManifest? AvailableUpdate
    {
        get { lock (_stateLock) return _manifest; }
    }

    /// <inheritdoc />
    public UpdateManifest? ReadyUpdate
    {
        get { lock (_stateLock) return _readyUpdate; }
    }

    /// <inheritdoc />
    public bool CanSelfUpdate =>
        !IsStoreManaged && !OperatingSystem.IsLinux()
        && UpdateManifest.CurrentRid() != null && _applier.IsApplicationDirectoryWritable();

    /// <summary>检查是否存在可用更新;商店版、平台不受支持或检查失败时返回 false。</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        long generation;
        lock (_stateLock)
        {
            // 下载和 Ready 固定到用户已选的候选,后台检查不能换成刚发布的新版本。
            if (_operationInProgress || _readyUpdate is not null)
            {
                return _manifest is not null;
            }
            generation = ++_checkGeneration;
            _manifest = null;
            _asset = null;
        }
        // 商店版连网络都不必发:更新归商店管,查到了也无从安装,徒增一次请求和一条误导提示。
        if (IsStoreManaged || UpdateManifest.CurrentRid() == null)
        {
            return false;
        }
        try
        {
            bool includePreRelease = string.Equals(
                await _channelProvider(), "preview", StringComparison.OrdinalIgnoreCase);
            UpdateManifest? manifest = await _source.GetLatestManifestAsync(includePreRelease);
            if (manifest == null
                || !SemanticVersion.TryParse(manifest.Version, out SemanticVersion latest)
                || !SemanticVersion.TryParse(CurrentVersion, out SemanticVersion current)
                || latest.CompareTo(current) <= 0
                || manifest.AssetForCurrentPlatform() is not { } asset)
            {
                return false;
            }
            lock (_stateLock)
            {
                if (generation != _checkGeneration || _operationInProgress || _readyUpdate is not null)
                {
                    return false;
                }
                _manifest = manifest;
                _asset = asset;
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 单次下载尝试连续这么久没有任何进度,就判定连接已经静默断开。NAT 表项过期、VPN 掉线之类
    /// 的断开不会让 TCP 报错,读操作会一直挂着(下载用 ResponseHeadersRead,HttpClient 的整体超时
    /// 管不到响应体),只能靠这个看门狗把它掐掉重连。45 秒:远长于界面开始提示停滞的 10 秒,
    /// 慢网偶尔的长停顿不会被误杀。
    /// </summary>
    internal static readonly TimeSpan DefaultDownloadStallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>因停滞断开重连的次数上限;用尽仍收不到数据就报下载失败,半成品留着下次续传。</summary>
    internal const int MaxStallReconnects = 3;

    /// <summary>判定下载停滞的时长;默认 <see cref="DefaultDownloadStallTimeout" />,测试用短值。</summary>
    internal TimeSpan DownloadStallTimeout { get; init; } = DefaultDownloadStallTimeout;

    /// <summary>
    /// 下载已检查到的更新包到暂存目录并做 SHA-256 校验,失败抛出异常。
    /// 支持断点续传:上次中断的半成品(<c>*.partial</c> 及其分段断点 <c>*.partial.meta</c>)
    /// 从断点继续;上次已完整下载且校验通过的包直接复用,不再耗费流量。
    /// 产物名带版本号与 RID,过期残留按名清理。校验优先采用下载过程中流式算出的
    /// 哈希(更新源返回),免去落盘后重读;源没给哈希时才读文件校验兜底。
    /// 下载停滞超过 <see cref="DownloadStallTimeout" /> 就断开、按断点重连,
    /// 最多 <see cref="MaxStallReconnects" /> 次,用尽抛出 <see cref="TimeoutException" />。
    /// 没有已选定的目标(没检查过,或检查结果被另一次并发检查清掉)时抛出
    /// <see cref="InvalidOperationException" />,不能当成下载成功。
    /// </summary>
    public Task DownloadUpdateAsync(
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsStoreManaged)
        {
            throw new InvalidOperationException("This installation does not support self-updating.");
        }
        UpdateManifest? manifest;
        UpdateAsset? asset;
        lock (_stateLock)
        {
            if (_operationInProgress)
            {
                throw new InvalidOperationException("An update operation is already in progress.");
            }
            manifest = _manifest;
            asset = _asset;
        }
        // 启动时的通知检查一开始就清空检查结果:它若恰好插在关于页的检查与下载之间,
        // 这里拿到的就是空目标。以前直接返回已完成的任务,调用方据此报「已就绪」,手里却没有包。
        if (manifest is null || asset is null)
        {
            throw new InvalidOperationException(
                "No update has been selected for download. Call CheckForUpdateAsync first.");
        }
        return DownloadUpdateCoreAsync(manifest, asset, progress, requireSelfUpdate: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task DownloadUpdateAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        UpdateAsset asset = ValidateExplicitManifest(manifest);
        return DownloadUpdateCoreAsync(manifest, asset, progress, requireSelfUpdate: true, cancellationToken);
    }

    private async Task DownloadUpdateCoreAsync(
        UpdateManifest manifest,
        UpdateAsset asset,
        IProgress<UpdateDownloadProgress>? progress,
        bool requireSelfUpdate,
        CancellationToken cancellationToken)
    {
        if (IsStoreManaged || (requireSelfUpdate && !CanSelfUpdate))
        {
            throw new InvalidOperationException("This installation does not support self-updating.");
        }
        if (!UpdateManifest.IsEligibleAsset(asset))
        {
            throw new ArgumentException("The update asset is invalid.", nameof(manifest));
        }
        lock (_stateLock)
        {
            if (_operationInProgress)
            {
                throw new InvalidOperationException("An update operation is already in progress.");
            }
            if (_readyUpdate is not null && !SameSnapshot(_readyUpdate, _readyAsset!, manifest, asset))
            {
                throw new InvalidOperationException("A different update is already ready; repair the update state first.");
            }
            _operationInProgress = true;
            ++_checkGeneration;
            _manifest = manifest;
            _asset = asset;
            // 重用前也必须重新校验磁盘文件;任何失败都不能留下虚假的 Ready。
            _readyUpdate = null;
            _readyAsset = null;
            _downloadedArchivePath = null;
        }

        try
        {
            string staging = _applier.EnsureStagingDirectory();
            string archivePath = Path.Combine(staging, asset.Name);
            string partialPath = archivePath + ".partial";
            string metaPath = partialPath + ".meta";
            foreach (string file in Directory.EnumerateFiles(staging))
            {
                if (file.Equals(archivePath, StringComparison.OrdinalIgnoreCase)
                    || file.Equals(partialPath, StringComparison.OrdinalIgnoreCase)
                    || file.Equals(metaPath, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).StartsWith(UpdateApplier.JournalFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                TryDeleteFile(file);
            }
            if (File.Exists(archivePath))
            {
                if (await IsChecksumValidAsync(archivePath, asset.Sha256))
                {
                    lock (_stateLock)
                    {
                        _downloadedArchivePath = archivePath;
                        _readyUpdate = manifest;
                        _readyAsset = asset;
                    }
                    progress?.Report(UpdateDownloadProgress.Completed(asset.Size));
                    return;
                }
                File.Delete(archivePath);
            }
            string? streamedHash = await DownloadWithStallWatchdogAsync(
                manifest, asset, archivePath, progress, cancellationToken);
            bool valid = streamedHash != null
                ? streamedHash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)
                : await IsChecksumValidAsync(archivePath, asset.Sha256);
            if (!valid)
            {
                File.Delete(archivePath);
                throw new InvalidDataException(
                    $"Update package checksum mismatch for {asset.Name}; the corrupt download was discarded.");
            }
            lock (_stateLock)
            {
                _downloadedArchivePath = archivePath;
                _readyUpdate = manifest;
                _readyAsset = asset;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _operationInProgress = false;
            }
        }
    }

    private UpdateAsset ValidateExplicitManifest(UpdateManifest manifest)
    {
        if (IsStoreManaged || !CanSelfUpdate)
        {
            throw new InvalidOperationException("This installation does not support self-updating.");
        }
        if (!manifest.IsEligibleForInstallation()
            || !SemanticVersion.TryParse(manifest.Version, out SemanticVersion latest)
            || !SemanticVersion.TryParse(CurrentVersion, out SemanticVersion current)
            || latest.CompareTo(current) <= 0)
        {
            throw new ArgumentException("The update manifest is not an eligible newer installation candidate.", nameof(manifest));
        }
        return manifest.AssetForCurrentPlatform()!;
    }

    private static bool SameSnapshot(
        UpdateManifest leftManifest, UpdateAsset leftAsset,
        UpdateManifest rightManifest, UpdateAsset rightAsset) =>
        string.Equals(leftManifest.Version, rightManifest.Version, StringComparison.Ordinal)
        && string.Equals(leftManifest.Tag, rightManifest.Tag, StringComparison.Ordinal)
        && string.Equals(leftAsset.Name, rightAsset.Name, StringComparison.Ordinal)
        && string.Equals(leftAsset.Sha256, rightAsset.Sha256, StringComparison.OrdinalIgnoreCase)
        && leftAsset.Size == rightAsset.Size;

    /// <summary>
    /// 带停滞看门狗地下载:每次尝试配一个"空闲超时"——每收到一条进度就把它往后推,
    /// 连续 <see cref="DownloadStallTimeout" /> 没有进度就取消这次尝试,再调一次更新源,
    /// 由它按 <c>*.partial</c> 断点续传。调用方自己取消时不重连,原样抛出。
    /// </summary>
    private async Task<string?> DownloadWithStallWatchdogAsync(
        UpdateManifest manifest,
        UpdateAsset asset,
        string archivePath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (int reconnects = 0; ; reconnects++)
        {
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdog.CancelAfter(DownloadStallTimeout);
            try
            {
                return await _source.DownloadAssetAsync(
                    manifest, asset, archivePath,
                    new StallWatchProgress(progress, watchdog, DownloadStallTimeout),
                    watchdog.Token);
            }
            catch (Exception ex) when (watchdog.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (reconnects >= MaxStallReconnects)
                {
                    throw new TimeoutException(
                        $"Update download made no progress for {DownloadStallTimeout.TotalSeconds:0}s "
                        + $"even after {MaxStallReconnects} reconnects.", ex);
                }
                Trace.WriteLine(
                    $"[VelaShell] Update download stalled, reconnecting ({reconnects + 1}/{MaxStallReconnects}): {ex.Message}");
            }
        }
    }

    /// <summary>转发进度的同时给看门狗续命:有进度就说明连接还活着。</summary>
    private sealed class StallWatchProgress(
        IProgress<UpdateDownloadProgress>? inner, CancellationTokenSource watchdog, TimeSpan stallTimeout)
        : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value)
        {
            // CancelAfter 会重置尚未触发的计时;已触发时无效,这次尝试照样作废重连。
            watchdog.CancelAfter(stallTimeout);
            inner?.Report(value);
        }
    }

    private static async Task<bool> IsChecksumValidAsync(string archivePath, string expectedSha256)
    {
        byte[] hash;
        await using (FileStream stream = File.OpenRead(archivePath))
        {
            hash = await SHA256.HashDataAsync(stream);
        }
        return Convert.ToHexStringLower(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="IsChecksumValidAsync" /> 的同步版,给本就跑在线程池上的换版用。</summary>
    private static bool IsChecksumValid(string archivePath, string expectedSha256)
    {
        byte[] hash;
        using (FileStream stream = File.OpenRead(archivePath))
        {
            hash = SHA256.HashData(stream);
        }
        return Convert.ToHexStringLower(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 清理旧残留失败不阻断下载,留待下次再清。
        }
    }

    /// <inheritdoc />
    public string? LastUpdateError => _applier.ReadLastError();

    /// <inheritdoc />
    public void ClearUpdateError() => _applier.ClearLastError();

    /// <inheritdoc />
    public bool RepairUpdateState()
    {
        lock (_stateLock)
        {
            if (_operationInProgress)
            {
                return false;
            }
            _operationInProgress = true;
            ++_checkGeneration;
            _manifest = null;
            _asset = null;
            _readyUpdate = null;
            _readyAsset = null;
            _downloadedArchivePath = null;
        }
        try
        {
            return _applier.TryRepair();
        }
        finally
        {
            lock (_stateLock)
            {
                _operationInProgress = false;
            }
        }
    }

    /// <summary>
    /// 解包 → 把换版交给外置进程 → 关闭当前应用。解包(<see cref="UpdateApplier.Stage" />)
    /// 不改动应用目录里的任何文件,因此磁盘不足、包损坏之类的失败都发生在"应用仍然完好"的状态下,
    /// 异常上抛后重试即可。换版本身由退出后才动手的外置进程完成(应用目录无文件被占用,
    /// 覆盖必然成功);包里没有主程序或外置进程拉不起来时退回本进程原地换版。
    /// 动手前先按就绪快照重新校验已下载的包:包已被删或损坏时丢掉就绪状态再抛出,
    /// 下一次检查更新重新下载;其余失败(文件被占用、磁盘满)保留就绪状态,可直接重试。
    /// </summary>
    public void ApplyUpdateAndRestart()
    {
        string archivePath;
        UpdateAsset readyAsset;
        lock (_stateLock)
        {
            if (IsStoreManaged || _operationInProgress)
            {
                throw new InvalidOperationException("An update cannot be applied in the current state.");
            }
            if (_readyUpdate is null || _readyAsset is not { } asset || _downloadedArchivePath is not { } path)
            {
                throw new InvalidOperationException(
                    "No update has been downloaded. Call CheckForUpdateAsync and DownloadUpdateAsync first.");
            }
            archivePath = path;
            readyAsset = asset;
            _operationInProgress = true;
            ++_checkGeneration;
        }
        try
        {
            // 就绪之后包仍可能被杀软隔离、被手动清理或在磁盘上损坏。就绪状态若原样留着,
            // 检查更新与立即更新都会短路成「已就绪」,重启又失败,只能修复更新状态才走得出来。
            if (!File.Exists(archivePath) || !IsChecksumValid(archivePath, readyAsset.Sha256))
            {
                TryDeleteFile(archivePath);
                lock (_stateLock)
                {
                    _readyUpdate = null;
                    _readyAsset = null;
                    _downloadedArchivePath = null;
                }
                throw new InvalidDataException(
                    $"The downloaded update package {readyAsset.Name} is missing or corrupt; it must be downloaded again.");
            }
            _applier.ClearLastError();
            _applier.Stage(archivePath);
            if (_applier.TryHandOffToExternalUpdater(Environment.ProcessId))
            {
                // 换版与重启都交给它了,本进程只管干净退出——退得越快,它等得越短。
                _shutdownForRestart();
                return;
            }
            _applier.SwapFromPayload();
            string exePath = Environment.ProcessPath
                ?? Path.Combine(_applier.ApplicationDirectory, "VelaShell" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!OperatingSystem.IsWindows())
            {
                // tar 包的可执行位在解包时已还原,这里兜底保证主程序可执行。
                File.SetUnixFileMode(exePath, File.GetUnixFileMode(exePath)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            Process.StartAndForget(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = _applier.ApplicationDirectory,
                UseShellExecute = false,
                ArgumentList = { "--after-update" }
            });
            _shutdownForRestart();
        }
        finally
        {
            lock (_stateLock)
            {
                _operationInProgress = false;
            }
        }
    }

    /// <summary>默认的重启前关闭动作:走 Avalonia 生命周期正常退出,拿不到时硬退。</summary>
    private static void DefaultShutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
        else
        {
            Environment.Exit(0);
        }
    }
}
