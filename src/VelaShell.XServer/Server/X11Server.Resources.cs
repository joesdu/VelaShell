// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 2 节「Syntactic Conventions」里的资源 ID
//   (resource-id-base / resource-id-mask,ID 由客户端在自己的范围里选,重复或越界是 BadIDChoice)
//   XC-MISC Extension —— XCMiscGetVersion 0、XCMiscGetXIDRange 1、XCMiscGetXIDList 2(客户端用完 ID 时向服务端要空闲的)

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    internal T? Lookup<T>(uint id) where T : XResource =>
        _resources.TryGetValue(id, out XResource? r) ? r as T : null;

    internal void AddResource(XClient client, XResource resource)
    {
        if (!client.OwnsId(resource.Id) || _resources.ContainsKey(resource.Id))
        {
            throw new XProtocolError(XErrorCode.IDChoice, resource.Id);
        }
        _resources[resource.Id] = resource;
    }

    internal void RemoveResource(uint id) => _resources.Remove(id);

    /// <summary>资源表里的全部资源(只读遍历;只在执行线程上用)。</summary>
    internal IEnumerable<XResource> AllResources => _resources.Values;

    private XWindow CreateRootWindow() => new(RootWindowId, null, null)
    {
        Width = _options.ScreenWidth,
        Height = _options.ScreenHeight,
        Depth = 24,
        Visual = RootVisualId,
        Colormap = DefaultColormapId,
        BackgroundPixel = 0,
        Mapped = true,
    };

    // ------------------------------------------------------------------ XC-MISC

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
}
