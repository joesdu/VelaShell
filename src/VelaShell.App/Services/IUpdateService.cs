namespace VelaShell.App.Services;

public interface IUpdateService
{
    string? CurrentVersion { get; }

    string? AvailableVersion { get; }

    Task<bool> CheckForUpdateAsync();
    Task DownloadUpdateAsync(IProgress<int>? progress = null);
    void ApplyUpdateAndRestart();
}
