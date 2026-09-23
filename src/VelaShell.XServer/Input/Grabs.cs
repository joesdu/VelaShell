// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「GrabPointer」「GrabButton」「GrabKeyboard」「GrabKey」各节
//   (主动抓取、被动抓取的激活条件、owner-events 的含义、按钮全部松开时解除)

using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Input;

/// <summary>被动抓取(GrabButton / GrabKey 登记在窗口上的)。</summary>
/// <param name="Client">登记的客户端。</param>
/// <param name="Detail">按钮号或键码;0 = AnyButton / AnyKey。</param>
/// <param name="Modifiers">修饰键组合;0x8000 = AnyModifier。</param>
/// <param name="OwnerEvents">owner-events。</param>
/// <param name="EventMask">指针抓取的事件掩码(按键抓取不用)。</param>
/// <param name="ConfineTo">限制指针的窗口(不实现限制,只记录)。</param>
/// <param name="Cursor">抓取期间的光标。</param>
/// <param name="Xi2">XInput2 的被动抓取(XIPassiveGrabDevice):激活后事件以 XI2 格式投递。</param>
/// <param name="Xi2Mask">XI2 抓取的事件掩码(按 evtype)。</param>
internal sealed record PassiveGrab(
    XClient Client, int Detail, ushort Modifiers, bool OwnerEvents, uint EventMask, XWindow? ConfineTo, XCursor? Cursor,
    bool Xi2 = false, ulong Xi2Mask = 0)
{
    public bool Matches(int detail, ushort modifiers) =>
        (Detail == 0 || Detail == detail) && (Modifiers == 0x8000 || Modifiers == (modifiers & 0xFF));
}

/// <summary>一个生效中的抓取(指针或键盘)。</summary>
internal sealed class ActiveGrab
{
    public required XClient Client { get; init; }

    public required XWindow Window { get; init; }

    public bool OwnerEvents { get; init; }

    public uint EventMask { get; set; }

    public XCursor? Cursor { get; set; }

    /// <summary>XInput2 的抓取:事件以 XI2 格式、按 <see cref="Xi2Mask" /> 投递。</summary>
    public bool Xi2 { get; init; }

    public ulong Xi2Mask { get; set; }

    /// <summary>由按钮按下自动(或被动抓取)激活 —— 按钮全部松开时自动解除。</summary>
    public bool ReleaseWhenButtonsUp { get; init; }
}
