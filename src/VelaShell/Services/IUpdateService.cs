namespace VelaShell.Services;

/// <summary>应用自动更新服务:检查、下载并应用新版本。</summary>
public interface IUpdateService
{
    /// <summary>当前正在运行的应用版本;无法确定时为 null。</summary>
    string? CurrentVersion { get; }

    /// <summary>检查后发现的可用新版本;无可用更新时为 null。</summary>
    string? AvailableVersion { get; }

    /// <summary>
    /// 能否原地自更新:应用目录可写且平台受支持时为 true。装在 Program Files 等
    /// 只读位置时为 false,发现新版本后只能提示用户手动下载。
    /// </summary>
    bool CanSelfUpdate { get; }

    /// <summary>检查是否有可用更新;返回 true 表示存在比当前版本更新的版本。</summary>
    Task<bool> CheckForUpdateAsync();

    /// <summary>下载已检测到的更新包并校验完整性,可通过 <paramref name="progress"/> 汇报下载进度百分比。</summary>
    Task DownloadUpdateAsync(IProgress<int>? progress = null);

    /// <summary>应用已下载的更新并重启应用。</summary>
    void ApplyUpdateAndRestart();
}
