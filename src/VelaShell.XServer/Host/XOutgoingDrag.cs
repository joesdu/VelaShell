// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 一个 X 程序正把东西拖到所有 X 窗口以外(<see cref="IX11ServerHost.OutgoingDragStarted" />):拖的是什么、哪个程序拖的。
/// 每一次拖放一个对象(按引用区分);宿主照它发起本机的拖放,结果用 <see cref="X11Server.CompleteOutgoingDrag" /> 交回。
/// </summary>
public sealed class XOutgoingDrag
{
    internal XOutgoingDrag(IReadOnlyList<string> uris, string? text, string? clientLabel, int rootX, int rootY)
    {
        Uris = uris;
        Text = text;
        ClientLabel = clientLabel;
        RootX = rootX;
        RootY = rootY;
    }

    /// <summary>
    /// 拖的 URI(<c>text/uri-list</c>,RFC 2483:去掉了注释行与空行,原样未解码)。文件是 <c>file:</c> 的,指的是 X 程序<b>那台机器</b>上的路径 ——
    /// 程序经 SSH 转发而来时(<see cref="ClientLabel" /> 不为 null)宿主要先把文件取回本机。没有为空列表。
    /// </summary>
    public IReadOnlyList<string> Uris { get; }

    /// <summary>拖的文字(程序给的 UTF8_STRING / <c>text/plain</c> / STRING / COMPOUND_TEXT,按类型解好);没有为 null。</summary>
    public string? Text { get; }

    /// <summary>
    /// 拖出它的程序的连接名(<see cref="X11Server.ServeAuthenticatedAsync(Stream, string?, CancellationToken)" /> 给的,如 <c>user@host:22</c>):
    /// 宿主据此找到那个 SSH 会话、经 SFTP 把文件取回来。本机直接连进来的程序为 null(文件就在本机)。
    /// </summary>
    public string? ClientLabel { get; }

    /// <summary>拖出 X 窗口时指针的根坐标(X 程序发来的最后一条 XdndPosition)。</summary>
    public int RootX { get; }

    /// <inheritdoc cref="RootX" />
    public int RootY { get; }
}
