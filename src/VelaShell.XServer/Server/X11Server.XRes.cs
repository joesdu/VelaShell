// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X-Resource Extension, Version 1.2 —— QueryVersion 0、QueryClients 1、QueryClientResources 2、
//   QueryClientPixmapBytes 3、QueryClientIds 4(ClientXIDMask)、QueryResourceBytes 5
//
//   只回答问题、不改状态:数据取自资源表。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private void XRes(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U16(1).U16(2).Zero(20));
                break;
            case 1:   // QueryClients
                {
                    XClient[] clients = [.. _clients.Values];
                    c.Reply(0, w =>
                    {
                        w.U32((uint)clients.Length).Zero(20);
                        foreach (XClient client in clients)
                        {
                            w.U32(client.ResourceBase).U32(XClient.ResourceMask);
                        }
                    });
                    break;
                }
            case 2:   // QueryClientResources:按类型计数,类型用原子表示
                {
                    XClient owner = ClientOfXid(r.U32());
                    List<(uint Type, uint Count)> counts = [.. _resources.Values
                    .Where(res => ReferenceEquals(res.Owner, owner))
                    .GroupBy(ResourceTypeName)
                    .Select(g => (Intern(g.Key), (uint)g.Count()))];
                    c.Reply(0, w =>
                    {
                        w.U32((uint)counts.Count).Zero(20);
                        foreach ((uint type, uint count) in counts)
                        {
                            w.U32(type).U32(count);
                        }
                    });
                    break;
                }
            case 3:   // QueryClientPixmapBytes
                {
                    XClient owner = ClientOfXid(r.U32());
                    ulong bytes = 0;
                    foreach (XResource res in _resources.Values)
                    {
                        if (ReferenceEquals(res.Owner, owner) && res is XPixmap p)
                        {
                            bytes += (ulong)BitmapStride(p.Width * BitsPerPixel(p.Depth)) * (ulong)p.Height;   // 按 ZPixmap 的线上大小算
                        }
                    }
                    c.Reply(0, w => w.U32((uint)bytes).U32((uint)(bytes >> 32)).Zero(16));
                    break;
                }
            case 4:   // QueryClientIds:只回答 ClientXIDMask(远端客户端的 PID 我们不知道)
                {
                    uint count = r.U32();
                    List<XClient> matched = [];
                    for (uint i = 0; i < count && r.Remaining >= 8; i++)
                    {
                        uint client = r.U32();
                        uint mask = r.U32();
                        if ((mask & 1) == 0 && mask != 0)
                        {
                            continue;
                        }
                        if (client == 0)
                        {
                            matched.AddRange(_clients.Values);
                        }
                        else
                        {
                            matched.Add(ClientOfXid(client));
                        }
                    }
                    c.Reply(0, w =>
                    {
                        w.U32((uint)matched.Count).Zero(20);
                        foreach (XClient client in matched)
                        {
                            w.U32(client.ResourceBase).U32(1).U32(0);   // spec(client, mask)、length = 0
                        }
                    });
                    break;
                }
            case 5:   // QueryResourceBytes:不做逐资源的内存统计,回空表(规范允许服务端不报)
                c.Reply(0, w => w.U32(0).Zero(20));
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>任意一个 XID 所属的客户端(按 resource-base);不属于任何在线客户端时为 BadValue。</summary>
    private XClient ClientOfXid(uint xid)
    {
        int index = (int)(xid >> 21);
        return _clients.TryGetValue(index, out XClient? client) ? client : throw new XProtocolError(XErrorCode.Value, xid);
    }

    private static string ResourceTypeName(XResource resource) => resource switch
    {
        XWindow => "WINDOW",
        XPixmap => "PIXMAP",
        XGc => "GC",
        XFontResource => "FONT",
        XCursorResource => "CURSOR",
        XColormap => "COLORMAP",
        XPicture => "PICTURE",
        XGlyphSet => "GLYPHSET",
        XRegionResource => "REGION",
        _ => resource.GetType().Name.TrimStart('X').ToUpperInvariant(),
    };
}
