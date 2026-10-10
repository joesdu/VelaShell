// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 3 节「Window Hierarchy」(兄弟按堆叠顺序遮挡、边框画在外框里)、
//   「CreateWindow」「ChangeWindowAttributes」的 background-pixmap / background-pixel / border-pixel / border-pixmap
//   (根窗口的背景、平铺原点是窗口内区原点)
//   SHAPE Extension 1.1 —— 边界形状裁窗口的外框
//   ICCCM 2.0 —— §2.8(屏幕的管理器选区 WM_S0)、§4(窗口管理器经根窗口的 SubstructureRedirect 管理顶层)
//   架构:velashell-docs/zh/xserver/design/architecture.md §2(单窗口模式)
//
//   单窗口(rootful)模式(F13,决策 Q2):整个根窗口作为一个顶层交给宿主(<see cref="X11Server.Screen" />),宿主开一个原生窗口显示它,
//   远端的窗口管理器(xfwm4、metacity、openbox……)照常管理顶层。顶层仍各有各的缓冲(绘图路径不变);每批执行完、放锁之前,
//   把这一批画过的地方与窗口树的变化换成屏幕上的区域,按堆叠次序拼进根窗口的缓冲 —— 像一个永远开着的合成器。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>单窗口模式下整块屏幕的句柄;rootless(默认)为 null。</summary>
    private XTopLevelWindow? _screenHandle;

    /// <summary>拼好、还没交给宿主的屏幕区域(根坐标)。在像素锁里攒,放锁之后交。</summary>
    private readonly List<XRect> _screenDamage = [];

    /// <summary>上一次拼屏幕时各顶层的样子(从下往上):与这一批结束时比,变了的地方重拼。</summary>
    private List<ScreenLayer> _composedLayers = [];

    /// <summary>上一次拼屏幕时根窗口的背景。</summary>
    private (uint? Pixel, PixelBuffer? Tile) _composedBackground;

    /// <summary>拼进屏幕的一个顶层:外框(根坐标)、边界形状(按引用与外接矩形比)、边框。</summary>
    private readonly record struct ScreenLayer(XWindow Window, XRect Outer, Region? Shape, XRect ShapeBounds, uint BorderPixel, XPixmap? BorderTile);

    /// <summary>
    /// 单窗口(rootful)模式下整块屏幕的句柄(<see cref="X11ServerOptions.Rootful" />);rootless(默认)时为 null。
    /// 宿主附着之后照普通顶层一样给它开一个原生窗口:读像素(<see cref="XTopLevelWindow.ReadPixels" />)、收损伤
    /// (<see cref="IX11ServerHost.TopLevelDamaged" />)、注入指针(坐标就是根坐标)、显示光标;缩放它就是改屏幕尺寸(客户端收到 RANDR 通知)。
    /// 移动、关闭、改状态、给焦点对它都不起作用 —— 那些归远端的窗口管理器。其余顶层不再单独交给宿主。
    /// </summary>
    public XTopLevelWindow? Screen => _screenHandle;

    /// <summary>单窗口模式。</summary>
    private bool Rootful => _screenHandle is not null;

    /// <summary>构造时:单窗口模式给根窗口一块缓冲、建屏幕句柄,铺上背景。要在占窗口管理器 / XSETTINGS / 托盘的选区之前调。</summary>
    private void InitRootful()
    {
        if (!_options.Rootful)
        {
            return;
        }
        Root.Buffer = new PixelBuffer(Root.Width, Root.Height, 24);
        _host.DropWindowManagerRequests = true;
        _screenHandle = new XTopLevelWindow(Root, _pixelGate, this) { Snapshot = ScreenSnapshot(new XTopLevelSnapshot()) };
        _composedBackground = (Root.BackgroundPixel, Root.BackgroundTile?.Buffer);
        ComposeScreen(Root.Buffer.Bounds);
    }

    private XTopLevelSnapshot ScreenSnapshot(XTopLevelSnapshot previous) => previous with
    {
        X = 0,
        Y = 0,
        Width = Root.Width,
        Height = Root.Height,
        NeedsPlacement = true,
        IsMapped = true,
        ClassName = "VelaShell-XScreen",
        InstanceName = "screen",
    };

    /// <summary>屏幕尺寸变了(<see cref="ApplyScreenLayout" />):缓冲跟着改、整屏重拼,告诉宿主新尺寸。</summary>
    private void OnRootfulScreenResized()
    {
        if (_screenHandle is not { } screen || Root.Buffer is not { } buffer)
        {
            return;
        }
        buffer.Resize(Root.Width, Root.Height);
        _screenDamage.Clear();
        ComposeScreen(buffer.Bounds);
        _screenDamage.Add(buffer.Bounds);
        screen.Snapshot = ScreenSnapshot(screen.Snapshot);
        _host.TopLevelChanged(screen, XTopLevelChanges.Geometry);
    }

    /// <summary>宿主缩放了屏幕窗口:屏幕改成这么大(一台显示器覆盖全部)。</summary>
    private void ApplyScreenResize(int width, int height) =>
        ApplyScreenLayout(width, height, NormalizeMonitors(null, width, height, "monitors"));

    /// <summary>
    /// 单窗口模式,每批执行完、还拿着像素锁时:这一批画过的顶层区域、顶层的映射 / 位置 / 尺寸 / 堆叠 / 形状 / 边框与根窗口背景的变化
    /// 换成屏幕上的区域,重新拼进根窗口的缓冲,记下来放锁之后交给宿主。顶层自己的损伤不再交给宿主。
    /// </summary>
    private void ComposeScreenBatch()
    {
        if (_screenHandle is null || Root.Buffer is not { } screen)
        {
            return;
        }
        Region dirty = new();
        foreach ((XWindow top, List<XRect> rects) in _damage)
        {
            if (!top.IsTopLevel || !top.Mapped)
            {
                continue;
            }
            int ix = top.X + top.BorderWidth, iy = top.Y + top.BorderWidth;
            foreach (XRect r in rects)
            {
                dirty.Union(new XRect(ix + r.X, iy + r.Y, r.Width, r.Height));
            }
        }
        _damage.Clear();

        List<ScreenLayer> layers = CurrentLayers();
        DiffLayers(_composedLayers, layers, dirty);
        _composedLayers = layers;
        (uint? Pixel, PixelBuffer? Tile) background = (Root.BackgroundPixel, Root.BackgroundTile?.Buffer);
        if (background != _composedBackground)
        {
            _composedBackground = background;
            dirty = new Region(screen.Bounds);
        }

        foreach (XRect r in dirty.Intersect(screen.Bounds).Rects)
        {
            ComposeScreen(r);
            _screenDamage.Add(r);
        }
    }

    /// <summary>放锁之后:把拼好的屏幕区域交给宿主。</summary>
    private void FlushScreenDamage()
    {
        if (_screenHandle is not { } screen || _screenDamage.Count == 0)
        {
            return;
        }
        XRect[] rects = [.. _screenDamage];
        _screenDamage.Clear();
        _host.TopLevelDamaged(screen, rects);
    }

    /// <summary>此刻映射着的顶层,从下往上。</summary>
    private List<ScreenLayer> CurrentLayers()
    {
        List<ScreenLayer> layers = [];
        foreach (XWindow top in Root.Children)
        {
            if (!top.Mapped || top.IsInputOnly || top.Buffer is null)
            {
                continue;
            }
            int bw = top.BorderWidth;
            XRect outer = new(top.X, top.Y, top.Width + (2 * bw), top.Height + (2 * bw));
            layers.Add(new ScreenLayer(top, outer, top.BoundingShape, top.BoundingShape?.Bounds ?? default, top.BorderPixel, top.BorderTile));
        }
        return layers;
    }

    /// <summary>
    /// 两次的顶层比一比,变了的地方并进 <paramref name="dirty" />:新映射的、没了的整个外框;位置、尺寸、形状、边框变了的新旧两个外框;
    /// 只是堆叠次序变了的,只有相互重叠、上下颠倒了的两个窗口的交集。
    /// </summary>
    private static void DiffLayers(List<ScreenLayer> before, List<ScreenLayer> after, Region dirty)
    {
        Dictionary<XWindow, int> oldIndex = new(before.Count);
        for (int i = 0; i < before.Count; i++)
        {
            oldIndex[before[i].Window] = i;
        }
        List<(int Old, ScreenLayer Layer)> kept = [];
        HashSet<XWindow> present = [];
        foreach (ScreenLayer layer in after)
        {
            present.Add(layer.Window);
            if (!oldIndex.TryGetValue(layer.Window, out int old))
            {
                dirty.Union(layer.Outer);
            }
            else if (before[old] != layer)
            {
                dirty.Union(before[old].Outer);
                dirty.Union(layer.Outer);
            }
            else
            {
                kept.Add((old, layer));
            }
        }
        foreach (ScreenLayer layer in before)
        {
            if (!present.Contains(layer.Window))
            {
                dirty.Union(layer.Outer);
            }
        }
        // 没变的窗口按新次序排,旧下标若是递增的,堆叠就没变(多数批次如此)。
        bool reordered = false;
        for (int i = 1; i < kept.Count && !reordered; i++)
        {
            reordered = kept[i].Old < kept[i - 1].Old;
        }
        if (!reordered)
        {
            return;
        }
        for (int i = 0; i < kept.Count; i++)
        {
            for (int j = i + 1; j < kept.Count; j++)
            {
                if (kept[j].Old < kept[i].Old && kept[i].Layer.Outer.Intersect(kept[j].Layer.Outer) is { IsEmpty: false } overlap)
                {
                    dirty.Union(overlap);
                }
            }
        }
    }

    /// <summary>
    /// 把根坐标里的 <paramref name="area" /> 重新拼进屏幕缓冲:先铺根窗口的背景(像素图按根窗口原点平铺,没有背景是黑的),
    /// 再从下往上画映射着的顶层 —— 边框(边框像素图按窗口内区原点平铺)与内区,按边界形状裁;深度 32 的在有合成管理器时按预乘 over 叠上去,
    /// 没有时与别的窗口一样整块盖上(X 不看 alpha)。拿着像素锁时调。
    /// </summary>
    private void ComposeScreen(XRect area)
    {
        PixelBuffer screen = Root.Buffer!;
        area = area.Intersect(screen.Bounds);
        if (area.IsEmpty)
        {
            return;
        }
        bool compositorPresent = _selections.ContainsKey(new SelectionSlot(Intern("_NET_WM_CM_S0"), null));
        uint[] pixels = screen.Pixels;
        int stride = screen.Width;
        PixelBuffer? backgroundTile = Root.BackgroundTile?.Buffer;
        uint backgroundPixel = Root.BackgroundPixel ?? 0;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            if (backgroundTile is not null)
            {
                screen.FillTiledRow(y, area.X, area.Right, backgroundTile, 0, 0, 0xFFFFFFFFu);
            }
            else
            {
                Array.Fill(pixels, backgroundPixel & 0xFFFFFF, (y * stride) + area.X, area.Width);
            }
        }

        foreach (XWindow top in Root.Children)
        {
            if (!top.Mapped || top.IsInputOnly || top.Buffer is not { } buffer)
            {
                continue;
            }
            int bw = top.BorderWidth;
            int ix = top.X + bw, iy = top.Y + bw;
            XRect outer = new XRect(top.X, top.Y, top.Width + (2 * bw), top.Height + (2 * bw)).Intersect(area);
            if (outer.IsEmpty)
            {
                continue;
            }
            // 内区里只有缓冲覆盖到的部分有像素(被 PixelBuffer.MaxPixels 削掉的行、缩放之后还没换的缓冲之外是黑的)。
            XRect inner = new(ix, iy, Math.Min(top.Width, buffer.Width), Math.Min(top.Height, buffer.Height));
            // ARGB 窗口只在有合成管理器(远端的合成器占着 _NET_WM_CM_S0)时按 alpha 叠;没有时 X 显示它不看 alpha。
            bool alpha = top.Depth == 32 && compositorPresent;
            PixelBuffer? borderTile = bw > 0 ? top.BorderTile?.Buffer : null;
            IReadOnlyList<XRect> pieces = top.BoundingShape is { } shape
                ? shape.Clone().Translate(ix, iy).Intersect(outer).Rects
                : [outer];
            foreach (XRect piece in pieces)
            {
                for (int y = piece.Y; y < piece.Bottom; y++)
                {
                    int row = y * stride;
                    bool innerRow = y >= inner.Y && y < iy + top.Height;
                    int from = piece.X, to = piece.Right;
                    if (!innerRow || bw > 0)
                    {
                        // 边框:外框里内区以外的部分(内区之外的行整段,内区的行只有左右两截)。
                        int innerLeft = innerRow ? Math.Clamp(ix, from, to) : to;
                        int innerRight = innerRow ? Math.Clamp(ix + top.Width, from, to) : to;
                        FillBorder(screen, y, from, innerLeft, top, borderTile, ix, iy);
                        FillBorder(screen, y, innerRight, to, top, borderTile, ix, iy);
                        if (!innerRow)
                        {
                            continue;
                        }
                        (from, to) = (innerLeft, innerRight);
                    }
                    if (to <= from)
                    {
                        continue;
                    }
                    int copyTo = Math.Min(to, inner.Right);
                    if (y >= inner.Bottom || copyTo <= from)
                    {
                        Array.Clear(pixels, row + from, to - from);
                        continue;
                    }
                    ReadOnlySpan<uint> source = buffer.Pixels.AsSpan(((y - iy) * buffer.Width) + from - ix, copyTo - from);
                    Span<uint> target = pixels.AsSpan(row + from, copyTo - from);
                    if (alpha)
                    {
                        BlendOver(source, target);
                    }
                    else
                    {
                        source.CopyTo(target);
                    }
                    if (to > copyTo)
                    {
                        Array.Clear(pixels, row + copyTo, to - copyTo);
                    }
                }
            }
        }
    }

    /// <summary>屏幕缓冲第 <paramref name="y" /> 行的 [x1, x2) 画成顶层的边框。</summary>
    private static void FillBorder(PixelBuffer screen, int y, int x1, int x2, XWindow top, PixelBuffer? tile, int originX, int originY)
    {
        if (x2 <= x1)
        {
            return;
        }
        if (tile is not null)
        {
            screen.FillTiledRow(y, x1, x2, tile, originX, originY, 0xFFFFFFFFu);
        }
        else
        {
            Array.Fill(screen.Pixels, top.BorderPixel & 0xFFFFFF, (y * screen.Width) + x1, x2 - x1);
        }
    }

    /// <summary>预乘 ARGB 的 over:目标 = 源 + 目标 × (1 − 源的 alpha)。目标不透明,结果的高 8 位不要。</summary>
    private static void BlendOver(ReadOnlySpan<uint> source, Span<uint> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            uint s = source[i];
            uint a = s >> 24;
            if (a == 0xFF)
            {
                target[i] = s & 0xFFFFFF;
                continue;
            }
            uint d = target[i], inv = 255 - a;
            uint r = Math.Min(255u, ((s >> 16) & 0xFF) + ((((d >> 16) & 0xFF) * inv) + 127) / 255);
            uint g = Math.Min(255u, ((s >> 8) & 0xFF) + ((((d >> 8) & 0xFF) * inv) + 127) / 255);
            uint b = Math.Min(255u, (s & 0xFF) + (((d & 0xFF) * inv) + 127) / 255);
            target[i] = (r << 16) | (g << 8) | b;
        }
    }
}
