using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>连接诊断窗口,打开时自动执行连通性检测并支持导出诊断报告。</summary>
public partial class ConnectionDiagnosticsView : Window
{
    /// <summary>初始化连接诊断窗口,并在打开时触发首轮诊断。</summary>
    public ConnectionDiagnosticsView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    /// <summary>打开即自动执行一轮诊断;"重新检测"按钮可随时重跑。</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ConnectionDiagnosticsViewModel viewModel)
        {
            viewModel.RunCommand.Execute().Subscribe();
        }
    }

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>导出诊断报告为文本文件(设计 RGXg1 exportDiag)。</summary>
    private async void ExportReport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDiagnosticsViewModel viewModel)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("Diag_ExportReport"),
            SuggestedFileName = viewModel.SuggestedReportFileName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new(Strings.Get("Main_FileTypeText")) { Patterns = ["*.txt"] }
            ]
        });
        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, viewModel.BuildReportText());
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowMessageAsync(this, Strings.Get("Main_ExportFailed"), ex.Message, MessageDialogKind.Error);
        }
    }
}
