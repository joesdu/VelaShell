// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「QueryExtension」「ListExtensions」(主操作码 128 起分配)、
//   第 10 节「Connection Close」(CloseDownMode = Destroy 时释放该连接的全部资源、选区、抓取)
//   Big Requests Extension(BigReqEnable,次操作码 0:回复 maximum-request-length)
//   XC-MISC Extension(XCMiscGetVersion 0、XCMiscGetXIDRange 1、XCMiscGetXIDList 2)

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>一个扩展:名字、分到的主操作码与事件 / 错误编号起点、请求处理。</summary>
internal sealed class Extension(string name, byte majorOpcode, Action<XClient, XRequestReader> handle)
{
    public string Name { get; } = name;

    public byte MajorOpcode { get; } = majorOpcode;

    /// <summary>这个扩展的第一个事件码;没有自己的事件时为 0。</summary>
    public byte FirstEvent { get; init; }

    /// <summary>这个扩展的第一个错误码;没有自己的错误时为 0。</summary>
    public byte FirstError { get; init; }

    public void Handle(XClient client, XRequestReader request) => handle(client, request);
}

public sealed partial class X11Server
{
    private readonly Dictionary<string, Extension> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, Extension> _extensionsByOpcode = [];

    private void InitExtensions()
    {
        Register(new Extension("BIG-REQUESTS", 128, BigRequests));
        Register(new Extension("XC-MISC", 129, XcMisc));
        Register(new Extension("SHAPE", ShapeMajor, Shape) { FirstEvent = ShapeEventBase });
        Register(new Extension("XFIXES", XFixesMajor, XFixes) { FirstEvent = XFixesEventBase, FirstError = XFixesErrorBase });
        Register(new Extension("RANDR", RandRMajor, RandR) { FirstEvent = RandREventBase, FirstError = RandRErrorBase });
        Register(new Extension("RENDER", RenderMajor, Render) { FirstError = RenderErrorBase });
    }

    private void Register(Extension extension)
    {
        _extensions[extension.Name] = extension;
        _extensionsByOpcode[extension.MajorOpcode] = extension;
    }

    private void QueryExtension(XClient c, XRequestReader r)
    {
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        if (_extensions.TryGetValue(name, out Extension? ext))
        {
            c.Reply(0, w => w.Bool(true).U8(ext.MajorOpcode).U8(ext.FirstEvent).U8(ext.FirstError).Zero(20));
        }
        else
        {
            c.Reply(0, w => w.Zero(24));
        }
    }

    private void ListExtensions(XClient c)
    {
        string[] names = [.. _extensions.Keys.Order(StringComparer.Ordinal)];
        c.Reply((byte)names.Length, w =>
        {
            w.Zero(24);
            foreach (string name in names)
            {
                byte[] bytes = XWire.Latin1.GetBytes(name);
                w.U8((byte)bytes.Length).Bytes(bytes);
            }
            w.Pad4();
        });
    }

    private static void BigRequests(XClient c, XRequestReader r)
    {
        if (r.Data != 0)
        {
            throw new XProtocolError(XErrorCode.Request);
        }
        c.BigRequestsEnabled = true;
        c.Reply(0, w => w.U32(MaxBigRequestLength).Zero(20));
    }

    private void XcMisc(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:
                c.Reply(0, w => w.U16(1).U16(1).Zero(20));
                break;
            case 1:
                // 客户端的 ID 用完了,找一段连续的空闲 ID 给它。
                (uint start, uint count) = LargestFreeRange(c);
                c.Reply(0, w => w.U32(start).U32(count).Zero(16));
                break;
            case 2:
                uint wanted = r.U32();
                List<uint> ids = [];
                for (uint i = 1; i <= XClient.ResourceMask && ids.Count < wanted; i++)
                {
                    uint id = c.ResourceBase | i;
                    if (!_resources.ContainsKey(id))
                    {
                        ids.Add(id);
                    }
                }
                c.Reply(0, w =>
                {
                    w.U32((uint)ids.Count).Zero(20);
                    foreach (uint id in ids)
                    {
                        w.U32(id);
                    }
                });
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private (uint Start, uint Count) LargestFreeRange(XClient c)
    {
        List<uint> used = [.. _resources.Keys.Where(c.OwnsId).Select(id => id & XClient.ResourceMask).Order()];
        uint bestStart = 0, bestCount = 0, cursor = 1;
        foreach (uint u in used.Append(XClient.ResourceMask + 1))
        {
            if (u > cursor && u - cursor > bestCount)
            {
                bestStart = cursor;
                bestCount = u - cursor;
            }
            cursor = Math.Max(cursor, u + 1);
        }
        return bestCount == 0 ? (0, 0) : (c.ResourceBase | bestStart, bestCount);
    }

    // ------------------------------------------------------------------ 断开

    private void CleanupClient(XClient client)
    {
        foreach ((uint atom, var owner) in _selections.ToArray())
        {
            if (ReferenceEquals(owner.Client, client))
            {
                _selections.Remove(atom);
                NotifySelectionChange(atom, 2, 0, owner.Time);
            }
        }
        if (_fetch is { } fetch && !_selections.ContainsKey(fetch.Selection))
        {
            _fetch = null;   // 正在取的选区,属主走了
        }
        if (ReferenceEquals(_pointerGrab?.Client, client))
        {
            _pointerGrab = null;
        }
        if (ReferenceEquals(_keyboardGrab?.Client, client))
        {
            _keyboardGrab = null;
        }

        // 先销毁这个客户端的顶层窗口(连同其子窗口),再清其余资源。
        List<XWindow> windows = [.. _resources.Values.OfType<XWindow>().Where(w => ReferenceEquals(w.Owner, client))];
        foreach (XWindow window in windows)
        {
            if (_resources.ContainsKey(window.Id) && window.Parent is { } parent && !ReferenceEquals(parent.Owner, client))
            {
                DestroyWindow(window);
            }
        }
        foreach (XWindow window in windows)
        {
            DestroyWindow(window);
        }
        foreach (uint id in _resources.Where(kv => ReferenceEquals(kv.Value.Owner, client)).Select(kv => kv.Key).ToArray())
        {
            _resources.Remove(id);
        }

        // 它在别人窗口上选的事件、登记的被动抓取一并摘掉。
        foreach (XWindow window in _resources.Values.OfType<XWindow>())
        {
            window.EventSelections.Remove(client);
            window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, client));
            window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, client));
            window.ShapeSelections.Remove(client);
        }
        CleanupXFixes(client, null);
        UpdatePointerWindow();
        UpdateCursor();
    }
}
