// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Double Buffer Extension (DBE), Version 1.0 —— §「Types」(SWAPACTION:Undefined / Background / Untouched / Copied)、
//   §「Errors」(BadBuffer)、GetVersion 0、AllocateBackBufferName 1、DeallocateBackBufferName 2、SwapBuffers 3、
//   BeginIdiom 4、EndIdiom 5、GetVisualInfo 6、GetBackBufferAttributes 7
//
//   后缓冲是一个挂在后缓冲 ID 下的像素图,核心绘图请求照常能画进去;SwapBuffers 把它拷到窗口可见的部分。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>DBE:窗口 → 它的后缓冲(可以有多个名字指向同一块)。</summary>
    private readonly Dictionary<XWindow, (XPixmap Buffer, List<uint> Names)> _backBuffers = [];

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
                PixelBuffer.CopyRect(target.Buffer, target.OriginX, target.OriginY, saved, 0, 0, window.Width, window.Height);
            }
            foreach (XRect rect in target.Clip.Rects)
            {
                XRect local = rect.Offset(-target.OriginX, -target.OriginY).Intersect(back.Bounds);
                PixelBuffer.CopyRect(back, local.X, local.Y, target.Buffer, local.X + target.OriginX, local.Y + target.OriginY, local.Width, local.Height);
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

    /// <summary>窗口销毁:它的后缓冲连同所有名字一起释放。</summary>
    private void CleanupDbe(XWindow window)
    {
        if (_backBuffers.Remove(window, out var back))
        {
            foreach (uint name in back.Names)
            {
                _resources.Remove(name);
            }
        }
    }

    /// <summary>客户端断开:它分配的后缓冲名字已随它的资源一起释放,从登记里去掉;没名字了就整块丢掉。</summary>
    private void CleanupDbe(XClient client)
    {
        foreach ((XWindow w, var back) in _backBuffers.ToArray())
        {
            back.Names.RemoveAll(name => !_resources.ContainsKey(name));
            if (back.Names.Count == 0)
            {
                _backBuffers.Remove(w);
            }
        }
    }
}
