// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Display Power Management Signaling (DPMS) Extension, Version 1.1 —— GetVersion 0、Capable 1、GetTimeouts 2、
//   SetTimeouts 3、Enable 4、Disable 5、ForceLevel 6、Info 7
//
//   服务端从不真的关显示器 —— 那是宿主桌面的事。这里如实记下客户端设的参数并回答查询。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private ushort _dpmsStandby, _dpmsSuspend, _dpmsOff;
    private bool _dpmsEnabled;
    private ushort _dpmsLevel;

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
