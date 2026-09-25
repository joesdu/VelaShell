// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   XTEST Extension Protocol, Version 2.2 —— GetVersion 0(回复的 data 字节是主版本)、CompareCursor 1
//   (cursor:None = 0、CurrentCursor = 1)、FakeInput 2(type 与核心事件码相同;MotionNotify 的 detail
//   为 True 表示相对移动;time 是以毫秒计的延迟,0 = 立即;root 为 None 表示指针当前所在屏幕)、GrabControl 3
//
//   xdotool、自动化测试、屏幕键盘都靠它。伪造的输入与宿主注入的输入走同一条路径,同样重置空闲计时。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
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
                        _ => Lookup<XCursorResource>(cursorId) ?? throw new XProtocolError(XErrorCode.Cursor, cursorId),
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
                ? () => MovePointer(Math.Max(0, _pointerX) + rootX, Math.Max(0, _pointerY) + rootY)
                : () => MovePointer(rootX, rootY),
            _ => throw new XProtocolError(XErrorCode.Value, detail),
        };

        bool keyboard = type is XEventCode.KeyPress or XEventCode.KeyRelease;

        void Run()
        {
            NoteUserActivity();
            if (keyboard)
            {
                ProcessKeyboardInput(inject);
            }
            else
            {
                ProcessPointerInput(inject);
            }
        }

        if (delay == 0)
        {
            Run();
        }
        else
        {
            // 延迟以毫秒计;到点后回到执行线程执行,不阻塞后面的请求。发请求的客户端先断开了就不再注入。
            _ = DelayThenPostAsync(c, delay, Run);
        }
    }

    /// <summary>延迟上限约 24 天(Task.Delay 的上限);协议允许的更大值没有实际意义。</summary>
    private const uint MaxFakeInputDelay = int.MaxValue;

    private async Task DelayThenPostAsync(XClient client, uint milliseconds, Action action)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxFakeInputDelay)), _lifetime.Token).ConfigureAwait(false);
            Post(null, () =>
            {
                if (!client.Closed)
                {
                    action();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 服务端收工了。
        }
    }
}
