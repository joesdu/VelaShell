// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The DAMAGE Extension, Version 1.1 —— §3「Data types」(DAMAGE 对象、四种报告级别)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>一个 DAMAGE 对象:累计的损伤区域与报告级别。</summary>
internal sealed class XDamage(uint id, XClient owner, XResource drawable, byte level) : XResource(id, owner)
{
    public const byte Raw = 0, Delta = 1, BoundingBox = 2, NonEmpty = 3;

    public XResource Drawable { get; } = drawable;

    public byte Level { get; } = level;

    /// <summary>累计损伤(可绘对象坐标)。</summary>
    public Region Accumulated { get; set; } = new();
}
