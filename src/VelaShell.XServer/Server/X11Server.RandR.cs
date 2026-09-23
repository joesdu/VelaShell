// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Resize and Rotate Extension (RandR), Version 1.5 —— §4「Protocol Types」(MODEINFO、MONITORINFO)、
//   §5「Extension Initialization」(QueryVersion)、§7.1「Protocol Requests added with version 1.0」
//   (GetScreenInfo、SelectInput)、§7.2「…version 1.2」(GetScreenSizeRange、GetScreenResources、GetOutputInfo、
//   输出属性、GetCrtcInfo、Gamma)、§7.3「…version 1.3」(GetScreenResourcesCurrent、GetCrtcTransform、
//   GetPanning、GetOutputPrimary)、§7.4「…version 1.4」(GetProviders)、§7.5「…version 1.5」(GetMonitors)、
//   附录「Protocol Encoding」(次操作码、错误 BadOutput / BadCrtc / BadMode / BadProvider)
//
//   只读:一台虚拟显示器(一个 CRTC、一个输出、一个模式)覆盖整个根窗口。rootless 模式下窗口
//   摆在哪由宿主决定,客户端问显示器只是为了取尺寸与 DPI;改配置的请求一律回 Failed 或 BadAccess。

using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private const byte RandRMajor = 132;
    private const byte RandREventBase = 67;   // ScreenChangeNotify = +0,Notify = +1
    private const byte RandRErrorBase = 129;  // BadOutput = +0,BadCrtc = +1,BadMode = +2,BadProvider = +3

    // 服务端自己的资源 ID,落在任何客户端的 resource-base 之外(同根窗口、默认颜色表)。
    private const uint RandRCrtcId = 0x40, RandROutputId = 0x41, RandRModeId = 0x42;

    private const byte RandRStatusSuccess = 0, RandRStatusFailed = 3;
    private const ushort RandRRotate0 = 1;
    private const ushort RandRRefreshRate = 60;
    private const ushort RandRGammaSize = 256;

    private static readonly byte[] RandROutputName = "default"u8.ToArray();

    private (int Width, int Height) ScreenMillimeters() =>
        ((int)Math.Round(_options.ScreenWidth * 25.4 / _options.Dpi), (int)Math.Round(_options.ScreenHeight * 25.4 / _options.Dpi));

    private void RandR(XClient c, XRequestReader r)
    {
        int width = _options.ScreenWidth, height = _options.ScreenHeight;
        (int mmW, int mmH) = ScreenMillimeters();
        switch (r.Data)
        {
            case 0:   // QueryVersion
                {
                    uint major = r.U32(), minor = r.U32();
                    (uint maj, uint min) = major > 1 || (major == 1 && minor >= 5) ? (1u, 5u) : (major, minor);
                    c.Reply(0, w => w.U32(maj).U32(min).Zero(16));
                    break;
                }
            case 2:   // SetScreenConfig:只读,回 Failed
                {
                    uint now = Now;
                    c.Reply(RandRStatusFailed, w => w.U32(now).U32(0).U32(RootWindowId).U16(0).Zero(10));
                    break;
                }
            case 4:   // SelectInput:配置从不变,记不记都不会有事件;只校验窗口
                _ = Window(r.U32());
                break;
            case 5:   // GetScreenInfo(1.0):一个尺寸、一个刷新率
                {
                    _ = Window(r.U32());
                    c.Reply((byte)RandRRotate0, w => w
                        .U32(RootWindowId).U32(0).U32(0)
                        .U16(1).U16(0).U16(RandRRotate0).U16(RandRRefreshRate).U16(2).Zero(2)
                        .U16((ushort)width).U16((ushort)height).U16((ushort)mmW).U16((ushort)mmH)
                        .U16(1).U16(RandRRefreshRate));
                    break;
                }
            case 6:   // GetScreenSizeRange:只有当前尺寸
                _ = Window(r.U32());
                c.Reply(0, w => w.U16((ushort)width).U16((ushort)height).U16((ushort)width).U16((ushort)height).Zero(16));
                break;
            case 8:   // GetScreenResources
            case 25:  // GetScreenResourcesCurrent
                {
                    _ = Window(r.U32());
                    byte[] modeName = XWire.Latin1.GetBytes($"{width}x{height}");
                    c.Reply(0, w =>
                    {
                        w.U32(0).U32(0).U16(1).U16(1).U16(1).U16((ushort)modeName.Length).Zero(8)
                            .U32(RandRCrtcId).U32(RandROutputId);
                        WriteModeInfo(w, width, height, modeName.Length);
                        w.Bytes(modeName).Pad4();
                    });
                    break;
                }
            case 9:   // GetOutputInfo
                {
                    CheckRandRId(r.U32(), RandROutputId, 0);
                    c.Reply(RandRStatusSuccess, w => w
                        .U32(0).U32(RandRCrtcId).U32((uint)mmW).U32((uint)mmH)
                        .U8(0).U8(0)                                  // Connected、SubPixelUnknown
                        .U16(1).U16(1).U16(1).U16(0).U16((ushort)RandROutputName.Length)
                        .U32(RandRCrtcId).U32(RandRModeId).Bytes(RandROutputName).Pad4());
                    break;
                }
            case 10:  // ListOutputProperties:没有输出属性
                CheckRandRId(r.U32(), RandROutputId, 0);
                c.Reply(0, w => w.U16(0).Zero(22));
                break;
            case 11:  // QueryOutputProperty
                {
                    CheckRandRId(r.U32(), RandROutputId, 0);
                    uint property = r.U32();
                    throw new XProtocolError(XErrorCode.Name, property);
                }
            case 15:  // GetOutputProperty:属性不存在 → type None、format 0
                CheckRandRId(r.U32(), RandROutputId, 0);
                c.Reply(0, w => w.U32(0).U32(0).U32(0).Zero(12));
                break;
            case 20:  // GetCrtcInfo
                {
                    CheckRandRId(r.U32(), RandRCrtcId, 1);
                    c.Reply(RandRStatusSuccess, w => w
                        .U32(0).I16(0).I16(0).U16((ushort)width).U16((ushort)height)
                        .U32(RandRModeId).U16(RandRRotate0).U16(RandRRotate0).U16(1).U16(1)
                        .U32(RandROutputId).U32(RandROutputId));
                    break;
                }
            case 21:  // SetCrtcConfig:只读,回 Failed
                {
                    uint now = Now;
                    c.Reply(RandRStatusFailed, w => w.U32(now).Zero(20));
                    break;
                }
            case 22:  // GetCrtcGammaSize:报 256 级(不能改,见 SetCrtcGamma)
                CheckRandRId(r.U32(), RandRCrtcId, 1);
                c.Reply(0, w => w.U16(RandRGammaSize).Zero(22));
                break;
            case 23:  // GetCrtcGamma:线性斜坡,红绿蓝相同
                CheckRandRId(r.U32(), RandRCrtcId, 1);
                c.Reply(0, w =>
                {
                    w.U16(RandRGammaSize).Zero(22);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        for (int i = 0; i < RandRGammaSize; i++)
                        {
                            w.U16((ushort)(i * 0x101));
                        }
                    }
                    w.Pad4();
                });
                break;
            case 27:  // GetCrtcTransform:单位变换,无滤镜
                CheckRandRId(r.U32(), RandRCrtcId, 1);
                c.Reply(0, w =>
                {
                    WriteIdentityTransform(w);
                    w.Bool(false).Zero(3);
                    WriteIdentityTransform(w);
                    w.Zero(4).U16(0).U16(0).U16(0).U16(0);
                });
                break;
            case 28:  // GetPanning:不平移
                CheckRandRId(r.U32(), RandRCrtcId, 1);
                c.Reply(RandRStatusSuccess, w => w.U32(0).Zero(24));
                break;
            case 29:  // SetPanning:只读,回 Failed
                {
                    uint now = Now;
                    c.Reply(RandRStatusFailed, w => w.U32(now).Zero(20));
                    break;
                }
            case 31:  // GetOutputPrimary
                _ = Window(r.U32());
                c.Reply(0, w => w.U32(RandROutputId).Zero(20));
                break;
            case 32:  // GetProviders:没有 provider
                _ = Window(r.U32());
                c.Reply(0, w => w.U32(0).U16(0).Zero(18));
                break;
            case 42:  // GetMonitors
                {
                    _ = Window(r.U32());
                    uint name = Intern("default");
                    c.Reply(0, w => w
                        .U32(0).U32(1).U32(1).Zero(12)
                        .U32(name).Bool(true).Bool(true).U16(1)
                        .I16(0).I16(0).U16((ushort)width).U16((ushort)height).U32((uint)mmW).U32((uint)mmH)
                        .U32(RandROutputId));
                    break;
                }
            case 7:   // SetScreenSize
            case 12:  // ConfigureOutputProperty
            case 13:  // ChangeOutputProperty
            case 14:  // DeleteOutputProperty
            case 16:  // CreateMode
            case 17:  // DestroyMode
            case 18:  // AddOutputMode
            case 19:  // DeleteOutputMode
            case 24:  // SetCrtcGamma
            case 26:  // SetCrtcTransform
            case 30:  // SetOutputPrimary
            case 43:  // SetMonitor
            case 44:  // DeleteMonitor
                throw new XProtocolError(XErrorCode.Access);
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>RANDR 的 CRTC / 输出 ID 只有一个;对不上就是对应的 RANDR 错误(BadOutput = +0,BadCrtc = +1)。</summary>
    private static void CheckRandRId(uint id, uint expected, int errorOffset)
    {
        if (id != expected)
        {
            throw new XProtocolError((XErrorCode)(RandRErrorBase + errorOffset), id);
        }
    }

    /// <summary>MODEINFO:行总长 = 宽、帧总行数 = 高,点时钟按 60 Hz 反推(刷新率 = 点时钟 / (htotal × vtotal))。</summary>
    private static void WriteModeInfo(XWriter w, int width, int height, int nameLength) => w
        .U32(RandRModeId).U16((ushort)width).U16((ushort)height).U32((uint)(width * height * RandRRefreshRate))
        .U16((ushort)width).U16((ushort)width).U16((ushort)width).U16(0)
        .U16((ushort)height).U16((ushort)height).U16((ushort)height)
        .U16((ushort)nameLength).U32(0);

    /// <summary>3×3 的 16.16 定点单位矩阵。</summary>
    private static void WriteIdentityTransform(XWriter w)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                w.U32(row == col ? 0x10000u : 0u);
            }
        }
    }
}
