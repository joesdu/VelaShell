// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Fixes Extension, Version 5.0 —— §6「Region Objects」

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>XFIXES 的区域对象。</summary>
internal sealed class XRegionResource(uint id, XClient? owner, Region region) : XResource(id, owner)
{
    public Region Region { get; set; } = region;
}
