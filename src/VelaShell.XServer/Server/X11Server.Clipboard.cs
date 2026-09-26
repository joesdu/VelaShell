// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Inter-Client Communication Conventions Manual (ICCCM) 2.0 —— §2.2「Responsibilities of the Selection Owner」
//   (回应 SelectionRequest:写属性再发 SelectionNotify;property 为 None 的旧式请求用 target 当属性名)、
//   §2.4「Requesting a Selection」(作为请求方:ConvertSelection、读属性、删属性)、
//   §2.5「Large Data Transfers」(INCR 分块协议)、§2.6.2「Target Atoms」(TARGETS、TIMESTAMP、TEXT、STRING)
//   X Window System Protocol —— 「SetSelectionOwner」「ConvertSelection」及 SelectionRequest / SelectionNotify 事件
//
//   与宿主的剪贴板互通:
//   · 宿主 → X:SetClipboardText 让服务端自己占有 CLIPBOARD(可选 PRIMARY),X 客户端来要时直接回;
//   · X → 宿主:X 客户端占有 CLIPBOARD(可选 PRIMARY)时,服务端以一个隐藏的 InputOnly 窗口为请求方
//     把内容要过来(支持 INCR),交给宿主的 ClipboardChanged。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>服务端作为选区请求方 / 属主时用的窗口(不映射、不挂进窗口树,客户端的 QueryTree 看不到)。</summary>
    private const uint SelectionWindowId = 0x43;

    /// <summary>从 X 客户端要过来的文本上限;超过就放弃这次同步。</summary>
    private const int MaxClipboardBytes = 16 * 1024 * 1024;

    private XWindow? _selectionWindow;

    /// <summary>宿主最近一次给的文本;服务端占有选区时拿它回应。</summary>
    private string _hostClipboard = "";

    /// <summary>最近一次交给宿主的文本 —— 宿主把它写回来时不再抢选区(防回声)。</summary>
    private string? _lastDeliveredText;

    private SelectionFetch? _fetch;

    /// <summary>一次进行中的「从 X 客户端取选区」。</summary>
    private sealed class SelectionFetch(uint selection, uint target, uint time)
    {
        public uint Selection { get; } = selection;

        public uint Target { get; set; } = target;

        public uint Time { get; } = time;

        /// <summary>INCR 传输中累积的字节;不在 INCR 里为 null。</summary>
        public List<byte>? Incr { get; set; }

        public uint IncrType { get; set; }
    }

    private XWindow SelectionWindow
    {
        get
        {
            if (_selectionWindow is null)
            {
                _selectionWindow = new XWindow(SelectionWindowId, null, Root)
                {
                    Class = 2,   // InputOnly
                    Width = 1,
                    Height = 1,
                    Visual = RootVisualId,
                };
                _resources[SelectionWindowId] = _selectionWindow;
            }
            return _selectionWindow;
        }
    }

    /// <summary>宿主的剪贴板有了新文本(见 <see cref="SetClipboardText" />):服务端替宿主占有 CLIPBOARD(与 PRIMARY)。</summary>
    private void ApplyClipboardText(string text)
    {
        if (!_options.SyncClipboard || text == _lastDeliveredText)
        {
            return;   // 宿主把我们刚给的写回来了
        }
        _hostClipboard = text;
        TakeSelectionForHost(Intern("CLIPBOARD"));
        if (_options.SyncPrimary)
        {
            TakeSelectionForHost(XAtom.Primary);
        }
    }

    private void TakeSelectionForHost(uint selection)
    {
        uint now = Now;
        if (_selections.TryGetValue(selection, out var current) && current.Client is { } previous)
        {
            XWindow old = current.Window;
            previous.Event(XEventCode.SelectionClear, 0, w => w.U32(now).U32(old.Id).U32(selection));
        }
        _selections[selection] = (SelectionWindow, null, now);
        NotifySelectionChange(selection, 0, SelectionWindowId, now);
    }

    private bool IsSyncedSelection(uint selection) =>
        _options.SyncClipboard && (selection == Intern("CLIPBOARD") || (_options.SyncPrimary && selection == XAtom.Primary));

    // ------------------------------------------------------------------ 服务端当属主

    /// <summary>服务端占有的选区被 ConvertSelection 了:按目标写属性,再发 SelectionNotify(ICCCM §2.2)。</summary>
    private void ServeSelection(XClient c, XWindow requestor, uint selection, uint target, uint property, uint time, uint ownerTime)
    {
        if (property == 0)
        {
            property = target;   // 旧式请求方(ICCCM §2.2)
        }
        uint utf8 = Intern("UTF8_STRING");
        uint targets = Intern("TARGETS");
        uint timestamp = Intern("TIMESTAMP");
        uint text = Intern("TEXT");
        uint plainUtf8 = Intern("text/plain;charset=utf-8");

        XProperty? value = null;
        if (selection != Intern("CLIPBOARD") && selection != XAtom.Primary)
        {
            // 服务端占有的其它选区(_XSETTINGS_S0 这类管理器选区)没有可转换的内容。
        }
        else if (target == targets)
        {
            uint[] atoms = [targets, timestamp, utf8, plainUtf8, XAtom.String, text];
            byte[] data = new byte[atoms.Length * 4];
            for (int i = 0; i < atoms.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), atoms[i]);
            }
            value = new XProperty(XAtom.Atom, 32, data);
        }
        else if (target == timestamp)
        {
            byte[] data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, ownerTime);
            value = new XProperty(XAtom.Integer, 32, data);
        }
        else if (target == utf8 || target == plainUtf8)
        {
            value = new XProperty(target, 8, Encoding.UTF8.GetBytes(_hostClipboard));
        }
        else if (target == XAtom.String || target == text)
        {
            value = new XProperty(XAtom.String, 8, Encoding.Latin1.GetBytes(_hostClipboard));
        }

        if (value is null)
        {
            property = 0;   // 不支持的目标:拒绝
        }
        else
        {
            requestor.Properties[property] = value;
            SendPropertyNotify(requestor, property, deleted: false);
        }
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
    }

    // ------------------------------------------------------------------ 服务端当请求方

    /// <summary>X 客户端占有了同步的选区:向它要 UTF8_STRING(不给再退回 STRING)。</summary>
    private void OnClientTookSelection(XClient owner, XWindow ownerWindow, uint selection, uint time)
    {
        if (!IsSyncedSelection(selection))
        {
            return;
        }
        _fetch = new SelectionFetch(selection, Intern("UTF8_STRING"), time);
        RequestFetch(owner, ownerWindow);
    }

    private void RequestFetch(XClient owner, XWindow ownerWindow)
    {
        if (_fetch is not { } fetch)
        {
            return;
        }
        uint property = Intern("_VELASHELL_SELECTION");
        uint requestor = SelectionWindow.Id;
        owner.Event(XEventCode.SelectionRequest, 0, w => w
            .U32(fetch.Time).U32(ownerWindow.Id).U32(requestor).U32(fetch.Selection).U32(fetch.Target).U32(property));
    }

    /// <summary>属主用 SendEvent 把 SelectionNotify 发到了我们的请求窗口。</summary>
    private void OnSelectionWindowEvent(byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.SelectionNotify || _fetch is not { } fetch)
        {
            return;
        }
        uint selection = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        uint property = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(20)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
        if (selection != fetch.Selection)
        {
            return;
        }
        if (property == 0)
        {
            // 属主不给这个目标:UTF8_STRING 不行就退回 STRING,再不行就算了。
            if (fetch.Target != XAtom.String && _selections.TryGetValue(selection, out var owner) && owner.Client is { } client)
            {
                fetch.Target = XAtom.String;
                RequestFetch(client, owner.Window);
            }
            else
            {
                _fetch = null;
            }
            return;
        }

        XWindow window = SelectionWindow;
        if (!window.Properties.TryGetValue(property, out XProperty? value))
        {
            _fetch = null;
            return;
        }
        if (value.Type == Intern("INCR"))
        {
            // 大数据:删掉属性表示「准备好了」,属主随后一块块往里写(ICCCM §2.5)。
            fetch.Incr = [];
            DeleteSelectionProperty(property);
            return;
        }
        DeleteSelectionProperty(property);
        _fetch = null;
        Deliver(value.Type, value.Data);
    }

    /// <summary>INCR 传输中:属主往请求窗口写了一块。空块表示结束。</summary>
    private void OnSelectionWindowProperty(uint property, bool deleted)
    {
        if (deleted || _fetch is not { Incr: { } buffer } fetch || property != Intern("_VELASHELL_SELECTION"))
        {
            return;
        }
        XProperty chunk = SelectionWindow.Properties[property];
        DeleteSelectionProperty(property);
        if (chunk.Data.Length == 0)
        {
            _fetch = null;
            Deliver(fetch.IncrType, [.. buffer]);
            return;
        }
        fetch.IncrType = chunk.Type;
        buffer.AddRange(chunk.Data);
        if (buffer.Count > MaxClipboardBytes)
        {
            _fetch = null;   // 太大:放弃。属主写下一块时没人删属性,它自己会超时
        }
    }

    private void DeleteSelectionProperty(uint property)
    {
        if (SelectionWindow.Properties.Remove(property))
        {
            SendPropertyNotify(SelectionWindow, property, deleted: true);
        }
    }

    private void Deliver(uint type, byte[] data)
    {
        if (data.Length > MaxClipboardBytes)
        {
            return;
        }
        string text = type == XAtom.String ? Encoding.Latin1.GetString(data) : Encoding.UTF8.GetString(data);
        _lastDeliveredText = text;
        _host.ClipboardChanged(text);
    }
}
