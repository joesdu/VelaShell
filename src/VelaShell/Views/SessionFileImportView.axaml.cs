using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 导入连接文件对话框(#571)。<c>ShowDialog</c> 的结果是 <c>SessionFileImportOutcome</c>,取消为 null。
/// </summary>
public partial class SessionFileImportView : Window
{
    /// <summary>初始化对话框,打开后与本机数据比对生成预览。</summary>
    public SessionFileImportView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>
    /// 正在写库时不许关:Esc、取消、右上角的 × 都一样。关掉了写入照样会做完,宿主却以为什么都没发生 ——
    /// 写完之后它才刷新资源管理器(见 <c>SessionTreeActions</c>)。
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is SessionFileImportViewModel { IsBusy: true, HasWritten: true })
        {
            e.Cancel = true;
        }
    }

    private void OnOpened(object? sender, EventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SessionFileImportViewModel viewModel)
        {
            return;
        }
        // 导入成功后关闭并把结果交回宿主(刷新资源管理器、弹浮层提示)。失败时结果为 null,对话框留着显示原因。
        viewModel.ImportCommand.Subscribe(outcome =>
        {
            if (outcome is not null)
            {
                this.PostClose(outcome);
            }
        });
        await viewModel.InitializeAsync();
    });

    /// <summary>Esc 等价于取消;口令框里按回车是解锁。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && PassphraseBox.IsFocused && DataContext is SessionFileImportViewModel viewModel)
        {
            // CanExecute 为 false 时执行 ReactiveCommand 会把异常抛进订阅链,先问一句。
            if (((System.Windows.Input.ICommand)viewModel.UnlockCommand).CanExecute(null))
            {
                viewModel.UnlockCommand.Execute().Subscribe();
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

    private void Cancel_Click(object? sender, RoutedEventArgs e) => this.PostClose(null);
}
