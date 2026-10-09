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

    /// <summary>进资源表时记在 <see cref="Owner" /> 名下的字节数(见 <c>X11Server.ChargeMemory</c>);离开资源表时如数退还。</summary>
    public long Charged { get; set; }
}

/// <summary>像素图:一块离屏帧缓冲。</summary>
/// <remarks>
/// 通常自己新建一块缓冲;Composite 的 NameWindowPixmap 与 DOUBLE-BUFFER 的后缓冲则包住一块已有的缓冲。
/// </remarks>
internal sealed class XPixmap(uint id, XClient? owner, PixelBuffer buffer) : XResource(id, owner)
{
    public XPixmap(uint id, XClient? owner, int width, int height, byte depth)
        : this(id, owner, new PixelBuffer(width, height, depth)) => OwnsBuffer = true;

    /// <summary>缓冲是它自己的(而不是包住顶层窗口或后缓冲的那一块):只有这样才按像素记账。</summary>
    public bool OwnsBuffer { get; }

    public PixelBuffer Buffer { get; } = buffer;

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
/// 位图光标(核心 CreateCursor)、字形光标(CreateGlyphCursor)与 ARGB 光标(RENDER CreateCursor)在创建时把图像烙成
/// <see cref="Image" />;来自 cursor 字体的光标另记下字形号(<see cref="Glyph" />)。宿主看到的 <see cref="XCursor" /> 由这些推出,见 X11Server.Cursors.cs。
/// </remarks>
internal sealed class XCursorResource(uint id, XClient? owner) : XResource(id, owner)
{
    /// <summary>cursor 字体的字形号(如 68 = left_ptr、152 = xterm);别的光标为 -1。</summary>
    public int Glyph { get; set; } = -1;

    /// <summary>光标的图像(预乘的 ARGB 与热点);隐形的、太大的为 null。</summary>
    public XCursorImage? Image { get; set; }

    /// <summary>一个像素也不显示的光标(xterm 拿 nil2 字体的空白字形做的隐形指针):交给宿主时是 Hidden。</summary>
    public bool Blank { get; set; }

    /// <summary>客户端经 XFIXES SetCursorName 起的名字(光标主题里的名字,如 <c>text</c>、<c>pointer</c>);没起为 null。</summary>
    public string? Name { get; set; }

    /// <summary>交给宿主的样子(第一次用到时推出,改名、被 XFIXES ChangeCursor 换掉样子后作废)。</summary>
    public XCursor? Appearance { get; set; }
}

/// <summary>客户端打开的一个字体(同一份字体数据可以被多次打开)。</summary>
internal sealed class XFontResource(uint id, XClient? owner, XFont font) : XResource(id, owner)
{
    public XFont Font { get; } = font;
}
