// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「SetScreenSaver」「GetScreenSaver」「ForceScreenSaver」
//   (timeout / interval 以秒计,−1 恢复默认;Reset 重置空闲计时)
//   MIT-SCREEN-SAVER Extension, Version 1.1 —— QueryVersion 0、QueryInfo 1(state、til-or-since、idle、kind)、
//   SelectInput 2、SetAttributes 3、UnsetAttributes 4、Suspend 5;ScreenSaverNotify 事件
//   Display Power Management Signaling (DPMS) Extension, Version 1.1 —— GetVersion 0、Capable 1、GetTimeouts 2、
//   SetTimeouts 3、Enable 4、Disable 5、ForceLevel 6、Info 7
//
//   服务端从不真的启动屏保、也不关显示器 —— 那是宿主桌面的事。这里如实记下客户端设的参数、
//   报出真实的空闲时间(xprintidle、xss-lock、视频播放器靠它判断用户是否离开)。

using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private const byte ScreenSaverMajor = 137;
    private const byte ScreenSaverEventBase = 69;
    private const byte DpmsMajor = 138;

    private short _saverTimeout;           // 秒;0 = 关
    private short _saverInterval;
    private byte _saverPreferBlanking = 1;
    private byte _saverAllowExposures = 1;
    private uint _lastActivity;

    private readonly Dictionary<XClient, uint> _saverSelections = [];

    private ushort _dpmsStandby, _dpmsSuspend, _dpmsOff;
    private bool _dpmsEnabled;
    private ushort _dpmsLevel;

    /// <summary>用户有了输入(宿主注入或 XTEST):空闲计时归零。</summary>
    private void NoteUserActivity() => _lastActivity = Now;

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
            NoteUserActivity();   // Reset:播放器用它防屏保
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
            case 5:   // Suspend
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private void CheckDrawable(uint id)
    {
        if (Lookup<Resources.XResource>(id) is not (Windowing.XWindow or Resources.XPixmap))
        {
            throw new XProtocolError(XErrorCode.Drawable, id);
        }
    }

    // ------------------------------------------------------------------ DPMS

    private void Dpms(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // GetVersion
                c.Reply(0, w => w.U16(1).U16(1).Zero(20));
                break;
            case 1:   // Capable
                c.Reply(0, w => w.Bool(true).Zero(23));
                break;
            case 2:   // GetTimeouts
                c.Reply(0, w => w.U16(_dpmsStandby).U16(_dpmsSuspend).U16(_dpmsOff).Zero(18));
                break;
            case 3:   // SetTimeouts:非零值必须 standby ≤ suspend ≤ off
                {
                    ushort standby = r.U16(), suspend = r.U16(), off = r.U16();
                    if ((standby != 0 && suspend != 0 && standby > suspend) || (suspend != 0 && off != 0 && suspend > off)
                        || (standby != 0 && off != 0 && standby > off))
                    {
                        throw new XProtocolError(XErrorCode.Value);
                    }
                    (_dpmsStandby, _dpmsSuspend, _dpmsOff) = (standby, suspend, off);
                    break;
                }
            case 4:   // Enable
                _dpmsEnabled = true;
                break;
            case 5:   // Disable
                _dpmsEnabled = false;
                break;
            case 6:   // ForceLevel:0 On、1 Standby、2 Suspend、3 Off;DPMS 关着时是 BadMatch
                {
                    ushort level = r.U16();
                    if (level > 3)
                    {
                        throw new XProtocolError(XErrorCode.Value, level);
                    }
                    if (!_dpmsEnabled)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    _dpmsLevel = level;
                    break;
                }
            case 7:   // Info
                c.Reply(0, w => w.U16(_dpmsLevel).Bool(_dpmsEnabled).Zero(21));
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }
}
