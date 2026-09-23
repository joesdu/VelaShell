// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 10 节「Events」:结构事件同时发给窗口本身
//   (StructureNotify,event = 窗口)与父窗口(SubstructureNotify,event = 父窗口);
//   设备事件沿窗口树向上传播,直到有客户端选了它或碰上 do-not-propagate-mask

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    /// <summary>发给在 <paramref name="window" /> 上选了 <paramref name="mask" /> 的每个客户端。</summary>
    private static void DeliverToSelectors(XWindow window, XEventMask mask, Action<XClient> send)
    {
        foreach ((XClient client, uint selected) in window.EventSelections)
        {
            if ((selected & (uint)mask) != 0 && !client.Closed)
            {
                send(client);
            }
        }
    }

    /// <summary>
    /// 结构事件:发给窗口上选了 StructureNotify 的客户端(event = 窗口),
    /// 和父窗口上选了 SubstructureNotify 的客户端(event = 父窗口)。
    /// </summary>
    /// <param name="window">发生变化的窗口。</param>
    /// <param name="code">事件码。</param>
    /// <param name="detail">头里的 detail 字节。</param>
    /// <param name="body">写 event 窗口之后的字段;参数是 event 窗口 ID。</param>
    /// <param name="parent">父窗口(ReparentNotify 这类要指定旧 / 新父窗口时传);默认取当前父窗口。</param>
    private static void DeliverStructure(XWindow window, byte code, byte detail, Action<XWriter> body, XWindow? parent = null)
    {
        DeliverToSelectors(window, XEventMask.StructureNotify, c =>
            c.Event(code, detail, w =>
            {
                w.U32(window.Id);
                body(w);
            }));
        if ((parent ?? window.Parent) is { } p)
        {
            DeliverToSelectors(p, XEventMask.SubstructureNotify, c =>
                c.Event(code, detail, w =>
                {
                    w.U32(p.Id);
                    body(w);
                }));
        }
    }

    /// <summary>选了 SubstructureRedirect 的那个客户端(每个窗口最多一个);没有返回 null。</summary>
    private static XClient? RedirectClient(XWindow window, XEventMask mask)
    {
        foreach ((XClient client, uint selected) in window.EventSelections)
        {
            if ((selected & (uint)mask) != 0 && !client.Closed)
            {
                return client;
            }
        }
        return null;
    }
}
