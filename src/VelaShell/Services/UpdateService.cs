using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Services.Update;

namespace VelaShell.Services;

/// <summary>
/// 便携式自更新服务:从 GitHub Releases 读取 CI 生成的 latest.json 清单,下载与当前
/// 平台匹配的压缩包到应用目录下的暂存目录,SHA-256 校验后由 <see cref="UpdateApplier" />
/// 原地换版并重启。应用装在哪里就更新哪里,不强制安装位置,也绝不触碰
/// %LocalAppData%/VelaShell 数据目录。
/// </summary>
public class UpdateService : IUpdateService
{
    private readonly IUpdateSource _source;
    private readonly Func<Task<string>> _channelProvider;
    private readonly UpdateApplier _applier;
    private readonly Action _shutdownForRestart;
    private readonly string? _currentVersionOverride;
    private UpdateManifest? _manifest;
    private UpdateAsset? _asset;
    private string? _downloadedArchivePath;

    /// <summary>
    /// 以 GitHub 仓库地址构造:更新源即该仓库的 Releases,无需自建服务器。
    /// <paramref name="channelProvider" /> 返回更新通道("preview" 时预发布版也纳入,
    /// 其余走稳定通道;beta 阶段没有正式版时稳定通道自动放宽到最新预发布)。
    /// </summary>
    public UpdateService(string repositoryUrl, Func<Task<string>>? channelProvider = null)
        : this(new GitHubReleaseSource(repositoryUrl), channelProvider)
    {
    }

    /// <summary>核心构造,测试可注入更新源、应用目录、版本号与"关闭应用"动作。</summary>
    public UpdateService(
        IUpdateSource source,
        Func<Task<string>>? channelProvider = null,
        string? applicationDirectory = null,
        string? currentVersionOverride = null,
        Action? shutdownForRestart = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _channelProvider = channelProvider ?? (static () => Task.FromResult("stable"));
        _applier = new(applicationDirectory
            ?? Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory);
        _currentVersionOverride = currentVersionOverride;
        _shutdownForRestart = shutdownForRestart ?? DefaultShutdown;
    }

    /// <summary>当前运行版本:程序集 InformationalVersion(含预发布后缀),读不到退回四段数字版。</summary>
    public string? CurrentVersion
    {
        get
        {
            if (_currentVersionOverride != null)
            {
                return _currentVersionOverride;
            }
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString();
        }
    }

    /// <summary>最近一次检查发现的可用更新版本;无可用更新时为 null。</summary>
    public string? AvailableVersion => _manifest?.Version;

    /// <inheritdoc />
    public bool CanSelfUpdate =>
        UpdateManifest.CurrentRid() != null && _applier.IsApplicationDirectoryWritable();

    /// <summary>检查是否存在可用更新;平台不受支持或检查失败时返回 false。</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        _manifest = null;
        _asset = null;
        _downloadedArchivePath = null;
        if (UpdateManifest.CurrentRid() == null)
        {
            return false;
        }
        try
        {
            bool includePreRelease = string.Equals(
                await _channelProvider(), "preview", StringComparison.OrdinalIgnoreCase);
            UpdateManifest? manifest = await _source.GetLatestManifestAsync(includePreRelease);
            if (manifest == null
                || !UpdateVersion.TryParse(manifest.Version, out UpdateVersion latest)
                || !UpdateVersion.TryParse(CurrentVersion, out UpdateVersion current)
                || latest.CompareTo(current) <= 0
                || manifest.AssetForCurrentPlatform() is not { } asset)
            {
                return false;
            }
            _manifest = manifest;
            _asset = asset;
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载已检查到的更新包到暂存目录并做 SHA-256 校验,失败抛出异常。
    /// 支持断点续传:上次中断的半成品(<c>*.partial</c> 及其分段断点 <c>*.partial.meta</c>)
    /// 从断点继续;上次已完整下载且校验通过的包直接复用,不再耗费流量。
    /// 产物名带版本号与 RID,过期残留按名清理。校验优先采用下载过程中流式算出的
    /// 哈希(更新源返回),免去落盘后重读;源没给哈希时才读文件校验兜底。
    /// </summary>
    public async Task DownloadUpdateAsync(IProgress<int>? progress = null)
    {
        if (_manifest == null || _asset == null)
        {
            return;
        }
        _downloadedArchivePath = null;
        string staging = _applier.EnsureStagingDirectory();
        string archivePath = Path.Combine(staging, _asset.Name);
        string partialPath = archivePath + ".partial";
        string metaPath = partialPath + ".meta";
        foreach (string file in Directory.EnumerateFiles(staging))
        {
            if (!file.Equals(archivePath, StringComparison.OrdinalIgnoreCase)
                && !file.Equals(partialPath, StringComparison.OrdinalIgnoreCase)
                && !file.Equals(metaPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(file);
            }
        }
        foreach (string directory in Directory.EnumerateDirectories(staging))
        {
            // 子目录只可能是上次换版中断留下的解压残留,与下载无关,直接清掉。
            TryDeleteDirectory(directory);
        }
        if (File.Exists(archivePath))
        {
            if (await IsChecksumValidAsync(archivePath, _asset.Sha256))
            {
                _downloadedArchivePath = archivePath;
                progress?.Report(100);
                return;
            }
            File.Delete(archivePath);
        }
        string? streamedHash = await _source.DownloadAssetAsync(_manifest, _asset, archivePath, progress);
        bool valid = streamedHash != null
            ? streamedHash.Equals(_asset.Sha256, StringComparison.OrdinalIgnoreCase)
            : await IsChecksumValidAsync(archivePath, _asset.Sha256);
        if (!valid)
        {
            File.Delete(archivePath);
            throw new InvalidDataException(
                $"Update package checksum mismatch for {_asset.Name}; the corrupt download was discarded.");
        }
        _downloadedArchivePath = archivePath;
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // 清理旧残留失败不阻断下载,留待下次再清。
        }
    }

    /// <summary>
    /// 原地换版 → 拉起新进程(带 --after-update,新进程会等待本进程释放单实例锁)→
    /// 关闭当前应用。若尚未下载更新则抛出异常;换版失败时 <see cref="UpdateApplier" />
    /// 已回滚,异常原样上抛,应用继续以当前版本运行。
    /// </summary>
    public void ApplyUpdateAndRestart()
    {
        if (_downloadedArchivePath == null || !File.Exists(_downloadedArchivePath))
        {
            throw new InvalidOperationException(
                "No update has been downloaded. Call CheckForUpdateAsync and DownloadUpdateAsync first.");
        }
        _applier.Apply(_downloadedArchivePath);
        string exePath = Environment.ProcessPath
            ?? Path.Combine(_applier.ApplicationDirectory, "VelaShell" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!OperatingSystem.IsWindows())
        {
            // tar 包的可执行位在解包时已还原,这里兜底保证主程序可执行。
            File.SetUnixFileMode(exePath, File.GetUnixFileMode(exePath)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        Process.Start(new ProcessStartInfo(exePath)
        {
            WorkingDirectory = _applier.ApplicationDirectory,
            UseShellExecute = false,
            ArgumentList = { "--after-update" }
        });
        _shutdownForRestart();
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
