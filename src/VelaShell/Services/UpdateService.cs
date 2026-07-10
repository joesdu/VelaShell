using System.Reflection;
using Velopack;
using Velopack.Locators;

namespace VelaShell.Services;

public class UpdateService : IUpdateService
{
    private readonly UpdateManager _updateManager;
    private UpdateInfo? _updateInfo;

    public UpdateService(string updateUrl, IVelopackLocator? locator = null)
    {
        if (updateUrl is null)
        {
            throw new ArgumentNullException(nameof(updateUrl));
        }
        _updateManager = new(updateUrl, locator: locator);
    }

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

    public string? AvailableVersion => _updateInfo?.TargetFullRelease.Version?.ToString();

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

    public async Task DownloadUpdateAsync(IProgress<int>? progress = null)
    {
        if (_updateInfo == null)
        {
            return;
        }
        await _updateManager.DownloadUpdatesAsync(_updateInfo,
            progress != null ? progress.Report : null);
    }

    public void ApplyUpdateAndRestart()
    {
        if (_updateInfo?.TargetFullRelease == null)
        {
            throw new InvalidOperationException("No update has been downloaded. Call CheckForUpdateAsync and DownloadUpdateAsync first.");
        }
        _updateManager.ApplyUpdatesAndRestart(_updateInfo.TargetFullRelease);
    }
}
