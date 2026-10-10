using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.XServer;
using VelaShell.XServer;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views.XServer;

/// <summary>
/// X 原生窗口接住本机的拖放(F16):文本、本机文件拖进 X 程序的窗口。服务端替宿主扮演 XDND 的源
/// (<see cref="X11Server.InjectDragOver" />),这里只把 Avalonia 的拖放事件转过去:拖着时报位置、按目标的回应显示能不能放;
/// 松手时交数据。文件要看程序在哪:经 SSH 转发来的程序(窗口带连接名)先把文件经那个会话的 SFTP 传到远端的临时目录,
/// 交的是<b>远端路径</b>(<see cref="IXServerDropUploader" />);本机直接连进来的程序交本机路径。
/// </summary>
internal sealed class XDropTarget
{
    /// <summary>文件拖进来时给出的类型:<c>text/uri-list</c> 在前(文件管理器、IDE 认它),再给纯文本的路径(编辑器把路径插进去)。</summary>
    private static readonly string[] FileTypes = ["text/uri-list", "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING"];

    /// <summary>文本拖进来时给出的类型:UTF-8 的 MIME 与 UTF8_STRING,以及 XDND 默认按 Latin-1 解释的 text/plain 与 STRING、TEXT。</summary>
    private static readonly string[] TextTypes = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING", "TEXT"];

    private readonly Control _surface;
    private readonly XTopLevelWindow _handle;
    private readonly Func<X11Server?> _server;
    private readonly Func<Point, (int X, int Y)> _toPixels;
    private readonly IXServerDropUploader? _uploader;
    private readonly Action<string, bool> _notify;

    private XDropTarget(Control surface, XTopLevelWindow handle, Func<X11Server?> server, Func<Point, (int X, int Y)> toPixels,
        IXServerDropUploader? uploader, Action<string, bool> notify)
    {
        _surface = surface;
        _handle = handle;
        _server = server;
        _toPixels = toPixels;
        _uploader = uploader;
        _notify = notify;
    }

