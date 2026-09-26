// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「GrabPointer」「GrabButton」「GrabKeyboard」「GrabKey」的
//   pointer-mode / keyboard-mode(Synchronous 0 冻结设备、Asynchronous 1 照常);「AllowEvents」
//   (AsyncPointer 0、SyncPointer 1、ReplayPointer 2、AsyncKeyboard 3、SyncKeyboard 4、ReplayKeyboard 5、AsyncBoth 6、SyncBoth 7:
//   冻结期间设备事件排队、放行后按原顺序处理;Sync* 放行到下一个按钮 / 按键事件报给客户端为止再冻结;
//   Replay* 解除抓取并把引起冻结的那个事件重新处理一遍,这次不看抓取窗口及其以上的被动抓取);
//   抓取解除时它冻结的设备随之解冻。
//   The X Input Extension 2.2 ——「XIGrabDevice」「XIPassiveGrabDevice」的 grab_mode / paired_device_mode 与
//   「XIAllowEvents」(AsyncDevice 0、SyncDevice 1、ReplayDevice 2、AsyncPairedDevice 3、AsyncPair 4、SyncPair 5)。
//
//   冻结是按设备的:指针冻住时键盘事件照常处理,反之亦然。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private ActiveGrab? _pointerGrab;
    private ActiveGrab? _keyboardGrab;

    /// <summary>冻住指针的那个抓取;null = 没冻。</summary>
    private ActiveGrab? _pointerFrozenBy;

    /// <summary>冻住键盘的那个抓取;null = 没冻。</summary>
    private ActiveGrab? _keyboardFrozenBy;

    /// <summary>SyncPointer / SyncBoth 放行中:下一个按钮事件报给抓取方之后重新冻结(Both 时两个设备一起)。</summary>
    private ActiveGrab? _pointerSyncOnce;

    /// <summary>SyncKeyboard / SyncBoth 放行中:下一个按键事件报给抓取方之后重新冻结。</summary>
    private ActiveGrab? _keyboardSyncOnce;

    private bool _syncBoth;

    /// <summary>引起指针冻结、可被 ReplayPointer 重放的按钮按下:按钮号与当时的抓取窗口。</summary>
    private (int Button, XWindow GrabWindow)? _pointerReplay;

    /// <summary>引起键盘冻结、可被 ReplayKeyboard 重放的按键按下。</summary>
    private (byte Keycode, XWindow GrabWindow)? _keyboardReplay;

    private readonly Queue<Action> _pointerQueue = new();
    private readonly Queue<Action> _keyboardQueue = new();
    private bool _drainScheduled;

    /// <summary>当前的指针抓取。换掉(解除或被别的抓取取代)时,它冻结的设备随之解冻。</summary>
    private ActiveGrab? PointerGrab
    {
        get => _pointerGrab;
        set
        {
            ActiveGrab? old = _pointerGrab;
            _pointerGrab = value;
            if (old is not null && !ReferenceEquals(old, value))
            {
                ThawGrab(old);
            }
        }
    }

    /// <summary>当前的键盘抓取。换掉时它冻结的设备随之解冻。</summary>
    private ActiveGrab? KeyboardGrab
    {
        get => _keyboardGrab;
        set
        {
            ActiveGrab? old = _keyboardGrab;
            _keyboardGrab = value;
            if (old is not null && !ReferenceEquals(old, value))
            {
                ThawGrab(old);
            }
        }
    }

    // ------------------------------------------------------------------ 入口:设备事件按冻结状态排队

    /// <summary>一个指针事件(移动、按钮、离开):指针冻着、或前面还有排着的,就排队;否则立即处理。</summary>
    private void ProcessPointerInput(Action input)
    {
        if (_pointerFrozenBy is not null || _pointerQueue.Count > 0)
        {
            _pointerQueue.Enqueue(input);
            return;
        }
        input();
    }

    /// <summary>一个键盘事件:同上。</summary>
    private void ProcessKeyboardInput(Action input)
    {
        if (_keyboardFrozenBy is not null || _keyboardQueue.Count > 0)
        {
            _keyboardQueue.Enqueue(input);
            return;
        }
        input();
    }

    /// <summary>解冻之后排着的事件在执行线程的下一个工作项里处理 —— 不在当前事件处理的半途插进去。</summary>
    private void ScheduleDrain()
    {
        if (_drainScheduled || (_pointerQueue.Count == 0 && _keyboardQueue.Count == 0))
        {
            return;
        }
        _drainScheduled = true;
        Post(null, DrainFrozenQueues);
    }

    private void DrainFrozenQueues()
    {
        _drainScheduled = false;
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            if (_pointerFrozenBy is null && _pointerQueue.TryDequeue(out Action? pointer))
            {
                pointer();
                progressed = true;
            }
            if (_keyboardFrozenBy is null && _keyboardQueue.TryDequeue(out Action? keyboard))
            {
                keyboard();
                progressed = true;
            }
        }
    }

    // ------------------------------------------------------------------ 冻结与解冻

    private void FreezePointer(ActiveGrab grab) => _pointerFrozenBy = grab;

    private void FreezeKeyboard(ActiveGrab grab) => _keyboardFrozenBy = grab;

    private void ThawPointer()
    {
        _pointerFrozenBy = null;
        _pointerReplay = null;
        ScheduleDrain();
    }

    private void ThawKeyboard()
    {
        _keyboardFrozenBy = null;
        _keyboardReplay = null;
        ScheduleDrain();
    }

    /// <summary>抓取解除:它冻住的设备解冻,Sync 放行的等待作废。</summary>
    private void ThawGrab(ActiveGrab grab)
    {
        if (ReferenceEquals(_pointerFrozenBy, grab))
        {
            ThawPointer();
        }
        if (ReferenceEquals(_keyboardFrozenBy, grab))
        {
            ThawKeyboard();
        }
        if (ReferenceEquals(_pointerSyncOnce, grab))
        {
            _pointerSyncOnce = null;
        }
        if (ReferenceEquals(_keyboardSyncOnce, grab))
        {
            _keyboardSyncOnce = null;
        }
    }

    /// <summary>按抓取的模式冻结(主动抓取建立时、被动抓取激活并把事件投递出去之后)。</summary>
    private void ApplyGrabModes(ActiveGrab grab, bool pointerSync, bool keyboardSync)
    {
        if (pointerSync)
        {
            FreezePointer(grab);
        }
        if (keyboardSync)
        {
            FreezeKeyboard(grab);
        }
    }

    /// <summary>
    /// 一个按钮事件刚报给了指针抓取方:SyncPointer / SyncBoth 放行到此为止,重新冻结;按下还记下来供 ReplayPointer 重放。
    /// </summary>
    private void NotePointerEventReported(int button, bool pressed)
    {
        if (_pointerSyncOnce is { } grab && ReferenceEquals(grab, PointerGrab))
        {
            _pointerSyncOnce = null;
            FreezePointer(grab);
            if (pressed)
            {
                _pointerReplay = (button, grab.Window);
            }
            if (_syncBoth)
            {
                _syncBoth = false;
                _keyboardSyncOnce = null;
                FreezeKeyboard(grab);
            }
        }
    }

    /// <summary>一个按键事件刚报给了键盘抓取方:同上。</summary>
    private void NoteKeyboardEventReported(byte keycode, bool pressed)
    {
        if (_keyboardSyncOnce is { } grab && ReferenceEquals(grab, KeyboardGrab))
        {
            _keyboardSyncOnce = null;
            FreezeKeyboard(grab);
            if (pressed)
            {
                _keyboardReplay = (keycode, grab.Window);
            }
            if (_syncBoth)
            {
                _syncBoth = false;
                _pointerSyncOnce = null;
                FreezePointer(grab);
            }
        }
    }

    // ------------------------------------------------------------------ AllowEvents

    private void AllowEvents(XClient c, byte mode)
    {
        bool pointerMine = _pointerFrozenBy is { } pf && ReferenceEquals(pf.Client, c);
        bool keyboardMine = _keyboardFrozenBy is { } kf && ReferenceEquals(kf.Client, c);
        switch (mode)
        {
            case 0:   // AsyncPointer
                if (pointerMine)
                {
                    ThawPointer();
                }
                break;
            case 1:   // SyncPointer
                if (pointerMine && PointerGrab is { } pg && ReferenceEquals(pg.Client, c))
                {
                    _pointerSyncOnce = pg;
                    ThawPointer();
                }
                break;
            case 2:   // ReplayPointer
                if (pointerMine && PointerGrab is { } rg && ReferenceEquals(rg.Client, c) && _pointerReplay is { } replay)
                {
                    ReplayPointer(replay.Button, replay.GrabWindow);
                }
                break;
            case 3:   // AsyncKeyboard
                if (keyboardMine)
                {
                    ThawKeyboard();
                }
                break;
            case 4:   // SyncKeyboard
                if (keyboardMine && KeyboardGrab is { } kg && ReferenceEquals(kg.Client, c))
                {
                    _keyboardSyncOnce = kg;
                    ThawKeyboard();
                }
                break;
            case 5:   // ReplayKeyboard
                if (keyboardMine && KeyboardGrab is { } rk && ReferenceEquals(rk.Client, c) && _keyboardReplay is { } kr)
                {
                    ReplayKeyboard(kr.Keycode, kr.GrabWindow);
                }
                break;
            case 6:   // AsyncBoth:两个设备都被这个客户端冻着时才生效
                if (pointerMine && keyboardMine)
                {
                    ThawPointer();
                    ThawKeyboard();
                }
                break;
            case 7:   // SyncBoth
                if (pointerMine && keyboardMine)
                {
                    _pointerSyncOnce = _pointerFrozenBy;
                    _keyboardSyncOnce = _keyboardFrozenBy;
                    _syncBoth = true;
                    ThawPointer();
                    ThawKeyboard();
                }
                break;
            default:
                throw new XProtocolError(XErrorCode.Value, mode);
        }
    }

    /// <summary>ReplayPointer:解除指针抓取,把那次按钮按下重新处理 —— 这次不看抓取窗口及其以上的被动抓取。</summary>
    private void ReplayPointer(int button, XWindow grabWindow)
    {
        _pointerReplay = null;
        _pointerFrozenBy = null;
        PointerGrab = null;
        UpdateCursor();
        PressButton(button, ignoreGrabsThrough: grabWindow, replay: true);
        ScheduleDrain();
    }

    /// <summary>ReplayKeyboard:解除键盘抓取,把那次按键按下重新处理 —— 这次不看抓取窗口及其以上的被动抓取。</summary>
    private void ReplayKeyboard(byte keycode, XWindow grabWindow)
    {
        _keyboardReplay = null;
        _keyboardFrozenBy = null;
        KeyboardGrab = null;
        PressKey(keycode, ignoreGrabsThrough: grabWindow, replay: true);
        ScheduleDrain();
    }

    /// <summary>XIAllowEvents 的模式换成核心 AllowEvents 的(按请求里的设备是指针还是键盘)。</summary>
    private static byte CoreAllowMode(byte xiMode, bool pointer) => (xiMode, pointer) switch
    {
        (0, true) => 0,   // AsyncDevice
        (0, false) => 3,
        (1, true) => 1,   // SyncDevice
        (1, false) => 4,
        (2, true) => 2,   // ReplayDevice
        (2, false) => 5,
        (3, true) => 3,   // AsyncPairedDevice:放行配对的那个设备
        (3, false) => 0,
        (4, _) => 6,      // AsyncPair
        (5, _) => 7,      // SyncPair
        _ => byte.MaxValue,
    };
}
