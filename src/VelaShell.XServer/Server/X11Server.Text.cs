// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「OpenFont」「CloseFont」「QueryFont」(回复布局、FONTPROP、
//   CHARINFO 数组的顺序)「QueryTextExtents」「ListFonts」「ListFontsWithInfo」(每个字体一条回复,
//   最后一条 name 长度为 0 的结束回复)「GetFontPath」「PolyText8」「PolyText16」(TEXTITEM:长度 255 表示换字体,
//   字体 ID 四字节总是高位在前)「ImageText8」「ImageText16」

using VelaShell.XServer.Fonts;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private XFont? _defaultFont;

    /// <summary>GC 没设字体时用的服务端默认字体(协议没指定是哪个;X.Org 惯例是 fixed)。</summary>
    private XFont DefaultFont => _defaultFont ??= _fonts.Open("fixed") ?? throw new InvalidOperationException("内置字体 fixed 缺失。");

    private void OpenFont(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        XFont? font = _fonts.Open(name);
        if (font is null)
        {
            Log($"{c} OpenFont: no font matches '{name}'");
            throw new XProtocolError(XErrorCode.Name);
        }
        AddResource(c, new XFontResource(id, c, font));
    }

    private void CloseFont(XRequestReader r)
    {
        uint id = r.U32();
        _ = Lookup<XFontResource>(id) ?? throw new XProtocolError(XErrorCode.Font, id);
        RemoveResource(id);
    }

    /// <summary>FONTABLE:字体 ID,或 GC ID(取 GC 当前的字体)。</summary>
    private XFont Fontable(uint id) => Lookup<XResource>(id) switch
    {
        XFontResource f => f.Font,
        XGc gc => gc.Font?.Font ?? DefaultFont,
        _ => throw new XProtocolError(XErrorCode.Font, id),
    };

    private static void WriteCharInfo(XWriter w, XCharInfo ci) =>
        w.I16(ci.LeftBearing).I16(ci.RightBearing).I16(ci.Width).I16(ci.Ascent).I16(ci.Descent).U16(ci.Attributes);

    /// <summary>QueryFont 与 ListFontsWithInfo 共用的那一大段字体信息(从 min-bounds 到 font-descent)。</summary>
    private static void WriteFontInfo(XWriter w, XFont font, List<(uint Atom, uint Value)> props)
    {
        WriteCharInfo(w, font.MinBounds);
        w.Zero(4);
        WriteCharInfo(w, font.MaxBounds);
        w.Zero(4);
        w.U16(font.MinChar2).U16(font.MaxChar2).U16(font.DefaultChar).U16((ushort)props.Count);
        w.U8(0);                                  // draw-direction:LeftToRight
        w.U8(font.MinByte1).U8(font.MaxByte1).Bool(font.AllCharsExist);
        w.I16(font.Ascent).I16(font.Descent);
    }

    private List<(uint Atom, uint Value)> FontProps(XFont font) =>
        [.. font.Properties.Select(p => (Intern(p.Name), p.StringValue is { } s ? Intern(s) : unchecked((uint)p.Value)))];

    private void QueryFont(XClient c, XRequestReader r)
    {
        XFont font = Fontable(r.U32());
        List<(uint Atom, uint Value)> props = FontProps(font);
        List<int> codes = [.. font.CharInfoRange()];
        c.Reply(0, w =>
        {
            WriteFontInfo(w, font, props);
            w.U32((uint)codes.Count);
            foreach ((uint atom, uint value) in props)
            {
                w.U32(atom).U32(value);
            }
            foreach (int code in codes)
            {
                WriteCharInfo(w, font.InfoOf(code));
            }
        });
    }

    private void QueryTextExtents(XClient c, XRequestReader r)
    {
        bool oddLength = r.Data != 0;
        XFont font = Fontable(r.U32());
        int count = (r.Remaining / 2) - (oddLength ? 1 : 0);
        int[] codes = new int[Math.Max(0, count)];
        for (int i = 0; i < codes.Length; i++)
        {
            byte b1 = r.U8(), b2 = r.U8();
            codes[i] = font.IsTwoByte ? (b1 << 8) | b2 : b2;
        }
        (int width, int left, int right, int ascent, int descent) = font.Measure(codes);
        c.Reply(0, w => w
            .I16(font.Ascent).I16(font.Descent).I16(ascent).I16(descent)
            .I32(width).I32(left).I32(right).Zero(4));
    }

    private void ListFonts(XClient c, XRequestReader r)
    {
        int max = r.U16();
        int length = r.U16();
        string pattern = r.String8(length);
        List<string> names = _fonts.Match(pattern, max);
        c.Reply(0, w =>
        {
            w.U16((ushort)names.Count).Zero(22);
            foreach (string name in names)
            {
                byte[] bytes = XWire.Latin1.GetBytes(name);
                w.U8((byte)bytes.Length).Bytes(bytes);
            }
            w.Pad4();
        });
    }

    private void ListFontsWithInfo(XClient c, XRequestReader r)
    {
        int max = r.U16();
        int length = r.U16();
        string pattern = r.String8(length);
        List<string> names = _fonts.Match(pattern, max);
        for (int i = 0; i < names.Count; i++)
        {
            if (_fonts.Open(names[i]) is not { } font)
            {
                continue;
            }
            byte[] name = XWire.Latin1.GetBytes(names[i]);
            List<(uint Atom, uint Value)> props = FontProps(font);
            uint remaining = (uint)(names.Count - i - 1);
            c.Reply((byte)name.Length, w =>
            {
                WriteFontInfo(w, font, props);
                w.U32(remaining);                 // replies-hint
                foreach ((uint atom, uint value) in props)
                {
                    w.U32(atom).U32(value);
                }
                w.Bytes(name).Pad4();
            });
        }
        // 结束标记:name 长度为 0,其余全零,总长 60 字节。
        c.Reply(0, w => w.Zero(52));
    }

    private static void GetFontPath(XClient c)
    {
        byte[] path = XWire.Latin1.GetBytes("built-ins");
        c.Reply(0, w => w.U16(1).Zero(22).U8((byte)path.Length).Bytes(path).Pad4());
    }

    // ------------------------------------------------------------------ 画文字

    private void PolyText(XRequestReader r, bool wide)
    {
        uint drawable = r.U32(), gcId = r.U32();
        int x = r.I16(), y = r.I16();
        XGc gc = Gc(gcId);
        // 画第一项时用的是请求开始时 GC 上的字体;文字项里的换字体同时改写 GC 的字体(协议规定)。
        XFont initial = gc.Font?.Font ?? DefaultFont;
        List<(int Delta, XFont? SwitchTo, int[] Codes)> items = [];
        while (r.Remaining >= 2)
        {
            byte length = r.U8();
            if (length == 255)
            {
                // 换字体:四字节字体 ID,**总是高位在前**(与客户端字节序无关)。
                if (r.Remaining < 4)
                {
                    break;
                }
                uint fid = ((uint)r.U8() << 24) | ((uint)r.U8() << 16) | ((uint)r.U8() << 8) | r.U8();
                XFontResource font = Lookup<XFontResource>(fid) ?? throw new XProtocolError(XErrorCode.Font, fid);
                gc.Font = font;
                items.Add((0, font.Font, []));
                continue;
            }
            int delta = r.I8();
            if (r.Remaining < length * (wide ? 2 : 1))
            {
                break;   // 结尾的补齐字节
            }
            int[] codes = new int[length];
            for (int i = 0; i < length; i++)
            {
                codes[i] = wide ? (r.U8() << 8) | r.U8() : r.U8();
            }
            items.Add((delta, null, codes));
        }

        Draw(drawable, gcId, raster =>
        {
            XFont font = initial;
            int pen = x;
            foreach ((int delta, XFont? switchTo, int[] codes) in items)
            {
                if (switchTo is not null)
                {
                    font = switchTo;
                    continue;
                }
                pen += delta;
                foreach (int code in codes)
                {
                    if (font.Lookup(font.IsTwoByte ? code : code & 0xFF) is { } glyph)
                    {
                        pen += raster.DrawGlyph(glyph, pen, y);
                    }
                }
            }
        });
    }
    private void ImageText(XRequestReader r, bool wide)
    {
        int n = r.Data;
        uint drawable = r.U32(), gcId = r.U32();
        int x = r.I16(), y = r.I16();
        XGc gc = Gc(gcId);
        XFont font = gc.Font?.Font ?? DefaultFont;
        int[] codes = new int[n];
        for (int i = 0; i < n; i++)
        {
            codes[i] = wide ? (r.U8() << 8) | r.U8() : r.U8();
            if (!font.IsTwoByte)
            {
                codes[i] &= 0xFF;
            }
        }
        Draw(drawable, gcId, raster => raster.ImageText(font, codes, x, y));
    }
}
