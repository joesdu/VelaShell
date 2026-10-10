// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The Input Method Protocol(X Consortium Standard,Version 1.0)——「Default Preconnection Convention」:
//     XIM_SERVERS 是屏幕 0 的根窗口上的 ATOM 列表,每个原子是一个输入法服务端的名字(@server=名字)、也是它占的选区;
//     转换目标 TRANSPORT 回传输地址、LOCALES 回支持的区域(附录 B 的写法 {category=[value,...]})
//   X Window System Protocol, X Version 11 ——「ConvertSelection」「SelectionNotify」
//   ICCCM §2.6.2(TARGETS、TIMESTAMP)
//
//   协议本身在 Server/XimServer.cs;这里是它与服务端其余部分的接线:占选区、写 XIM_SERVERS、回答 LOCALES / TRANSPORT、
//   宿主的上屏走 XIM 还是借键码,以及 XimServer 用到的几个 internal 成员。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>XIM 选区 <c>@server=名字</c> 的属主窗口(服务端自己的窗口,不进窗口树)。</summary>
    internal const uint XimServerWindowId = 0x47;

    /// <summary>当 XIM 输入法服务端时的状态(<see cref="X11ServerOptions.InputMethodName" />);没开为 null。</summary>
    private XimServer? _xim;

    /// <summary>XIM 输入法服务端(测试看它的内部状态用)。</summary>
    internal XimServer? Xim => _xim;

    /// <summary>开着 <see cref="X11ServerOptions.InputMethodName" /> 时占住 <c>@server=名字</c>、把它加进根窗口的 XIM_SERVERS(已有的照留)。</summary>
    private void InitXim()
    {
        if (_options.InputMethodName is not { } name)
        {
            return;
        }
        _xim = new XimServer(this, name);
        _selections[new SelectionSlot(_xim.SelectionAtom, null)] = (_xim.ServerWindow, null, 0);
        uint servers = Intern("XIM_SERVERS");
        List<uint> atoms = [];
        if (Root.Properties.GetValueOrDefault(servers) is { Type: XAtom.Atom, Format: 32 } existing)
        {
            for (int i = 0; i + 4 <= existing.Length; i += 4)
            {
                atoms.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(existing.Data[i..]));
            }
        }
        if (!atoms.Contains(_xim.SelectionAtom))
        {
            atoms.Add(_xim.SelectionAtom);
        }
        StoreServerProperty(Root, servers, new XProperty(XAtom.Atom, 32, AtomList([.. atoms])));
    }

    /// <summary>
    /// 程序要 <c>@server=名字</c> 选区的内容(连接之前先问清楚):TARGETS、TIMESTAMP、LOCALES(<c>@locale=…</c>)、TRANSPORT(<c>@transport=X/</c>)。
    /// LOCALES / TRANSPORT 的属性类型就是目标原子本身,格式 8。
    /// </summary>
    private void ServeXimSelection(XClient c, XWindow requestor, uint selection, uint target, uint property, uint time, uint ownerTime)
    {
        if (property == 0)
        {
            property = target;   // 旧式请求方(ICCCM §2.2)
        }
        uint locales = Intern("LOCALES"), transport = Intern("TRANSPORT");
        if (target == Intern("TARGETS"))
        {
            WriteConverted(requestor, property, (XAtom.Atom, 32, AtomList([Intern("TARGETS"), Intern("TIMESTAMP"), locales, transport])));
        }
        else if (target == Intern("TIMESTAMP"))
        {
            WriteConverted(requestor, property, (XAtom.Integer, 32, AtomList([ownerTime])));
        }
        else if (target == locales)
        {
            WriteConverted(requestor, property, (locales, 8, XWire.Latin1.GetBytes(XimServer.Locales)));
        }
        else if (target == transport)
        {
            WriteConverted(requestor, property, (transport, 8, XWire.Latin1.GetBytes(XimServer.Transport)));
        }
        else
        {
            property = 0;
        }
        Log($"xim client#{c.Index} converted {AtomName(target)}{(property == 0 ? " (refused)" : "")}");
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
    }

    /// <summary>
    /// 宿主上屏的字经 XIM 交出去(键盘焦点所在的程序有报了焦点的输入上下文时):可见的字一段段 XIM_COMMIT,换行、制表照常按键
    /// (回车 / 制表键,程序按它自己的键位表解释;CR LF 只按一次),别的控制字符不输入。没有那样的输入上下文时返回 false,调用方借键码输入。
    /// </summary>
    private bool CommitThroughXim(string text)
    {
        if (_xim is not { } xim)
        {
            return false;
        }
        xim.Refresh();
        if (!xim.HasActiveContext)
        {
            return false;
        }
        int run = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            char ch = i < text.Length ? text[i] : '\0';
            if (i < text.Length && ch is not (< ' ' or (>= (char)0x7F and < (char)0xA0)))   // C0、DEL、C1 控制字符
            {
                continue;
            }
            if (i > run)
            {
                xim.Commit(text[run..i]);
            }
            run = i + 1;
            if (ch is '\r' or '\t' || (ch == '\n' && (i == 0 || text[i - 1] != '\r')))
            {
                TypeText(ch.ToString(), 0);   // 回车 / 制表在键位表里本来就有:直接按那个键,不借键码
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ 给 XimServer 用的

    /// <summary>服务端自己的窗口进资源表(不挂进窗口树:客户端的 QueryTree 看不到,也映射不了、销毁不了)。</summary>
    internal void AddServerWindow(XWindow window) => _resources[window.Id] = window;

    /// <summary>服务端自己的窗口出资源表,上面的属性一并清掉。</summary>
    internal void RemoveServerWindow(XWindow window)
    {
        foreach (XProperty property in window.Properties.Values)
        {
            ReleaseProperty(property);
        }
        window.Properties.Clear();
        RemoveResource(window.Id);
    }

    /// <summary>取走窗口上的一个属性(读完即删,发 PropertyNotify);没有为 null。</summary>
    internal byte[]? TakeServerProperty(XWindow window, uint property)
    {
        if (!window.Properties.Remove(property, out XProperty? value))
        {
            return null;
        }
        byte[] data = value.Data.ToArray();
        ReleaseProperty(value);
        SendPropertyNotify(window, property, deleted: true);
        return data;
    }

    /// <summary>服务端往窗口上写一个格式 8 的属性(发 PropertyNotify)。</summary>
    internal void PutServerProperty(XWindow window, uint property, uint type, byte[] data)
    {
        StoreServerProperty(window, property, new XProperty(type, 8, data));
        SendPropertyNotify(window, property, deleted: false);
    }

    /// <summary>键盘焦点所在的顶层:焦点是 PointerRoot 时看指针所在的顶层;没有焦点(None)为 null。</summary>
    internal XWindow? KeyboardFocusTopLevel() => _focus switch
    {
        null => null,
        { IsRoot: true } => _pointerWindow.TopLevel,
        { } focus => focus.TopLevel,
    };

    /// <summary>
    /// 窗口内区原点在宿主手里哪个顶层的哪儿:rootless 下是它所属的顶层(顶层内区坐标);单窗口模式下是屏幕窗口(根坐标)。
    /// 所属的顶层还没交给宿主时为 null。
    /// </summary>
    internal (XTopLevelWindow Handle, int X, int Y)? LocateInTopLevel(XWindow window)
    {
        if (_screenHandle is { } screen)
        {
            (int x, int y) = window.AbsoluteInner();
            return (screen, x, y);
        }
        if (window.TopLevel is { } top && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            (int x, int y) = window.OffsetInTopLevel();
            return (handle, x, y);
        }
        return null;
    }

    /// <summary>接受宿主输入的输入上下文换了、或它的插入点动了(经延后队列交给宿主)。</summary>
    internal void ReportInputMethodFocus(XInputMethodFocus? focus) => _host.InputMethodFocusChanged(focus);

    /// <summary>当前报给客户端的 DPI(估插入点的行高用)。</summary>
    internal int ScreenDpi => _dpi;
}
