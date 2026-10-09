// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateGlyphCursor」(源与掩码字形的原点重合、即热点;没有掩码时整个源字形都显示;
//   字形没定义回 BadValue)、「CreateCursor」(source / mask 是深度 1 的像素图,mask 与 source 同尺寸;
//   mask 为 None 时整个 source 都显示;source 为 1 的像素用前景色、0 用背景色;热点必须落在 source 里,否则 BadMatch)、
//   「FreeCursor」「QueryBestSize」;Xlib 附录 B「X Font Cursors」(cursor 字体的字形号与名字)
//   The X Rendering Extension, Version 0.11 —— 「CreateCursor」(ARGB 光标取 picture 的像素,预乘 alpha)
//   X Fixes Extension —— §7「Cursor Names」(SetCursorName / GetCursorName)
//   CSS Basic User Interface Module Level 4 —— §5.1「cursor」的关键字(光标主题按它们给光标起名)
//
//   光标怎么交给宿主:位图、字形(含 cursor 字体)与 ARGB 光标在创建时烙好图像;cursor 字体里有对应系统光标的字形按字形号推出
//   语义形状、不给宿主图像,其余形状按客户端经 XFIXES 起的名字推出(libXcursor 从主题加载光标后会这样命名)。
//   宿主优先显示图像,没有就按形状选系统光标。XFIXES GetCursorImage 一律给烙好的图像。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Fonts;
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
        XPixmap source = Use<XPixmap>(sourceId) ?? throw new XProtocolError(XErrorCode.Pixmap, sourceId);
        XPixmap? mask = maskId == 0 ? null : Use<XPixmap>(maskId) ?? throw new XProtocolError(XErrorCode.Pixmap, maskId);
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

    /// <summary>
    /// 协议「CreateGlyphCursor」:字形烙成图像 —— 两个字形的原点重合、就是热点,有掩码字形时只显示掩码为 1 的像素,
    /// 源为 1 用前景色、0 用背景色,没有掩码时整个源字形的方框都显示。什么也不显示的(xterm 拿 nil2 字体做的隐形指针)给 Hidden;
    /// 原先非 cursor 字体一律按默认箭头。cursor 字体(X.Org 的 cursor.bdf)的字形另记下字形号:有对应系统光标的按形状交给宿主
    /// (系统光标跟着桌面的主题与缩放),图像留给 XFIXES GetCursorImage;原先那份字体只有度量,截屏 / 录屏拿到的是 1×1 透明像素。
    /// 字形在字体里没定义回 BadValue。
    /// </summary>
    private void CreateGlyphCursor(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        uint sourceFont = r.U32();
        uint maskFont = r.U32();
        ushort sourceChar = r.U16();
        ushort maskChar = r.U16();
        ushort fr = r.U16(), fg = r.U16(), fb = r.U16(), br = r.U16(), bg = r.U16(), bb = r.U16();
        XFontResource font = Use<XFontResource>(sourceFont) ?? throw new XProtocolError(XErrorCode.Font, sourceFont);
        XFontResource? maskResource = maskFont == 0 ? null : Use<XFontResource>(maskFont) ?? throw new XProtocolError(XErrorCode.Font, maskFont);
        if (!font.Font.Glyphs.TryGetValue(sourceChar, out XGlyph? source))
        {
            throw new XProtocolError(XErrorCode.Value, sourceChar);
        }
        XGlyph? mask = null;
        if (maskResource is not null && !maskResource.Font.Glyphs.TryGetValue(maskChar, out mask))
        {
            throw new XProtocolError(XErrorCode.Value, maskChar);
        }
        (XCursorImage? image, bool blank) = GlyphCursorImage(source, mask, PixelOf(fr, fg, fb), PixelOf(br, bg, bb));
        AddResource(c, new XCursorResource(id, c)
        {
            Glyph = font.Font.Name == "cursor" ? sourceChar : -1,
            Image = image,
            Blank = blank,
        });
    }

    private void FreeCursor(XRequestReader r)
    {
        uint id = r.U32();
        _ = Use<XCursorResource>(id) ?? throw new XProtocolError(XErrorCode.Cursor, id);
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
        (int width, int height) = src.Drawable switch
        {
            XPixmap p => (p.Width, p.Height),
            XWindow w => (w.Width, w.Height),
            _ => (0, 0),
        };
        if (x >= width || y >= height)
        {
            throw new XProtocolError(XErrorCode.Match);   // 热点必须落在图里(RENDER 规范 CreateCursor)
        }
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
            first ??= Use<XCursorResource>(cursor) ?? throw new XProtocolError(XErrorCode.Cursor, cursor);
        }
        AddResource(c, new XCursorResource(id, c) { Glyph = first?.Glyph ?? -1, Image = first?.Image, Blank = first?.Blank ?? false, Name = first?.Name });
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

    /// <summary>
    /// 字形光标的图像:源与掩码字形的原点重合(热点),图像是两个字形方框的并集。返回的 blank 表示一个像素也不显示(隐形指针)。
    /// 太大时不烙图像(按形状显示)。
    /// </summary>
    private static (XCursorImage? Image, bool Blank) GlyphCursorImage(XGlyph source, XGlyph? mask, uint foreground, uint background)
    {
        XCharInfo s = source.Info, m = mask?.Info ?? s;
        int left = Math.Min(s.LeftBearing, m.LeftBearing), right = Math.Max(s.RightBearing, m.RightBearing);
        int ascent = Math.Max(s.Ascent, m.Ascent), descent = Math.Max(s.Descent, m.Descent);
        int width = right - left, height = ascent + descent;
        if (width <= 0 || height <= 0)
        {
            return (null, true);
        }
        if (width > MaxCursorImageSize || height > MaxCursorImageSize)
        {
            return (null, false);
        }
        uint[] pixels = new uint[width * height];
        bool any = false;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int gx = x + left, gy = y - ascent;   // 相对原点
                bool shown = mask is null ? InBox(source, gx, gy) : IsSet(mask, gx, gy);
                if (shown)
                {
                    pixels[(y * width) + x] = 0xFF000000 | (IsSet(source, gx, gy) ? foreground : background);
                    any = true;
                }
            }
        }
        return any
            ? (new XCursorImage(width, height, Math.Clamp(-left, 0, width - 1), Math.Clamp(ascent, 0, height - 1), pixels), false)
            : (null, true);

        static bool InBox(XGlyph g, int gx, int gy) =>
            gx >= g.Info.LeftBearing && gx < g.Info.RightBearing && gy >= -g.Info.Ascent && gy < g.Info.Descent;

        static bool IsSet(XGlyph g, int gx, int gy) => InBox(g, gx, gy) && g.IsSet(gx - g.Info.LeftBearing, gy + g.Info.Ascent);
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
        XTopLevelWindow? handle = _screenHandle   // 单窗口模式:光标都显示在屏幕窗口上
            ?? (_pointerWindow.TopLevel is { } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? h) ? h : null);
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

    /// <summary>
    /// 交给宿主的样子:形状按 XFIXES 起的名字、隐形与否、cursor 字体的字形号推出。cursor 字体里有对应系统光标的字形不带图像
    /// (宿主显示系统光标);没有对应的(pencil、gumby、dotbox……)带上字形烙成的图像,原先一律显示成箭头。
    /// </summary>
    private static XCursor AppearanceOf(XCursorResource cursor)
    {
        if (cursor.Appearance is { } known)
        {
            return known;
        }
        XCursorShape? glyphShape = cursor.Glyph >= 0 ? ShapeOfGlyph(cursor.Glyph) : null;
        XCursorShape shape = (cursor.Name is { } name ? ShapeOfName(name) : null)
            ?? (cursor.Blank ? XCursorShape.Hidden : glyphShape ?? XCursorShape.Arrow);
        return cursor.Appearance = new XCursor(shape, glyphShape is null ? cursor.Image : null);
    }

    /// <summary>cursor 字体的字形号 → 形状(Xlib 附录 B);没有对应系统光标的为 null(宿主显示字形的图像)。</summary>
    private static XCursorShape? ShapeOfGlyph(int glyph) => glyph switch
    {
        2 or 68 or 132 => XCursorShape.Arrow,                   // arrow、left_ptr、top_left_arrow
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
        _ => null,
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
