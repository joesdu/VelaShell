// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The DAMAGE Extension, Version 1.1 —— §3「Data types」(DAMAGE、四种报告级别 RawRectangles / DeltaRectangles /
//   BoundingBox / NonEmpty)、§4「Errors」(BadDamage)、§5「Events」(DamageNotify:level | more 位、drawable、damage、
//   timestamp、area、geometry)、§6「Extension Initialization」(QueryVersion)、§7「Extension Requests」
//   (Create、Destroy、Subtract —— repair / parts 两个 XFIXES 区域、Add)
//
//   跟踪可绘对象上被画过的区域(坐标相对可绘对象原点)。窗口上的损伤包括画进它后代的内容。
//   截屏 / 远程桌面工具(x11vnc 一类)靠它只抓变了的部分。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>可绘对象 → 挂在它上面的损伤对象。空时绘图路径上的检查只是一次字典查找。</summary>
    private readonly Dictionary<XResource, List<XDamage>> _damageObjects = [];

    private XDamage DamageRes(uint id) =>
        Lookup<XDamage>(id) ?? throw new XProtocolError((XErrorCode)DamageErrorBase, id);

    private void DamageExtension(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U32(1).U32(1).Zero(16));
                break;
            case 1:   // Create
                {
                    uint id = r.U32();
                    uint drawableId = r.U32();
                    byte level = r.U8();
                    XResource drawable = Lookup<XResource>(drawableId) is XWindow or XPixmap
                        ? Lookup<XResource>(drawableId)!
                        : throw new XProtocolError(XErrorCode.Drawable, drawableId);
                    if (level > XDamage.NonEmpty)
                    {
                        throw new XProtocolError(XErrorCode.Value, level);
                    }
                    XDamage damage = new(id, c, drawable, level);
                    AddResource(c, damage);
                    if (!_damageObjects.TryGetValue(drawable, out List<XDamage>? list))
                    {
                        _damageObjects[drawable] = list = [];
                    }
                    list.Add(damage);
                    break;
                }
            case 2:   // Destroy
                DestroyDamage(DamageRes(r.U32()));
                break;
            case 3:   // Subtract
                {
                    XDamage damage = DamageRes(r.U32());
                    uint repairId = r.U32(), partsId = r.U32();
                    Region parts;
                    if (repairId == 0)
                    {
                        parts = damage.Accumulated;
                        damage.Accumulated = new Region();
                    }
                    else
                    {
                        Region repair = RegionRes(repairId).Region;
                        parts = damage.Accumulated.Clone().Intersect(repair);
                        damage.Accumulated.Subtract(repair);
                        // 还剩损伤时,按级别重新报一次(规范:Subtract 之后若区域非空,生成一个新的 DamageNotify)。
                        if (!damage.Accumulated.IsEmpty)
                        {
                            SendDamageNotify(damage, damage.Accumulated.Bounds, more: false);
                        }
                    }
                    if (partsId != 0)
                    {
                        RegionRes(partsId).Region = parts;
                    }
                    break;
                }
            case 4:   // Add:客户端自己报告的损伤(比如它用直接渲染画了东西)
                {
                    uint drawableId = r.U32();
                    XResource drawable = Lookup<XResource>(drawableId) ?? throw new XProtocolError(XErrorCode.Drawable, drawableId);
                    AccumulateDamage(drawable, RegionRes(r.U32()).Region);
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private void DestroyDamage(XDamage damage)
    {
        RemoveResource(damage.Id);
        if (_damageObjects.TryGetValue(damage.Drawable, out List<XDamage>? list))
        {
            list.Remove(damage);
            if (list.Count == 0)
            {
                _damageObjects.Remove(damage.Drawable);
            }
        }
    }

    /// <summary>可绘对象没了(窗口销毁 / 像素图释放):建在它上面的损伤对象一并销毁。</summary>
    private void CleanupDamage(XResource drawable)
    {
        if (_damageObjects.Remove(drawable, out List<XDamage>? gone))
        {
            foreach (XDamage d in gone)
            {
                RemoveResource(d.Id);
            }
        }
    }

    /// <summary>客户端断开:摘掉它建的损伤对象(资源本身已随资源表释放)。</summary>
    private void CleanupDamage(XClient client)
    {
        if (_damageObjects.Count == 0)
        {
            return;
        }
        foreach (List<XDamage> list in _damageObjects.Values)
        {
            list.RemoveAll(d => ReferenceEquals(d.Owner, client));
        }
        foreach (XResource key in _damageObjects.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToArray())
        {
            _damageObjects.Remove(key);
        }
    }

    // ------------------------------------------------------------------ 绘图路径上的钩子

    /// <summary>像素图上画过一块(缓冲坐标,即像素图坐标)。</summary>
    internal void NotePixmapDrawn(XPixmap pixmap, XRect rect)
    {
        if (_damageObjects.Count != 0 && !rect.IsEmpty && _damageObjects.ContainsKey(pixmap))
        {
            AccumulateDamage(pixmap, new Region(rect));
        }
    }

    /// <summary>顶层缓冲上画过一块(缓冲坐标):分给这个顶层树里挂了损伤对象的窗口,换成各自的窗口坐标。</summary>
    private void NoteWindowDrawn(XWindow top, Region bufferRegion)
    {
        if (_damageObjects.Count == 0 || bufferRegion.IsEmpty)
        {
            return;
        }
        // AccumulateDamage 只改损伤对象、发事件,不增删字典:直接遍历,不拷键。
        foreach (XResource key in _damageObjects.Keys)
        {
            if (key is not XWindow { IsViewable: true } window)
            {
                continue;
            }
            int ox, oy;
            if (window.IsRoot)
            {
                // 根窗口的内容就是所有顶层窗口拼起来的样子(合成管理器正是在根上建 Damage):
                // 顶层缓冲的坐标换到根坐标,缓冲原点 = 顶层内部左上角,即 -(顶层在根里的位置)。
                (int ax, int ay) = top.AbsoluteInner();
                (ox, oy) = (-ax, -ay);
            }
            else if (ReferenceEquals(window.TopLevel ?? window, top))
            {
                (ox, oy) = window.OffsetInTopLevel();
            }
            else
            {
                continue;
            }
            Region local = bufferRegion.Clone().Intersect(new XRect(ox, oy, window.Width, window.Height)).Translate(-ox, -oy);
            if (!local.IsEmpty)
            {
                AccumulateDamage(window, local);
            }
        }
    }

    /// <summary>把一块损伤并进可绘对象上的每个损伤对象,并按各自的级别发 DamageNotify。</summary>
    private void AccumulateDamage(XResource drawable, Region region)
    {
        if (!_damageObjects.TryGetValue(drawable, out List<XDamage>? list))
        {
            return;
        }
        foreach (XDamage damage in list)
        {
            bool wasEmpty = damage.Accumulated.IsEmpty;
            XRect oldBounds = damage.Accumulated.Bounds;
            switch (damage.Level)
            {
                case XDamage.Raw:
                    {
                        damage.Accumulated.Union(region);
                        IReadOnlyList<XRect> rects = region.Rects;
                        for (int i = 0; i < rects.Count; i++)
                        {
                            SendDamageNotify(damage, rects[i], more: i < rects.Count - 1);
                        }
                        break;
                    }
                case XDamage.Delta:
                    {
                        Region fresh = region.Clone().Subtract(damage.Accumulated);
                        damage.Accumulated.Union(fresh);
                        IReadOnlyList<XRect> rects = fresh.Rects;
                        for (int i = 0; i < rects.Count; i++)
                        {
                            SendDamageNotify(damage, rects[i], more: i < rects.Count - 1);
                        }
                        break;
                    }
                case XDamage.BoundingBox:
                    damage.Accumulated.Union(region);
                    if (damage.Accumulated.Bounds != oldBounds)
                    {
                        SendDamageNotify(damage, damage.Accumulated.Bounds, more: false);
                    }
                    break;
                default:   // NonEmpty
                    damage.Accumulated.Union(region);
                    if (wasEmpty && !damage.Accumulated.IsEmpty)
                    {
                        SendDamageNotify(damage, damage.Accumulated.Bounds, more: false);
                    }
                    break;
            }
        }
    }

    private void SendDamageNotify(XDamage damage, XRect area, bool more)
    {
        if (damage.Owner is not { Closed: false } client)
        {
            return;
        }
        (int gx, int gy, int gw, int gh) = damage.Drawable switch
        {
            XWindow w => (w.X, w.Y, w.Width, w.Height),
            XPixmap p => (0, 0, p.Width, p.Height),
            _ => (0, 0, 0, 0),
        };
        uint time = Now;
        client.Event(DamageEventBase, (byte)(damage.Level | (more ? 0x80 : 0)), w => w
            .U32(damage.Drawable.Id).U32(damage.Id).U32(time)
            .I16(area.X).I16(area.Y).U16((ushort)area.Width).U16((ushort)area.Height)
            .I16(gx).I16(gy).U16((ushort)gw).U16((ushort)gh));
    }
}
