// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   XTEST Extension Protocol, Version 2.2 —— GetVersion 0(回复的 data 字节是主版本)、CompareCursor 1
//   (cursor:None = 0、CurrentCursor = 1)、FakeInput 2(type 与核心事件码相同;MotionNotify 的 detail
//   为 True 表示相对移动;time 是以毫秒计的延迟,0 = 立即 —— 「延迟到点、伪造的事件处理完之前,这个客户端的其它请求
//   一律不处理」;root 为 None 表示指针当前所在屏幕)、GrabControl 3
//
//   xdotool、自动化测试、屏幕键盘都靠它。伪造的输入与宿主注入的输入走同一条路径,同样重置空闲计时。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>
    /// FakeInput 带了延迟、正在等的客户端 → 计时器与暂存的请求。按规范,到点之前这个客户端后面的请求一律不处理:
    /// 伪造的按下 / 松开因此按发出的顺序生效(不会先松开后按下、把键卡住),一个客户端同一时刻也只挂着一个计时器。
    /// </summary>
    private readonly Dictionary<XClient, (CancellationTokenSource Timer, List<WorkItem> Deferred)> _fakeInputDelays = [];

    /// <summary>正在等延迟的 FakeInput 数(测试用)。</summary>
    internal int PendingFakeInputDelays => _fakeInputDelays.Count;

    private void XTest(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // GetVersion
                c.Reply(2, w => w.U16(2).Zero(22));
                break;
            case 1:   // CompareCursor
                {
                    XWindow window = Window(r.U32());
                    uint cursorId = r.U32();
                    XCursorResource? cursor = cursorId switch
                    {
                        0 => null,
                        1 => CurrentCursor(),
                        _ => Use<XCursorResource>(cursorId) ?? throw new XProtocolError(XErrorCode.Cursor, cursorId),
                    };
                    c.Reply(ReferenceEquals(window.Cursor, cursor) ? (byte)1 : (byte)0, w => w.Zero(24));
                    break;
                }
            case 2:   // FakeInput
                FakeInput(c, r);
                break;
            case 3:   // GrabControl:我们不会因为别人的 GrabServer 挡住 XTEST 客户端以外的东西,接受即可
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private void FakeInput(XClient c, XRequestReader r)
    {
        byte type = r.U8();
        byte detail = r.U8();
        r.Skip(2);
        uint delay = r.U32();
        uint rootId = r.U32();
        r.Skip(8);
        short rootX = r.I16(), rootY = r.I16();

        if (rootId != 0 && rootId != Root.Id)
        {
            _ = Window(rootId);   // 只有一块屏幕:别的窗口一律当作它的屏幕
        }

        Action inject = type switch
        {
            XEventCode.KeyPress or XEventCode.KeyRelease when detail is >= Input.Keymap.MinKeycode and <= Input.Keymap.MaxKeycode =>
                () => KeyEvent(detail, type == XEventCode.KeyPress),
            XEventCode.ButtonPress or XEventCode.ButtonRelease when detail != 0 =>
                () => ButtonEvent(detail, type == XEventCode.ButtonPress),
            XEventCode.MotionNotify => detail != 0
                ? () => MovePointer(_pointerX + rootX, _pointerY + rootY)
                : () => MovePointer(rootX, rootY),
            _ => throw new XProtocolError(XErrorCode.Value, detail),
        };

        void Run()
        {
            NoteInputActivity();
            switch (type)
            {
                case XEventCode.KeyPress or XEventCode.KeyRelease:
                    ProcessKeyboardInput(detail, type == XEventCode.KeyPress, inject);
                    break;
                case XEventCode.ButtonPress or XEventCode.ButtonRelease:
                    ProcessPointerButton(detail, type == XEventCode.ButtonPress, inject);
                    break;
                default:
                    ProcessPointerMotion(inject);
                    break;
            }
        }

        if (delay == 0)
        {
            Run();
            return;
        }
        // 延迟以毫秒计:到点之前这个客户端的后续请求暂存(RunItem 里 DeferIfFakeInputPending),执行线程不阻塞;
        // 到点后回到执行线程注入,再按原顺序放回暂存的请求。客户端先断开了计时器随之取消。
        var timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fakeInputDelays[c] = (timer, []);
        _ = DelayThenPostAsync(delay, () => EndFakeInputDelay(c, Run), timer.Token);
    }

    /// <summary>延迟到点:注入伪造的事件,再按原顺序放回这段时间里暂存的请求(其中可能又有带延迟的 FakeInput)。</summary>
    private void EndFakeInputDelay(XClient client, Action inject)
    {
        if (!_fakeInputDelays.Remove(client, out (CancellationTokenSource Timer, List<WorkItem> Deferred) pending))
        {
            return;   // 客户端已经走了
        }
        pending.Timer.Dispose();
        inject();
        Requeue(pending.Deferred);
    }

    /// <summary>执行循环在跑一项工作之前问一句:这个客户端是不是在等 FakeInput 的延迟?是就把请求暂存。</summary>
    private bool DeferIfFakeInputPending(WorkItem item)
    {
        if (item.Client is { } client && _fakeInputDelays.TryGetValue(client, out (CancellationTokenSource Timer, List<WorkItem> Deferred) pending)
            && !client.Closed)
        {
            pending.Deferred.Add(item);
            return true;
        }
        return false;
    }

    /// <summary>客户端断开:取消它的延迟计时器,暂存的请求随之丢弃。</summary>
    private void CleanupXTest(XClient client)
    {
        if (_fakeInputDelays.Remove(client, out (CancellationTokenSource Timer, List<WorkItem> Deferred) pending))
        {
            pending.Timer.Cancel();
            pending.Timer.Dispose();
        }
    }

    /// <summary>延迟上限约 24 天(Task.Delay 的上限);协议允许的更大值没有实际意义。</summary>
    private const uint MaxDelayMilliseconds = int.MaxValue;

    /// <summary>等 <paramref name="milliseconds" /> 毫秒再把 <paramref name="action" /> 排回执行线程;令牌取消(客户端走了、服务端收工)就不排。</summary>
    private async Task DelayThenPostAsync(uint milliseconds, Action action, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDelayMilliseconds)), cancellationToken).ConfigureAwait(false);
            Post(null, action);
        }
        catch (OperationCanceledException)
        {
            // 客户端走了,或服务端收工了。
        }
    }
}
