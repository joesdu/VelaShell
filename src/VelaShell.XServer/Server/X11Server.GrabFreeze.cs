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

    /// <summary>
    /// 冻着指针的那些抓取(空 = 没冻)。一个设备可以同时被指针抓取与键盘抓取冻着(协议「AllowEvents」:两个都放开才继续处理);
    /// 原先每个设备只记一个冻结者,后冻的把先冻的换掉,一个抓取解除就提前解冻了另一个本该冻着的设备。
    /// </summary>
    private readonly List<ActiveGrab> _pointerFreezers = [];

    /// <summary>冻着键盘的那些抓取。</summary>
    private readonly List<ActiveGrab> _keyboardFreezers = [];

    /// <summary>SyncPointer / SyncBoth 放行中:下一个按钮事件报给抓取方之后重新冻结(Both 时两个设备一起)。</summary>
    private ActiveGrab? _pointerSyncOnce;

    /// <summary>SyncKeyboard / SyncBoth 放行中:下一个按键事件报给抓取方之后重新冻结。</summary>
    private ActiveGrab? _keyboardSyncOnce;

    private bool _syncBoth;

    /// <summary>引起指针冻结、可被 ReplayPointer 重放的按钮按下:按钮号与当时的抓取窗口。</summary>
    private (int Button, XWindow GrabWindow, InputState Before)? _pointerReplay;

    /// <summary>引起键盘冻结、可被 ReplayKeyboard 重放的按键按下。</summary>
    private (byte Keycode, XWindow GrabWindow, InputState Before)? _keyboardReplay;

    /// <summary>排着的设备事件是哪一类:决定队列满了时能不能丢(见 <see cref="ProcessInput" />)。</summary>
    private enum InputKind : byte
    {
        /// <summary>指针移动、离开、按键的自动重复:不改变按键 / 按钮的状态,丢掉中间的一个只是少一个点。</summary>
        Droppable,

        /// <summary>按键 / 按钮按下(<c>Detail</c> 是键码或物理按钮号)。</summary>
        Press,

        /// <summary>按键 / 按钮松开。</summary>
        Release,
    }

    /// <summary>
    /// 冻结期间排着的设备事件,指针与键盘排在同一个队列里、按到达的先后 —— 两个设备都放行时按原来的相对顺序处理。
    /// 冻结是按设备的:某个设备冻着时,另一个设备排在它后面的事件照样可以先走。
    /// </summary>
    private readonly List<(bool Pointer, InputKind Kind, Action Input)> _frozenInput = [];

    /// <summary>
    /// 排着的事件上限。客户端抓着不放(一直不 AllowEvents)时宿主的输入一直进来:满了先丢最早的一个可丢的事件(移动之类);
    /// 没有可丢的,丢新来的按下,并记下它 —— 它的松开来时一并丢掉。松开一律留着(可以超出上限:能排进来的松开不会多于按着的键与按钮),
    /// 原先连松开也丢,解冻后键或按钮一直按着、自动抓取也不解除。
    /// </summary>
    internal const int MaxFrozenInput = 4096;

    /// <summary>队列满时丢掉的按下:(是不是指针, 键码或物理按钮号)。它们的松开来时同样丢掉。</summary>
    private readonly HashSet<(bool Pointer, int Detail)> _droppedPresses = [];

    /// <summary>放行之后一个工作项最多回放这么多个事件,余下的排到下一个工作项 —— 中间客户端的请求(下一个 AllowEvents)能插进来。</summary>
    private const int DrainBatch = 64;

    private bool _drainScheduled;

    /// <summary>排着的事件数(测试用)。</summary>
    internal int FrozenInputCount => _frozenInput.Count;

    /// <summary>
    /// 当前的指针抓取。换掉(解除或被别的抓取取代)时,它冻结的设备随之解冻。抓取激活 / 解除时按协议「Pointer Window events」
    /// 发 mode 为 Grab / Ungrab 的 Enter / Leave:「就像指针从所在的窗口 P 瞬移到抓取窗口 G」,解除时反过来(指针并没有动)。
    /// 自动抓取同样适用:激活时的那一组由 PressButton 在投递 ButtonPress 之前发(这里不重复发);解除时在 ButtonRelease 之后照常发 ——
    /// 在 A 里按下、拖到另一个客户端的窗口 B 松开,B 这才知道指针在它里面(原先自动抓取一律不发,B 的悬停高亮要移出再移入才恢复)。
    /// 抓取窗口已经销毁了不发。
    /// </summary>
    private ActiveGrab? PointerGrab
    {
        get => _pointerGrab;
        set
        {
            ActiveGrab? old = _pointerGrab;
            _pointerGrab = value;
            if (ReferenceEquals(old, value))
            {
                return;
            }
            if (old is not null)
            {
                ThawGrab(old);
                if (ReferenceEquals(Lookup<XWindow>(old.Window.Id), old.Window))
                {
                    GenerateCrossing(old.Window, _pointerWindow, CrossingModeUngrab);
                }
            }
            if (value is not null)
            {
                _lastPointerGrabTime = value.Time = value.Time != 0 ? value.Time : Math.Max(1u, Now);
            }
            if (value is { Automatic: false })
            {
                GenerateCrossing(_pointerWindow, value.Window, CrossingModeGrab);
            }
            ReportConfinement(value?.ConfineTo);
        }
    }

    /// <summary>上次告诉宿主的 confine-to 范围(根坐标);null = 没有。</summary>
    private XRect? _reportedConfinement;

    /// <summary>指针抓取的 confine-to 变了:把那个窗口的内区(根坐标,夹在根窗口里)告诉宿主,解除时报 null(<see cref="IX11ServerHost.PointerConfinementChanged" />)。</summary>
    private void ReportConfinement(XWindow? confineTo)
    {
        XRect? area = null;
        if (confineTo is not null && IsLiveWindow(confineTo))
        {
            (int x, int y) = confineTo.AbsoluteInner();
            XRect inner = new XRect(x, y, Math.Max(1, confineTo.Width), Math.Max(1, confineTo.Height)).Intersect(new XRect(0, 0, Root.Width, Root.Height));
            area = inner.IsEmpty ? null : inner;
        }
        if (area != _reportedConfinement)
        {
            _reportedConfinement = area;
            _host.PointerConfinementChanged(area);
        }
    }

    /// <summary>协议的 last-pointer-grab time:早于它的 GrabPointer 回 InvalidTime,早于它的 UngrabPointer 不生效。</summary>
    private uint _lastPointerGrabTime;

    /// <summary>协议的 last-keyboard-grab time。</summary>
    private uint _lastKeyboardGrabTime;

    /// <summary>
    /// 请求里的时间戳(协议「GrabPointer」「UngrabPointer」「AllowEvents」……):CurrentTime(0)换成当前服务端时间;早于
    /// <paramref name="last" /> 或晚于当前服务端时间时返回 false(按 32 位回绕比较;<paramref name="last" /> 为 0 表示还没有过)。
    /// 原先这些请求一律不看时间戳,从不回 InvalidTime。
    /// </summary>
    private bool TimeAcceptable(ref uint time, uint last)
    {
        uint now = Math.Max(1u, Now);
        if (time == 0)
        {
            time = now;
        }
        return (last == 0 || unchecked((int)(time - last)) >= 0) && unchecked((int)(time - now)) <= 0;
    }

    /// <summary>这个客户端最近生效的那个主动抓取的时间(AllowEvents 的时间戳与它比);没有抓取时为 0。</summary>
    private uint LastGrabTimeOf(XClient client)
    {
        uint last = 0;
        foreach (ActiveGrab? grab in (ReadOnlySpan<ActiveGrab?>)[_pointerGrab, _keyboardGrab])
        {
            if (grab is not null && ReferenceEquals(grab.Client, client) && (last == 0 || unchecked((int)(grab.Time - last)) > 0))
            {
                last = grab.Time;
            }
        }
        return last;
    }

    /// <summary>设备被别的客户端的抓取冻着(GrabPointer / GrabKeyboard / XIGrabDevice 回 Frozen)。</summary>
    private bool FrozenByOther(bool pointer, XClient client) =>
        (pointer ? _pointerFreezers : _keyboardFreezers).Exists(g => !ReferenceEquals(g.Client, client));

    /// <summary>
    /// 当前的键盘抓取。换掉时它冻结的设备随之解冻。抓取激活 / 解除时按协议「Input Focus events」发 mode 为 Grab / Ungrab 的
    /// 焦点事件:「就像焦点从当前焦点移到抓取窗口」,解除时反过来(抓取窗口已经销毁了就不发)。
    /// </summary>
    private ActiveGrab? KeyboardGrab
    {
        get => _keyboardGrab;
        set
        {
            ActiveGrab? old = _keyboardGrab;
            _keyboardGrab = value;
            if (ReferenceEquals(old, value))
            {
                return;
            }
            if (old is not null)
            {
                ThawGrab(old);
                if (ReferenceEquals(Lookup<XWindow>(old.Window.Id), old.Window))
                {
                    GenerateFocusEvents(old.Window, _focus, FocusModeUngrab);
                }
            }
            if (value is not null)
            {
                _lastKeyboardGrabTime = value.Time = value.Time != 0 ? value.Time : Math.Max(1u, Now);
                GenerateFocusEvents(_focus, value.Window, FocusModeGrab);
            }
        }
    }

    // ------------------------------------------------------------------ 入口:设备事件按冻结状态排队

    /// <summary>指针移动、离开(可丢):见 <see cref="ProcessInput" />。</summary>
    private void ProcessPointerMotion(Action input) => ProcessInput(pointer: true, InputKind.Droppable, 0, input);

    /// <summary>按钮按下 / 松开(<paramref name="button" /> 是物理按钮号):见 <see cref="ProcessInput" />。</summary>
    private void ProcessPointerButton(int button, bool pressed, Action input) =>
        ProcessInput(pointer: true, pressed ? InputKind.Press : InputKind.Release, button, input);

    /// <summary>按键按下 / 松开:见 <see cref="ProcessInput" />。</summary>
    private void ProcessKeyboardInput(byte keycode, bool pressed, Action input) =>
        ProcessInput(pointer: false, pressed ? InputKind.Press : InputKind.Release, keycode, input);

    private bool IsFrozen(bool pointer) => (pointer ? _pointerFreezers : _keyboardFreezers).Count != 0;

    /// <summary>
    /// 设备冻着、或者前面还排着这个设备的事件、或者排着已经可以走的事件(回放还没做完)时排队,否则立即处理 —— 保证先来的先处理。
    /// 队列满了时怎么丢见 <see cref="MaxFrozenInput" />。
    /// </summary>
    private void ProcessInput(bool pointer, InputKind kind, int detail, Action input)
    {
        if (kind == InputKind.Release ? _droppedPresses.Remove((pointer, detail)) : kind == InputKind.Droppable && detail != 0 && _droppedPresses.Contains((pointer, detail)))
        {
            return;   // 它的按下在队列满时丢掉了:X 这边从没按下过,松开与自动重复一并丢掉
        }
        if (kind == InputKind.Press)
        {
            _droppedPresses.Remove((pointer, detail));
        }
        bool frozen = IsFrozen(pointer);
        if (!frozen && !_frozenInput.Exists(e => e.Pointer == pointer || !IsFrozen(e.Pointer)))
        {
            input();
            return;
        }
        if (_frozenInput.Count >= MaxFrozenInput && kind != InputKind.Release)
        {
            int oldest = _frozenInput.FindIndex(e => e.Kind == InputKind.Droppable);
            if (oldest >= 0)
            {
                _frozenInput.RemoveAt(oldest);   // 移动带的是绝对位置,丢掉中间的一个只是轨迹少一个点
            }
            else if (kind == InputKind.Press)
            {
                _droppedPresses.Add((pointer, detail));
                return;
            }
            else
            {
                return;   // 没有可丢的,新来的也是可丢的
            }
        }
        _frozenInput.Add((pointer, kind, input));
        if (!frozen)
        {
            ScheduleDrain();
        }
    }

    /// <summary>解冻之后排着的事件在执行线程的下一个工作项里处理 —— 不在当前事件处理的半途插进去。</summary>
    private void ScheduleDrain()
    {
        if (_drainScheduled || !_frozenInput.Exists(e => !IsFrozen(e.Pointer)))
        {
            return;
        }
        _drainScheduled = true;
        Post(null, DrainFrozenQueues);
    }

    /// <summary>按到达的先后回放没冻着的设备的事件;回放的事件可能又把设备冻上(Sync 放行)。一次最多 <see cref="DrainBatch" /> 个。</summary>
    private void DrainFrozenQueues()
    {
        _drainScheduled = false;
        for (int budget = DrainBatch; budget > 0; budget--)
        {
            int index = _frozenInput.FindIndex(e => !IsFrozen(e.Pointer));
            if (index < 0)
            {
                return;
            }
            Action input = _frozenInput[index].Input;
            _frozenInput.RemoveAt(index);
            input();
        }
        ScheduleDrain();
    }

    // ------------------------------------------------------------------ 冻结与解冻

    private void FreezePointer(ActiveGrab grab)
    {
        if (!_pointerFreezers.Contains(grab))
        {
            _pointerFreezers.Add(grab);
        }
    }

    private void FreezeKeyboard(ActiveGrab grab)
    {
        if (!_keyboardFreezers.Contains(grab))
        {
            _keyboardFreezers.Add(grab);
        }
    }

    /// <summary>设备被这个客户端(的某个抓取)冻着。</summary>
    private bool FrozenBy(bool pointer, XClient client) =>
        (pointer ? _pointerFreezers : _keyboardFreezers).Exists(g => ReferenceEquals(g.Client, client));

    /// <summary>
    /// 去掉冻着这个设备的那些抓取(<paramref name="client" /> 为 null 时全部,否则只去掉这个客户端的 —— 协议:同一个客户端冻了两次,
    /// 一个 AllowEvents 全放开);一个都不剩了才真的解冻。
    /// </summary>
    private void ThawPointer(XClient? client = null) =>
        Thaw(_pointerFreezers, g => client is null || ReferenceEquals(g.Client, client), pointer: true);

    private void ThawKeyboard(XClient? client = null) =>
        Thaw(_keyboardFreezers, g => client is null || ReferenceEquals(g.Client, client), pointer: false);

    private void Thaw(List<ActiveGrab> freezers, Predicate<ActiveGrab> which, bool pointer)
    {
        if (freezers.RemoveAll(which) == 0 || freezers.Count != 0)
        {
            return;
        }
        if (pointer)
        {
            _pointerReplay = null;
        }
        else
        {
            _keyboardReplay = null;
        }
        ScheduleDrain();
    }

    /// <summary>抓取解除:它对两个设备的冻结都解除(别的抓取的冻结还在),Sync 放行的等待作废。</summary>
    private void ThawGrab(ActiveGrab grab)
    {
        Thaw(_pointerFreezers, g => ReferenceEquals(g, grab), pointer: true);
        Thaw(_keyboardFreezers, g => ReferenceEquals(g, grab), pointer: false);
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
                _pointerReplay = (button, grab.Window, CurrentInputState());
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
                _keyboardReplay = (keycode, grab.Window, CurrentInputState());
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

    /// <summary>
    /// AllowEvents / XIAllowEvents。时间戳早于这个客户端最近生效的主动抓取的时间、或晚于当前服务端时间时什么也不做(协议「AllowEvents」)。
    /// </summary>
    private void AllowEvents(XClient c, byte mode, uint time)
    {
        if (mode > 7)
        {
            throw new XProtocolError(XErrorCode.Value, mode);
        }
        if (!TimeAcceptable(ref time, LastGrabTimeOf(c)))
        {
            return;
        }
        bool pointerMine = FrozenBy(pointer: true, c);
        bool keyboardMine = FrozenBy(pointer: false, c);
        switch (mode)
        {
            case 0:   // AsyncPointer
                if (pointerMine)
                {
                    ThawPointer(c);
                }
                break;
            case 1:   // SyncPointer
                if (pointerMine && PointerGrab is { } pg && ReferenceEquals(pg.Client, c))
                {
                    _pointerSyncOnce = pg;
                    ThawPointer(c);
                }
                break;
            case 2:   // ReplayPointer
                if (pointerMine && PointerGrab is { } rg && ReferenceEquals(rg.Client, c) && _pointerReplay is { } replay)
                {
                    ReplayPointer(replay.Button, replay.GrabWindow, replay.Before);
                }
                break;
            case 3:   // AsyncKeyboard
                if (keyboardMine)
                {
                    ThawKeyboard(c);
                }
                break;
            case 4:   // SyncKeyboard
                if (keyboardMine && KeyboardGrab is { } kg && ReferenceEquals(kg.Client, c))
                {
                    _keyboardSyncOnce = kg;
                    ThawKeyboard(c);
                }
                break;
            case 5:   // ReplayKeyboard
                if (keyboardMine && KeyboardGrab is { } rk && ReferenceEquals(rk.Client, c) && _keyboardReplay is { } kr)
                {
                    ReplayKeyboard(kr.Keycode, kr.GrabWindow, kr.Before);
                }
                break;
            case 6:   // AsyncBoth:两个设备都被这个客户端冻着时才生效
                if (pointerMine && keyboardMine)
                {
                    ThawPointer(c);
                    ThawKeyboard(c);
                }
                break;
            case 7:   // SyncBoth:放行到这个客户端的抓取收到下一个按钮 / 按键事件为止,那时两个设备一起再冻上(各冻一次)
                if (pointerMine && keyboardMine)
                {
                    _pointerSyncOnce = PointerGrab is { } sp && ReferenceEquals(sp.Client, c) ? sp : null;
                    _keyboardSyncOnce = KeyboardGrab is { } sk && ReferenceEquals(sk.Client, c) ? sk : null;
                    _syncBoth = true;
                    ThawPointer(c);
                    ThawKeyboard(c);
                }
                break;
            default:
                throw new XProtocolError(XErrorCode.Value, mode);
        }
    }

    /// <summary>
    /// 一个设备事件之前的状态:修饰(base / latched / locked / 生效)与核心的按钮位。重放的事件按它报 ——
    /// 协议:事件里的 state 是事件之前的;原先重放时这次按下的按钮位(以及按下的修饰键自己的位)已经在 state 里了。
    /// </summary>
    private readonly record struct InputState(byte Base, byte Latched, byte Locked, ushort Modifiers, ushort Buttons);

    /// <summary>此刻的状态。在记下可重放的按下时调:那时按钮位与修饰状态都还没随这次按下更新。</summary>
    private InputState CurrentInputState() => new(_baseMods, _latchedMods, _lockedMods, _modifiers, _buttons);

    /// <summary>
    /// 换上 <paramref name="state" /> 跑 <paramref name="replay" />(<paramref name="button" /> 不为 0 时这个按钮在 XI2 的按钮掩码里也先去掉),
    /// 跑完换回现在的状态。重放不改这些状态(PressButton / PressKey 在 replay 时只投递)。
    /// </summary>
    private void WithInputState(InputState state, int button, Action replay)
    {
        InputState now = CurrentInputState();
        bool buttonDown = button != 0 && (_buttonsDown[button >> 3] & (1 << (button & 7))) != 0;
        (_baseMods, _latchedMods, _lockedMods, _modifiers, _buttons) = state;
        if (buttonDown)
        {
            _buttonsDown[button >> 3] &= (byte)~(1 << (button & 7));
        }
        try
        {
            replay();
        }
        finally
        {
            (_baseMods, _latchedMods, _lockedMods, _modifiers, _buttons) = now;
            if (buttonDown)
            {
                _buttonsDown[button >> 3] |= (byte)(1 << (button & 7));
            }
        }
    }

    /// <summary>ReplayPointer:解除指针抓取,把那次按钮按下按当时的状态重新处理 —— 这次不看抓取窗口及其以上的被动抓取。</summary>
    private void ReplayPointer(int button, XWindow grabWindow, InputState before)
    {
        _pointerReplay = null;
        XClient? owner = PointerGrab?.Client;
        _pointerFreezers.RemoveAll(g => ReferenceEquals(g.Client, owner));
        PointerGrab = null;
        UpdateCursor();
        WithInputState(before, button, () => PressButton(button, ignoreGrabsThrough: grabWindow, replay: true));
        ScheduleDrain();
    }

    /// <summary>ReplayKeyboard:解除键盘抓取,把那次按键按下按当时的状态重新处理 —— 这次不看抓取窗口及其以上的被动抓取。</summary>
    private void ReplayKeyboard(byte keycode, XWindow grabWindow, InputState before)
    {
        _keyboardReplay = null;
        XClient? owner = KeyboardGrab?.Client;
        _keyboardFreezers.RemoveAll(g => ReferenceEquals(g.Client, owner));
        KeyboardGrab = null;
        WithInputState(before, 0, () => PressKey(keycode, ignoreGrabsThrough: grabWindow, replay: true));
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

    /// <summary>见 <see cref="BreakGrabs" />。</summary>
    private void ApplyBreakGrabs()
    {
        // 抓取的 setter 负责解冻(排着的事件随之回放)与 Ungrab 模式的 crossing / 焦点事件。
        PointerGrab = null;
        KeyboardGrab = null;
        UpdateCursor();
        if (_serverGrabber is not null)
        {
            ReleaseServerGrab();
        }
        ReattachFloatingSlaves();
    }
}
