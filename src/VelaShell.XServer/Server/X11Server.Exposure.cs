// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 3 节「Window Hierarchy」(子窗口裁剪在父窗口内区里、
//   兄弟按堆叠顺序遮挡)、「CreateWindow」(背景 None / ParentRelative / 像素 / 像素图,边框)、
//   「Expose」事件(矩形列表、count 递减到 0)、「CreateGC」的 subwindow-mode
//   架构:velashell-docs/zh/xserver/design/architecture.md §6(每个顶层一块缓冲)

using System.Text;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Host;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    /// <summary>窗口树结构变了(映射、配置、堆叠、销毁)。可见区域目前按需现算,这里留作以后加缓存的挂点。</summary>
    private static void InvalidateVisibility()
    {
    }

    // ------------------------------------------------------------------ 几何(顶层缓冲坐标)

    /// <summary>外框(含边框)在所属顶层缓冲里的矩形;顶层自己就是整块缓冲(边框由宿主原生窗口替代)。</summary>
    private static XRect OuterRect(XWindow w)
    {
        if (w.IsTopLevel)
        {
            return new(0, 0, w.Width, w.Height);
        }
        (int px, int py) = w.Parent!.OffsetInTopLevel();
        return new(px + w.X, py + w.Y, w.Width + (2 * w.BorderWidth), w.Height + (2 * w.BorderWidth));
    }

    private static XRect InnerRect(XWindow w)
    {
        (int x, int y) = w.OffsetInTopLevel();
        return new(x, y, w.Width, w.Height);
    }

    /// <summary>外框里真正露在外面的部分:被祖先的内区裁、被堆叠在上面的已映射兄弟挡。</summary>
    internal static Region VisibleOuter(XWindow w)
    {
        if (!w.IsViewable || w.TopLevel is not { Buffer: { } buffer })
        {
            return new Region();
        }
        Region region = ShapedOuter(w);
        for (XWindow cur = w; !cur.IsTopLevel; cur = cur.Parent!)
        {
            XWindow parent = cur.Parent!;
            // 父窗口的内区、裁剪形状与边界形状都裁它的全部后代(SHAPE 规范 §2)。
            region.Intersect(InnerRect(parent));
            ApplyShapes(region, parent);
            int index = parent.Children.IndexOf(cur);
            for (int i = index + 1; i < parent.Children.Count; i++)
            {
                XWindow sibling = parent.Children[i];
                if (sibling.Mapped && !sibling.IsInputOnly)
                {
                    region.Subtract(ShapedOuter(sibling));
                }
            }
        }
        return region.Intersect(buffer.Bounds);
    }

    /// <summary>外框矩形再与边界形状求交(没有形状就是外框本身),顶层缓冲坐标。</summary>
    private static Region ShapedOuter(XWindow w)
    {
        Region region = new(OuterRect(w));
        if (w.BoundingShape is { } bounding)
        {
            (int x, int y) = w.OffsetInTopLevel();
            region.Intersect(bounding.Clone().Translate(x, y));
        }
        return region;
    }

    /// <summary>用窗口的边界与裁剪形状(若有)裁一块区域(顶层缓冲坐标)。</summary>
    private static void ApplyShapes(Region region, XWindow w)
    {
        if (w.BoundingShape is null && w.ClipShape is null)
        {
            return;
        }
        (int x, int y) = w.OffsetInTopLevel();
        if (w.BoundingShape is { } bounding)
        {
            region.Intersect(bounding.Clone().Translate(x, y));
        }
        if (w.ClipShape is { } clip)
        {
            region.Intersect(clip.Clone().Translate(x, y));
        }
    }

    internal static Region VisibleInner(XWindow w)
    {
        Region region = VisibleOuter(w).Intersect(InnerRect(w));
        if (w.ClipShape is { } clip)
        {
            (int x, int y) = w.OffsetInTopLevel();
            region.Intersect(clip.Clone().Translate(x, y));
        }
        return region;
    }

    /// <summary>ClipByChildren:可见内区再挖掉已映射子窗口(InputOnly 子窗口不挡画)。</summary>
    internal static Region ClipByChildren(XWindow w)
    {
        Region region = VisibleInner(w);
        foreach (XWindow child in w.Children)
        {
            if (child.Mapped && !child.IsInputOnly)
            {
                region.Subtract(ShapedOuter(child));
            }
        }
        return region;
    }

    // ------------------------------------------------------------------ 可绘对象 → 缓冲

    /// <summary>画到某个可绘对象上时的缓冲、原点与可画区域。窗口不可见时返回 null(画了也看不见,按协议是空操作)。</summary>
    internal (PixelBuffer Buffer, int OriginX, int OriginY, Region Clip, XWindow? TopLevel)? DrawTarget(uint drawable, XGc? gc)
    {
        switch (Lookup<XResource>(drawable))
        {
            case XPixmap pixmap:
                if (gc is not null && gc.Depth != pixmap.Depth)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                return (pixmap.Buffer, 0, 0, new Region(pixmap.Buffer.Bounds), null);
            case XWindow window:
                if (window.IsInputOnly)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                if (gc is not null && gc.Depth != window.Depth && !window.IsRoot)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                if (window.IsRoot || !window.IsViewable || window.TopLevel is not { Buffer: { } buffer } top)
                {
                    return null;
                }
                (int ox, int oy) = window.OffsetInTopLevel();
                Region clip = gc?.SubwindowMode == 1 ? VisibleInner(window) : ClipByChildren(window);
                return (buffer, ox, oy, clip, top);
            default:
                throw new XProtocolError(XErrorCode.Drawable, drawable);
        }
    }

    // ------------------------------------------------------------------ Expose

    /// <summary>
    /// 重画顶层里落在 <paramref name="region" />(缓冲坐标)内的一切:各窗口的边框与背景,
    /// 并给选了 Exposure 的客户端发 Expose(每个窗口一组,count 递减到 0)。
    /// </summary>
    private void ExposeWindowTree(XWindow top, Region region)
    {
        if (top.Buffer is null || region.IsEmpty)
        {
            return;
        }
        ExposeRecursive(top, region);
        MarkDamage(top, region);
    }

    private static void ExposeRecursive(XWindow w, Region region)
    {
        if (!w.Mapped && !w.IsTopLevel)
        {
            return;
        }
        if (!w.IsInputOnly)
        {
            if (!w.IsTopLevel && w.BorderWidth > 0)
            {
                Region border = VisibleOuter(w).Intersect(region).Subtract(InnerRect(w));
                PaintBorder(w, border);
            }
            Region area = ClipByChildren(w).Intersect(region);
            if (!area.IsEmpty)
            {
                PaintBackground(w, area);
                SendExpose(w, area);
            }
        }
        foreach (XWindow child in w.Children)
        {
            ExposeRecursive(child, region);
        }
    }

    private static void PaintBackground(XWindow w, Region area)
    {
        // ParentRelative:沿祖先找到第一个不是 ParentRelative 的背景,平铺原点也跟着那个祖先走。
        XWindow source = w;
        while (source.BackgroundPixmap == XWindow.BackgroundParentRelative && source.Parent is { IsRoot: false } p)
        {
            source = p;
        }
        PixelBuffer buffer = w.TopLevel!.Buffer!;
        uint mask = buffer.DepthMask;
        if (source.BackgroundTile is { } tile)
        {
            (int tx, int ty) = source.OffsetInTopLevel();
            foreach (XRect r in area.Rects)
            {
                for (int y = r.Y; y < r.Bottom; y++)
                {
                    for (int x = r.X; x < r.Right; x++)
                    {
                        buffer.Pixels[(y * buffer.Width) + x] =
                            tile.Buffer.Get(PositiveMod(x - tx, tile.Width), PositiveMod(y - ty, tile.Height)) & mask;
                    }
                }
            }
        }
        else if (source.BackgroundPixel is { } pixel)
        {
            foreach (XRect r in area.Rects)
            {
                for (int y = r.Y; y < r.Bottom; y++)
                {
                    Array.Fill(buffer.Pixels, pixel & mask, (y * buffer.Width) + r.X, r.Width);
                }
            }
        }
        // 背景 None:不画,保留原内容(协议规定)。
    }

    private static void PaintBorder(XWindow w, Region area)
    {
        PixelBuffer buffer = w.TopLevel!.Buffer!;
        uint mask = buffer.DepthMask;
        (int ox, int oy) = w.OffsetInTopLevel();
        foreach (XRect r in area.Rects)
        {
            for (int y = r.Y; y < r.Bottom; y++)
            {
                for (int x = r.X; x < r.Right; x++)
                {
                    uint value = w.BorderTile is { } tile
                        ? tile.Buffer.Get(PositiveMod(x - ox, tile.Width), PositiveMod(y - oy, tile.Height))
                        : w.BorderPixel;
                    buffer.Pixels[(y * buffer.Width) + x] = value & mask;
                }
            }
        }
    }

    private static int PositiveMod(int a, int m) => m <= 0 ? 0 : ((a % m) + m) % m;

    private static void SendExpose(XWindow w, Region area)
    {
        if (!w.AnySelects(XEventMask.Exposure))
        {
            return;
        }
        (int ox, int oy) = w.OffsetInTopLevel();
        List<XRect> rects = [.. area.Rects];
        DeliverToSelectors(w, XEventMask.Exposure, c =>
        {
            for (int i = 0; i < rects.Count; i++)
            {
                XRect r = rects[i];
                int count = rects.Count - 1 - i;
                c.Event(XEventCode.Expose, 0, e => e
                    .U32(w.Id).U16((ushort)(r.X - ox)).U16((ushort)(r.Y - oy)).U16((ushort)r.Width).U16((ushort)r.Height)
                    .U16((ushort)count));
            }
        });
    }

    // ------------------------------------------------------------------ 损伤与宿主

    internal void MarkDamage(XWindow top, Region region)
    {
        NoteWindowDrawn(top, region);
        if (!_damage.TryGetValue(top, out Region? pending))
        {
            _damage[top] = region.Clone();
            return;
        }
        pending.Union(region);
    }

    internal void MarkDamage(XWindow top, XRect rect)
    {
        if (!rect.IsEmpty)
        {
            MarkDamage(top, new Region(rect));
        }
    }

    /// <summary>把这一批攒下的损伤一次性交给宿主(每个顶层合并成一组矩形)。</summary>
    private void FlushDamage()
    {
        if (_damage.Count == 0)
        {
            return;
        }
        KeyValuePair<XWindow, Region>[] batch = [.. _damage];
        _damage.Clear();
        foreach ((XWindow top, Region region) in batch)
        {
            if (top.Mapped && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
            {
                _host.TopLevelDamaged(handle, [.. region.Rects]);
            }
        }
    }

    private XTopLevelWindow HandleFor(XWindow top)
    {
        if (!_topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            handle = new XTopLevelWindow(top.Id, PixelLock,
                () => top.Buffer is { } b ? (b.Pixels, b.Width, b.Height) : null);
            _topLevelHandles[top] = handle;
        }
        return handle;
    }

    /// <summary>从窗口与它的 ICCCM / EWMH 属性刷新宿主看到的那份快照。</summary>
    private void RefreshHandle(XWindow top, XTopLevelWindow handle)
    {
        handle.X = top.X;
        handle.Y = top.Y;
        handle.Width = top.Width;
        handle.Height = top.Height;
        handle.OverrideRedirect = top.OverrideRedirect;
        handle.Shape = top.BoundingShape is { } shape
            ? [.. shape.Clone().Intersect(new XRect(0, 0, top.Width, top.Height)).Rects]
            : null;

        uint netWmName = Intern("_NET_WM_NAME");
        if (top.Properties.TryGetValue(netWmName, out XProperty? utf8) && utf8.Format == 8)
        {
            handle.Title = Encoding.UTF8.GetString(utf8.Data);
        }
        else if (top.Properties.TryGetValue(XAtom.WmName, out XProperty? name) && name.Format == 8)
        {
            handle.Title = XWire.Latin1.GetString(name.Data);
        }

        if (top.Properties.TryGetValue(XAtom.WmClass, out XProperty? cls) && cls.Format == 8)
        {
            // WM_CLASS = "instance\0class\0"
            string[] parts = XWire.Latin1.GetString(cls.Data).Split('\0');
            handle.ClassName = parts.Length > 1 ? parts[1] : parts[0];
        }

        handle.TransientFor = top.Properties.TryGetValue(XAtom.WmTransientFor, out XProperty? transient)
                              && transient is { Format: 32, Data.Length: >= 4 }
            ? BitConverter.ToUInt32(transient.Data, 0)
            : 0;

        uint protocols = Intern("WM_PROTOCOLS");
        uint delete = Intern("WM_DELETE_WINDOW");
        handle.SupportsDeleteWindow = top.Properties.TryGetValue(protocols, out XProperty? p) && p.Format == 32
                                      && Enumerable.Range(0, p.Data.Length / 4).Any(i => BitConverter.ToUInt32(p.Data, i * 4) == delete);
    }

    /// <summary>顶层窗口的属性变了:标题、类名、协议、瞬态父窗口可能跟着变,告诉宿主。</summary>
    private void OnTopLevelPropertyChanged(XWindow window, uint property)
    {
        _ = property;
        if (window.IsTopLevel && _topLevelHandles.TryGetValue(window, out XTopLevelWindow? handle))
        {
            RefreshHandle(window, handle);
            if (window.Mapped)
            {
                _host.TopLevelChanged(handle);
            }
        }
    }
}
