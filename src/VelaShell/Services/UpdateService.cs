using System.Reflection;
using System.Runtime.InteropServices;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace VelaShell.Services;

/// <summary>基于 Velopack + GitHub Releases 的应用更新服务:检查、下载并应用更新包后重启。</summary>
public class UpdateService : IUpdateService
{
    private readonly UpdateManager _updateManager;
    private UpdateInfo? _updateInfo;

    /// <summary>
    /// 以 GitHub 仓库地址构造:更新源即该仓库的 Releases,无需自建服务器。
    /// <paramref name="allowPreRelease" /> 为 <c>true</c> 时预发布(beta)也纳入更新——beta 阶段应保持开启,
    /// 发布正式版后可置 <c>false</c> 只推稳定版。更新渠道按当前进程架构解析(win-x64 / win-arm64),
    /// 与 CI <c>vpk pack --channel win-&lt;arch&gt;</c> 一致:x64 与 arm64 各自独立更新轨。
    /// </summary>
    public UpdateService(string repositoryUrl, bool allowPreRelease = true, IVelopackLocator? locator = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryUrl);
        GithubSource source = new(repositoryUrl, accessToken: null, prerelease: allowPreRelease);
        UpdateOptions options = new() { ExplicitChannel = ResolveChannel() };
        _updateManager = new(source, options, locator: locator);
    }

    /// <summary>按运行架构解析更新渠道,匹配 CI 的 <c>--channel win-x64 / win-arm64</c>。</summary>
    private static string ResolveChannel() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>是否由 Velopack 安装器管理(仅经 Setup.exe 安装的版本为 true);便携版 / MSI / 非安装版为 false。</summary>
    public bool IsUpdaterManaged => _updateManager.IsInstalled;

    /// <summary>当前运行版本:已安装时取 Velopack 记录,否则回退到程序集版本。</summary>
    public string? CurrentVersion
    {
        get
        {
            if (_updateManager.IsInstalled)
            {
                return _updateManager.CurrentVersion?.ToString();
            }
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetName().Version?.ToString();
        }
    }

    /// <summary>最近一次检查发现的可用更新版本;无可用更新时为 <c>null</c>。</summary>
    public string? AvailableVersion => _updateInfo?.TargetFullRelease.Version?.ToString();

    /// <summary>检查是否存在可用更新;未安装或检查失败时返回 <c>false</c>。</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        if (!_updateManager.IsInstalled)
        {
            return false;
        }
        try
        {
            _updateInfo = await _updateManager.CheckForUpdatesAsync();
            return _updateInfo != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>下载已检查到的更新包,可通过 <paramref name="progress" /> 报告下载进度。</summary>
    public async Task DownloadUpdateAsync(IProgress<int>? progress = null)
    {
        if (_updateInfo == null)
        {
            return;
        }
        await _updateManager.DownloadUpdatesAsync(_updateInfo,
            progress != null ? progress.Report : null);
    }

    /// <summary>应用已下载的更新并重启应用;若尚未下载更新则抛出异常。</summary>
    public void ApplyUpdateAndRestart()
    {
        if (_updateInfo?.TargetFullRelease == null)
        {
            throw new InvalidOperationException("No update has been downloaded. Call CheckForUpdateAsync and DownloadUpdateAsync first.");
        }
        _updateManager.ApplyUpdatesAndRestart(_updateInfo.TargetFullRelease);
    }
}
