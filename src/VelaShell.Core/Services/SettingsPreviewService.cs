using VelaShell.Core.Models;

namespace VelaShell.Core.Services;

/// <summary>
/// 设置「即时预览」通道:设置窗口在外观项(主题/强调色除外,它们直接走 IThemeService)
/// 变化时广播一份未持久化的设置快照,主窗口与主 VM 按 SettingsSaved 同样的方式应用;
/// 取消/未保存关闭时用打开设置前的基线快照再广播一次即恢复原状。
/// </summary>
public interface ISettingsPreviewService
{
    /// <summary>预览快照(未持久化)。在 UI 线程触发。</summary>
    event Action<AppSettings>? PreviewRequested;

    void Preview(AppSettings settings);
}

public sealed class SettingsPreviewService : ISettingsPreviewService
{
    public event Action<AppSettings>? PreviewRequested;

    public void Preview(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PreviewRequested?.Invoke(settings);
    }
}
