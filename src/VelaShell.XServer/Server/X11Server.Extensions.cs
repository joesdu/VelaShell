// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「QueryExtension」「ListExtensions」(主操作码 128 起分配,
//   扩展事件码 64–127、扩展错误码 128–255)
//   Generic Event Extension, Version 1.0 —— GEQueryVersion 0(客户端声明版本之后才可以收 GenericEvent)
//
//   扩展注册表:每个扩展的主操作码、事件 / 错误编号都在这里的一张表里分配,注册时检查互不重叠;
//   扩展自己的状态在客户端断开、窗口销毁时经注册的钩子清掉。新增扩展:在表里加编号,在 InitExtensions 里登记,
//   请求处理放进自己的 partial 文件 —— 不用再去改连接收尾与窗口销毁的代码。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    // ------------------------------------------------------------------ 编号表(主操作码 / 第一个事件码 / 第一个错误码)

    private const byte BigRequestsMajor = 128;
    private const byte XcMiscMajor = 129;
    private const byte ShapeMajor = 130, ShapeEventBase = 64;                                  // ShapeNotify
    private const byte XFixesMajor = 131, XFixesEventBase = 65, XFixesErrorBase = 128;         // SelectionNotify +0、CursorNotify +1;BadRegion +0
    private const byte RandRMajor = 132, RandREventBase = 67, RandRErrorBase = 129;            // ScreenChangeNotify +0、Notify +1;BadOutput / BadCrtc / BadMode / BadProvider
    private const byte RenderMajor = 133, RenderErrorBase = 133;                               // PictFormat +0、Picture +1、PictOp +2、GlyphSet +3、Glyph +4
    private const byte GenericEventMajor = 134;
    private const byte XTestMajor = 135;
    private const byte XineramaMajor = 136;
    private const byte ScreenSaverMajor = 137, ScreenSaverEventBase = 69;                      // ScreenSaverNotify
    private const byte DpmsMajor = 138;
    private const byte XResMajor = 139;
    private const byte SyncMajor = 140, SyncEventBase = 70, SyncErrorBase = 138;               // CounterNotify +0、AlarmNotify +1;Counter +0、Alarm +1、Fence +2
    private const byte DamageMajor = 141, DamageEventBase = 72, DamageErrorBase = 141;         // DamageNotify;BadDamage
    private const byte CompositeMajor = 142;
    private const byte DbeMajor = 143, DbeErrorBase = 142;                                     // BadBuffer
    private const byte PresentMajor = 144;                                                     // 事件走 GenericEvent
    private const byte XInputMajor = 145, XInputEventBase = 74, XInputErrorBase = 144;         // XI 1.x 的 17 个事件;BadDevice +0 … BadClass +4
    private const byte XkbMajor = 146, XkbEventBase = 73, XkbErrorBase = 143;                  // 一个事件码(子类型在 xkbType);BadKeyboard
    private const byte ShmMajor = 147, ShmEventBase = 91, ShmErrorBase = 149;                  // Completion;BadShmSeg
    internal const byte GlxMajor = 148, GlxEventBase = 92, GlxErrorBase = 150;                  // PbufferClobber;BadContext +0 … GLXBadProfileARB +13;internal:GlxExtension 在类外

    /// <summary>按名字查(QueryExtension)。</summary>
    private readonly Dictionary<string, Extension> _extensions = [with(StringComparer.Ordinal)];

    /// <summary>按主操作码查(请求分派)。</summary>
    private readonly Dictionary<byte, Extension> _extensionsByOpcode = [];

    /// <summary>注册顺序(清理钩子按这个顺序调)。</summary>
    private readonly List<Extension> _extensionList = [];

    private void InitExtensions()
    {
        Register(new Extension("BIG-REQUESTS", BigRequestsMajor, BigRequests));
        Register(new Extension("XC-MISC", XcMiscMajor, XcMisc));
        Register(new Extension("SHAPE", ShapeMajor, Shape) { FirstEvent = ShapeEventBase, EventCount = 1 });
        Register(new Extension("XFIXES", XFixesMajor, XFixes)
        {
            FirstEvent = XFixesEventBase,
            EventCount = 2,
            FirstError = XFixesErrorBase,
            ErrorCount = 1,
            ClientClosed = CleanupXFixes,
            WindowDestroyed = CleanupXFixes,
        });
        Register(new Extension("RANDR", RandRMajor, RandR)
        {
            FirstEvent = RandREventBase,
            EventCount = 2,
            FirstError = RandRErrorBase,
            ErrorCount = 4,
            ClientClosed = CleanupRandR,
            WindowDestroyed = CleanupRandR,
        });
        Register(new Extension("RENDER", RenderMajor, Render) { FirstError = RenderErrorBase, ErrorCount = 5 });
        Register(new Extension("Generic Event Extension", GenericEventMajor, GenericEventExtension));
        Register(new Extension("XTEST", XTestMajor, XTest));
        Register(new Extension("XINERAMA", XineramaMajor, Xinerama));
        Register(new Extension("MIT-SCREEN-SAVER", ScreenSaverMajor, ScreenSaverExtension)
        {
            FirstEvent = ScreenSaverEventBase,
            EventCount = 1,
            ClientClosed = CleanupScreenSaver,
        });
        Register(new Extension("DPMS", DpmsMajor, Dpms));
        Register(new Extension("X-Resource", XResMajor, XRes));
        Register(new Extension("SYNC", SyncMajor, Sync)
        {
            FirstEvent = SyncEventBase,
            EventCount = 2,
            FirstError = SyncErrorBase,
            ErrorCount = 3,
            ClientClosed = CleanupSync,
        });
        Register(new Extension("DAMAGE", DamageMajor, DamageExtension)
        {
            FirstEvent = DamageEventBase,
            EventCount = 1,
            FirstError = DamageErrorBase,
            ErrorCount = 1,
            ClientClosed = CleanupDamage,
            WindowDestroyed = CleanupDamage,
        });
        Register(new Extension("Composite", CompositeMajor, CompositeExtension)
        {
            ClientClosed = CleanupComposite,
            WindowDestroyed = CleanupComposite,
        });
        Register(new Extension("DOUBLE-BUFFER", DbeMajor, Dbe)
        {
            FirstError = DbeErrorBase,
            ErrorCount = 1,
            ClientClosed = CleanupDbe,
            WindowDestroyed = CleanupDbe,
        });
        Register(new Extension("Present", PresentMajor, Present)
        {
            ClientClosed = CleanupPresent,
            WindowDestroyed = CleanupPresent,
        });
        Register(new Extension("XKEYBOARD", XkbMajor, Xkb)
        {
            FirstEvent = XkbEventBase,
            EventCount = 1,
            FirstError = XkbErrorBase,
            ErrorCount = 1,
            ClientClosed = CleanupXkb,
        });
        Register(new Extension("XInputExtension", XInputMajor, XInput)
        {
            FirstEvent = XInputEventBase,
            EventCount = 17,
            FirstError = XInputErrorBase,
            ErrorCount = 5,
            ClientClosed = CleanupXInput,
        });
        Register(new Extension("GLX", GlxMajor, _glx.Handle)
        {
            FirstEvent = GlxEventBase,
            EventCount = 1,
            FirstError = GlxErrorBase,
            ErrorCount = 14,
            ClientClosed = _glx.CleanupClient,
            WindowDestroyed = _glx.CleanupWindow,
        });
        if (ShmSupported)
        {
            Register(new Extension("MIT-SHM", ShmMajor, Shm)
            {
                FirstEvent = ShmEventBase,
                EventCount = 1,
                FirstError = ShmErrorBase,
                ErrorCount = 1,
                VisibleTo = static client => client.SameHost,
            });
        }
    }

    /// <summary>登记一个扩展。主操作码重复、事件或错误编号与已登记的重叠、或越出协议给扩展的范围时抛异常(编号表写错了)。</summary>
    private void Register(Extension extension)
    {
        if (extension.MajorOpcode < XOpcode.FirstExtension || _extensionsByOpcode.ContainsKey(extension.MajorOpcode))
        {
            throw new InvalidOperationException($"扩展 {extension.Name} 的主操作码 {extension.MajorOpcode} 不可用。");
        }
        CheckRange(extension.Name, "事件", extension.FirstEvent, extension.EventCount, 64, 127, e => (e.FirstEvent, e.EventCount));
        CheckRange(extension.Name, "错误", extension.FirstError, extension.ErrorCount, 128, 255, e => (e.FirstError, e.ErrorCount));
        _extensions[extension.Name] = extension;
        _extensionsByOpcode[extension.MajorOpcode] = extension;
        _extensionList.Add(extension);

        void CheckRange(string name, string kind, byte first, int count, int min, int max, Func<Extension, (byte First, int Count)> range)
        {
            if (count == 0)
            {
                return;
            }
            if (first < min || first + count - 1 > max
                || _extensionList.Select(range).Any(r => r.Count != 0 && first < r.First + r.Count && r.First < first + count))
            {
                throw new InvalidOperationException($"扩展 {name} 的{kind}编号 {first}–{first + count - 1} 越界或与别的扩展重叠。");
            }
        }
    }

    private void QueryExtension(XClient c, XRequestReader r)
    {
        int length = r.U16();
        r.Skip(2);
        string name = r.String8(length);
        if (_extensions.TryGetValue(name, out Extension? ext) && ext.IsVisibleTo(c))
        {
            c.Reply(0, w => w.Bool(true).U8(ext.MajorOpcode).U8(ext.FirstEvent).U8(ext.FirstError).Zero(20));
        }
        else
        {
            c.Reply(0, w => w.Zero(24));
        }
    }

    private void ListExtensions(XClient c)
    {
        string[] names = [.. _extensionList.Where(e => e.IsVisibleTo(c)).Select(e => e.Name).Order(StringComparer.Ordinal)];
        c.Reply((byte)names.Length, w =>
        {
            w.Zero(24);
            foreach (string name in names)
            {
                byte[] bytes = XWire.Latin1.GetBytes(name);
                w.U8((byte)bytes.Length).Bytes(bytes);
            }
            w.Pad4();
        });
    }

    // ------------------------------------------------------------------ Generic Event Extension

    private static void GenericEventExtension(XClient c, XRequestReader r)
    {
        if (r.Data != 0)
        {
            throw new XProtocolError(XErrorCode.Request);
        }
        c.GenericEventsEnabled = true;
        c.Reply(0, w => w.U16(1).U16(0).Zero(20));
    }
}
