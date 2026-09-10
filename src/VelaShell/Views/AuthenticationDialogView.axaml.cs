using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 身份验证对话框视图:采集连接凭据(密码 / 证书 / 私钥),并在登录或取消命令完成后自动关闭窗口。
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
        // 登录/取消命令由按钮点击触发,回调仍在输入事件栈内:推迟关闭,避免后续路由
        // 打到已销毁的窗口刷 "PlatformImpl is null" 警告。
        viewModel.LoginCommand.Subscribe(this.PostClose);
        viewModel.CancelCommand.Subscribe(this.PostClose);
    }

    /// <summary>Esc 等价于点击取消:经 CancelCommand 走与取消按钮完全相同的关闭路径(结果为 null)。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is AuthenticationDialogViewModel viewModel)
            {
                viewModel.CancelCommand.Execute().Subscribe();
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void BrowseKeyFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not AuthenticationDialogViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    });

    /// <summary>
    /// 选择 OpenSSH 用户证书文件;私钥还空着时按 <c>&lt;key&gt;-cert.pub</c> 约定顺手补上。
    /// </summary>
    /// <remarks>
    /// 与连接配置页同一套口径,推断逻辑落在 <see cref="OpenSshCertificate" /> 里 ——
    /// 两个对话框各写一份的话,改了后缀只会改好其中一处。
    /// </remarks>
    private void BrowseCertificateFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not AuthenticationDialogViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectCertFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }
        viewModel.CertificatePath = path;
        if (string.IsNullOrWhiteSpace(viewModel.PrivateKeyPath) && OpenSshCertificate.InferPrivateKeyPath(path) is { } keyPath)
        {
            viewModel.PrivateKeyPath = keyPath;
        }
    });
}
