using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PulseTerm.App.Views;

/// <summary>
/// SFTP「打开」的内置快速文本编辑器(AvaloniaEdit)。文件已由 FileBrowserViewModel 下载到
/// 本地临时副本;保存 = 按原编码写回临时文件 + 通过回调上传到服务器。窗口关闭时删除临时副本。
/// </summary>
public partial class RemoteFileEditorView : Window
{
    private readonly string _localPath = string.Empty;
    private readonly Func<Task>? _uploadAsync;
    private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private bool _dirty;
    private bool _saving;
    private bool _forceClose;

    public RemoteFileEditorView()
    {
        InitializeComponent();
    }

    public RemoteFileEditorView(string fileName, string remotePath, string localPath, Func<Task> uploadAsync)
        : this()
    {
        _localPath = localPath;
        _uploadAsync = uploadAsync;

        Title = fileName;
        TitleText.Text = fileName;
        PathText.Text = remotePath;

        LoadFile();
        Editor.TextChanged += (_, _) =>
        {
            _dirty = true;
            StatusText.Text = "● 未保存的更改";
        };
    }

    private void LoadFile()
    {
        // 保留原文件的 BOM/编码:UTF-8(无 BOM)为缺省,识别 UTF-8 BOM 与 UTF-16 LE/BE。
        var bytes = File.ReadAllBytes(_localPath);
        _encoding = DetectEncoding(bytes);
        Editor.Text = _encoding.GetString(bytes, PreambleLength(bytes, _encoding), bytes.Length - PreambleLength(bytes, _encoding));
        _dirty = false;
        StatusText.Text = $"UTF 文本 · {bytes.Length:N0} B · 已就绪";
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static int PreambleLength(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0 || bytes.Length < preamble.Length)
            return 0;

        for (var i = 0; i < preamble.Length; i++)
        {
            if (bytes[i] != preamble[i])
                return 0;
        }

        return preamble.Length;
    }

    private async Task SaveAsync()
    {
        if (_saving || _uploadAsync is null)
            return;

        _saving = true;
        StatusText.Text = "保存中…";
        try
        {
            await File.WriteAllTextAsync(_localPath, Editor.Text, _encoding);
            await _uploadAsync();
            _dirty = false;
            StatusText.Text = $"已保存并同步到服务器 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败:{ex.Message}";
        }
        finally
        {
            _saving = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = SaveAsync();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_dirty && !_forceClose)
        {
            e.Cancel = true;
            _ = ConfirmDiscardAndCloseAsync();
        }

        base.OnClosing(e);
    }

    private async Task ConfirmDiscardAndCloseAsync()
    {
        var discard = await MessageDialog.ConfirmAsync(this, "未保存的更改",
            "文件有未保存的修改,关闭将丢弃这些更改。", confirmText: "放弃并关闭",
            kind: MessageDialogKind.Warning, danger: true);
        if (discard)
        {
            _forceClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 清理本地临时副本(整个独占子目录)。
        try
        {
            var dir = Path.GetDirectoryName(_localPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 尽力而为;残留交给应用退出清理。
        }

        base.OnClosed(e);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => _ = SaveAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Header_DoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }
}
