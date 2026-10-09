// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The Present Extension, Version 1.2 —— §「Types」(PresentEventMask:ConfigureNotify 1 / CompleteNotify 2 /
//   IdleNotify 4;PresentOption Async 1 / Copy 2 / UST 4;CompleteKind Pixmap / NotifyMSC;CompleteMode Copy / Flip / Skip)、
//   §「Extension Initialization」(QueryVersion:回服务端支持的最高版本,但不高于客户端要的)、
//   §「Requests」(QueryVersion 0、Pixmap 1、NotifyMSC 2、SelectInput 3、QueryCapabilities 4)、§「Events」(经 Generic Event
//   Extension 发出的 ConfigureNotify 0、CompleteNotify 1、IdleNotify 2)
//
//   纯软件实现,只有拷贝模式:MSC 按 60 Hz 从服务端时钟推算。PresentPixmap 等 wait-fence 触发(或被销毁)、
//   等到 target-msc / divisor / remainder 指定的那一帧再把像素图拷进窗口,随即报完成与空闲、触发 idle-fence;
//   同一窗口上较早排队、还没呈现的那几条按 Skip 报完成。NotifyMSC 同样等到那一帧再报。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private const uint PresentConfigureMask = 1, PresentCompleteMask = 2, PresentIdleMask = 4;

    private const uint PresentOptionUst = 4;

    private const byte PresentModeCopy = 0, PresentModeSkip = 2;

    /// <summary>窗口 → 挂在它上面的事件上下文(按窗口索引,呈现时不必扫整个资源表)。</summary>
    private readonly Dictionary<XWindow, List<XPresentEventContext>> _presentContexts = [];

    /// <summary>
    /// 一个客户端同时挂着的 NotifyMSC 与还没呈现的 PresentPixmap 合计的上限。每条是一个计时器(或者等一个栅栏),
    /// 目标 MSC 可以远到几十天之后;真实的程序按帧节拍只挂一两条。
    /// </summary>
    internal const int MaxPendingPresents = 256;

    /// <summary>
    /// 一条 PresentPixmap 最多带几个 PRESENTNOTIFY。每一项都给那个窗口上选了 CompleteNotify 的每个上下文发一条 40 字节的事件:
    /// 不设上限的话,一条 16 MB 的请求就能给别的客户端塞约 80 MB 的事件,把对方顶过输出积压的上限断开。
    /// 真实的客户端(Mesa、Vulkan WSI)都不带。
    /// </summary>
    internal const int MaxPresentNotifies = 64;

    /// <summary>各客户端挂着的 NotifyMSC / PresentPixmap:取消用的令牌(客户端断开时一并取消)与条数。</summary>
    private readonly Dictionary<XClient, (CancellationTokenSource Cancel, int Count)> _presentPending = [];

    /// <summary>还没呈现的 PresentPixmap,按请求的先后。</summary>
    private readonly List<PendingPresent> _pendingPresents = [];

    /// <summary>正在执行到点的呈现(呈现会触发 idle-fence,栅栏又会回来要求再跑一轮)。</summary>
    private bool _runningPresents;

    /// <summary>执行途中又有条件变了:这一轮结束后再来一轮。</summary>
    private bool _presentsDirty;

    /// <summary>挂着的 NotifyMSC 与 PresentPixmap 总条数(测试用)。</summary>
    internal int PendingPresents => _presentPending.Values.Sum(p => p.Count);

    /// <summary>当前帧号:按 60 Hz 从服务端时钟推算。</summary>
    private ulong CurrentMsc => (ulong)(_clock.ElapsedTicks * 60 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>UST:微秒计的服务端时间。</summary>
    private ulong CurrentUst => (ulong)(_clock.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>一条排着队的 PresentPixmap:请求里的参数,外加它占着的计时器与内存。</summary>
    private sealed class PendingPresent(XClient client, XWindow window, XPixmap pixmap, uint serial)
    {
        public XClient Client { get; } = client;

        public XWindow Window { get; } = window;

        /// <summary>规范:呈现之前一直持有像素图的引用,客户端可以在请求之后立刻 FreePixmap。</summary>
        public XPixmap Pixmap { get; } = pixmap;

        public uint Serial { get; } = serial;

        /// <summary>要拷的部分:像素图坐标,update ∩ valid ∩ 像素图范围(请求执行时就算好,之后改了区域对象不影响)。</summary>
        public required Region Copy { get; init; }

        public short XOff { get; init; }

        public short YOff { get; init; }

        public XSyncFence? WaitFence { get; init; }

        public XSyncFence? IdleFence { get; init; }

        public ulong TargetMsc { get; init; }

        public required List<(XWindow Window, uint Serial)> Notifies { get; init; }

        /// <summary>像素图在呈现之前就被释放了:它的像素改记在这条请求的客户端名下,呈现或丢弃时退还。</summary>
        public long Charged { get; set; }

        /// <summary>为它排的计时器还没到点。</summary>
        public bool TimerArmed { get; set; }
    }

    private void Present(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion:1.2,但不高于客户端要的
                {
                    uint major = r.U32(), minor = r.U32();
                    (uint maj, uint min) = major > 1 || (major == 1 && minor >= 2) ? (1u, 2u) : (major, minor);
                    c.Reply(0, w => w.U32(maj).U32(min).Zero(16));
                    break;
                }
            case 1:   // Pixmap
                PresentPixmap(c, r);
                break;
            case 2:   // NotifyMSC
                {
                    XWindow window = Window(r.U32());
                    uint serial = r.U32();
                    r.Skip(4);
                    ulong target = r.U64(), divisor = r.U64(), remainder = r.U64();
                    ulong when = TargetMsc(target, divisor, remainder);
                    if (when <= CurrentMsc)
                    {
                        SendPresentComplete(window, serial, kind: 1, PresentModeCopy, CurrentMsc);
                        break;
                    }
                    CancellationToken token = BeginPresentPending(c);
                    void Fire()
                    {
                        if (CurrentMsc < when)
                        {
                            _ = DelayThenPostAsync(MillisecondsUntilMsc(when), Fire, token);   // 计时器早到了一点:再等
                            return;
                        }
                        EndPresentPending(c);
                        SendPresentComplete(window, serial, kind: 1, PresentModeCopy, CurrentMsc);   // 窗口已销毁时事件上下文随之没了,不会发
                    }
                    _ = DelayThenPostAsync(MillisecondsUntilMsc(when), Fire, token);
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
                    if (Use<XResource>(target) is not XWindow && MonitorIndexOf(target, RandRCrtcBase) < 0)
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

    /// <summary>
    /// 规范的 MSC 目标:target 大于当前帧时就是 target;否则是之后第一个满足 msc % divisor == remainder 的帧
    /// (divisor 为 0 时就是现在 —— 只有拷贝模式,不存在撕裂,不必等下一帧)。
    /// </summary>
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

    /// <summary>离第 <paramref name="msc" /> 帧开始还有多少毫秒(向上取整、至少 1;远在天边的目标由计时器的上限截住,到点再看)。</summary>
    private uint MillisecondsUntilMsc(ulong msc)
    {
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        UInt128 startTicks = (((UInt128)msc * (ulong)frequency) + 59) / 60;
        UInt128 nowTicks = (ulong)_clock.ElapsedTicks;
        if (startTicks <= nowTicks)
        {
            return 1;
        }
        UInt128 ms = (((startTicks - nowTicks) * 1000) + (ulong)frequency - 1) / (ulong)frequency;
        return ms >= uint.MaxValue ? uint.MaxValue : Math.Max(1u, (uint)ms);
    }

    /// <summary>PresentOptionUST:target / divisor / remainder 是微秒计的 UST,换算成帧(规范:服务端把 UST 换成合适的 MSC)。</summary>
    private static ulong UstToMsc(ulong ust, bool roundUp) =>
        (ulong)((((UInt128)ust * 60) + (roundUp ? 999_999u : 0u)) / 1_000_000);

    private void PresentPixmap(XClient c, XRequestReader r)
    {
        // 先把参数全部核对完,任何一项出错都不留下效果。
        XWindow window = Window(r.U32());
        uint pixmapId = r.U32();
        XPixmap pixmap = Use<XPixmap>(pixmapId) ?? throw new XProtocolError(XErrorCode.Pixmap, pixmapId);
        uint serial = r.U32();
        uint validId = r.U32(), updateId = r.U32();
        short xOff = r.I16(), yOff = r.I16();
        r.Skip(4);                     // target-crtc:只有一种节拍
        uint waitFenceId = r.U32(), idleFenceId = r.U32();
        uint options = r.U32();
        r.Skip(4);                     // pad
        ulong target = r.U64(), divisor = r.U64(), remainder = r.U64();
        if (r.Remaining / 8 > MaxPresentNotifies)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
        List<(XWindow Window, uint Serial)> notifies = [];
        while (r.Remaining >= 8)
        {
            notifies.Add((Window(r.U32()), r.U32()));
        }
        if (pixmap.Depth != (window.IsRoot ? 24 : window.Depth))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        XSyncFence? waitFence = waitFenceId == 0 ? null : Fence(waitFenceId);
        XSyncFence? idleFence = idleFenceId == 0 ? null : Fence(idleFenceId);

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

        if ((options & PresentOptionUst) != 0)
        {
            (target, divisor, remainder) = (UstToMsc(target, roundUp: true), UstToMsc(divisor, roundUp: false), UstToMsc(remainder, roundUp: false));
        }
        PendingPresent present = new(c, window, pixmap, serial)
        {
            Copy = copy,
            XOff = xOff,
            YOff = yOff,
            WaitFence = waitFence,
            IdleFence = idleFence,
            TargetMsc = TargetMsc(target, divisor, remainder),
            Notifies = notifies,
        };
        if (PresentReady(present))
        {
            ExecutePresent(present);   // 常见的情形:不等栅栏、目标帧已到 —— 就地呈现,不占队列
            return;
        }
        _ = BeginPresentPending(c);   // 超过上限抛 Alloc,队列不变
        _pendingPresents.Add(present);
        ArmPresentTimer(present);
    }

    /// <summary>栅栏不用等了(没给、已触发、或者已被销毁),目标帧也到了。</summary>
    private bool PresentReady(PendingPresent present) =>
        (present.WaitFence is not { Triggered: false } fence || !ReferenceEquals(Lookup<XSyncFence>(fence.Id), fence))
        && present.TargetMsc <= CurrentMsc;

    /// <summary>目标帧还没到:排一个到那一帧的计时器(早到了就再排,见 <see cref="RunReadyPresents" />)。</summary>
    private void ArmPresentTimer(PendingPresent present)
    {
        if (present.TimerArmed || present.TargetMsc <= CurrentMsc
            || !_presentPending.TryGetValue(present.Client, out (CancellationTokenSource Cancel, int Count) pending))
        {
            return;
        }
        present.TimerArmed = true;
        _ = DelayThenPostAsync(MillisecondsUntilMsc(present.TargetMsc), () =>
        {
            present.TimerArmed = false;
            RunReadyPresents();
        }, pending.Cancel.Token);
    }

    /// <summary>栅栏触发或被销毁、计时器到点、客户端断开:按请求的先后把已经可以呈现的那几条呈现掉。</summary>
    private void RunReadyPresents()
    {
        if (_pendingPresents.Count == 0)
        {
            return;
        }
        if (_runningPresents)
        {
            _presentsDirty = true;   // 呈现触发了 idle-fence,又有别的在等它:这一轮结束后再来一轮
            return;
        }
        _runningPresents = true;
        try
        {
            do
            {
                _presentsDirty = false;
                foreach (PendingPresent present in _pendingPresents.ToArray())
                {
                    if (!_pendingPresents.Contains(present))
                    {
                        continue;   // 前面那条呈现时把它当成过时的跳过了
                    }
                    if (PresentReady(present))
                    {
                        ExecutePresent(present);
                    }
                    else
                    {
                        ArmPresentTimer(present);
                    }
                }
            }
            while (_presentsDirty);
        }
        finally
        {
            _runningPresents = false;
        }
    }

    /// <summary>
    /// 呈现一条:把像素拷进窗口、报完成与空闲、触发 idle-fence。同一窗口上排在它前面还没呈现的已经过时
    /// (之后再呈现就会拿旧内容盖掉新的),按 Skip 了结。
    /// </summary>
    private void ExecutePresent(PendingPresent present)
    {
        int index = _pendingPresents.IndexOf(present);
        List<PendingPresent> stale = [.. _pendingPresents.Take(index >= 0 ? index : _pendingPresents.Count)
            .Where(p => ReferenceEquals(p.Window, present.Window))];
        ForgetPendingPresent(present);
        foreach (PendingPresent skipped in stale)
        {
            ForgetPendingPresent(skipped);
            FinishPresent(skipped, PresentModeSkip);
        }

        XWindow window = present.Window;
        if (DrawTarget(window.Id, null) is { } target)
        {
            int dx = present.XOff + target.OriginX, dy = present.YOff + target.OriginY;
            Region dest = present.Copy.Clone().Translate(dx, dy).Intersect(target.Clip);
            foreach (XRect rect in dest.Rects)
            {
                PixelBuffer.CopyRect(present.Pixmap.Buffer, rect.X - dx, rect.Y - dy, target.Buffer, rect.X, rect.Y, rect.Width, rect.Height);
            }
            if (target.TopLevel is { } top)
            {
                MarkDamage(top, dest);
            }
        }
        FinishPresent(present, PresentModeCopy);
    }

    /// <summary>报完成(窗口与 notifies 里的每一项)与空闲,触发 idle-fence(还在的话)。</summary>
    private void FinishPresent(PendingPresent present, byte mode)
    {
        ulong msc = CurrentMsc;
        SendPresentComplete(present.Window, present.Serial, kind: 0, mode, msc);
        foreach ((XWindow other, uint otherSerial) in present.Notifies)
        {
            if (ReferenceEquals(Lookup<XWindow>(other.Id), other))
            {
                SendPresentComplete(other, otherSerial, kind: 0, mode, msc);
            }
        }
        SendPresentIdle(present.Window, present.Serial, present.Pixmap.Id, present.IdleFence?.Id ?? 0);
        if (present.IdleFence is { } fence && ReferenceEquals(Lookup<XSyncFence>(fence.Id), fence))
        {
            TriggerFence(fence);   // 规范:idle-fence 在呈现之前被销毁就不触发
        }
    }

    /// <summary>从队列里摘掉一条:退还它占的条数与(像素图已释放时)记在它名下的像素。</summary>
    private void ForgetPendingPresent(PendingPresent present)
    {
        if (!_pendingPresents.Remove(present))
        {
            return;
        }
        RefundMemory(present.Client, present.Charged);
        present.Charged = 0;
        EndPresentPending(present.Client);
    }

    /// <summary>像素图的 ID 释放了,还有排着队的呈现要读它:像素改记在发请求的客户端名下,直到呈现。</summary>
    private void PresentPixmapFreed(XPixmap pixmap)
    {
        foreach (PendingPresent present in _pendingPresents)
        {
            if (ReferenceEquals(present.Pixmap, pixmap) && present.Charged == 0 && pixmap.OwnsBuffer)
            {
                present.Charged = PixelBytes(pixmap.Width, pixmap.Height);
                ChargeMemory(present.Client, present.Charged, force: true);
            }
        }
    }

    private List<XPresentEventContext> PresentSelectors(XWindow window, uint mask) =>
        _presentContexts.TryGetValue(window, out List<XPresentEventContext>? list)
            ? [.. list.Where(ctx => (ctx.Mask & mask) != 0 && ctx.Owner is { Closed: false })]
            : [];

    /// <summary>窗口销毁:摘掉并释放它上面的事件上下文;排着队要呈现到它上面的作废(规范:窗口没了,呈现不再完成)。</summary>
    private void CleanupPresent(XWindow window)
    {
        if (_presentContexts.Remove(window, out List<XPresentEventContext>? gone))
        {
            foreach (XPresentEventContext ctx in gone)
            {
                RemoveResource(ctx.Id);
            }
        }
        foreach (PendingPresent present in _pendingPresents.ToArray())
        {
            if (ReferenceEquals(present.Window, window))
            {
                ForgetPendingPresent(present);
            }
        }
    }

    /// <summary>挂一条 NotifyMSC 或 PresentPixmap:这个客户端的条数加一,超过上限抛 Alloc。返回取消用的令牌。</summary>
    private CancellationToken BeginPresentPending(XClient client)
    {
        (CancellationTokenSource cancel, int count) = _presentPending.GetValueOrDefault(client);
        if (count >= MaxPendingPresents)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
        cancel ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _presentPending[client] = (cancel, count + 1);
        return cancel.Token;
    }

    /// <summary>一条 NotifyMSC 到点了、或者一条 PresentPixmap 呈现了:从这个客户端的计数里减掉,减到 0 就收掉令牌。</summary>
    private void EndPresentPending(XClient client)
    {
        if (!_presentPending.TryGetValue(client, out (CancellationTokenSource Cancel, int Count) pending))
        {
            return;
        }
        if (pending.Count > 1)
        {
            _presentPending[client] = (pending.Cancel, pending.Count - 1);
            return;
        }
        _presentPending.Remove(client);
        pending.Cancel.Cancel();   // 排队的呈现被别的一条当成过时跳过时,它的计时器还挂着:一并取消
        pending.Cancel.Dispose();
    }

    /// <summary>客户端断开:取消它挂着的 NotifyMSC 与排队的呈现。</summary>
    private void CleanupPresent(XClient client)
    {
        foreach (PendingPresent present in _pendingPresents.ToArray())
        {
            if (ReferenceEquals(present.Client, client))
            {
                ForgetPendingPresent(present);
            }
        }
        if (_presentPending.Remove(client, out (CancellationTokenSource Cancel, int Count) pending))
        {
            pending.Cancel.Cancel();
            pending.Cancel.Dispose();
        }
    }

    /// <summary>客户端的资源销毁了(<see cref="Extension.ClientResourcesDestroyed" />):摘掉它的事件上下文(它们是资源,Retain 模式断开时还留着);等它的栅栏的呈现不再等(栅栏随它没了)。</summary>
    private void CleanupPresentResources(XClient client)
    {
        foreach ((XWindow w, List<XPresentEventContext> list) in _presentContexts.ToArray())
        {
            list.RemoveAll(ctx => ReferenceEquals(ctx.Owner, client));
            if (list.Count == 0)
            {
                _presentContexts.Remove(w);
            }
        }
        RunReadyPresents();
    }

    private void SendPresentComplete(XWindow window, uint serial, byte kind, byte mode, ulong msc)
    {
        ulong ust = CurrentUst;
        foreach (XPresentEventContext ctx in PresentSelectors(window, PresentCompleteMask))
        {
            ctx.Owner!.GenericEvent(PresentMajor, 1, w => w
                .U8(kind).U8(mode)
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
