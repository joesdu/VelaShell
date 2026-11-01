using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Core.Data;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Services.MasterPassword;

/// <summary>
/// 交互式解锁框(<see cref="IMasterPasswordPrompt" /> 的界面实现):模态于此刻活动的窗口(没有就是主窗口)。
/// 同一时刻只有一个框由 <c>MasterPasswordService</c> 保证,这里只管弹。
/// </summary>
public sealed class MasterPasswordPromptDialogService(Func<IMasterPasswordService?> service, Action<string>? notify = null)
    : IMasterPasswordPrompt
{
    /// <inheritdoc />
    public Task<bool> PromptUnlockAsync(CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(ShowAsync);

    private async Task<bool> ShowAsync()
    {
        if (service() is not { } masterPassword || Owner() is not { } owner)
        {
            return false;
        }
        var viewModel = new MasterPasswordDialogViewModel(masterPassword, MasterPasswordDialogMode.Unlock);
        var dialog = new MasterPasswordDialogView { DataContext = viewModel };
        bool done = await dialog.ShowDialog<bool>(owner);
        if (viewModel.CompletionNotice is { } notice)
        {
            notify?.Invoke(notice); // 忘记主密码:说明旧密钥留在哪、已存的密码要重输
        }
        return done && !masterPassword.IsLocked;
    }

    private static Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }
        Window? active = desktop.Windows.FirstOrDefault(static w => w.IsActive && w.IsVisible);
        return active ?? (desktop.MainWindow is { IsVisible: true } main ? main : null);
    }
}
