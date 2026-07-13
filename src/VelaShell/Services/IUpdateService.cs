namespace VelaShell.Services;

/// <summary>应用自动更新服务:检查、下载并应用新版本。</summary>
public interface IUpdateService
{
    /// <summary>当前正在运行的应用版本;无法确定时为 null。</summary>
    string? CurrentVersion { get; }

    /// <summary>检查后发现的可用新版本;无可用更新时为 null。</summary>
    string? AvailableVersion { get; }

    /// <summary>检查是否有可用更新;返回 true 表示存在比当前版本更新的版本。</summary>
    Task<bool> CheckForUpdateAsync();

    /// <summary>下载已检测到的更新包,可通过 <paramref name="progress"/> 汇报下载进度百分比。</summary>
    Task DownloadUpdateAsync(IProgress<int>? progress = null);

    /// <summary>应用已下载的更新并重启应用。</summary>
    void ApplyUpdateAndRestart();
}
