using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.XServer;
using VelaShell.XServer;

namespace VelaShell.Views.XServer;

/// <summary>
/// X 程序往本机拖出来(F16 的另一半):服务端接住拖到所有 X 窗口以外的 XDND、把数据取了过来(<see cref="IX11ServerHost.OutgoingDragStarted" />),
/// 这里把它变成一次本机拖放的数据 —— 文字原样给;文件要看程序在哪:经 SSH 转发来的程序(连接名不为空)先经那个会话的 SFTP 取回本机的临时目录
/// (<see cref="IXServerDragDownloader" />),本机直接连进来的程序给的本来就是本机路径。与 <see cref="XDropTarget" /> 方向相反。
/// </summary>
internal static class XDragSource
{
    /// <summary>
    /// 标记:这份拖放数据是从 X 程序拖出来的。拖回 X 窗口时 <see cref="XDropTarget" /> 不接 —— 那只是把刚取回本机的文件再传回远端,
    /// 而发起拖放的那个 X 程序还抓着指针、等着松手。
    /// </summary>
    internal static DataFormat<string> Marker { get; } = DataFormat.CreateStringApplicationFormat("velashell-xdrag");

    /// <summary>这份拖放数据是不是从 X 程序拖出来的。</summary>
    internal static bool IsFromX(IDataTransfer data) => data.Contains(Marker);

    /// <summary>
    /// <c>file:</c> URI 指的路径(RFC 8089;主机名不看 —— 程序给的就是它自己那台机器上的文件):<paramref name="local" /> 时按本机的写法
    /// (Windows 上是盘符路径),否则是远端的 POSIX 路径(解码后的 URI 路径部分)。不是 <c>file:</c> 的为 null。
    /// </summary>
    internal static string? FilePath(string uri, bool local)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeFile)
        {
            return null;
        }
        return local ? parsed.LocalPath : Uri.UnescapeDataString(parsed.AbsolutePath);
    }

    /// <summary>
    /// 本机拖放的数据:有文件给文件(每个一项),否则给文字(没有文字时给 URI,一行一个)。经 SSH 转发来的程序的文件先取回本机,取的时候提示用户按住鼠标;
    /// 取不回来(会话断了、传输失败)、或者什么都给不出为 null(已经提示过用户)。<paramref name="cancellationToken" /> 取消时抛
    /// <see cref="OperationCanceledException" />。
    /// </summary>
    internal static async Task<DataTransfer?> BuildAsync(TopLevel top, XOutgoingDrag drag, IXServerDragDownloader? downloader,
        Action<string, bool> notify, CancellationToken cancellationToken)
    {
        bool local = drag.ClientLabel is null;
        string[] paths = [.. drag.Uris.Select(uri => FilePath(uri, local)).OfType<string>().Where(p => p.Length > 0)];
        List<DataTransferItem> items = [];
        if (paths.Length > 0)
        {
            IReadOnlyList<string>? files = local
                ? [.. paths.Where(p => File.Exists(p) || Directory.Exists(p))]
                : await DownloadAsync(drag.ClientLabel!, paths, downloader, notify, cancellationToken);
            if (files is not { Count: > 0 })
            {
                return null;
            }
            foreach (string file in files)
            {
                Uri uri = new(Path.GetFullPath(file));
                IStorageItem? item = Directory.Exists(file)
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(uri)
                    : await top.StorageProvider.TryGetFileFromPathAsync(uri);
                if (item is not null)
                {
                    items.Add(DataTransferItem.CreateFile(item));
                }
            }
        }
        else if ((drag.Text ?? (drag.Uris.Count > 0 ? string.Join('\n', drag.Uris) : null)) is { Length: > 0 } text)
        {
            items.Add(DataTransferItem.CreateText(text));
        }
        if (items.Count == 0)
        {
            return null;
        }
        items[0].Set(Marker, "1");   // 标记放进第一项:单独一项会被当成多拖了一样东西
        DataTransfer data = new();
        foreach (DataTransferItem item in items)
        {
            data.Add(item);
        }
        return data;
    }

    /// <summary>经 SSH 转发来的程序拖出的文件:经那个会话的 SFTP 取回本机。取不回来为 null(已经提示过用户)。</summary>
    private static async Task<IReadOnlyList<string>?> DownloadAsync(string label, string[] paths, IXServerDragDownloader? downloader,
        Action<string, bool> notify, CancellationToken cancellationToken)
    {
        if (downloader is null || !downloader.CanDownload(label))
        {
            notify(Strings.Format("XServer_DragOutNoSession", label), true);
            return null;
        }
        notify(Strings.Format("XServer_DragOutDownloading", paths.Length, label), false);
        try
        {
            if (await downloader.DownloadAsync(label, paths, cancellationToken) is not { Count: > 0 } files)
            {
                notify(Strings.Format("XServer_DragOutNoSession", label), true);
                return null;
            }
            return files;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notify(Strings.Format("XServer_DragOutFailed", ex.Message), true);
            return null;
        }
    }
}
