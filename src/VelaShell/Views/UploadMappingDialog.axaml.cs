using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Sftp;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>仅选择目录的远端、本地映射绑定窗口;关闭即丢弃未确认的草稿。</summary>
public partial class UploadMappingDialog : Window
{
    private readonly UploadMappingEditor? _editor;
    private bool _closed;
    private bool _saving;

    /// <summary>创建绑定窗口;无参构造供设计期和外框检查使用。</summary>
    public UploadMappingDialog()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog, ResizeGrips);
    }

    internal UploadMappingDialog(UploadMappingEditor editor) : this()
    {
        _editor = editor;
        DataContext = editor;
        editor.Remote.PropertyChanged += OnPaneChanged;
        editor.Local.PropertyChanged += OnPaneChanged;
        UpdateState();
    }

    internal static async Task<UploadMappingBinding?> BindAsync(Window owner, ISftpService service, Guid sessionId,
        string remotePath, string localPath)
    {
        var editor = new UploadMappingEditor(service, sessionId);
        var dialog = new UploadMappingDialog(editor);
        dialog.Opened += (_, _) => FireAndForget.Run(() => editor.InitializeAsync(remotePath, localPath));
        return await dialog.ShowDialog<UploadMappingBinding?>(owner);
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e) => UpdateState();
    private void UpdateState()
    {
        SaveButton.IsEnabled = !_closed && !_saving && _editor?.CanSave == true;
        ContentArea.IsEnabled = !_saving;
    }

    private void OnOpenPath(object? sender, RoutedEventArgs e) => NavigateInput(sender);
    private void NavigateInput(object? sender)
    {
        if (!_saving && sender is Control { DataContext: UploadDirectoryPane pane })
        {
            FireAndForget.Run(async () => { await pane.NavigateAsync(pane.InputPath); });
        }
    }
    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateInput(sender);
        }
    }
    private void OnParent(object? sender, RoutedEventArgs e)
    {
        if (!_saving && sender is Control { DataContext: UploadDirectoryPane pane })
        {
            FireAndForget.Run(pane.GoUpAsync);
        }
    }
    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (!_saving && sender is Control { DataContext: UploadDirectoryPane pane })
        {
            FireAndForget.Run(async () => { await pane.NavigateAsync(pane.CurrentPath); });
        }
    }
    private void OnDirectoryDoubleTapped(object? sender, TappedEventArgs e) => EnterSelected(sender);
    private void OnDirectoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EnterSelected(sender);
        }
    }
    private void EnterSelected(object? sender)
    {
        if (!_saving && sender is ListBox { DataContext: UploadDirectoryPane pane, SelectedItem: UploadDirectoryItem item })
        {
            FireAndForget.Run(async () => { await pane.NavigateAsync(item.FullPath); });
        }
    }
    private void OnSave(object? sender, RoutedEventArgs e) => FireAndForget.Run(SaveAsync);
    private async Task SaveAsync()
    {
        if (_saving || _editor?.CanSave != true)
        {
            return;
        }
        _saving = true;
        UpdateState();
        try
        {
            UploadMappingBinding? binding = await _editor.ValidateAsync();
            if (!_closed && binding is not null)
            {
                this.PostClose(binding);
            }
        }
        finally
        {
            _saving = false;
            if (!_closed) { UpdateState(); }
        }
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>取消尚未完成的列举,禁止关闭后的结果更新控件。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_editor is { } editor)
        {
            editor.Remote.PropertyChanged -= OnPaneChanged;
            editor.Local.PropertyChanged -= OnPaneChanged;
            editor.Dispose();
        }
        base.OnClosed(e);
    }
    /// <summary>Esc 与取消按钮等价。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; this.PostClose(); }
        else { base.OnKeyDown(e); }
    }
    /// <summary>小屏工作区内保持两侧目录和操作条可见。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            double availableWidth = screen.WorkingArea.Width / screen.Scaling;
            double availableHeight = screen.WorkingArea.Height / screen.Scaling;
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
        }
    }
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { this.BeginWindowMoveDrag(e); }
    }
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal
            && sender is Border { Tag: string tag } && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }
}
