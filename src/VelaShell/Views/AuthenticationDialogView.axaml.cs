using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 身份验证对话框视图:采集连接凭据(密码/私钥),并在登录或取消命令完成后自动关闭窗口。
/// </summary>
public partial class AuthenticationDialogView : Window
{
    /// <summary>初始化身份验证对话框,注册窗口打开后的命令订阅。</summary>
    public AuthenticationDialogView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not AuthenticationDialogViewModel viewModel)
        {
            return;
        }
        viewModel.LoginCommand.Subscribe(Close);
        viewModel.CancelCommand.Subscribe(Close);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AuthenticationDialogViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    }
}
