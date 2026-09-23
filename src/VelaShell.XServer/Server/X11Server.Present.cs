// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The Present Extension, Version 1.2 —— §「Types」(PresentEventMask:ConfigureNotify 1 / CompleteNotify 2 /
//   IdleNotify 4;CompleteKind Pixmap / NotifyMSC;CompleteMode Copy / Flip / Skip)、§「Requests」(QueryVersion 0、
//   Pixmap 1、NotifyMSC 2、SelectInput 3、QueryCapabilities 4)、§「Events」(经 Generic Event Extension 发出的
//   ConfigureNotify 0、CompleteNotify 1、IdleNotify 2)
//
//   纯软件实现:Pixmap 请求立即把像素图拷到窗口(Copy 模式)、立即报完成与空闲;MSC 按 60 Hz 从服务端时钟推算,
//   NotifyMSC 等到那一帧再报。wait-fence 未触发时不等待(与立即拷贝一致),idle-fence 在空闲时触发。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>Present 的事件上下文(SelectInput 的 eid)。</summary>
internal sealed class XPresentEventContext(uint id, XClient owner, XWindow window, uint mask) : XResource(id, owner)
{
    public XWindow Window { get; } = window;

    public uint Mask { get; set; } = mask;
}

public sealed partial class X11Server
{
    private const byte PresentMajor = 144;
    private const uint PresentConfigureMask = 1, PresentCompleteMask = 2, PresentIdleMask = 4;

    /// <summary>窗口 → 挂在它上面的事件上下文(按窗口索引,呈现时不必扫整个资源表)。</summary>
    private readonly Dictionary<XWindow, List<XPresentEventContext>> _presentContexts = [];

