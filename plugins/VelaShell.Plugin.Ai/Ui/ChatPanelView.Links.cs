using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 回复正文里的链接点开之后怎么办。
/// </summary>
/// <remarks>
/// 模型经常在回答里给出服务器上的文件路径,写成 <c>[名字](/root/xxx.md)</c>。
/// 那既不是网址也不是本机文件,默认什么都不会发生 —— 用户点了没反应,只能自己去终端里找。
/// 网址交给浏览器,本机文件在文件管理器中打开所在目录,
/// <b>远端绝对路径经 SFTP 下载下来</b>(存到哪儿由用户挑)。
/// </remarks>
public partial class ChatPanelView
{
    private void OnMarkdownLinkClicked(LinkClickedEventArgs e)
    {
        if (e.HRef is not { } uri || uri.OriginalString.Length == 0)
        {
            return;
        }
        e.Handled = true;
        string href = uri.OriginalString;
        if (uri.IsAbsoluteUri && uri.Scheme is "http" or "https" or "mailto")
        {
            _ = LaunchAsync(uri);
            return;
        }
        if (GetLocalLinkPath(uri) is { } path)
        {
            _ = OpenLocalLinkFolderAsync(path);
            return;
        }
        if (href.StartsWith('/'))
        {
            _ = DownloadRemoteAsync(href);
            return;
        }
        // 相对路径/锚点之类:没有能对上的东西,别装作能打开
        StatusText.Text = _loc.F("LinkUnsupported", href);
    }

    private async Task LaunchAsync(Uri uri)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Opening '{uri}' failed: {ex.Message}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    internal static string? GetLocalLinkPath(Uri uri)
    {
        string href = uri.OriginalString;
        if (OperatingSystem.IsWindows() && href.StartsWith('/') && Path.IsPathFullyQualified(href[1..]) && char.IsAsciiLetter(href[1]))
        {
            return Uri.UnescapeDataString(href[1..]);
        }
        // Bare /paths refer to the remote session, even on hosts where Uri treats them as files.
        return !href.StartsWith('/') && uri.IsAbsoluteUri && uri.IsFile ? uri.LocalPath : null;
    }

    internal static DirectoryInfo? GetLocalLinkFolder(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path) : File.Exists(path) ? new FileInfo(path).Directory : null;

    private async Task OpenLocalLinkFolderAsync(string path)
    {
        try
        {
            if (GetLocalLinkFolder(path) is not { } folder)
            {
                StatusText.Text = _loc.F("LinkMissingLocal", path);
                return;
            }
            if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher || !await launcher.LaunchDirectoryInfoAsync(folder))
            {
                StatusText.Text = _loc.F("LinkFolderOpenFailed", folder.FullName);
            }
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Opening '{path}' failed: {ex.Message}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    /// <summary>
    /// 把远端文件拉下来。存到哪儿让用户挑 —— 悄悄落到某个目录里,人一样找不着。
    /// </summary>
    private async Task DownloadRemoteAsync(string remotePath)
    {
        // 做不了就别弹保存框:让用户挑完位置才说宿主不支持,等于白挑一次。
        if (!_context.RemoteFs.SupportsTransferOperations || !_context.RemoteFs.SupportsDownloadOperations)
        {
            StatusText.Text = $"{_loc["Error"]}: {_loc["TransferDownloadUnsupported"]}";
            return;
        }
        if (SelectedSessionId is not { } sessionId)
        {
            StatusText.Text = _loc["NoSession"];
            return;
        }
        try
        {
            RemoteFileEntry? entry = await _context.RemoteFs.StatAsync(sessionId, remotePath);
            if (entry is null)
            {
                StatusText.Text = _loc.F("LinkMissingRemote", remotePath);
                return;
            }
            if (entry.IsDirectory)
            {
                StatusText.Text = _loc.F("LinkIsDirectory", remotePath);
                return;
            }
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }
            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _loc["LinkDownload"],
                SuggestedFileName = Path.GetFileName(remotePath.TrimEnd('/')),
                // 选到已有文件时由系统框先问一句"要替换吗";点了替换,下面才按覆盖去请宿主确认
                ShowOverwritePrompt = true
            });
            if (target?.TryGetLocalPath() is not { } localPath)
            {
                return;
            }
            await StartLinkDownloadAsync(sessionId, remotePath, localPath);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Downloading '{remotePath}' failed: {ex.Message}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    /// <summary>保存位置已经选好:起后台下载并挂一张进度卡。</summary>
    /// <remarks>
    /// 选的是已有文件,说明用户在系统保存框里已经确认过替换 —— 这时仍按"不覆盖"去请求,
    /// 宿主落盘只会报一句"目标已存在",等于让人白确认一次。覆盖意图如实带给宿主,
    /// 宿主自己的逐任务确认框也就照实写着"替换"。
    /// </remarks>
    private async Task StartLinkDownloadAsync(string sessionId, string remotePath, string localPath)
    {
        FileTransferRunScope scope = BeginLinkTransferScope(sessionId);
        string operationId = FileTransferJobRegistry.StableOperationId(scope.OwnerId, scope.RunId,
            "link:" + remotePath + "->" + localPath);
        RemoteTransferSnapshot snapshot = await scope.StartDownloadAsync(sessionId, remotePath, localPath,
            operationId, overwrite: File.Exists(localPath), CancellationToken.None);
        StatusText.Text = TransferPresentation.Text(_loc, snapshot);
        _ = new LinkTransferBinding(this, scope, snapshot.TransferId, localPath);
    }
}
