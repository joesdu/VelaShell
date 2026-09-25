// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The Present Extension, Version 1.2 —— SelectInput(事件上下文 eid:窗口与事件掩码)

using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Resources;

/// <summary>Present 的事件上下文(SelectInput 的 eid)。</summary>
internal sealed class XPresentEventContext(uint id, XClient owner, XWindow window, uint mask) : XResource(id, owner)
{
    public XWindow Window { get; } = window;

    public uint Mask { get; set; } = mask;
}
