using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.Views;

public partial class RemoteFileEditorView
{
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly Func<string, CancellationToken, Task>? _downloadAsync;
    private readonly CancellationToken _sourceLifetime;
    private CancellationTokenRegistration _sourceLifetimeRegistration;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;
    private Task? _loadTask;
    private long _refreshGeneration;
    private int _refreshIntervalMs = 1000;
    private bool _refreshImmediately;
    private bool _applyingRefresh;
    private bool _closed;
    private bool _closingRequested;
    private bool _waitingToClose;
    private bool _closeConfirmationOpen;

    private bool CanRefresh => _loaded && !_closed && !_closingRequested && !_saving
                               && !_sourceLifetime.IsCancellationRequested
                               && _downloadAsync is not null && AutoRefreshCheckBox.IsChecked == true;

    /// <summary>受控推进一轮刷新,不必在回归测试中依赖实际计时器到点。</summary>
    internal Task RefreshForTestAsync()
    {
        _refreshTimer.Stop();
        BeginRefresh();
        return _refreshTask ?? Task.CompletedTask;
    }

    private void InitializeAutoRefresh()
    {
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshIntervalMs);
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            BeginRefresh();
        };
        AutoRefreshCheckBox.IsCheckedChanged += AutoRefresh_Changed;
        RefreshIntervalInput.ValueChanged += RefreshInterval_Changed;
    }

    private void AutoRefresh_Changed(object? sender, RoutedEventArgs e)
    {
        CancelRefresh();
        if (CanRefresh)
        {
            _refreshImmediately = true;
            BeginRefresh();
        }
    }

    private void RefreshInterval_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        int milliseconds = (int)Math.Clamp(decimal.Truncate(e.NewValue ?? _refreshIntervalMs), 100, int.MaxValue);
        if (RefreshIntervalInput.Value != milliseconds)
        {
            RefreshIntervalInput.Value = milliseconds;
        }
        _refreshIntervalMs = milliseconds;
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        ScheduleRefresh();
    }

    private void CancelRefresh()
    {
        _refreshTimer.Stop();
        _refreshImmediately = false;
        _refreshGeneration++;
        _refreshCancellation?.Cancel(); // 由拥有这轮读取的 finally 释放。
    }

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        if (CanRefresh && _refreshCancellation is null)
        {
            _refreshTimer.Start();
        }
    }

    private void BeginRefresh()
    {
        if (!CanRefresh || _refreshCancellation is not null)
        {
            return;
        }
        _refreshImmediately = false;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sourceLifetime);
        _refreshCancellation = cancellation;
        _refreshTask = RefreshFromRemoteAsync(cancellation, _refreshGeneration);
    }

    private async Task RefreshFromRemoteAsync(CancellationTokenSource cancellation, long generation)
    {
        string stagingPath = Path.Combine(Path.GetDirectoryName(_localPath)!, $".refresh-{Guid.NewGuid():N}");
        CancellationToken token = cancellation.Token;
        try
        {
            await _downloadAsync!(stagingPath, token);
            token.ThrowIfCancellationRequested();
            // 解码与磁盘读取在后台完成;任何 Span 都只在同步方法中存活。
            (string text, EditorEncodingDetector.Result detected, long length) = await Task.Run(() =>
            {
                long size = new FileInfo(stagingPath).Length;
                if (size > LargeFileThresholdBytes)
                {
                    throw new IOException(Strings.Format("Editor_TooLarge", (size / (1024.0 * 1024)).ToString("F1"),
                                                         (LargeFileThresholdBytes / (1024.0 * 1024)).ToString("F0")));
                }
                byte[] bytes = File.ReadAllBytes(stagingPath);
                EditorEncodingDetector.Result encoding = EditorEncodingDetector.Detect(bytes, _sessionEncoding);
                return (EditorEncodingDetector.Decode(bytes, encoding), encoding, size);
            }, token);
            token.ThrowIfCancellationRequested();
            if (!CanRefresh || generation != _refreshGeneration)
            {
                return;
            }

            // 在 UI 线程的一次提交中改副本和文档;保存请求不会插到两者之间。
            File.Move(stagingPath, _localPath, overwrite: true);
            _encoding = detected.Encoding;
            ApplyRefreshedText(text, generation);
            StatusText.Text = detected.FellBackToSessionEncoding
                ? Strings.Format("Editor_LoadedStatusFallback", length.ToString("N0"), _encoding.WebName)
                : Strings.Format("Editor_RefreshedStatus", DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消从不把现有内容当作失败或覆盖它。
        }
        catch (Exception ex)
        {
            if (!_closed && generation == _refreshGeneration && !token.IsCancellationRequested)
            {
                AutoRefreshCheckBox.IsChecked = false;
                StatusText.Text = Strings.Format("Editor_RefreshFailed", ex.Message);
            }
        }
        finally
        {
            try { File.Delete(stagingPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _refreshCancellation = null;
            cancellation.Dispose();
            if (_refreshImmediately && CanRefresh)
            {
                // 让当前任务先返回,避免同步下载回调导致递归重入。
                Dispatcher.UIThread.Post(BeginRefresh);
            }
            else
            {
                ScheduleRefresh();
            }
        }
    }

    private void ApplyRefreshedText(string text, long generation)
    {
        bool keepPosition = KeepViewPositionCheckBox.IsChecked == true;
        ScrollViewer? scroll = Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        Vector offset = scroll?.Offset ?? default;
        int caret = Editor.CaretOffset;
        int start = Editor.SelectionStart;
        int length = Editor.SelectionLength;
        string previous = Editor.Text;
        _applyingRefresh = true;
        try
        {
            if (!string.Equals(previous, text, StringComparison.Ordinal))
            {
                int prefix = 0;
                int limit = Math.Min(previous.Length, text.Length);
                while (prefix < limit && previous[prefix] == text[prefix]) { prefix++; }
                int suffix = 0;
                while (suffix < limit - prefix && previous[^(suffix + 1)] == text[^(suffix + 1)]) { suffix++; }
                Editor.Document.Replace(prefix, previous.Length - prefix - suffix,
                                        text.Substring(prefix, text.Length - prefix - suffix));
                if (!string.Equals(FirstLineOf(previous), FirstLineOf(text), StringComparison.Ordinal))
                {
                    ApplySyntaxHighlighting();
                }
            }
            Editor.Document.UndoStack.ClearAll();
            _savedRevision = _revision;
            RefreshSaveIndicator();
            if (keepPosition)
            {
                int selectionStart = Math.Min(start, text.Length);
                Editor.Select(selectionStart, Math.Min(length, text.Length - selectionStart));
                Editor.CaretOffset = Math.Min(caret, text.Length);
            }
            else
            {
                Editor.Select(text.Length, 0);
                Editor.CaretOffset = text.Length;
            }
        }
        finally
        {
            _applyingRefresh = false;
        }

        void RestorePosition()
        {
            if (keepPosition)
            {
                if (scroll is not null)
                {
                    scroll.Offset = new Vector(Math.Clamp(offset.X, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)),
                                               Math.Clamp(offset.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
                }
            }
            else
            {
                Editor.TextArea.Caret.BringCaretToView();
            }
        }
        Editor.UpdateLayout();
        RestorePosition();
        long revision = _revision;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && !_closingRequested && generation == _refreshGeneration && revision == _revision
                && keepPosition == (KeepViewPositionCheckBox.IsChecked == true))
            {
                Editor.UpdateLayout();
                RestorePosition();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnRefreshSourceEnded()
    {
        if (_closed) { return; }
        AutoRefreshCheckBox.IsChecked = false;
        AutoRefreshCheckBox.IsEnabled = false;
        CancelRefresh();
        StatusText.Text = Strings.Get("Editor_RefreshSourceEnded");
    }
}
