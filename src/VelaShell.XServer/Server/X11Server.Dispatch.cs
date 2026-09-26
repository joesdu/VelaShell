// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 4 节「Errors」(出错时请求不产生任何效果、错误带序号与操作码)、
//   附录 B「Requests」(操作码表)

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>执行一条请求。只在执行线程上调用。</summary>
    private void ExecuteRequest(XClient client, byte[] request)
    {
        client.PendingRequests.Release();   // 这条请求已从队列里取出:读端可以再读一条
        if (client.Closed)
        {
            return;
        }
        unchecked
        {
            client.Sequence++;
        }
        XRequestReader r = new(request, client.BigEndian);
        ushort minor = r.Opcode >= XOpcode.FirstExtension ? r.Data : (ushort)0;
        if (_options.Log is not null)
        {
            if (client.RecentRequests.Count == 8)
            {
                client.RecentRequests.Dequeue();
            }
            client.RecentRequests.Enqueue($"{r.Opcode}.{minor}");
        }
        try
        {
            Dispatch(client, r);
        }
        catch (XProtocolError error)
        {
            Log($"{client} #{client.Sequence} opcode {r.Opcode}.{minor}: Bad{error.Code} 0x{error.BadValue:x}"
                + $"(之前:{string.Join(' ', client.RecentRequests)})");
            client.Error(error.Code, error.BadValue, minor, r.Opcode);
        }
        catch (Exception ex)
        {
            Log($"{client} #{client.Sequence} opcode {r.Opcode}.{minor}: BadImplementation {ex}");
            client.Error(XErrorCode.Implementation, 0, minor, r.Opcode);
        }
    }

    private void Dispatch(XClient c, XRequestReader r)
    {
        switch (r.Opcode)
        {
            case XOpcode.CreateWindow: CreateWindow(c, r); break;
            case XOpcode.ChangeWindowAttributes: ChangeWindowAttributes(c, r); break;
            case XOpcode.GetWindowAttributes: GetWindowAttributes(c, r); break;
            case XOpcode.DestroyWindow: DestroyWindow(c, r); break;
            case XOpcode.DestroySubwindows: DestroySubwindows(r); break;
            case XOpcode.ChangeSaveSet: ChangeSaveSet(c, r); break;
            case XOpcode.ReparentWindow: ReparentWindow(r); break;
            case XOpcode.MapWindow: MapWindow(c, r); break;
            case XOpcode.MapSubwindows: MapSubwindows(c, r); break;
            case XOpcode.UnmapWindow: UnmapWindow(r); break;
            case XOpcode.UnmapSubwindows: UnmapSubwindows(r); break;
            case XOpcode.ConfigureWindow: ConfigureWindow(c, r); break;
            case XOpcode.CirculateWindow: CirculateWindow(r); break;
            case XOpcode.GetGeometry: GetGeometry(c, r); break;
            case XOpcode.QueryTree: QueryTree(c, r); break;
            case XOpcode.InternAtom: InternAtom(c, r); break;
            case XOpcode.GetAtomName: GetAtomName(c, r); break;
            case XOpcode.ChangeProperty: ChangeProperty(c, r); break;
            case XOpcode.DeleteProperty: DeleteProperty(r); break;
            case XOpcode.GetProperty: GetProperty(c, r); break;
            case XOpcode.ListProperties: ListProperties(c, r); break;
            case XOpcode.SetSelectionOwner: SetSelectionOwner(c, r); break;
            case XOpcode.GetSelectionOwner: GetSelectionOwner(c, r); break;
            case XOpcode.ConvertSelection: ConvertSelection(c, r); break;
            case XOpcode.SendEvent: SendEvent(c, r); break;
            case XOpcode.GrabPointer: GrabPointer(c, r); break;
            case XOpcode.UngrabPointer: UngrabPointer(c); break;
            case XOpcode.GrabButton: GrabButton(c, r); break;
            case XOpcode.UngrabButton: UngrabButton(c, r); break;
            case XOpcode.ChangeActivePointerGrab: ChangeActivePointerGrab(c, r); break;
            case XOpcode.GrabKeyboard: GrabKeyboard(c, r); break;
            case XOpcode.UngrabKeyboard: UngrabKeyboard(c); break;
            case XOpcode.GrabKey: GrabKey(c, r); break;
            case XOpcode.UngrabKey: UngrabKey(c, r); break;
            case XOpcode.AllowEvents: AllowEvents(c, r.Data); break;
            case XOpcode.GrabServer: _serverGrabber = c; break;
            case XOpcode.UngrabServer: if (ReferenceEquals(_serverGrabber, c)) { ReleaseServerGrab(); } break;
            case XOpcode.QueryPointer: QueryPointer(c, r); break;
            case XOpcode.GetMotionEvents: c.MotionHint = default; c.Reply(0, w => w.U32(0).Zero(20)); break;
            case XOpcode.TranslateCoordinates: TranslateCoordinates(c, r); break;
            case XOpcode.WarpPointer: WarpPointer(r); break;
            case XOpcode.SetInputFocus: SetInputFocus(r); break;
            case XOpcode.GetInputFocus: GetInputFocus(c); break;
            case XOpcode.QueryKeymap: c.Reply(0, w => w.Bytes(_keysDown)); break;
            case XOpcode.OpenFont: OpenFont(c, r); break;
            case XOpcode.CloseFont: CloseFont(r); break;
            case XOpcode.QueryFont: QueryFont(c, r); break;
            case XOpcode.QueryTextExtents: QueryTextExtents(c, r); break;
            case XOpcode.ListFonts: ListFonts(c, r); break;
            case XOpcode.ListFontsWithInfo: ListFontsWithInfo(c, r); break;
            case XOpcode.SetFontPath: break;   // 字体来自内置目录,路径无意义;接受但不生效
            case XOpcode.GetFontPath: GetFontPath(c); break;
            case XOpcode.CreatePixmap: CreatePixmap(c, r); break;
            case XOpcode.FreePixmap: FreePixmap(r); break;
            case XOpcode.CreateGC: CreateGC(c, r); break;
            case XOpcode.ChangeGC: ChangeGC(r); break;
            case XOpcode.CopyGC: CopyGC(r); break;
            case XOpcode.SetDashes: SetDashes(r); break;
            case XOpcode.SetClipRectangles: SetClipRectangles(r); break;
            case XOpcode.FreeGC: FreeGC(r); break;
            case XOpcode.ClearArea: ClearArea(r); break;
            case XOpcode.CopyArea: CopyArea(c, r); break;
            case XOpcode.CopyPlane: CopyPlane(c, r); break;
            case XOpcode.PolyPoint: PolyPoint(r); break;
            case XOpcode.PolyLine: PolyLine(r); break;
            case XOpcode.PolySegment: PolySegment(r); break;
            case XOpcode.PolyRectangle: PolyRectangle(r); break;
            case XOpcode.PolyArc: PolyArc(r); break;
            case XOpcode.FillPoly: FillPoly(r); break;
            case XOpcode.PolyFillRectangle: PolyFillRectangle(r); break;
            case XOpcode.PolyFillArc: PolyFillArc(r); break;
            case XOpcode.PutImage: PutImage(r); break;
            case XOpcode.GetImage: GetImage(c, r); break;
            case XOpcode.PolyText8: PolyText(r, wide: false); break;
            case XOpcode.PolyText16: PolyText(r, wide: true); break;
            case XOpcode.ImageText8: ImageText(r, wide: false); break;
            case XOpcode.ImageText16: ImageText(r, wide: true); break;
            case XOpcode.CreateColormap: CreateColormap(c, r); break;
            case XOpcode.FreeColormap: FreeColormap(r); break;
            case XOpcode.CopyColormapAndFree: CopyColormapAndFree(c, r); break;
            case XOpcode.InstallColormap: break;   // 只有一种 TrueColor 视觉,安装与否没有区别
            case XOpcode.UninstallColormap: break;
            case XOpcode.ListInstalledColormaps: c.Reply(0, w => w.U16(1).Zero(22).U32(DefaultColormapId)); break;
            case XOpcode.AllocColor: AllocColor(c, r); break;
            case XOpcode.AllocNamedColor: AllocNamedColor(c, r); break;
            case XOpcode.AllocColorCells: throw new XProtocolError(XErrorCode.Alloc);
            case XOpcode.AllocColorPlanes: throw new XProtocolError(XErrorCode.Alloc);
            case XOpcode.FreeColors: break;
            case XOpcode.StoreColors: break;       // TrueColor 只读;照 X.Org 的宽容做法不报错
            case XOpcode.StoreNamedColor: break;
            case XOpcode.QueryColors: QueryColors(c, r); break;
            case XOpcode.LookupColor: LookupColor(c, r); break;
            case XOpcode.CreateCursor: CreateCursor(c, r); break;
            case XOpcode.CreateGlyphCursor: CreateGlyphCursor(c, r); break;
            case XOpcode.FreeCursor: FreeCursor(r); break;
            case XOpcode.RecolorCursor: break;
            case XOpcode.QueryBestSize: QueryBestSize(c, r); break;
            case XOpcode.QueryExtension: QueryExtension(c, r); break;
            case XOpcode.ListExtensions: ListExtensions(c); break;
            case XOpcode.ChangeKeyboardMapping: ChangeKeyboardMapping(c, r); break;
            case XOpcode.GetKeyboardMapping: GetKeyboardMapping(c, r); break;
            case XOpcode.ChangeKeyboardControl: break;
            case XOpcode.GetKeyboardControl: GetKeyboardControl(c); break;
            case XOpcode.Bell: Bell(r); break;
            case XOpcode.ChangePointerControl: break;
            case XOpcode.GetPointerControl: c.Reply(0, w => w.U16(2).U16(1).U16(4).Zero(18)); break;
            case XOpcode.SetScreenSaver: SetScreenSaver(r); break;
            case XOpcode.GetScreenSaver: GetScreenSaver(c); break;
            case XOpcode.ChangeHosts: break;
            case XOpcode.ListHosts: c.Reply(0, w => w.U16(0).Zero(22)); break;
            case XOpcode.SetAccessControl: break;
            case XOpcode.SetCloseDownMode: c.CloseDownMode = r.Data; break;
            case XOpcode.KillClient: KillClient(r); break;
            case XOpcode.RotateProperties: RotateProperties(r); break;
            case XOpcode.ForceScreenSaver: ForceScreenSaver(r); break;
            case XOpcode.SetPointerMapping: c.Reply(0, w => w.Zero(24)); break;
            case XOpcode.GetPointerMapping: GetPointerMapping(c); break;
            case XOpcode.SetModifierMapping: SetModifierMapping(c, r); break;
            case XOpcode.GetModifierMapping: GetModifierMapping(c); break;
            case XOpcode.NoOperation: break;
            default:
                if (r.Opcode >= XOpcode.FirstExtension && _extensionsByOpcode.TryGetValue(r.Opcode, out Extension? ext) && ext.IsVisibleTo(c))
                {
                    ext.Handle(c, r);
                    break;
                }
                throw new XProtocolError(XErrorCode.Request);
        }
    }
}
