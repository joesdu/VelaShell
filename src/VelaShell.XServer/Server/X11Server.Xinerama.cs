// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   XINERAMA / PanoramiX(xorgproto panoramiXproto 的线上定义)—— PanoramiXQueryVersion 0、PanoramiXGetState 1、
//   PanoramiXGetScreenCount 2、PanoramiXGetScreenSize 3、XineramaIsActive 4、XineramaQueryScreens 5
//
//   只回答问题、不改状态:显示器的个数与矩形取自显示器布局(X11Server.Monitors.cs)。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private void Xinerama(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // PanoramiXQueryVersion
                c.Reply(0, w => w.U16(1).U16(1).Zero(20));
                break;
            case 1:   // PanoramiXGetState
                {
                    XWindow window = Window(r.U32());
                    c.Reply(1, w => w.U32(window.Id).Zero(20));
                    break;
                }
            case 2:   // PanoramiXGetScreenCount
                {
                    XWindow window = Window(r.U32());
                    c.Reply((byte)_monitors.Count, w => w.U32(window.Id).Zero(20));
                    break;
                }
            case 3:   // PanoramiXGetScreenSize
                {
                    XWindow window = Window(r.U32());
                    uint screen = r.U32();
                    if (screen >= _monitors.Count)
                    {
                        throw new XProtocolError(XErrorCode.Value, screen);
                    }
                    XMonitor m = _monitors[(int)screen];
                    c.Reply(0, w => w.U32((uint)m.Width).U32((uint)m.Height).U32(window.Id).U32(screen).Zero(8));
                    break;
                }
            case 4:   // XineramaIsActive
                c.Reply(0, w => w.U32(1).Zero(20));
                break;
            case 5:   // XineramaQueryScreens:主显示器排第一(老程序把第 0 块当主屏)
                {
                    List<XMonitor> ordered = [.. _monitors.Where(m => m.Primary), .. _monitors.Where(m => !m.Primary)];
                    c.Reply(0, w =>
                    {
                        w.U32((uint)ordered.Count).Zero(20);
                        foreach (XMonitor m in ordered)
                        {
                            w.I16(m.X).I16(m.Y).U16((ushort)m.Width).U16((ushort)m.Height);
                        }
                    });
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }
}
