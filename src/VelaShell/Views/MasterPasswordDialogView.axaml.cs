using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 主密码对话框(见 <see cref="MasterPasswordDialogViewModel" />)。<c>ShowDialog&lt;bool&gt;</c> 的结果是这一次解开了 / 做完了。
/// </summary>
public partial class MasterPasswordDialogView : Window
{
    /// <summary>初始化对话框。</summary>
    public MasterPasswordDialogView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Opened += (_, _) => FocusFirstField();
    }

    private MasterPasswordDialogViewModel? ViewModel => DataContext as MasterPasswordDialogViewModel;

    /// <summary>Esc 等价于取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseCancelled();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void FocusFirstField()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        TextBox? target = vm.ShowsPassword ? PasswordBox : vm.ShowsRecoveryInput ? RecoveryCodeBox : vm.ShowsNewPassword ? NewPasswordBox : null;
        target?.Focus();
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => CloseCancelled();

    private void CloseCancelled()
    {
        if (ViewModel is { IsBusy: true })
        {
            return; // 派生 / 写存储一旦开始就做完,不半途关框
        }
        ViewModel?.Cancel();
        this.PostClose(ViewModel?.Succeeded == true);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        if (await vm.ConfirmAsync())
        {
            vm.Cancel(); // 停掉倒数
            this.PostClose(vm.Succeeded || vm.Mode == MasterPasswordDialogMode.ShowRecoveryCode);
            return;
        }
        FocusFirstField();
    });

    private void ToggleRecovery_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.UseRecoveryCode = !vm.UseRecoveryCode;
            FocusFirstField();
        }
    }

    private void Forgot_Click(object? sender, RoutedEventArgs e) => ViewModel?.BeginForget();
}
