using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Core.Ssh;

namespace VelaShell.Services;

/// <summary>
/// 主机指纹人工确认(设置 → 安全审计):在 UI 线程弹既有的 HostKeyPromptView。
/// 由 SSH 握手线程同步等待,拿不到主窗口时按拒绝处理(fail-closed)。
/// </summary>
public sealed class HostKeyPromptDialogService : IHostKeyPrompt
{
    public async Task<bool> ConfirmAsync(string host,
        int port,
        string keyType,
        string fingerprint,
        HostKeyVerification verification)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                {
                    return false;
                }
                var dialog = new HostKeyPromptView
                {
                    DataContext = new HostKeyPromptViewModel(host, port, keyType, fingerprint, verification)
                };
                bool? result = await dialog.ShowDialog<bool?>(owner);
                return result == true;
            });
        }
        catch
        {
            // 安全决策失败时一律拒绝。
            return false;
        }
    }
}
