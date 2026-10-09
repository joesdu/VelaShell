// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Security Extension Specification, Version 7.1 ——
//     第二章 SecurityQueryVersion 0、SecurityGenerateAuthorization 1、SecurityRevokeAuthorization 2;SecurityAuthorizationRevoked 事件;
//     第三章「Changes to Core Requests」:非受信客户端的 Resource ID Usage、Extension Security、Keyboard Security、Image Security、
//     Property Security、Miscellaneous Security;
//     第五章编码。错误与事件的编号依据 xorgproto 的协议头 secur.h(BadAuthorization +0、BadAuthorizationProtocol +1;一个事件);
//     SecurityGenerateAuthorization 的请求布局依据协议头 securproto.h(见 GenerateAuthorization)。
//
//   规范里由实现决定的几处,这里的取法:
//   - 「扩展是否安全」:XTEST、MIT-SCREEN-SAVER(报得出用户多久没动)、DPMS、X-Resource(别的客户端的资源)、Composite(重定向根窗口
//     就能看到所有窗口)、MIT-SHM、SECURITY 自己对非受信客户端不可见;其余可见,其中有改全局状态的请求(XKB 改键位表、XI 改设备)回 Access。
//   - 根窗口的例外(规范只列了核心请求):另许 RANDR、XINERAMA 的全部请求,XFIXES 的 SelectSelectionInput / SelectCursorInput,
//     XI 的 XIQueryPointer、XIGetSelectedEvents 与 XISelectEvents(后者在服务端的窗口上只留设备 / 层级 / 属性变化,不给键盘、指针与原始事件)——
//     工具包初始化时都会对根窗口发,回 BadWindow 的话 Xlib 默认的错误处理会让程序退出;规范 ISSUE 里点名的 ReparentWindow(parent)、
//     QueryPointer 也许。服务端自己的别的窗口(选区窗口、窗口管理器检查窗口)与根窗口同样对待:GTK 要读 XSETTINGS 的属性。
//   - 属性(规范说由实现决定):非受信客户端读服务端窗口上的属性照常,CUT_BUFFER0–7 隐藏(那是复制的文本);改、删、轮换一律忽略。
//   - GrabServer:非受信客户端的忽略(否则它能冻住所有受信的客户端)。KillClient(AllTemporary)、SetPointerMapping、ChangePointerControl
//     回 Access(改的是全显示的状态)。
//   - XC-QUERY-SECURITY-1(给早已不用的 X 防火墙代理探测用)不实现,按认不出的授权方式拒。

