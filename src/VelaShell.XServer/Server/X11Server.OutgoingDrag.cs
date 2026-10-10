// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   freedesktop 的 XDND 协议,第 5 版 ——「XdndProxy」(第 4 版起用它约定「拖到根窗口」:根窗口的 XdndProxy 指向代理窗口,代理窗口的
//   XdndProxy 指向它自己、XdndAware 设在代理上;消息投给代理,里面的窗口字段仍是指针所在的根窗口)、「Client Messages」
//   (XdndEnter / XdndPosition / XdndStatus / XdndLeave / XdndDrop / XdndFinished 的字段;XdndFinished 第 5 版的「接受了」位与动作)、
//   「Example walk-through」Step 2–8(超过三种类型时读源窗口的 XdndTypeList;目标拖着时就可以用 XdndPosition 的时间戳取数据;
//   不接受的放下也回 XdndFinished)
//   RFC 2483 —— text/uri-list(一行一个 URI,行尾 CRLF,# 开头的是注释)
//   ICCCM §2 —— ConvertSelection、INCR(取数据走剪贴板那套,见 X11Server.Clipboard.cs)
//
//   宿主拖进 X 窗口(服务端当源)在 X11Server.Xdnd.cs;这里是反方向:X 程序当源、服务端当「根窗口」这个目标。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

/// <summary>
/// X 程序往本机拖出来(<see cref="X11ServerOptions.AcceptOutgoingDrags" />):指针拖到所有 X 窗口以外时,X 程序按「拖到根窗口」的约定把 XDND 消息发给
/// 服务端的代理窗口。服务端答应接(XdndStatus,动作只给复制)、用 XdndPosition 的时间戳把数据(<c>text/uri-list</c> 与文字)取过来,
/// 交给宿主发起本机的拖放(<see cref="IX11ServerHost.OutgoingDragStarted" />);宿主交回结果(<see cref="CompleteOutgoingDrag" />)之后,
/// X 程序松手发来的 XdndDrop 按它回 XdndFinished。
/// </summary>
public sealed partial class X11Server
{
    /// <summary>拖出用的代理窗口(服务端自己的窗口,不进窗口树)。</summary>
    private const uint OutgoingDragWindowId = 0x48;

    /// <summary>XdndAware 报的版本(与当源时同一个)。</summary>
    private const int OutgoingDragVersion = XdndVersion;

    private XWindow? _outgoingDragWindow;

    /// <summary>进行中的那一次拖出;没有为 null。</summary>
    private OutgoingDrag? _outgoing;

    /// <summary>X 程序拖出来的类型里认这些:文件(URI 列表)与文字。文字按顺序挑第一个有的。</summary>
    private static readonly string[] OutgoingTextTargets =
        ["UTF8_STRING", "text/plain;charset=utf-8", "text/plain;charset=UTF-8", "COMPOUND_TEXT", "STRING", "text/plain", "TEXT"];

    private const string UriListTarget = "text/uri-list";

    /// <summary>一次拖出:源、协商的版本、类型、最后的位置与时间、取数据与宿主的进度。</summary>
    private sealed class OutgoingDrag(XClient source, uint sourceWindow, uint targetWindow, int version, uint[] types)
    {
        public XClient Source { get; } = source;

        public uint SourceWindow { get; } = sourceWindow;

        /// <summary>X 程序以为的目标(消息里的窗口字段,即根窗口):XdndStatus / XdndFinished 的 l0 回它。</summary>
        public uint TargetWindow { get; } = targetWindow;

        public int Version { get; } = version;

        public uint[] Types { get; } = types;

        public uint Time { get; set; }

        public (int X, int Y) Position { get; set; }

        /// <summary>已经开始取数据了。</summary>
        public bool Fetching { get; set; }

        /// <summary>数据取不到(属主不给、类型都不认):之后的 XdndStatus 一律不接受。</summary>
        public bool Failed { get; set; }

        /// <summary>交给宿主的那个对象;数据还没取到为 null。</summary>
        public XOutgoingDrag? Announced { get; set; }

        /// <summary>宿主交回的结果:放下了 / 取消了;还没交回为 null。</summary>
        public bool? Dropped { get; set; }
    }

