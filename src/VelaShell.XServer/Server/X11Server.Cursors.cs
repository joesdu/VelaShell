// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateCursor」(source / mask 是深度 1 的像素图,mask 与 source 同尺寸;
//   mask 为 None 时整个 source 都显示;source 为 1 的像素用前景色、0 用背景色;热点必须落在 source 里,否则 BadMatch)、
//   「CreateGlyphCursor」「FreeCursor」「QueryBestSize」;Xlib 附录 B「X Font Cursors」(cursor 字体的字形号与名字)
//   The X Rendering Extension, Version 0.11 —— 「CreateCursor」(ARGB 光标取 picture 的像素,预乘 alpha)
//   X Fixes Extension —— §7「Cursor Names」(SetCursorName / GetCursorName)
//   CSS Basic User Interface Module Level 4 —— §5.1「cursor」的关键字(光标主题按它们给光标起名)
//
//   光标怎么交给宿主:cursor 字体的光标按字形号推出语义形状;位图与 ARGB 光标在创建时烙好图像,
//   形状按客户端经 XFIXES 起的名字推出(libXcursor 从主题加载光标后会这样命名)。宿主优先显示图像,没有就按形状选系统光标。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>光标图像的边长上限:再大的光标系统也显示不了,就不烙图像(按形状显示)。</summary>
    private const int MaxCursorImageSize = 256;

    /// <summary>上一次告诉宿主的(指针所在的顶层, 光标)。</summary>
    private (XTopLevelWindow? Window, XCursor Cursor)? _reportedCursor;

    // ------------------------------------------------------------------ 请求

    private void CreateCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        uint sourceId = r.U32();
        uint maskId = r.U32();
        XPixmap source = Lookup<XPixmap>(sourceId) ?? throw new XProtocolError(XErrorCode.Pixmap, sourceId);
        XPixmap? mask = maskId == 0 ? null : Lookup<XPixmap>(maskId) ?? throw new XProtocolError(XErrorCode.Pixmap, maskId);
        if (source.Depth != 1 || mask is { Depth: not 1 }
            || (mask is not null && (mask.Buffer.Width != source.Buffer.Width || mask.Buffer.Height != source.Buffer.Height)))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        ushort fr = r.U16(), fg = r.U16(), fb = r.U16(), br = r.U16(), bg = r.U16(), bb = r.U16();
        short x = r.I16(), y = r.I16();
        if (!source.Buffer.Bounds.Contains(x, y))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        AddResource(c, new XCursorResource(id, c)
        {
            Image = BitmapCursorImage(source.Buffer, mask?.Buffer, PixelOf(fr, fg, fb), PixelOf(br, bg, bb), x, y),
        });
    }

    private void CreateGlyphCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        uint sourceFont = r.U32();
        _ = r.U32();                        // mask-font:形状由宿主的系统光标给出,掩码用不上
        ushort sourceChar = r.U16();
        _ = r.U16();
        r.Skip(12);                         // 前景、背景色:系统光标自带配色
        XFontResource font = Lookup<XFontResource>(sourceFont) ?? throw new XProtocolError(XErrorCode.Font, sourceFont);
        AddResource(c, new XCursorResource(id, c) { Glyph = font.Font.Name == "cursor" ? sourceChar : -1 });
    }

    private void FreeCursor(XRequestReader r)
    {
        uint id = r.U32();
        _ = Lookup<XCursorResource>(id) ?? throw new XProtocolError(XErrorCode.Cursor, id);
        RemoveResource(id);
    }

    private static void QueryBestSize(XClient c, XRequestReader r)
    {
        byte cls = r.Data;
        r.U32();
        ushort width = r.U16(), height = r.U16();
        if (cls == 0)
        {
            // Cursor:系统光标一般 32×32,再大宿主也画不出来。
            (width, height) = (Math.Min(width, (ushort)64), Math.Min(height, (ushort)64));
        }
        c.Reply(0, w => w.U16(width).U16(height).Zero(20));
    }

    /// <summary>RENDER CreateCursor:取 picture 的像素烙成 ARGB 图像。</summary>
    private void RenderCreateCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        XPicture src = Picture(r.U32());
        if (src.Drawable is null)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        int x = r.U16(), y = r.U16();
        AddResource(c, new XCursorResource(id, c) { Image = PictureCursorImage(src, x, y) });
    }

    /// <summary>RENDER CreateAnimCursor:动画只显示第一帧。</summary>
    private void CreateAnimCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        XCursorResource? first = null;
        while (r.Remaining >= 8)
        {
            uint cursor = r.U32();
            _ = r.U32();   // 帧间隔
            first ??= Lookup<XCursorResource>(cursor) ?? throw new XProtocolError(XErrorCode.Cursor, cursor);
        }
        AddResource(c, new XCursorResource(id, c) { Glyph = first?.Glyph ?? -1, Image = first?.Image, Name = first?.Name });
    }

    /// <summary>XFIXES SetCursorName:记下名字(宿主据此推出光标形状);正显示着这个光标时通知宿主。</summary>
    private void SetCursorName(XCursorResource cursor, string name)
    {
        cursor.Name = name;
        cursor.Appearance = null;
        UpdateCursor();
    }

    // ------------------------------------------------------------------ 光标图像

    /// <summary>核心 CreateCursor 的位图光标:mask 为 1(或没有 mask)的像素按 source 取前景 / 背景色,其余透明。</summary>
    private static XCursorImage? BitmapCursorImage(PixelBuffer source, PixelBuffer? mask, uint foreground, uint background, int hotX, int hotY)
    {
        if (source.Width > MaxCursorImageSize || source.Height > MaxCursorImageSize)
        {
            return null;
        }
        uint[] pixels = new uint[source.Width * source.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (mask is null || mask.Pixels[i] != 0)
            {
                pixels[i] = 0xFF000000 | (source.Pixels[i] != 0 ? foreground : background);
            }
        }
        return new XCursorImage(source.Width, source.Height, hotX, hotY, pixels);
    }

    /// <summary>RENDER 的 ARGB 光标:picture 所在像素图的像素按它的格式换成预乘的 0xAARRGGBB。窗口上的 picture 不烙图像。</summary>
    private static XCursorImage? PictureCursorImage(XPicture picture, int hotX, int hotY)
    {
        if (picture is not { Drawable: XPixmap { Buffer: var buffer }, Format: { } format }
            || buffer.Width > MaxCursorImageSize || buffer.Height > MaxCursorImageSize)
        {
            return null;
        }
        uint[] pixels = new uint[buffer.Width * buffer.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = PictFormat.A8R8G8B8.Encode(format.Decode(buffer.Pixels[i]));
        }
        return new XCursorImage(buffer.Width, buffer.Height, hotX, hotY, pixels);
    }

    // ------------------------------------------------------------------ 交给宿主

    /// <summary>指针当前处该显示的光标资源(抓取的光标优先,否则从指针所在窗口向上找第一个设了光标的窗口)。</summary>
    private XCursorResource? CurrentCursor()
    {
        XCursorResource? cursor = PointerGrab?.Cursor;
        for (XWindow? w = _pointerWindow; cursor is null && w is not null; w = w.Parent)
        {
            cursor = w.Cursor;
        }
        return cursor;
    }

    /// <summary>光标或指针所在的顶层变了就告诉宿主;光标本身变了时也给 XFIXES 的登记者发 CursorNotify。</summary>
    private void UpdateCursor()
    {
        XCursor cursor = CursorHiddenAt(_pointerWindow) ? XCursor.Hidden
            : CurrentCursor() is { } resource ? AppearanceOf(resource)
            : XCursor.Default;
        XTopLevelWindow? handle = _pointerWindow.TopLevel is { } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? h) ? h : null;
        if (_reportedCursor is { } last && ReferenceEquals(last.Window, handle) && last.Cursor == cursor)
        {
            return;
        }
        if (_reportedCursor?.Cursor != cursor)
        {
            NotifyCursorChange();
        }
        _reportedCursor = (handle, cursor);
        _host.CursorChanged(handle, cursor);
    }

    private static XCursor AppearanceOf(XCursorResource cursor) =>
        cursor.Appearance ??= new XCursor(
            (cursor.Name is { } name ? ShapeOfName(name) : null) ?? ShapeOfGlyph(cursor.Glyph),
            cursor.Image);

    /// <summary>cursor 字体的字形号 → 形状(Xlib 附录 B;没有对应的给箭头)。</summary>
    private static XCursorShape ShapeOfGlyph(int glyph) => glyph switch
    {
        0 or 24 or 88 => XCursorShape.NotAllowed,              // X_cursor、circle、pirate
        12 => XCursorShape.ResizeSouthWest,                     // bottom_left_corner
        14 => XCursorShape.ResizeSouthEast,                     // bottom_right_corner
        16 => XCursorShape.ResizeSouth,                         // bottom_side
        30 or 34 or 90 or 130 => XCursorShape.Crosshair,        // cross、crosshair、plus、tcross
        52 or 120 => XCursorShape.Move,                         // fleur、sizing
        58 or 60 => XCursorShape.Hand,                          // hand1、hand2
        70 => XCursorShape.ResizeWest,                          // left_side
        92 => XCursorShape.Help,                                // question_arrow
        96 => XCursorShape.ResizeEast,                          // right_side
        108 => XCursorShape.ResizeEastWest,                     // sb_h_double_arrow
        116 => XCursorShape.ResizeNorthSouth,                   // sb_v_double_arrow
        134 => XCursorShape.ResizeNorthWest,                    // top_left_corner
        136 => XCursorShape.ResizeNorthEast,                    // top_right_corner
        138 => XCursorShape.ResizeNorth,                        // top_side
        150 => XCursorShape.Wait,                               // watch
        152 => XCursorShape.Text,                               // xterm
        _ => XCursorShape.Arrow,
    };

    /// <summary>光标名 → 形状:cursor 字体的字形名(Xlib 附录 B)与 CSS 的 cursor 关键字;不认识的为 null。</summary>
    private static XCursorShape? ShapeOfName(string name) => name switch
    {
        "left_ptr" or "arrow" or "top_left_arrow" or "default" or "context-menu" => XCursorShape.Arrow,
        "none" => XCursorShape.Hidden,
        "xterm" or "text" or "vertical-text" => XCursorShape.Text,
        "watch" or "wait" => XCursorShape.Wait,
        "progress" => XCursorShape.Progress,
        "question_arrow" or "help" => XCursorShape.Help,
        "hand1" or "hand2" or "pointer" or "grab" => XCursorShape.Hand,
        "cross" or "crosshair" or "plus" or "tcross" or "cell" => XCursorShape.Crosshair,
        "fleur" or "sizing" or "move" or "all-scroll" or "grabbing" => XCursorShape.Move,
        "X_cursor" or "circle" or "pirate" or "not-allowed" or "no-drop" => XCursorShape.NotAllowed,
        "top_side" or "n-resize" => XCursorShape.ResizeNorth,
        "bottom_side" or "s-resize" => XCursorShape.ResizeSouth,
        "right_side" or "e-resize" => XCursorShape.ResizeEast,
        "left_side" or "w-resize" => XCursorShape.ResizeWest,
        "top_left_corner" or "nw-resize" or "nwse-resize" => XCursorShape.ResizeNorthWest,
        "top_right_corner" or "ne-resize" or "nesw-resize" => XCursorShape.ResizeNorthEast,
        "bottom_left_corner" or "sw-resize" => XCursorShape.ResizeSouthWest,
        "bottom_right_corner" or "se-resize" => XCursorShape.ResizeSouthEast,
        "sb_v_double_arrow" or "ns-resize" or "row-resize" => XCursorShape.ResizeNorthSouth,
        "sb_h_double_arrow" or "ew-resize" or "col-resize" => XCursorShape.ResizeEastWest,
        "copy" => XCursorShape.DragCopy,
        "alias" => XCursorShape.DragLink,
        _ => null,
    };
}
