using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 导出连接对话框(#571)。<c>ShowDialog</c> 的结果是 <see cref="SessionExportResult" />,取消为 null。
/// </summary>
public partial class SessionExportView : Window
{
    /// <summary>初始化对话框,打开后统计导出范围。</summary>
    public SessionExportView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is SessionExportViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    });

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

    private void Cancel_Click(object? sender, RoutedEventArgs e) => this.PostClose(null);

    private void TogglePassphrase_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionExportViewModel viewModel)
        {
            viewModel.ShowPassphrase = !viewModel.ShowPassphrase;
        }
    }

    /// <summary>选保存位置,生成文件写进去;成功即关闭,失败留在对话框里显示原因。</summary>
    private void Export_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SessionExportViewModel { CanExport: true } viewModel)
        {
            return;
        }
        bool json = viewModel.IsJson;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("SessExport_Title"),
            SuggestedFileName = viewModel.SuggestedFileName,
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
            DefaultExtension = viewModel.Extension,
            FileTypeChoices =
            [
                json
                    ? new FilePickerFileType(Strings.Get("SessFile_JsonType")) { Patterns = ["*.json"] }
                    : new FilePickerFileType(Strings.Get("SessFile_CsvType")) { Patterns = ["*.csv"] }
            ]
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return; // 用户取消了保存对话框:对话框留着,可以换个格式再来。
        }
        if (await viewModel.ExportToAsync(path) is { } result)
        {
            this.PostClose(result);
        }
    });
}
