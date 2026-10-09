// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「SetScreenSaver」「GetScreenSaver」「ForceScreenSaver」
//   (timeout / interval 以秒计,−1 恢复默认;Reset 重置空闲计时)
//   MIT-SCREEN-SAVER Extension, Version 1.1 —— QueryVersion 0、QueryInfo 1(state、til-or-since、idle、kind)、
//   SelectInput 2、SetAttributes 3、UnsetAttributes 4、Suspend 5;ScreenSaverNotify 事件
//   Suspend 的语义:libXss 手册 XScreenSaverSuspend(3)(每个客户端各自计数、成对调用、断开即作废;1.1 版的协议文档没有写这一条)
//
//   服务端从不真的启动屏保、也不关显示器(DPMS 见 X11Server.Dpms.cs)—— 那是宿主桌面的事。这里如实记下客户端设的参数、
//   报出真实的空闲时间(xprintidle、xss-lock、视频播放器靠它判断用户是否离开)。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private short _saverTimeout;           // 秒;0 = 关
    private short _saverInterval;
    private byte _saverPreferBlanking = 1;
    private byte _saverAllowExposures = 1;
    private uint _lastActivity;

    private readonly Dictionary<XClient, uint> _saverSelections = [];

    /// <summary>用户有了输入(宿主注入、XTEST、宿主报的本机活动 <see cref="NoteUserActivity" />):空闲计时归零。</summary>
    private void NoteInputActivity()
    {
        // 指针每动一下都会走到这里:只有真有触发器挂在 IDLETIME 上时才求值(否则什么都不会因此成立)。
        bool watched = NoteIdleReset(IdleMilliseconds);
        _lastActivity = Now;
        if (watched)
        {
            EvaluateSync();   // IDLETIME 归零:等它负向跨越的报警器此刻触发
        }
    }

    private uint IdleMilliseconds => unchecked(Now - _lastActivity);

    // ------------------------------------------------------------------ 核心协议

    private void SetScreenSaver(XRequestReader r)
    {
        short timeout = r.I16(), interval = r.I16();
        byte preferBlanking = r.U8(), allowExposures = r.U8();
        if (timeout < -1 || interval < -1 || preferBlanking > 2 || allowExposures > 2)
        {
            throw new XProtocolError(XErrorCode.Value);
        }
        _saverTimeout = timeout == -1 ? (short)0 : timeout;
        _saverInterval = interval == -1 ? (short)0 : interval;
        _saverPreferBlanking = preferBlanking == 2 ? (byte)1 : preferBlanking;
        _saverAllowExposures = allowExposures == 2 ? (byte)1 : allowExposures;
    }

    private void GetScreenSaver(XClient c) =>
        c.Reply(0, w => w.U16((ushort)_saverTimeout).U16((ushort)_saverInterval).U8(_saverPreferBlanking).U8(_saverAllowExposures).Zero(18));

    private void ForceScreenSaver(XRequestReader r)
    {
        byte mode = r.Data;
        if (mode > 1)
        {
            throw new XProtocolError(XErrorCode.Value, mode);
        }
        if (mode == 0)
        {
            NoteInputActivity();   // Reset:播放器用它防屏保
            // 远端播放器靠它防的是远端的屏保,用户眼前的是本机的:隔一会儿告诉宿主一次,由它重置系统的空闲计时。
            long now = Environment.TickCount64;
            if (now - _lastSaverResetReport >= SaverResetReportInterval)
            {
                _lastSaverResetReport = now;
                _host.ScreenSaverReset();
            }
        }
    }

    /// <summary>ForceScreenSaver(Reset) 至多每这么久告诉宿主一次(毫秒;播放器常常每几秒发一次)。</summary>
    internal const long SaverResetReportInterval = 5000;

    private long _lastSaverResetReport = long.MinValue / 2;

    /// <summary>
    /// MIT-SCREEN-SAVER 1.1 的 Suspend(libXss 手册 XScreenSaverSuspend(3)):每个客户端各记各的次数,True 加一、False 减一(没挂起过的 False 不算);
    /// 任何一个客户端的次数大于 0 屏保与 DPMS 计时就停着,客户端断开时它的次数作废。服务端自己不画屏保,停不停计时都一样;
    /// 要紧的是用户眼前的本机屏保 —— 变化时告诉宿主(<see cref="IX11ServerHost.ScreenSaverSuspensionChanged" />),由它抑制系统屏保。
    /// 原先接受但什么也不做:远端 mpv 全屏放视频,本机照样黑屏。
    /// </summary>
    private readonly Dictionary<XClient, int> _saverSuspends = [];

    private bool _saverSuspendReported;

    private void SuspendScreenSaver(XClient c, bool suspend)
    {
        int count = _saverSuspends.GetValueOrDefault(c);
        if (suspend)
        {
            _saverSuspends[c] = count + 1;
        }
        else if (count > 1)
        {
            _saverSuspends[c] = count - 1;
        }
        else
        {
            _saverSuspends.Remove(c);
        }
        ReportSaverSuspension();
    }

    private void ReportSaverSuspension()
    {
        bool suspended = _saverSuspends.Count != 0;
        if (suspended != _saverSuspendReported)
        {
            _saverSuspendReported = suspended;
            _host.ScreenSaverSuspensionChanged(suspended);
        }
    }

    // ------------------------------------------------------------------ MIT-SCREEN-SAVER

    private void ScreenSaverExtension(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U16(1).U16(1).Zero(20));
                break;
            case 1:   // QueryInfo
                {
                    CheckDrawable(r.U32());
                    bool disabled = _saverTimeout == 0;
                    uint idle = IdleMilliseconds;
                    uint til = disabled ? 0 : (uint)Math.Max(0, (_saverTimeout * 1000L) - idle);
                    uint mask = _saverSelections.GetValueOrDefault(c);
                    c.Reply(disabled ? (byte)2 : (byte)0, w => w.U32(0).U32(til).U32(idle).U32(mask).U8(0).Zero(7));
                    break;
                }
            case 2:   // SelectInput:屏保从不启动,记下掩码只为 QueryInfo 回显
                {
                    CheckDrawable(r.U32());
                    uint mask = r.U32();
                    if (mask == 0)
                    {
                        _saverSelections.Remove(c);
                    }
                    else
                    {
                        _saverSelections[c] = mask;
                    }
                    break;
                }
            case 3:   // SetAttributes:屏保窗口的属性 —— 我们不画屏保,接受即可
            case 4:   // UnsetAttributes
                break;
            case 5:   // Suspend(1.1):suspend 是 CARD32 的布尔
                SuspendScreenSaver(c, r.U32() != 0);
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private void CheckDrawable(uint id)
    {
        if (Use<Resources.XResource>(id) is not (Windowing.XWindow or Resources.XPixmap))
        {
            throw new XProtocolError(XErrorCode.Drawable, id);
        }
    }

    /// <summary>客户端断开:它的屏保事件选择与挂起次数作废。</summary>
    private void CleanupScreenSaver(XClient client)
    {
        _saverSelections.Remove(client);
        if (_saverSuspends.Remove(client))
        {
            ReportSaverSuspension();
        }
    }
}