using System.Security.Cryptography;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    // ================================================================== 授权(第二章)

    /// <summary>签出的授权最多这么多个(xauth 每次 generate 签一个;正常用法不会攒到这么多),超了回 Alloc。</summary>
    internal const int MaxAuthorizations = 256;

    private readonly Dictionary<uint, SecurityAuthorization> _authorizations = [];
    private uint _nextAuthorizationId = 1;

    /// <summary>签出的、还有效的授权个数。连接线程据此决定要不要把认不出的 cookie 留到执行线程上再查(见 ServeCoreAsync)。</summary>
    private int _authorizationCount;

    private void Security(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // SecurityQueryVersion:1.0
                c.Reply(0, static w => w.U16(1).U16(0).Zero(20));
                break;
            case 1:
                GenerateAuthorization(c, r);
                break;
            case 2:
                {
                    uint id = r.U32();
                    if (!_authorizations.TryGetValue(id, out SecurityAuthorization? authorization))
                    {
                        throw new XProtocolError((XErrorCode)SecurityErrorBase, id);   // BadAuthorization
                    }
                    Revoke(authorization);
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>
    /// SecurityGenerateAuthorization:只认 MIT-MAGIC-COOKIE-1(规范要求的唯一一种),cookie 是 16 字节随机数(客户端给的数据不用,规范允许);
    /// timeout 默认 60 秒、trust-level 默认非受信、group 只能是 None(没有实现 Application Group)、event-mask 只有 AuthorizationRevoked 一位。
    /// </summary>
    private void GenerateAuthorization(XClient c, XRequestReader r)
    {
        // 线格式按协议头 securproto.h:value-mask 在 12 字节的定长部分里、紧跟两个长度,名字与数据各自补齐到 4 字节。
        // 规范第五章的编码表把 value-mask 列在两个字符串之后(且两者合起来补齐)—— 与 xauth 实际发的不符,照协议头。
        int nameLength = r.U16(), dataLength = r.U16();
        uint mask = r.U32();
        string name = XWire.Latin1.GetString(r.Bytes(nameLength));
        r.Skip(XWire.Pad(nameLength) - nameLength);
        r.Skip(XWire.Pad(dataLength));
        if (name != "MIT-MAGIC-COOKIE-1")
        {
            throw new XProtocolError((XErrorCode)(SecurityErrorBase + 1), 0);   // BadAuthorizationProtocol
        }
        if ((mask & ~0xFu) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, mask);
        }
        uint timeout = 60;
        bool untrusted = true, revokedEvents = false;
        if ((mask & 0x1) != 0)
        {
            timeout = r.U32();
        }
        if ((mask & 0x2) != 0)
        {
            uint level = r.U32();
            if (level > 1)
            {
                throw new XProtocolError(XErrorCode.Value, level);
            }
            untrusted = level == 1;
        }
        if ((mask & 0x4) != 0 && r.U32() is var group && group != 0)
        {
            throw new XProtocolError(XErrorCode.Value, group);
        }
        if ((mask & 0x8) != 0)
        {
            uint events = r.U32();
            if ((events & ~1u) != 0)
            {
                throw new XProtocolError(XErrorCode.Value, events);
            }
            revokedEvents = events != 0;
        }
        if (_authorizations.Count >= MaxAuthorizations)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }

        while (_nextAuthorizationId == 0 || _authorizations.ContainsKey(_nextAuthorizationId))
        {
            _nextAuthorizationId++;
        }
        SecurityAuthorization authorization = new(_nextAuthorizationId++, RandomNumberGenerator.GetBytes(16), untrusted, timeout)
        {
            RevokedListener = revokedEvents ? c : null,
        };
        _authorizations[authorization.Id] = authorization;
        Volatile.Write(ref _authorizationCount, _authorizations.Count);
        c.Reply(0, authorization, static (w, a) => w.U32(a.Id).U16((ushort)a.Cookie.Length).Zero(18).Bytes(a.Cookie));
        ScheduleExpiry(authorization);   // 签出来时就处在「没有连接」的状态(规范)
        if (ShouldLogFrequent())
        {
            LogFrequent($"{c} generated authorization {authorization.Id} ({(untrusted ? "untrusted" : "trusted")}, timeout {timeout} s)");
        }
    }

    /// <summary>按 cookie 找一个签出的授权(常数时间比较,逐个比完);没有为 null。</summary>
    private SecurityAuthorization? FindAuthorization(byte[] cookie)
    {
        SecurityAuthorization? found = null;
        foreach (SecurityAuthorization authorization in _authorizations.Values)
        {
            if (CryptographicOperations.FixedTimeEquals(authorization.Cookie, cookie))
            {
                found = authorization;
            }
        }
        return found;
    }

    /// <summary>
    /// 授权又没有连接了:满 timeout 秒、期间没有新连接用过它,就作废(规范「timeout」)。timeout 为 0 永不作废。
    /// </summary>
    private void ScheduleExpiry(SecurityAuthorization authorization)
    {
        int epoch = ++authorization.IdleEpoch;
        if (authorization.Timeout == 0)
        {
            return;
        }
        uint milliseconds = (uint)Math.Min((ulong)authorization.Timeout * 1000, MaxDelayMilliseconds);
        _ = DelayThenPostAsync(milliseconds, () =>
        {
            if (!authorization.Revoked && authorization.Connections == 0 && authorization.IdleEpoch == epoch)
            {
                Revoke(authorization);
            }
        }, _lifetime.Token);
    }

    /// <summary>作废一个授权(SecurityRevokeAuthorization 或到期):断开用它连着的客户端,通知选了事件的那个客户端。</summary>
    private void Revoke(SecurityAuthorization authorization)
    {
        authorization.Revoked = true;
        _authorizations.Remove(authorization.Id);
        Volatile.Write(ref _authorizationCount, _authorizations.Count);
        if (authorization.RevokedListener is { Closed: false } listener)
        {
            listener.Event(SecurityEventBase, 0, w => w.U32(authorization.Id));
        }
        foreach (XClient client in _clients.Values.Where(c => ReferenceEquals(c.Authorization, authorization)).ToList())
        {
            KillClientOf(client);
        }
        if (ShouldLogFrequent())
        {
            LogFrequent($"authorization {authorization.Id} revoked");
        }
    }

    /// <summary>连接断开:用过的授权少一个连接(没了就开始计时),它选的 AuthorizationRevoked 不再发。</summary>
    private void CleanupSecurity(XClient client)
    {
        if (client.Authorization is { Revoked: false } used && --used.Connections == 0)
        {
            ScheduleExpiry(used);
        }
        foreach (SecurityAuthorization authorization in _authorizations.Values)
        {
            if (ReferenceEquals(authorization.RevokedListener, client))
            {
                authorization.RevokedListener = null;
            }
        }
    }

    // ================================================================== 非受信客户端的请求(第三章)

    /// <summary>正在执行的请求来自非受信客户端时是它(见 Execute),否则为 null。</summary>
    private XClient? _untrustedRequester;

    /// <summary>这条(非受信客户端的)请求可以指名服务端自己的窗口:根窗口与服务端建的那几个(「Resource ID Usage」的根窗口例外)。</summary>
    private bool _serverWindowsAllowed;

    /// <summary>这条请求可以指名任何窗口:QueryTree、GetGeometry、TranslateCoordinates(规范例外 1)。</summary>
    private bool _anyWindowAllowed;

    /// <summary>当前请求来自非受信客户端。</summary>
    private bool RequesterUntrusted => _untrustedRequester is not null;

    /// <summary>
    /// 「Resource ID Usage」:非受信客户端指名的资源要属于(某个)非受信客户端,否则当作不存在。服务端自己的资源里,
    /// 窗口只在规范列出的请求里可用(<see cref="_serverWindowsAllowed" />),默认颜色表等其它的照常可用(例外 2)。
    /// 受信客户端的请求不受影响。
    /// </summary>
    internal bool Permits(XResource resource)
    {
        if (_untrustedRequester is null)
        {
            return true;
        }
        if (resource is XWindow && _anyWindowAllowed)
        {
            return true;
        }
        return resource.Owner is { } owner ? owner.Untrusted : resource is not XWindow || _serverWindowsAllowed;
    }

    /// <summary>
    /// 解析请求里指名的资源:同 <see cref="Lookup{T}" />,当前请求来自非受信客户端而它不可以用这个资源时也是 null
    /// (调用方照常回那个类型的错误,规范:「a protocol error … indicating that the specified resource does not exist」)。
    /// 只用于请求里给出的 ID;服务端自己内部按 ID 找对象用 <see cref="Lookup{T}" />。
    /// </summary>
    internal T? Use<T>(uint id) where T : XResource => Lookup<T>(id) is { } resource && Permits(resource) ? resource : null;

    /// <summary>非受信客户端的这条请求可以指名哪些窗口(见文件头的取法)。</summary>
    private static (bool ServerWindows, bool AnyWindow) UntrustedWindowPolicy(byte opcode, ushort minor) => opcode switch
    {
        XOpcode.GetGeometry or XOpcode.QueryTree or XOpcode.TranslateCoordinates => (true, true),
        XOpcode.CreateWindow or XOpcode.ChangeWindowAttributes or XOpcode.GetWindowAttributes or XOpcode.ReparentWindow
            or XOpcode.ChangeProperty or XOpcode.DeleteProperty or XOpcode.GetProperty or XOpcode.ListProperties or XOpcode.RotateProperties
            or XOpcode.SendEvent or XOpcode.GrabPointer or XOpcode.UngrabButton or XOpcode.QueryPointer
            or XOpcode.CreatePixmap or XOpcode.CreateGC or XOpcode.QueryBestSize or XOpcode.CreateColormap => (true, false),
        RandRMajor or XineramaMajor => (true, false),
        XFixesMajor => (minor is 2 or 3, false),       // SelectSelectionInput、SelectCursorInput
        XInputMajor => (minor is 40 or 46 or 60, false),   // XIQueryPointer、XISelectEvents、XIGetSelectedEvents
        _ => (false, false),
    };

    /// <summary>非受信客户端发了只给受信客户端用的请求(改全显示的键盘、指针、访问控制):Access(规范「Keyboard / Miscellaneous Security」)。</summary>
    private static void DenyUntrusted(XClient c)
    {
        if (c.Untrusted)
        {
            throw new XProtocolError(XErrorCode.Access);
        }
    }

    /// <summary>扩展对非受信客户端不可见(「Extension Security」):见文件头的取法。</summary>
    private static bool TrustedOnly(XClient client) => !client.Untrusted;

    /// <summary>
    /// 「Keyboard Security」的前提:假设此刻产生一个键盘事件(按当前的焦点、指针位置、键盘抓取与事件选择),它会不会送到某个非受信客户端。
    /// 不会的话,非受信客户端的 QueryKeymap / KeymapNotify 全是 0、GrabKeyboard 回 AlreadyGrabbed、SetInputFocus 什么都不做、
    /// 它的被动键盘抓取不激活 —— 偷不到、也抢不走发给受信客户端的键盘输入。
    /// </summary>
    private bool KeyboardReachesUntrusted()
    {
        if (KeyboardGrab is { } grab)
        {
            return grab.Client.Untrusted;   // owner-events 与否都只送到抓取它的客户端
        }
        if (KeyboardSource() is not { } source)
        {
            return false;
        }
        XWindow? stopAt = _focus is { } focus && !ReferenceEquals(focus, Root) ? focus : null;
        if (Propagate(source, XEventMask.KeyPress, XEventCode.KeyPress, null, stopAt) is not { } hit)
        {
            return false;
        }
        bool core = !CoreBlockedBelow(source, hit.Window, XEventMask.KeyPress);
        foreach ((XClient client, uint selected) in hit.Window.EventSelections)
        {
            if (core && (selected & (uint)XEventMask.KeyPress) != 0 && client.Untrusted && !client.Closed)
            {
                return true;
            }
        }
        foreach ((XClient client, (ulong master, ulong slave)) in hit.Window.Xi2Selections)
        {
            if (((master | slave) & (1UL << XEventCode.KeyPress)) != 0 && client.Untrusted && !client.Closed)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 「Image Security」:非受信客户端 GetImage 一个窗口,被非下级窗口挡住的地方不给挡在上面的那个窗口的内容,填服务端定的图案(0)。
    /// 每个顶层各有自己的缓冲,能挡住它的只有同一个顶层里的兄弟窗口(比如受信程序嵌进来的窗口)。
    /// </summary>
    private void HideObscured(uint[] pixels, XWindow window, int x, int y, int width, int height)
    {
        (int ox, int oy) = window.OffsetInTopLevel();
        Region hidden = new Region(new XRect(x + ox, y + oy, width, height)).Subtract(CachedClip(window, includeInferiors: true));
        foreach (XRect r in hidden.Rects)
        {
            for (int row = r.Y; row < r.Bottom; row++)
            {
                Array.Clear(pixels, ((row - oy - y) * width) + r.X - ox - x, r.Width);
            }
        }
    }

    /// <summary>这个客户端此刻读得到键盘状态吗(受信的总读得到;非受信的只在键盘事件会送给非受信客户端时)。</summary>
    private bool MaySeeKeyboard(XClient client) => !client.Untrusted || KeyboardReachesUntrusted();

    /// <summary>
    /// 「Property Security」:非受信客户端看不见的属性 —— 服务端自己的窗口(根窗口等)上的 CUT_BUFFER0–7,那是程序复制的文本。
    /// 一致地隐藏:ListProperties 不列、PropertyNotify 不发、GetProperty 回「不存在」。
    /// </summary>
    private static bool HiddenFromUntrusted(XWindow window, uint property) =>
        window.Owner is null && property is >= XAtom.CutBuffer0 and <= XAtom.CutBuffer7;

    /// <summary>
    /// 非受信客户端改服务端窗口上的属性(ChangeProperty / DeleteProperty / RotateProperties):忽略,就像 NoOperation
    /// (规范许的做法之一;根窗口上的属性是给所有客户端共用的约定,不让非受信的改)。
    /// </summary>
    /// <remarks>
    /// 例外:服务端选区窗口上 <c>_VELASHELL_*</c> 的属性 —— 服务端替宿主向选区属主要剪贴板时让它写到这里;非受信程序复制的内容
    /// 照样能经宿主到本机剪贴板(选区经宿主中转)。XSETTINGS 的属性也在这个窗口上,不在例外里。
    /// </remarks>
    private bool IgnoredPropertyWrite(XWindow window, uint property) =>
        RequesterUntrusted && window.Owner is null
        && !(ReferenceEquals(window, _selectionWindow) && AtomName(property) is { } name && name.StartsWith("_VELASHELL_", StringComparison.Ordinal));

    /// <summary>
    /// 非受信客户端在服务端的窗口上改属性:只许 event-mask 一项、值只含 StructureNotify 与 PropertyChange(规范例外 3.g)——
    /// 盯着根窗口与 XSETTINGS 窗口的属性变化。其余当作窗口不存在。
    /// </summary>
    private void CheckUntrustedWindowAttributes(XWindow window, uint mask, uint eventMask)
    {
        if (RequesterUntrusted && window.Owner is null
            && (mask != 0x800 || (eventMask & ~(uint)(XEventMask.StructureNotify | XEventMask.PropertyChange)) != 0))
        {
            throw new XProtocolError(XErrorCode.Window, window.Id);
        }
    }

    /// <summary>
    /// 非受信客户端往服务端的窗口发 SendEvent:只许 ICCCM 规定发往根窗口的那几种(规范例外 3.f)—— propagate 为 False、
    /// event-mask 为 ColormapChange、StructureNotify 或 SubstructureRedirect | SubstructureNotify、事件为 UnmapNotify、ConfigureRequest、ClientMessage。
    /// </summary>
    private void CheckUntrustedSendEvent(XWindow destination, bool propagate, uint eventMask, byte code)
    {
        if (!RequesterUntrusted || destination.Owner is not null)
        {
            return;
        }
        bool maskOk = eventMask is (uint)XEventMask.ColormapChange or (uint)XEventMask.StructureNotify
            or (uint)(XEventMask.SubstructureRedirect | XEventMask.SubstructureNotify);
        if (propagate || !maskOk || code is not (XEventCode.UnmapNotify or XEventCode.ConfigureRequest or XEventCode.ClientMessage))
        {
            throw new XProtocolError(XErrorCode.Window, destination.Id);
        }
    }
}
