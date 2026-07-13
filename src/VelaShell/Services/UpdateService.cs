using System.Reflection;
using Velopack;
using Velopack.Locators;

namespace VelaShell.Services;

/// <summary>基于 Velopack 的应用更新服务:检查、下载并应用更新包后重启。</summary>
public class UpdateService : IUpdateService
{
    private readonly UpdateManager _updateManager;
    private UpdateInfo? _updateInfo;

    /// <summary>使用更新源地址与可选定位器构造服务。</summary>
    public UpdateService(string updateUrl, IVelopackLocator? locator = null)
    {
        ArgumentNullException.ThrowIfNull(updateUrl);
        _updateManager = new(updateUrl, locator: locator);
    }

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
