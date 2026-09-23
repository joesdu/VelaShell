// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 2 节「Syntactic Conventions」里的资源 ID 约定、
//   「CreatePixmap」「CreateColormap」「CreateCursor」「CreateGlyphCursor」「OpenFont」各节

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Fonts;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>所有带 ID 的服务端资源的基类。</summary>
/// <param name="id">资源 ID。</param>
/// <param name="owner">创建它的客户端;服务端自己的资源(根窗口、默认颜色表)为 null。</param>
internal abstract class XResource(uint id, XClient? owner)
{
    public uint Id { get; } = id;

    /// <summary>
    /// 创建者。客户端断开时它创建的资源一律释放(CloseDownMode = Destroy,默认)。
    /// </summary>
    public XClient? Owner { get; } = owner;
}

/// <summary>像素图:一块离屏帧缓冲。</summary>
internal sealed class XPixmap(uint id, XClient? owner, int width, int height, byte depth) : XResource(id, owner)
{
    public PixelBuffer Buffer { get; } = new(width, height, depth);

    public byte Depth => Buffer.Depth;

    public int Width => Buffer.Width;

    public int Height => Buffer.Height;
}

/// <summary>
/// 颜色表。只有 TrueColor 一种视觉(架构 §7),像素值就是 RGB —— 这里只记它属于哪个视觉。
/// </summary>
internal sealed class XColormap(uint id, XClient? owner, uint visual) : XResource(id, owner)
{
    public uint Visual { get; } = visual;
}

/// <summary>光标。</summary>
/// <remarks>
/// 来自 cursor 字体的光标记下字形号(<see cref="Glyph" />),宿主据此映射成系统光标;
/// 位图光标(CreateCursor)记下两张位图与热点,宿主可以自己合成。
/// </remarks>
internal sealed class XCursor(uint id, XClient? owner) : XResource(id, owner)
{
    /// <summary>cursor 字体的字形号(如 68 = left_ptr、152 = xterm);位图光标为 -1。</summary>
    public int Glyph { get; init; } = -1;

    public XPixmap? Source { get; init; }

    public XPixmap? Mask { get; init; }

    public int HotX { get; init; }

    public int HotY { get; init; }

    public uint ForegroundRgb { get; set; }

    public uint BackgroundRgb { get; set; } = 0xFFFFFF;
}

/// <summary>客户端打开的一个字体(同一份字体数据可以被多次打开)。</summary>
internal sealed class XFontResource(uint id, XClient? owner, XFont font) : XResource(id, owner)
{
    public XFont Font { get; } = font;
}
