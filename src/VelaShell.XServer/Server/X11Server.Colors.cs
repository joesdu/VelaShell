// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateColormap」「FreeColormap」「CopyColormapAndFree」
//   「AllocColor」(TrueColor 下返回最接近的可表示颜色)「AllocNamedColor」「QueryColors」「LookupColor」

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private XColormap Colormap(uint id) => Lookup<XColormap>(id) ?? throw new XProtocolError(XErrorCode.Colormap, id);

    /// <summary>16 位分量 → 8 位 TrueColor 像素值(取高 8 位)。</summary>
    private static uint PixelOf(ushort r, ushort g, ushort b) => ((uint)(r >> 8) << 16) | ((uint)(g >> 8) << 8) | (uint)(b >> 8);

    /// <summary>像素值 → 这个视觉实际能表示的 16 位分量(8 位复制到低字节)。</summary>
    private static (ushort R, ushort G, ushort B) RgbOf(uint pixel)
    {
        static ushort Expand(uint v) => (ushort)(((v & 0xFF) << 8) | (v & 0xFF));
        return (Expand(pixel >> 16), Expand(pixel >> 8), Expand(pixel));
    }

    private void CreateColormap(XClient c, XRequestReader r)
    {
        byte alloc = r.Data;
        uint id = r.U32();
        _ = Window(r.U32());
        uint visual = r.U32();
        if (visual is not (RootVisualId or ArgbVisualId))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (alloc != 0)
        {
            // AllocAll 只对可写视觉有意义;TrueColor 上是 BadMatch。
            throw new XProtocolError(XErrorCode.Match);
        }
        AddResource(c, new XColormap(id, c, visual));
    }

    private void FreeColormap(XRequestReader r)
    {
        uint id = r.U32();
        _ = Colormap(id);
        if (id != DefaultColormapId)
        {
            RemoveResource(id);
        }
    }

    private void CopyColormapAndFree(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        XColormap source = Colormap(r.U32());
        AddResource(c, new XColormap(id, c, source.Visual));
    }

    private void AllocColor(XClient c, XRequestReader r)
    {
        _ = Colormap(r.U32());
        ushort red = r.U16(), green = r.U16(), blue = r.U16();
        uint pixel = PixelOf(red, green, blue);
        (ushort R, ushort G, ushort B) actual = RgbOf(pixel);
        c.Reply(0, w => w.U16(actual.R).U16(actual.G).U16(actual.B).Zero(2).U32(pixel).Zero(12));
    }

    private void AllocNamedColor(XClient c, XRequestReader r)
    {
        _ = Colormap(r.U32());
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        (ushort R, ushort G, ushort B) exact = ColorNames.Lookup(name) ?? throw new XProtocolError(XErrorCode.Name);
        uint pixel = PixelOf(exact.R, exact.G, exact.B);
        (ushort R, ushort G, ushort B) visual = RgbOf(pixel);
        c.Reply(0, w => w.U32(pixel).U16(exact.R).U16(exact.G).U16(exact.B).U16(visual.R).U16(visual.G).U16(visual.B).Zero(8));
    }

    private void QueryColors(XClient c, XRequestReader r)
    {
        _ = Colormap(r.U32());
        List<uint> pixels = [];
        while (r.Remaining >= 4)
        {
            pixels.Add(r.U32());
        }
        c.Reply(0, w =>
        {
            w.U16((ushort)pixels.Count).Zero(22);
            foreach (uint pixel in pixels)
            {
                (ushort red, ushort green, ushort blue) = RgbOf(pixel);
                w.U16(red).U16(green).U16(blue).Zero(2);
            }
        });
    }

    private void LookupColor(XClient c, XRequestReader r)
    {
        _ = Colormap(r.U32());
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        (ushort R, ushort G, ushort B) exact = ColorNames.Lookup(name) ?? throw new XProtocolError(XErrorCode.Name);
        (ushort R, ushort G, ushort B) visual = RgbOf(PixelOf(exact.R, exact.G, exact.B));
        c.Reply(0, w => w.U16(exact.R).U16(exact.G).U16(exact.B).U16(visual.R).U16(visual.G).U16(visual.B).Zero(12));
    }
}
