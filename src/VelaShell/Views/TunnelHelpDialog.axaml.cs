using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VelaShell.Views;

/// <summary>SSH 隧道帮助对话框,静态本地化内容,无 ViewModel。</summary>
public partial class TunnelHelpDialog : Window
{
    /// <summary>初始化可视化组件。</summary>
    public TunnelHelpDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Esc 关闭对话框。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