    /// <summary>开着拖出时建代理窗口、让根窗口的 XdndProxy 指向它(单窗口模式不建:根窗口是远端桌面的)。</summary>
    private void InitOutgoingDrags()
    {
        if (!_options.AcceptOutgoingDrags || Rootful)
        {
            return;
        }
        XWindow proxy = new(OutgoingDragWindowId, null, Root)
        {
            Class = 2,   // InputOnly
            Width = 1,
            Height = 1,
            Visual = RootVisualId,
        };
        _resources[OutgoingDragWindowId] = proxy;
        _outgoingDragWindow = proxy;
        byte[] self = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(self, OutgoingDragWindowId);
        StoreServerProperty(proxy, Intern("XdndProxy"), new XProperty(XAtom.Window, 32, self));
        StoreServerProperty(proxy, Intern("XdndAware"), new XProperty(XAtom.Atom, 32, AtomList([OutgoingDragVersion])));
        // 接手窗口垫在底下时,源把它当一个由窗口管理器管着的顶层(ICCCM §4.1.3.1:顶层有 WM_STATE;Java 只对这样的窗口找 XdndAware)。
        uint wmState = Intern("WM_STATE");
        StoreServerProperty(proxy, wmState, new XProperty(wmState, 32, AtomList([1u, 0u])));   // NormalState、没有图标窗口
        StoreServerProperty(Root, Intern("XdndProxy"), new XProperty(XAtom.Window, 32, self));
    }

    private bool IsOutgoingDragWindow(XWindow window) => _outgoingDragWindow is not null && ReferenceEquals(window, _outgoingDragWindow);

    // ------------------------------------------------------------------ 接手窗口

    /// <summary>
    /// 一个 X 程序占了 XdndSelection(开始拖了):把代理窗口垫到根窗口子窗口的最底下、盖满整块根窗口。有的源(Java 的 AWT)只把拖放目标
    /// 找在「指针下面那个根窗口的子窗口」上,指针落在空白的根窗口上时(rootless 下那里是本机桌面)根本不找目标、也不看根窗口的 XdndProxy;
    /// 垫上它,那片空白就有了一个声明了 XdndAware 的顶层。GTK 这类看根窗口 XdndProxy 的源照旧经根窗口找到同一个代理。
    /// </summary>
    /// <remarks>
    /// 悄悄地垫上、撤下(不发 CreateNotify / MapNotify / UnmapNotify):它是服务端自己的窗口,只在拖动期间存在;源在拖动开始时建的窗口缓存里没有它,
    /// 照旧按根窗口找。拖动结束(源不再抓着指针、也没有进行中的拖出)就撤掉,见 <see cref="WatchDragCatcher" />。
    /// </remarks>
    private void ShowDragCatcher()
    {
        if (_outgoingDragWindow is not { Mapped: false } catcher)
        {
            return;
        }
        catcher.X = 0;
        catcher.Y = 0;
        catcher.Width = Root.Width;
        catcher.Height = Root.Height;
        Root.Children.Insert(0, catcher);
        catcher.Mapped = true;
        InvalidateVisibility();
        UpdateVisibility();
        UpdatePointerWindow();
        WatchDragCatcher(1000);
    }

