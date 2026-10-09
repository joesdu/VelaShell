using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 批量修改对话框(#571)。<c>ShowDialog</c> 的结果是 <see cref="SessionBatchEditOutcome" />,取消为 null。
/// </summary>
public partial class SessionBatchEditView : Window
{
    /// <summary>初始化对话框,并在打开后订阅应用 / 取消以随之关闭。</summary>
    public SessionBatchEditView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>正在保存时不许关(理由同导入对话框):关掉了保存照样做完,宿主却不知道要刷新资源管理器。</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is SessionBatchEditViewModel { IsBusy: true })
        {
            e.Cancel = true;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not SessionBatchEditViewModel viewModel)
        {
            return;
        }
        // 命令由按钮点击触发,回调仍在输入事件栈内:推迟关闭(同共享凭据编辑框)。
        viewModel.ApplyCommand.Subscribe(this.PostClose);
        viewModel.CancelCommand.Subscribe(_ => this.PostClose(null));
    }

    /// <summary>Esc 等价于取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
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

    private void TogglePassword_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionBatchEditViewModel viewModel)
        {
            viewModel.ShowPassword = !viewModel.ShowPassword;
        }
    }

    private void BrowseKeyFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SessionBatchEditViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    });
}
