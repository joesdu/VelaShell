// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X-Resource Extension, Version 1.2 —— QueryVersion 0、QueryClients 1、QueryClientResources 2、
//   QueryClientPixmapBytes 3、QueryClientIds 4(§4.2.1 ClientXIDMask 与 LocalClientPidMask、§5.2)、QueryResourceBytes 5
//
//   只回答问题、不改状态:数据取自资源表。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    // CLIENTIDMASK(§4.2.1)。
    private const uint ClientXidMask = 0x1, LocalClientPidMask = 0x2;

    private void XRes(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U16(1).U16(2).Zero(20));
                break;
            case 1:   // QueryClients
                {
                    XClient[] clients = [.. ClientsWithResources()];
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
            case 4:   // QueryClientIds:ClientXIDMask 与 LocalClientPidMask(只有经 Unix 套接字连进来、取得到 pid 的客户端有)
                {
                    uint count = r.U32();
                    // 规范:LocalClientPid 只回给本身也是本机客户端的请求方(远端问本机进程的 pid 没有意义)—— 这里指经 Unix 套接字连进来的。
                    bool askerLocal = c.PeerUid is not null || c.PeerPid > 0;
                    // 每个客户端的每种标识只回一次:client = 0 表示「全部客户端」,两百万条这样的 spec 各展开一遍就是几 GB 的回复。
                    List<(XClient Client, uint Mask)> ids = [];
                    HashSet<(XClient, uint)> seen = [];
                    for (uint i = 0; i < count && r.Remaining >= 8; i++)
                    {
                        uint client = r.U32();
                        uint mask = r.U32();
                        bool xid = mask == 0 || (mask & ClientXidMask) != 0;   // mask = None:所有支持的标识方法
                        bool pid = (mask == 0 || (mask & LocalClientPidMask) != 0) && askerLocal;
                        if (!xid && !pid)
                        {
                            continue;
                        }
                        foreach (XClient one in client == 0 ? [.. ClientsWithResources()] : (XClient[])[ClientOfXid(client)])
                        {
                            if (xid && seen.Add((one, ClientXidMask)))
                            {
                                ids.Add((one, ClientXidMask));
                            }
                            if (pid && one.PeerPid > 0 && seen.Add((one, LocalClientPidMask)))
                            {
                                ids.Add((one, LocalClientPidMask));
                            }
                        }
                    }
                    c.Reply(0, w =>
                    {
                        w.U32((uint)ids.Count).Zero(20);
                        foreach ((XClient client, uint mask) in ids)
                        {
                            w.U32(client.ResourceBase).U32(mask);   // spec(client, 单一一种标识)
                            if (mask == LocalClientPidMask)
                            {
                                w.U32(1).U32((uint)client.PeerPid);   // length = 1 个 CARD32:pid
                            }
                            else
                            {
                                w.U32(0);   // ClientXid:spec 本身就是标识,length = 0
                            }
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

    /// <summary>
    /// 任意一个 XID 所属的客户端(按 resource-base),连同以 Retain 模式断开、资源还留着的;都不是时为 BadValue。
    /// 保留的客户端原先在这里看不见:占着编号、留着像素图,xrestop 之类却列不出来,也就无从知道该 KillClient 谁。
    /// </summary>
    private XClient ClientOfXid(uint xid)
    {
        int index = (int)(xid >> 21);
        return _clients.GetValueOrDefault(index) ?? _retainedClients.GetValueOrDefault(index) ?? throw new XProtocolError(XErrorCode.Value, xid);
    }

    /// <summary>X-Resource 列出的客户端:连着的,连同以 Retain 模式断开、资源还留着的,按编号排。</summary>
    private IEnumerable<XClient> ClientsWithResources() => _clients.Values.Concat(_retainedClients.Values).OrderBy(c => c.Index);

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
        XPointerBarrier => "BARRIER",
        _ => resource.GetType().Name.TrimStart('X').ToUpperInvariant(),
    };
}