    /// <summary>让 <paramref name="window" /> 接拖放。</summary>
    /// <param name="window">X 顶层对应的原生窗口。</param>
    /// <param name="surface">画 X 像素的控件(坐标按它算)。</param>
    /// <param name="handle">X 顶层。</param>
    /// <param name="server">此刻的服务端(停了为 null)。</param>
    /// <param name="toPixels">控件坐标 → X 内区像素坐标。</param>
    /// <param name="uploader">把本机文件传到远端;没有时经 SSH 来的程序不接文件。</param>
    /// <param name="notify">给用户的提示(文本,是否错误)。</param>
    public static void Attach(Window window, Control surface, XTopLevelWindow handle, Func<X11Server?> server, Func<Point, (int X, int Y)> toPixels,
        IXServerDropUploader? uploader, Action<string, bool> notify)
    {
        XDropTarget target = new(surface, handle, server, toPixels, uploader, notify);
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragEnterEvent, target.OnDragOver);
        window.AddHandler(DragDrop.DragOverEvent, target.OnDragOver);
        window.AddHandler(DragDrop.DragLeaveEvent, target.OnDragLeave);
        window.AddHandler(DragDrop.DropEvent, target.OnDrop);
    }

    /// <summary>拖进来的是什么:本机文件(路径)或文本;都不是为 null。</summary>
    private sealed record Payload(IReadOnlyList<string> Files, string? Text)
    {
        public string[] Types => Files.Count > 0 ? FileTypes : TextTypes;
    }

    private static Payload? Read(DragEventArgs e)
    {
        if (XDragSource.IsFromX(e.DataTransfer))
        {
            return null;   // 从 X 程序拖出来的(还在拖、发起它的 X 程序还抓着指针):拖回 X 窗口不接,那只是把刚取回的文件再传回远端
        }
        string[] files = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(i => i.TryGetLocalPath()).OfType<string>().Where(p => p.Length > 0)];
        if (files.Length > 0)
        {
            return new Payload(files, null);
        }
        return e.DataTransfer.TryGetText() is { Length: > 0 } text ? new Payload([], text) : null;
    }

    /// <summary>经 SSH 转发来的程序要文件,得先传上去:会话不在了就接不了。</summary>
    private bool CanAccept(Payload payload) =>
        payload.Files.Count == 0 || _handle.Snapshot.ClientLabel is not { } label || _uploader?.CanUpload(label) == true;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_server() is not { } server || Read(e) is not { } payload || !CanAccept(payload))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        (int x, int y) = _toPixels(e.GetPosition(_surface));
        server.InjectDragOver(_handle, x, y, payload.Types);
        // 目标的回应(XdndStatus)是异步的:这里用最近一次的。拖着时系统一直在问,松手前早已跟上。
        e.DragEffects = server.IsDragAccepted ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => _server()?.InjectDragLeave();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_server() is not { } server || Read(e) is not { } payload || !CanAccept(payload))
        {
            _server()?.InjectDragLeave();
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        (int x, int y) = _toPixels(e.GetPosition(_surface));
        string? label = _handle.Snapshot.ClientLabel;
        FireAndForget.Run(async () =>
        {
            Dictionary<string, ReadOnlyMemory<byte>>? data = payload.Files.Count == 0
                ? TextData(payload.Text!)
                : await FileDataAsync(payload.Files, label);
            if (data is null)
            {
                server.InjectDragLeave();
                return;
            }
            server.InjectDrop(_handle, x, y, data);
        });
    }

    /// <summary>文件的数据:本机程序交本机路径;经 SSH 来的程序先传到远端、交远端路径。传不上去为 null(已经提示过用户)。</summary>
    private async Task<Dictionary<string, ReadOnlyMemory<byte>>?> FileDataAsync(IReadOnlyList<string> files, string? label)
    {
        if (label is null)
        {
            return FileData([.. files.Select(p => new Uri(Path.GetFullPath(p)).AbsoluteUri)], files);
        }
        if (_uploader is null)
        {
            return null;
        }
        _notify(Strings.Format("XServer_DropUploading", files.Count, label), false);
        try
        {
            if (await _uploader.UploadAsync(label, files) is not { Count: > 0 } remote)
            {
                _notify(Strings.Format("XServer_DropNoSession", label), true);
                return null;
            }
            return FileData([.. remote.Select(RemoteFileUri)], remote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notify(Strings.Format("XServer_DropFailed", ex.Message), true);
            return null;
        }
    }

    /// <summary>
    /// <c>text/uri-list</c>(RFC 2483:一行一个 URI,行尾 CRLF)与纯文本的路径(一行一个)。远端路径写成 <c>file:///路径</c> ——
    /// 主机名留空就是程序自己那台机器。
    /// </summary>
    private static Dictionary<string, ReadOnlyMemory<byte>> FileData(IReadOnlyList<string> uris, IReadOnlyList<string> paths)
    {
        byte[] list = Encoding.UTF8.GetBytes(string.Concat(uris.Select(u => u + "\r\n")));
        Dictionary<string, ReadOnlyMemory<byte>> data = TextData(string.Join('\n', paths));
        data.Remove("TEXT");
        data["text/uri-list"] = list;
        return data;
    }

    /// <summary>远端的 POSIX 路径 → <c>file:///…</c>,每一段按 URI 的规矩转义。</summary>
    internal static string RemoteFileUri(string path) =>
        "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>文本按各个类型编码:UTF-8 的给 UTF-8,text/plain 与 STRING 按 Latin-1(XDND 与 ICCCM 的默认字符集,装不下的变成「?」)。</summary>
    internal static Dictionary<string, ReadOnlyMemory<byte>> TextData(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] latin1 = Encoding.Latin1.GetBytes(text);
        return new()
        {
            ["text/plain;charset=utf-8"] = utf8,
            ["UTF8_STRING"] = utf8,
            ["text/plain"] = latin1,
            ["STRING"] = latin1,
            ["TEXT"] = utf8,
        };
    }
}
