// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Composite Extension, Version 0.4 —— QueryVersion 0、RedirectWindow 1、RedirectSubwindows 2、UnredirectWindow 3、
//   UnredirectSubwindows 4(同一窗口只能有一个客户端做 Manual 重定向,否则 BadAccess)、
//   CreateRegionFromBorderClip 5、NameWindowPixmap 6、GetOverlayWindow 7、ReleaseOverlayWindow 8
//   Double Buffer Extension (DBE), Version 1.0 —— §「Types」(SWAPACTION:Undefined / Background / Untouched / Copied)、
//   §「Errors」(BadBuffer)、GetVersion 0、AllocateBackBufferName 1、DeallocateBackBufferName 2、SwapBuffers 3、
//   BeginIdiom 4、EndIdiom 5、GetVisualInfo 6、GetBackBufferAttributes 7
//
//   rootless 下每个顶层本来就有自己的像素缓冲,Composite 的「重定向到离屏」是天然状态:
//   重定向只做登记与互斥检查,NameWindowPixmap 对顶层窗口给出与它共享像素的像素图(之后的绘制照样看得到),
//   对子窗口给出一份当下内容的拷贝。叠加窗口不挂进窗口树,不会被宿主当成顶层窗口。
//   DBE 的后缓冲是一个挂在后缓冲 ID 下的像素图,核心绘图请求照常能画进去;SwapBuffers 把它拷到窗口可见的部分。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private const byte CompositeMajor = 142;
    private const byte DbeMajor = 143;
    private const byte DbeErrorBase = 142;   // BadBuffer

    /// <summary>Composite 叠加窗口(服务端自己的 ID,不挂进窗口树)。</summary>
    private const uint OverlayWindowId = 0x46;

    private XWindow? _overlayWindow;

    /// <summary>Manual 重定向的持有者:窗口 → 客户端(同一窗口只能有一个)。</summary>
    private readonly Dictionary<XWindow, XClient> _manualRedirects = [];

    /// <summary>DBE:窗口 → 它的后缓冲(可以有多个名字指向同一块)。</summary>
    private readonly Dictionary<XWindow, (XPixmap Buffer, List<uint> Names)> _backBuffers = [];

    // ================================================================== Composite

    private void CompositeExtension(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U32(0).U32(4).Zero(16));
                break;
            case 1:   // RedirectWindow
            case 2:   // RedirectSubwindows
                {
                    XWindow window = Window(r.U32());
                    byte update = r.U8();
                    if (update > 1)
                    {
                        throw new XProtocolError(XErrorCode.Value, update);
                    }
                    if (window.IsRoot && r.Data == 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);   // 根窗口不能被重定向
                    }
                    if (update == 1)
                    {
                        if (_manualRedirects.TryGetValue(window, out XClient? holder) && !ReferenceEquals(holder, c))
                        {
                            throw new XProtocolError(XErrorCode.Access);
                        }
                        _manualRedirects[window] = c;
                    }
                    break;
                }
            case 3:   // UnredirectWindow
            case 4:   // UnredirectSubwindows
                {
                    XWindow window = Window(r.U32());
                    if (_manualRedirects.TryGetValue(window, out XClient? holder) && ReferenceEquals(holder, c))
                    {
                        _manualRedirects.Remove(window);
                    }
                    break;
                }
            case 5:   // CreateRegionFromBorderClip:窗口外框露在外面的部分(窗口坐标,原点在内区左上角)
                {
                    uint regionId = r.U32();
                    XWindow window = Window(r.U32());
                    Region clip = VisibleOuter(window);
                    if (!window.IsTopLevel)
                    {
                        (int ox, int oy) = window.OffsetInTopLevel();
                        clip.Translate(-ox, -oy);
                    }
                    else if (window.IsViewable)
                    {
                        clip = new Region(new XRect(-window.BorderWidth, -window.BorderWidth,
                            window.Width + (2 * window.BorderWidth), window.Height + (2 * window.BorderWidth)));
                    }
                    AddResource(c, new XRegionResource(regionId, c, clip));
                    break;
                }
            case 6:   // NameWindowPixmap
                {
                    XWindow window = Window(r.U32());
                    uint pixmapId = r.U32();
                    if (!window.IsViewable || window.IsRoot || window.TopLevel is not { Buffer: { } buffer })
                    {
                        throw new XProtocolError(XErrorCode.Match);   // 只有映射着的(即已重定向的)窗口才有像素图
                    }
                    XPixmap pixmap;
                    if (window.IsTopLevel)
                    {
                        pixmap = new XPixmap(pixmapId, c, buffer);   // 与顶层共享像素
                    }
                    else
                    {
                        pixmap = new XPixmap(pixmapId, c, window.Width, window.Height, window.Depth);
                        (int ox, int oy) = window.OffsetInTopLevel();
                        CopyPixels(buffer, ox, oy, pixmap.Buffer, 0, 0, window.Width, window.Height);
                    }
                    AddResource(c, pixmap);
                    break;
                }
            case 7:   // GetOverlayWindow
                {
                    _ = Window(r.U32());
                    _overlayWindow ??= CreateOverlayWindow();
                    c.Reply(0, w => w.U32(OverlayWindowId).Zero(20));
                    break;
                }
            case 8:   // ReleaseOverlayWindow
                _ = Window(r.U32());
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private XWindow CreateOverlayWindow()
    {
        XWindow overlay = new(OverlayWindowId, null, Root)
        {
            Width = Root.Width,
            Height = Root.Height,
            Depth = 24,
            Visual = RootVisualId,
            Colormap = DefaultColormapId,
            Mapped = true,
        };
        _resources[OverlayWindowId] = overlay;
        return overlay;
    }

    /// <summary>在两块缓冲之间拷一块矩形(两边都裁到各自范围内)。</summary>
    private static void CopyPixels(PixelBuffer src, int sx, int sy, PixelBuffer dst, int dx, int dy, int width, int height)
    {
        XRect srcRect = new XRect(sx, sy, width, height).Intersect(src.Bounds);
        for (int row = srcRect.Y; row < srcRect.Bottom; row++)
        {
            int ty = row - sy + dy;
            if ((uint)ty >= (uint)dst.Height)
            {
                continue;
            }
            int x1 = Math.Max(srcRect.X, sx - dx);
            int x2 = Math.Min(srcRect.Right, sx - dx + dst.Width);
            if (x2 > x1)
            {
                Array.Copy(src.Pixels, (row * src.Width) + x1, dst.Pixels, (ty * dst.Width) + (x1 - sx + dx), x2 - x1);
            }
        }
    }

    // ================================================================== DOUBLE-BUFFER

    private void Dbe(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // GetVersion
                c.Reply(0, w => w.U8(1).U8(0).Zero(22));
                break;
            case 1:   // AllocateBackBufferName
                {
                    XWindow window = Window(r.U32());
                    uint name = r.U32();
                    if (window.IsInputOnly || window.IsRoot)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    if (!c.OwnsId(name) || _resources.ContainsKey(name))
                    {
                        throw new XProtocolError(XErrorCode.IDChoice, name);
                    }
                    if (!_backBuffers.TryGetValue(window, out var back))
                    {
                        back = (new XPixmap(name, c, window.Width, window.Height, window.Depth), []);
                        _backBuffers[window] = back;
                    }
                    back.Names.Add(name);
                    // 每个名字都能当可绘对象用:同一块缓冲挂在各自的 ID 下。
                    _resources[name] = back.Names.Count == 1 ? back.Buffer : new XPixmap(name, c, back.Buffer.Buffer);
                    break;
                }
            case 2:   // DeallocateBackBufferName
                {
                    uint name = r.U32();
                    (XWindow window, var back) = BackBufferOf(name);
                    back.Names.Remove(name);
                    _resources.Remove(name);
                    if (back.Names.Count == 0)
                    {
                        _backBuffers.Remove(window);
                    }
                    break;
                }
            case 3:   // SwapBuffers
                {
                    uint count = r.U32();
                    List<(XWindow Window, byte Action)> swaps = [];
                    for (uint i = 0; i < count; i++)
                    {
                        XWindow window = Window(r.U32());
                        byte action = r.U8();
                        r.Skip(3);
                        if (action > 3)
                        {
                            throw new XProtocolError(XErrorCode.Value, action);
                        }
                        if (!_backBuffers.ContainsKey(window))
                        {
                            throw new XProtocolError(XErrorCode.Match);
                        }
                        swaps.Add((window, action));
                    }
                    foreach ((XWindow window, byte action) in swaps)
                    {
                        SwapBackBuffer(window, action);
                    }
                    break;
                }
            case 4:   // BeginIdiom
            case 5:   // EndIdiom
                break;
            case 6:   // GetVisualInfo:两个 TrueColor 视觉都支持双缓冲
                {
                    uint count = r.U32();
                    int screens = count == 0 ? 1 : (int)count;
                    for (uint i = 0; i < count; i++)
                    {
                        CheckDrawable(r.U32());
                    }
                    c.Reply(0, w =>
                    {
                        w.U32((uint)screens).Zero(20);
                        for (int s = 0; s < screens; s++)
                        {
                            w.U32(2).U32(RootVisualId).U8(24).U8(0).Zero(2).U32(ArgbVisualId).U8(32).U8(0).Zero(2);
                        }
                    });
                    break;
                }
            case 7:   // GetBackBufferAttributes
                {
                    uint name = r.U32();
                    uint window = _backBuffers.FirstOrDefault(kv => kv.Value.Names.Contains(name)).Key?.Id ?? 0;
                    c.Reply(0, w => w.U32(window).Zero(20));
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private (XWindow Window, (XPixmap Buffer, List<uint> Names) Back) BackBufferOf(uint name)
    {
        foreach ((XWindow window, var back) in _backBuffers)
        {
            if (back.Names.Contains(name))
            {
                return (window, back);
            }
        }
        throw new XProtocolError((XErrorCode)DbeErrorBase, name);
    }

    /// <summary>后缓冲 → 窗口可见部分;然后按 swap-action 处理后缓冲。</summary>
    private void SwapBackBuffer(XWindow window, byte action)
    {
        PixelBuffer back = _backBuffers[window].Buffer.Buffer;
        PixelBuffer? saved = null;
        if (DrawTarget(window.Id, null) is { } target)
        {
            if (action == 2)   // Untouched:后缓冲拿到交换前的前缓冲内容
            {
                saved = new PixelBuffer(window.Width, window.Height, back.Depth);
                CopyPixels(target.Buffer, target.OriginX, target.OriginY, saved, 0, 0, window.Width, window.Height);
            }
            foreach (XRect rect in target.Clip.Rects)
            {
                XRect local = rect.Offset(-target.OriginX, -target.OriginY).Intersect(back.Bounds);
                CopyPixels(back, local.X, local.Y, target.Buffer, local.X + target.OriginX, local.Y + target.OriginY, local.Width, local.Height);
            }
            if (target.TopLevel is { } top)
            {
                MarkDamage(top, target.Clip);
            }
        }
        switch (action)
        {
            case 1:   // Background
                Array.Fill(back.Pixels, window.BackgroundPixel ?? 0);
                break;
            case 2 when saved is not null:
                Array.Copy(saved.Pixels, back.Pixels, Math.Min(saved.Pixels.Length, back.Pixels.Length));
                break;
        }
    }

    /// <summary>窗口改了尺寸:后缓冲跟着改(左上角对齐保留内容)。</summary>
    private void ResizeBackBuffer(XWindow window)
    {
        if (_backBuffers.TryGetValue(window, out var back))
        {
            back.Buffer.Buffer.Resize(window.Width, window.Height);
        }
    }

    /// <summary>窗口销毁 / 客户端断开:清掉后缓冲与重定向登记。</summary>
    private void CleanupCompositeDbe(XClient? client, XWindow? window)
    {
        if (window is not null)
        {
            _manualRedirects.Remove(window);
            if (_backBuffers.Remove(window, out var back))
            {
                foreach (uint name in back.Names)
                {
                    _resources.Remove(name);
                }
            }
        }
        if (client is not null)
        {
            // 断开的客户端分配的后缓冲名字已随它的资源一起释放:从登记里去掉,没名字了就整块丢掉。
            foreach ((XWindow w, var back) in _backBuffers.ToArray())
            {
                back.Names.RemoveAll(name => !_resources.ContainsKey(name));
                if (back.Names.Count == 0)
                {
                    _backBuffers.Remove(w);
                }
            }
            foreach (XWindow w in _manualRedirects.Where(kv => ReferenceEquals(kv.Value, client)).Select(kv => kv.Key).ToArray())
            {
                _manualRedirects.Remove(w);
            }
        }
    }
}