    /// <summary>当前帧号:按 60 Hz 从服务端时钟推算。</summary>
    private ulong CurrentMsc => (ulong)(_clock.ElapsedTicks * 60 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>UST:微秒计的服务端时间。</summary>
    private ulong CurrentUst => (ulong)(_clock.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);

    private void Present(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U32(1).U32(2).Zero(16));
                break;
            case 1:   // Pixmap
                PresentPixmap(r);
                break;
            case 2:   // NotifyMSC
                {
                    XWindow window = Window(r.U32());
                    uint serial = r.U32();
                    r.Skip(4);
                    ulong target = r.U64(), divisor = r.U64(), remainder = r.U64();
                    ulong when = TargetMsc(target, divisor, remainder);
                    ulong now = CurrentMsc;
                    if (when <= now)
                    {
                        SendPresentComplete(window, serial, kind: 1, now);
                    }
                    else
                    {
                        uint delayMs = (uint)Math.Min(uint.MaxValue, (when - now) * 1000 / 60);
                        _ = DelayThenPostAsync(Math.Max(1, delayMs), () => SendPresentComplete(window, serial, kind: 1, CurrentMsc));
                    }
                    break;
                }
            case 3:   // SelectInput
                {
                    uint eid = r.U32();
                    XWindow window = Window(r.U32());
                    uint mask = r.U32();
                    if ((mask & ~7u) != 0)
                    {
                        throw new XProtocolError(XErrorCode.Value, mask);
                    }
                    if (Lookup<XPresentEventContext>(eid) is { } existing)
                    {
                        if (!ReferenceEquals(existing.Window, window) || !ReferenceEquals(existing.Owner, c))
                        {
                            throw new XProtocolError(XErrorCode.Match);
                        }
                        if (mask == 0)
                        {
                            RemoveResource(eid);
                            _presentContexts[window].Remove(existing);
                        }
                        else
                        {
                            existing.Mask = mask;
                        }
                    }
                    else if (mask != 0)
                    {
                        XPresentEventContext ctx = new(eid, c, window, mask);
                        AddResource(c, ctx);
                        if (!_presentContexts.TryGetValue(window, out List<XPresentEventContext>? list))
                        {
                            _presentContexts[window] = list = [];
                        }
                        list.Add(ctx);
                    }
                    break;
                }
            case 4:   // QueryCapabilities:可以不等垂直同步就呈现(Async)
                {
                    uint target = r.U32();
                    if (Lookup<XResource>(target) is not XWindow && (target < RandRCrtcBase || target >= RandRCrtcBase + _monitors.Count))
                    {
                        throw new XProtocolError(XErrorCode.Window, target);
                    }
                    c.Reply(0, w => w.U32(1).Zero(20));
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>规范的 MSC 目标:target 之后第一个满足 msc % divisor == remainder 的帧(divisor 为 0 时就是 target)。</summary>
    private ulong TargetMsc(ulong target, ulong divisor, ulong remainder)
    {
        ulong now = CurrentMsc;
        if (divisor == 0 || target > now)
        {
            return target;
        }
        ulong next = now - (now % divisor) + remainder;
        return next < now ? next + divisor : next;
    }

    private void PresentPixmap(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        uint pixmapId = r.U32();
        XPixmap pixmap = Lookup<XPixmap>(pixmapId) ?? throw new XProtocolError(XErrorCode.Pixmap, pixmapId);
        uint serial = r.U32();
        uint validId = r.U32(), updateId = r.U32();
        short xOff = r.I16(), yOff = r.I16();
        r.Skip(4);                     // target-crtc
        uint waitFence = r.U32(), idleFence = r.U32();
        r.Skip(8);                     // options、pad
        r.Skip(24);                    // target-msc、divisor、remainder:软件拷贝不等帧
        List<(XWindow Window, uint Serial)> notifies = [];
        while (r.Remaining >= 8)
        {
            notifies.Add((Window(r.U32()), r.U32()));
        }
        if (pixmap.Depth != (window.IsRoot ? 24 : window.Depth))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (waitFence != 0)
        {
            _ = Fence(waitFence);
        }

        // 要拷的部分:update 区域(像素图坐标)∩ valid 区域;没给就是整张像素图。
        Region copy = new(pixmap.Buffer.Bounds);
        if (updateId != 0)
        {
            copy.Intersect(RegionRes(updateId).Region);
        }
        if (validId != 0)
        {
            copy.Intersect(RegionRes(validId).Region);
        }
        if (DrawTarget(window.Id, null) is { } target)
        {
            Region dest = copy.Clone().Translate(xOff + target.OriginX, yOff + target.OriginY).Intersect(target.Clip);
            foreach (XRect rect in dest.Rects)
            {
                CopyPixels(pixmap.Buffer, rect.X - xOff - target.OriginX, rect.Y - yOff - target.OriginY,
                    target.Buffer, rect.X, rect.Y, rect.Width, rect.Height);
            }
            if (target.TopLevel is { } top)
            {
                MarkDamage(top, dest);
            }
        }

        ulong msc = CurrentMsc;
        SendPresentComplete(window, serial, kind: 0, msc);
        foreach ((XWindow other, uint otherSerial) in notifies)
        {
            SendPresentComplete(other, otherSerial, kind: 0, msc);
        }
        SendPresentIdle(window, serial, pixmap.Id, idleFence);
        if (idleFence != 0 && Lookup<XSyncFence>(idleFence) is { } fence)
        {
            TriggerFence(fence);
        }
    }

    private List<XPresentEventContext> PresentSelectors(XWindow window, uint mask) =>
        _presentContexts.TryGetValue(window, out List<XPresentEventContext>? list)
            ? [.. list.Where(ctx => (ctx.Mask & mask) != 0 && ctx.Owner is { Closed: false })]
            : [];

    /// <summary>窗口销毁 / 客户端断开:摘掉相关的事件上下文。</summary>
    private void CleanupPresent(XClient? client, XWindow? window)
    {
        if (_presentContexts.Count == 0)
        {
            return;
        }
        if (window is not null && _presentContexts.Remove(window, out List<XPresentEventContext>? gone))
        {
            foreach (XPresentEventContext ctx in gone)
            {
                RemoveResource(ctx.Id);
            }
        }
        if (client is not null)
        {
            foreach ((XWindow w, List<XPresentEventContext> list) in _presentContexts.ToArray())
            {
                list.RemoveAll(ctx => ReferenceEquals(ctx.Owner, client));
                if (list.Count == 0)
                {
                    _presentContexts.Remove(w);
                }
            }
        }
    }

    private void SendPresentComplete(XWindow window, uint serial, byte kind, ulong msc)
    {
        ulong ust = CurrentUst;
        foreach (XPresentEventContext ctx in PresentSelectors(window, PresentCompleteMask))
        {
            ctx.Owner!.GenericEvent(PresentMajor, 1, w => w
                .U8(kind).U8(0)                               // mode:Copy
                .U32(ctx.Id).U32(window.Id).U32(serial).U64(ust).U64(msc));
        }
    }

    private void SendPresentIdle(XWindow window, uint serial, uint pixmap, uint idleFence)
    {
        foreach (XPresentEventContext ctx in PresentSelectors(window, PresentIdleMask))
        {
            ctx.Owner!.GenericEvent(PresentMajor, 2, w => w
                .Zero(2).U32(ctx.Id).U32(window.Id).U32(serial).U32(pixmap).U32(idleFence));
        }
    }

    /// <summary>窗口几何变了:给选了 Present ConfigureNotify 的上下文发一条。</summary>
    private void NotifyPresentConfigure(XWindow window)
    {
        foreach (XPresentEventContext ctx in PresentSelectors(window, PresentConfigureMask))
        {
            ctx.Owner!.GenericEvent(PresentMajor, 0, w => w
                .Zero(2).U32(ctx.Id).U32(window.Id)
                .I16(window.X).I16(window.Y).U16((ushort)window.Width).U16((ushort)window.Height)
                .I16(0).I16(0).U16((ushort)window.Width).U16((ushort)window.Height).U32(0));
        }
    }
}
