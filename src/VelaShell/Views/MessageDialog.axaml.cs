using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VelaShell.Core.Resources;

namespace VelaShell.Views;

/// <summary>标题栏图标的语义分组:着色跟随主题令牌(info/成功/警告/错误/询问)。</summary>
public enum MessageDialogKind
{
    Info,
    Success,
    Warning,
    Error,
    Question
}

/// <summary>
/// 主题化的通用消息弹窗,替代原生 <see cref="Window" /> 消息框(原生框带系统标题栏,
/// 与设计的无边框圆角弹窗风格不符)。支持纯消息、确认、单行文本输入与自定义内容四种形态,
/// 通过静态方法调用。
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>纯消息:仅一个"确定"按钮。</summary>
    public static Task ShowMessageAsync(Window owner,
        string title,
        string message,
        MessageDialogKind kind = MessageDialogKind.Info)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, kind, Strings.OK, null, false);
        return dialog.ShowDialog(owner);
    }

    /// <summary>确认框:返回 true 表示用户点击了确认。danger 时确认按钮渲染为红色。</summary>
    public static Task<bool> ConfirmAsync(Window owner,
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        MessageDialogKind kind = MessageDialogKind.Question,
        bool danger = false)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, kind, confirmText ?? Strings.OK, cancelText ?? Strings.Cancel, danger);
        return dialog.ShowDialog<bool>(owner);
    }

    /// <summary>单行文本输入(新建/重命名/移动等):返回输入内容,取消返回 null。</summary>
    public static async Task<string?> PromptAsync(Window owner,
        string title,
        string initialValue,
        string? message = null)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, MessageDialogKind.Question, Strings.OK, Strings.Cancel, false);
        dialog.InputBox.IsVisible = true;
        dialog.InputBox.Text = initialValue;
        dialog.Opened += (_, _) =>
        {
            dialog.InputBox.SelectAll();
            dialog.InputBox.Focus();
        };
        bool confirmed = await dialog.ShowDialog<bool>(owner);
        return confirmed ? dialog.InputBox.Text : null;
    }

    /// <summary>自定义内容(属性表、权限矩阵等):返回 true 表示用户点击了确认。</summary>
    public static Task<bool> ShowCustomAsync(Window owner,
        string title,
        Control content,
        string? confirmText = null,
        string? cancelText = null,
        bool showCancel = true,
        MessageDialogKind kind = MessageDialogKind.Info)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, null, kind, confirmText ?? Strings.OK,
            showCancel ? cancelText ?? Strings.Cancel : null, false);
        dialog.BodyHost.IsVisible = true;
        dialog.BodyHost.Content = content;
        return dialog.ShowDialog<bool>(owner);
    }

    private void Configure(string title,
        string? message,
        MessageDialogKind kind,
        string confirmText,
        string? cancelText,
        bool danger)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrEmpty(message);

        // 画刷经样式类 + DynamicResource 解析(代码里 FindResource 拿不到主题字典画刷)。
        (string iconKey, string kindClass) = kind switch
        {
            MessageDialogKind.Success => ("Icon.circle-check", "success"),
            MessageDialogKind.Warning => ("Icon.triangle-alert", "warning"),
            MessageDialogKind.Error => ("Icon.circle-alert", "error"),
            MessageDialogKind.Question => ("Icon.circle-help", "question"),
            _ => ("Icon.info", "info")
        };
        KindIcon.Data = this.FindResource(iconKey) as Geometry;
        KindIcon.Classes.Add(kindClass);
        ConfirmButton.Content = confirmText;
        if (danger)
        {
            ConfirmButton.Classes.Remove("dlg-primary");
            ConfirmButton.Classes.Add("dlg-danger");
        }
        if (cancelText is null)
        {
            CancelButton.IsVisible = false;
        }
        else
        {
            CancelButton.Content = cancelText;
        }
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 无取消按钮的纯消息框也要能用 Esc 关闭(IsCancel 只在按钮可见时生效)。
        if (e.Key == Key.Escape)
        {
            Close(false);
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
