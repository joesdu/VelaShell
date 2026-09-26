// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 3 节「Window Hierarchy」(子窗口裁剪在父窗口内区里、
//   兄弟按堆叠顺序遮挡)、「CreateWindow」(背景 None / ParentRelative / 像素 / 像素图,边框)、
//   「Expose」事件(矩形列表、count 递减到 0)、「CreateGC」的 subwindow-mode
//   架构:velashell-docs/zh/xserver/design/architecture.md §6(每个顶层一块缓冲)

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>可见性代号:窗口树结构或形状一变就加一,窗口上缓存的可见区域随之作废。</summary>
    private int _visibilityGeneration;

    /// <summary>窗口树结构变了(映射、配置、堆叠、销毁、形状):作废所有缓存的可见区域。</summary>
    private void InvalidateVisibility() => _visibilityGeneration++;

    /// <summary>
    /// 带缓存的 ClipByChildren / VisibleInner:同一代号、同样的顶层缓冲尺寸下直接复用。
    /// 返回的区域是共享的,<b>只读</b> —— 要改就先 Clone。绘图请求每条都要它,算一次要走遍祖先与兄弟。
    /// </summary>
    private Region CachedClip(XWindow w, bool includeInferiors)
    {
        PixelBuffer? buffer = w.TopLevel?.Buffer;
        int bw = buffer?.Width ?? 0, bh = buffer?.Height ?? 0;
        var cache = w.VisibilityCache;
        if (cache.Generation != _visibilityGeneration || cache.BufferWidth != bw || cache.BufferHeight != bh)
        {
            cache = (_visibilityGeneration, bw, bh, null, null);
        }
        Region region;
        if (includeInferiors)
        {
            region = cache.VisibleInner ??= VisibleInner(w);
        }
        else
        {
            region = cache.ClipByChildren ??= ClipByChildren(w);
        }
        w.VisibilityCache = cache;
        return region;
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
                Region clip = CachedClip(window, includeInferiors: gc?.SubwindowMode == 1);
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
        ExposeRecursive(top, region, region.Bounds, 0, 0);
        MarkDamage(top, region);
    }

    /// <param name="w">要重画的窗口。</param>
    /// <param name="region">要重画的范围(缓冲坐标)。</param>
    /// <param name="bounds"><paramref name="region" /> 的外接矩形。</param>
    /// <param name="ix">窗口内区原点在缓冲里的 x(往下走时逐层累加)。</param>
    /// <param name="iy">同上,y。</param>
    private static void ExposeRecursive(XWindow w, Region region, XRect bounds, int ix, int iy)
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
            // 子窗口(连同它的整棵子树,都被裁在它的外框之内)与重画范围不相交:整棵跳过,不算它们的可见区域。
            int cx = ix + child.X, cy = iy + child.Y, bw = child.BorderWidth;
            if (new XRect(cx, cy, child.Width + (2 * bw), child.Height + (2 * bw)).Intersect(bounds).IsEmpty)
            {
                continue;
            }
            ExposeRecursive(child, region, bounds, cx + bw, cy + bw);
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
        foreach (XRect rect in region.Rects)
        {
            AddDamage(top, rect);
        }
    }

    internal void MarkDamage(XWindow top, XRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }
        if (_damageObjects.Count != 0)
        {
            NoteWindowDrawn(top, new Region(rect));
        }
        AddDamage(top, rect);
    }

    /// <summary>每个顶层这一批最多记这么多块损伤;再多就合成外接矩形 —— 宿主反正是按矩形重画,矩形多了反而慢。</summary>
    private const int MaxDamageRects = 8;

    /// <summary>
    /// 把一块损伤并进这个顶层这一批的损伤里:已被盖住的丢掉,盖住别人的替掉别人,数量超限就合成外接矩形。
    /// 每次绘图都走这里,所以不用 Region 的精确并集(那是 O(n²),一批几百次绘图会退化成几千块矩形)。
    /// </summary>
    private void AddDamage(XWindow top, XRect rect)
    {
        if (!_damage.TryGetValue(top, out List<XRect>? rects))
        {
            _damage[top] = [rect];
            return;
        }
        for (int i = rects.Count - 1; i >= 0; i--)
        {
            XRect r = rects[i];
            if (r.X <= rect.X && r.Y <= rect.Y && r.Right >= rect.Right && r.Bottom >= rect.Bottom)
            {
                return;
            }
            if (rect.X <= r.X && rect.Y <= r.Y && rect.Right >= r.Right && rect.Bottom >= r.Bottom)
            {
                rects.RemoveAt(i);
            }
        }
        rects.Add(rect);
        if (rects.Count > MaxDamageRects)
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (XRect r in rects)
            {
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.Right);
                y2 = Math.Max(y2, r.Bottom);
            }
            rects.Clear();
            rects.Add(new XRect(x1, y1, x2 - x1, y2 - y1));
        }
    }
}