    private void HideDragCatcher()
    {
        if (_outgoingDragWindow is not { Mapped: true } catcher)
        {
            return;
        }
        Root.Children.Remove(catcher);
        catcher.Mapped = false;
        InvalidateVisibility();
        UpdateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>
    /// 过一会儿看一眼拖动还在不在:XdndSelection 的属主还抓着指针(拖动期间源总抓着:按钮按下的自动抓取或拖动开始时的主动抓取),
    /// 或者有进行中的拖出,就接着垫着;否则撤掉。
    /// </summary>
    private void WatchDragCatcher(uint delayMilliseconds) => _ = DelayThenPostAsync(delayMilliseconds, () =>
    {
        if (_outgoingDragWindow is not { Mapped: true })
        {
            return;
        }
        bool dragging = _outgoing is not null
            || (_selections.TryGetValue(new SelectionSlot(Intern("XdndSelection"), null), out (XWindow Window, XClient? Client, uint Time) owner)
                && owner.Client is { } source && ReferenceEquals(PointerGrab?.Client, source));
        if (dragging)
        {
            WatchDragCatcher(500);
        }
        else
        {
            HideDragCatcher();
        }
    }, _lifetime.Token);

    /// <summary>选区换了属主(见 SetSelectionOwner):X 程序占了 XdndSelection 就垫上接手窗口,放掉了就撤下。</summary>
    private void OnXdndSelectionOwnerChanged(SelectionSlot slot, bool clientOwns)
    {
        if (_outgoingDragWindow is null || slot.Atom != Intern("XdndSelection"))
        {
            return;
        }
        if (clientOwns)
        {
            ShowDragCatcher();
        }
        else if (_outgoing is null)
        {
            HideDragCatcher();
        }
    }

    /// <summary>
    /// X 程序发给代理窗口的 XDND 消息。<paramref name="raw" /> 是发送方字节序的 32 字节事件。不认识的照收、不理。
    /// </summary>
    private void OnOutgoingDragMessage(XClient c, byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.ClientMessage || raw[1] != 32)
        {
            return;
        }
        uint Read(int offset) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
        uint window = Read(4), type = Read(8), l0 = Read(12), l1 = Read(16), l2 = Read(20), l3 = Read(24);
        if (type == Intern("XdndEnter"))
        {
            EndOutgoingDrag();   // 上一次没收尾(源崩溃了、漏了 XdndLeave):作废
            _outgoing = new OutgoingDrag(c, l0, window, (int)Math.Min(l1 >> 24, OutgoingDragVersion), OutgoingTypes(l0, l1, l2, l3, Read(28)));
            return;
        }
        if (_outgoing is not { } drag || drag.SourceWindow != l0 || !ReferenceEquals(drag.Source, c))
        {
            if (type == Intern("XdndPosition"))
            {
                SendOutgoingStatus(l0, window, accept: false);   // 没进入就报位置:回不接受,别让源一直等
            }
            return;
        }
        if (type == Intern("XdndPosition"))
        {
            drag.Time = l3;
            drag.Position = ((short)(l2 >> 16), (short)(l2 & 0xFFFF));
            bool accept = !drag.Failed && drag.Dropped != false && OutgoingTargets(drag.Types) is not null;
            if (accept && !drag.Fetching)
            {
                FetchOutgoingData(drag);
                accept = !drag.Failed;
            }
            SendOutgoingStatus(drag.SourceWindow, drag.TargetWindow, accept);
        }
        else if (type == Intern("XdndLeave"))
        {
            EndOutgoingDrag();
        }
        else if (type == Intern("XdndDrop"))
        {
            // 宿主交回「放下了」才算接了;别的(没发起成本机拖放、取消了、宿主还没回话)都回「没接受」(第 5 版的 l1 第 0 位)。
            bool dropped = drag.Dropped == true;
            SendOutgoingFinished(drag, dropped);
            EndOutgoingDrag();
        }
    }

    /// <summary>XdndEnter 带的类型:超过三种(l1 第 0 位)时读源窗口的 XdndTypeList,否则就是 l2–l4 里不为 None 的。</summary>
    private uint[] OutgoingTypes(uint sourceWindow, uint l1, uint l2, uint l3, uint l4)
    {
        if ((l1 & 1) != 0 && Lookup<XWindow>(sourceWindow) is { } source
            && source.Properties.TryGetValue(Intern("XdndTypeList"), out XProperty? list) && list is { Type: XAtom.Atom, Format: 32 })
        {
            uint[] types = new uint[Math.Min(list.Length / 4, 256)];
            for (int i = 0; i < types.Length; i++)
            {
                types[i] = BinaryPrimitives.ReadUInt32LittleEndian(list.Data[(i * 4)..]);
            }
            return types;
        }
        return [.. ((uint[])[l2, l3, l4]).Where(t => t != 0)];
    }

    /// <summary>要取的目标:有 URI 列表取它,文字挑一个(都没有为 null:不接这次拖放)。</summary>
    private uint[]? OutgoingTargets(uint[] types)
    {
        List<uint> targets = [];
        uint uris = Intern(UriListTarget);
        if (types.Contains(uris))
        {
            targets.Add(uris);
        }
        foreach (string name in OutgoingTextTargets)
        {
            uint atom = Intern(name);
            if (types.Contains(atom))
            {
                targets.Add(atom);
                break;
            }
        }
        return targets.Count > 0 ? [.. targets] : null;
    }

    /// <summary>
    /// 用 XdndPosition 的时间戳向 XdndSelection 的属主要数据(协议:目标拖着时就可以取),走剪贴板取选区的那一套(超时、INCR、
    /// 属主走了都照它)。属主不在时这次拖放就接不了。
    /// </summary>
    private void FetchOutgoingData(OutgoingDrag drag)
    {
        drag.Fetching = true;
        SelectionSlot slot = new(Intern("XdndSelection"), null);
        if (OutgoingTargets(drag.Types) is not { } targets
            || !_selections.TryGetValue(slot, out (XWindow Window, XClient? Client, uint Time) owner) || owner.Client is not { } client)
        {
            drag.Failed = true;
            return;
        }
        SelectionFetch fetch = new(slot, targets[0], drag.Time, Intern("_VELASHELL_XdndSelection"))
        {
            StringFallback = false,
            Completed = done => OnOutgoingDataFetched(drag, done),
        };
        foreach (uint target in targets.Skip(1))
        {
            fetch.Plan.Enqueue(target);
        }
        if (_fetches.Remove(slot.Atom, out SelectionFetch? replaced))
        {
            FinishFetch(replaced, delivered: false);
        }
        _fetches[slot.Atom] = fetch;
        RequestFetch(fetch, client, owner.Window);
    }

