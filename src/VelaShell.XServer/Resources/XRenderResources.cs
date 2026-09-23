// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §7「Picture」(picture 的属性:repeat、alpha-map、clip-mask 与原点、
//   graphics-exposures、subwindow-mode、poly-edge / poly-mode、dither、component-alpha)、
//   §12「Glyphs」(GLYPHINFO:width、height、x、y、x-off、y-off;字形集按格式存放、可被多个 ID 引用)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>一个 picture:可绘对象上的(<see cref="Drawable" /> 非 null),或纯色 / 渐变(<see cref="Fill" /> 非 null)。</summary>
internal sealed class XPicture(uint id, XClient? owner) : XResource(id, owner)
{
    /// <summary>所在的像素图或窗口;纯色与渐变为 null。</summary>
    public XResource? Drawable { get; init; }

    /// <summary>像素格式;纯色与渐变为 null(它们只能当源)。</summary>
    public PictFormat? Format { get; init; }

    /// <summary>纯色与渐变的取样器。</summary>
    public RenderSource? Fill { get; init; }

    public byte Repeat { get; set; }

    public bool ComponentAlpha { get; set; }

    /// <summary>0 ClipByChildren,1 IncludeInferiors。</summary>
    public byte SubwindowMode { get; set; }

    /// <summary>裁剪区域(picture 坐标,未加裁剪原点);null = 不裁剪。</summary>
    public Region? Clip { get; set; }

    public int ClipX { get; set; }

    public int ClipY { get; set; }

    public double[]? Transform { get; set; }

    public bool Bilinear { get; set; }
}

/// <summary>
/// 一个字形:度量与位图。只有 alpha 的字形集(a8 / a4 / a1,Xft 的常态)存 <paramref name="Alpha" />,每像素一个字节;
/// 带颜色的(次像素渲染)存 <paramref name="Color" />,每像素一个预乘的 0xAARRGGBB。
/// </summary>
internal sealed record XRenderGlyph(int Width, int Height, int X, int Y, int XOff, int YOff, byte[]? Alpha, uint[]? Color);

/// <summary>字形集的内容;ReferenceGlyphSet 让多个 ID 共用同一份。</summary>
internal sealed class GlyphTable(PictFormat format)
{
    public PictFormat Format { get; } = format;

    public Dictionary<uint, XRenderGlyph> Glyphs { get; } = [];
}

/// <summary>一个字形集 ID。</summary>
internal sealed class XGlyphSet(uint id, XClient? owner, GlyphTable table) : XResource(id, owner)
{
    public GlyphTable Table { get; } = table;
}
