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
    private const byte XFixesMajor = 131, XFixesEventBase = 65, XFixesErrorBase = 128;         // SelectionNotify +0、CursorNotify +1;BadRegion +0、BadBarrier +1
    private const byte RandRMajor = 132, RandREventBase = 67, RandRErrorBase = 130;            // ScreenChangeNotify +0、Notify +1;BadOutput / BadCrtc / BadMode / BadProvider
    private const byte RenderMajor = 133, RenderErrorBase = 134;                               // PictFormat +0、Picture +1、PictOp +2、GlyphSet +3、Glyph +4
    private const byte GenericEventMajor = 134;
    private const byte XTestMajor = 135;
    private const byte XineramaMajor = 136;
    private const byte ScreenSaverMajor = 137, ScreenSaverEventBase = 69;                      // ScreenSaverNotify
    private const byte DpmsMajor = 138;
    private const byte XResMajor = 139;
    private const byte SyncMajor = 140, SyncEventBase = 70, SyncErrorBase = 139;               // CounterNotify +0、AlarmNotify +1;Counter +0、Alarm +1、Fence +2
    private const byte DamageMajor = 141, DamageEventBase = 72, DamageErrorBase = 142;         // DamageNotify;BadDamage
    private const byte CompositeMajor = 142;
    private const byte DbeMajor = 143, DbeErrorBase = 143;                                     // BadBuffer
    private const byte PresentMajor = 144;                                                     // 事件走 GenericEvent
    private const byte XInputMajor = 145, XInputEventBase = 74, XInputErrorBase = 145;         // XI 1.x 的 17 个事件;BadDevice +0 … BadClass +4
    private const byte XkbMajor = 146, XkbEventBase = 73, XkbErrorBase = 144;                  // 一个事件码(子类型在 xkbType);BadKeyboard
    private const byte ShmMajor = 147, ShmEventBase = 91, ShmErrorBase = 150;                  // Completion;BadShmSeg
    internal const byte GlxMajor = 148, GlxEventBase = 92, GlxErrorBase = 151;                  // PbufferClobber;BadContext +0 … GLXBadProfileARB +13;internal:GlxExtension 在类外
    private const byte SecurityMajor = 149, SecurityEventBase = 93, SecurityErrorBase = 165;   // AuthorizationRevoked;BadAuthorization +0、BadAuthorizationProtocol +1

    /// <summary>按名字查(QueryExtension)。</summary>
    private readonly Dictionary<string, Extension> _extensions = [with(StringComparer.Ordinal)];

    /// <summary>按主操作码查(请求分派)。</summary>
    private readonly Dictionary<byte, Extension> _extensionsByOpcode = [];

    /// <summary>注册顺序(清理钩子按这个顺序调)。</summary>
    private readonly List<Extension> _extensionList = [];

    /// <summary>GLX 扩展(测试看它的内部状态用)。</summary>
    internal GlxExtension Glx => _glx;

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
            ErrorCount = 2,
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
        Register(new Extension("XTEST", XTestMajor, XTest)
        {
            ClientClosed = CleanupXTest,
            VisibleTo = client => !IsRestricted(client),   // 伪造的输入与真实键盘无从区分(见 RestrictForwardedClients;非受信客户端一律不给)
        });
        Register(new Extension("XINERAMA", XineramaMajor, Xinerama));
        Register(new Extension("MIT-SCREEN-SAVER", ScreenSaverMajor, ScreenSaverExtension)
        {
            FirstEvent = ScreenSaverEventBase,
            EventCount = 1,
            ClientClosed = CleanupScreenSaver,
            VisibleTo = TrustedOnly,   // 报得出用户多久没动键盘鼠标(SECURITY:不安全的扩展)
        });
        Register(new Extension("DPMS", DpmsMajor, Dpms) { VisibleTo = TrustedOnly });
        Register(new Extension("X-Resource", XResMajor, XRes) { VisibleTo = TrustedOnly });   // 别的客户端的资源与内存
        Register(new Extension("SYNC", SyncMajor, Sync)
        {
            FirstEvent = SyncEventBase,
            EventCount = 2,
            FirstError = SyncErrorBase,
            ErrorCount = 3,
            ClientClosed = CleanupSync,
            ClientResourcesDestroyed = CleanupSyncResources,
        });
        Register(new Extension("DAMAGE", DamageMajor, DamageExtension)
        {
            FirstEvent = DamageEventBase,
            EventCount = 1,
            FirstError = DamageErrorBase,
            ErrorCount = 1,
            ClientResourcesDestroyed = CleanupDamage,
            WindowDestroyed = CleanupDamage,
        });
        Register(new Extension("Composite", CompositeMajor, CompositeExtension)
        {
            VisibleTo = TrustedOnly,   // 重定向根窗口的子窗口就能读到所有窗口的内容
            ClientClosed = CleanupComposite,
            WindowDestroyed = CleanupComposite,
            PixmapFreed = CompositePixmapFreed,
        });
        Register(new Extension("DOUBLE-BUFFER", DbeMajor, Dbe)
        {
            FirstError = DbeErrorBase,
            ErrorCount = 1,
            ClientResourcesDestroyed = CleanupDbe,
            WindowDestroyed = CleanupDbe,
        });
        Register(new Extension("Present", PresentMajor, Present)
        {
            ClientClosed = CleanupPresent,
            ClientResourcesDestroyed = CleanupPresentResources,
            WindowDestroyed = CleanupPresent,
            PixmapFreed = PresentPixmapFreed,
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
            PixmapFreed = _glx.CleanupPixmap,
            ResourceFreed = _glx.ResourceFreed,
        });
        if (ShmSupported)
        {
            Register(new Extension("MIT-SHM", ShmMajor, Shm)
            {
                FirstEvent = ShmEventBase,
                EventCount = 1,
                FirstError = ShmErrorBase,
                ErrorCount = 1,
                VisibleTo = static client => client.SameHost && !client.Untrusted,
                ClientClosed = CleanupShm,
            });
        }
        Register(new Extension("SECURITY", SecurityMajor, Security)
        {
            FirstEvent = SecurityEventBase,
            EventCount = 1,
            FirstError = SecurityErrorBase,
            ErrorCount = 2,
            VisibleTo = TrustedOnly,   // 规范:不该对非受信客户端暴露
            ClientClosed = CleanupSecurity,
        });
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
        // 只回版本,不记「这个客户端声明过」:GenericEvent 只发给显式选了 XI2 / Present 事件的客户端,不必再按它把关。
        c.Reply(0, w => w.U16(1).U16(0).Zero(20));
    }
}