    /// <summary>数据取回来了:解出 URI 与文字,交给宿主(这一次已经结束、或什么都没取到的不交)。</summary>
    private void OnOutgoingDataFetched(OutgoingDrag drag, SelectionFetch fetch)
    {
        if (!ReferenceEquals(_outgoing, drag))
        {
            return;
        }
        uint uriTarget = Intern(UriListTarget);
        List<string> uris = [];
        string? text = null;
        foreach ((uint target, (uint type, byte[] data)) in fetch.Results)
        {
            if (target == uriTarget)
            {
                uris.AddRange(ParseUriList(Encoding.UTF8.GetString(data)));
            }
            else
            {
                text = XText.Decode(data, type == XAtom.String || target == Intern("text/plain") ? XTextEncoding.Latin1
                    : type == Intern("COMPOUND_TEXT") ? XTextEncoding.CompoundText
                    : XTextEncoding.Utf8);
            }
        }
        if (uris.Count == 0 && string.IsNullOrEmpty(text))
        {
            drag.Failed = true;
            return;
        }
        drag.Announced = new XOutgoingDrag(uris, string.IsNullOrEmpty(text) ? null : text, drag.Source.Label, drag.Position.X, drag.Position.Y);
        _host.OutgoingDragStarted(drag.Announced);
    }

    /// <summary>RFC 2483 的 text/uri-list:按行切,去掉行尾的 CR、空行与 # 开头的注释。</summary>
    internal static IEnumerable<string> ParseUriList(string list)
    {
        foreach (string line in list.Split('\n'))
        {
            string uri = line.TrimEnd('\r', '\0').Trim();
            if (uri.Length > 0 && uri[0] != '#')
            {
                yield return uri;
            }
        }
    }

    /// <summary>
    /// XdndStatus:接受时动作只给复制(本机拿到的是副本;不回移动,远端的原件不会因此被删),矩形为空(每动一下都报位置)。
    /// 投给源窗口的建窗口的客户端(SendEvent 掩码为空的语义)。
    /// </summary>
    private void SendOutgoingStatus(uint sourceWindow, uint targetWindow, bool accept)
    {
        if (Lookup<XWindow>(sourceWindow) is not { Owner: { Closed: false } owner })
        {
            return;
        }
        uint action = accept ? Intern("XdndActionCopy") : 0;
        uint status = Intern("XdndStatus");
        owner.Event(XEventCode.ClientMessage, 32, w => w.U32(sourceWindow).U32(status)
            .U32(targetWindow).U32(accept ? 1u : 0u).U32(0).U32(0).U32(action), sent: true);
    }

    /// <summary>XdndFinished:第 5 版的 l1 第 0 位是「接受了并做完了」,l2 是做的动作(没接受为 None)。</summary>
    private void SendOutgoingFinished(OutgoingDrag drag, bool accepted)
    {
        if (Lookup<XWindow>(drag.SourceWindow) is not { Owner: { Closed: false } owner })
        {
            return;
        }
        uint action = accepted ? Intern("XdndActionCopy") : 0;
        uint finished = Intern("XdndFinished");
        owner.Event(XEventCode.ClientMessage, 32, w => w.U32(drag.SourceWindow).U32(finished)
            .U32(drag.TargetWindow).U32(accepted ? 1u : 0u).U32(action).U32(0).U32(0), sent: true);
    }

    /// <summary>这一次结束了(离开、放下、源走了、换了新的一次):宿主拿到过数据却还没交回结果的,告诉它不用再发起了。</summary>
    private void EndOutgoingDrag()
    {
        if (_outgoing is not { } drag)
        {
            return;
        }
        _outgoing = null;
        if (drag.Announced is { } announced && drag.Dropped is null)
        {
            _host.OutgoingDragEnded(announced);
        }
    }

    /// <summary>窗口销毁时:是拖出那一次的源窗口就收尾(源程序崩溃或退出)。</summary>
    private void CleanupOutgoingDrag(XWindow window)
    {
        if (_outgoing is { } drag && drag.SourceWindow == window.Id)
        {
            EndOutgoingDrag();
        }
    }

    /// <summary>宿主交回的本机拖放结果(见 <see cref="CompleteOutgoingDrag" />)。</summary>
    private void ApplyCompleteOutgoingDrag(XOutgoingDrag drag, bool dropped)
    {
        if (_outgoing is { } current && ReferenceEquals(current.Announced, drag))
        {
            current.Dropped = dropped;
        }
    }
}
