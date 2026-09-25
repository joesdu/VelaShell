// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Input Device Extension Protocol, Version 2.2 —— §4「Devices」(主设备成对:主指针 + 主键盘;从设备挂在同类的主设备上,
//   也可以浮动;浮动的从设备不产生核心事件,也不经主设备报 XI2 事件)、「XIChangeHierarchy」(HIERARCHYCHANGE:
//   AddMaster 1 —— name_len、send_core、enable、name,新建「name pointer」「name keyboard」一对;RemoveMaster 2 ——
//   deviceid、return_mode(Float 1 / AttachToMaster 2)、return_pointer、return_keyboard;AttachSlave 3 —— deviceid、master;
//   DetachSlave 4 —— deviceid;虚拟核心指针 / 键盘不能删除)、「HierarchyEvent」(evtype 11:flags 与每个设备一条
//   HIERARCHYINFO —— deviceid、attachment、use、enabled、flags;MasterAdded 1、MasterRemoved 2、SlaveAdded 4、SlaveRemoved 8、
//   SlaveAttached 16、SlaveDetached 32、DeviceEnabled 64、DeviceDisabled 128)。
//
//   输入只有宿主注入的一对物理从设备(4、5):它们挂在哪个主设备上,事件就以那个主设备报;浮动时只报从设备的 XI2 事件。
//   指针位置、焦点与抓取仍是一份(不是完整的多指针 X,见架构 §7)。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private const int XiHierarchyChanged = 11;

    /// <summary>设备用途:主指针 1、主键盘 2、从指针 3、从键盘 4、浮动从设备 5(XI2 §4)。</summary>
    private const ushort XiUseMasterPointer = 1, XiUseMasterKeyboard = 2, XiUseSlavePointer = 3, XiUseSlaveKeyboard = 4, XiUseFloating = 5;

    /// <summary>一个 XI2 设备:主设备的 <see cref="Attachment" /> 是与它配对的另一个主设备,从设备的是它挂的主设备(浮动为 0)。</summary>
    private sealed class XiDevice(ushort id, bool pointer, bool master, string name)
    {
        public ushort Id { get; } = id;

        public bool Pointer { get; } = pointer;

        public bool Master { get; } = master;

        public string Name { get; } = name;

        public ushort Attachment { get; set; }

        public bool Enabled { get; set; } = true;

        public ushort Use => Master ? (Pointer ? XiUseMasterPointer : XiUseMasterKeyboard)
            : Attachment == 0 ? XiUseFloating
            : Pointer ? XiUseSlavePointer : XiUseSlaveKeyboard;
    }

    private readonly SortedDictionary<ushort, XiDevice> _xiDevices = CreateInitialDevices();

    private static SortedDictionary<ushort, XiDevice> CreateInitialDevices()
    {
        SortedDictionary<ushort, XiDevice> devices = new()
        {
            [XiMasterPointer] = new XiDevice(XiMasterPointer, pointer: true, master: true, "Virtual core pointer") { Attachment = XiMasterKeyboard },
            [XiMasterKeyboard] = new XiDevice(XiMasterKeyboard, pointer: false, master: true, "Virtual core keyboard") { Attachment = XiMasterPointer },
            [XiSlavePointer] = new XiDevice(XiSlavePointer, pointer: true, master: false, "VelaShell pointer") { Attachment = XiMasterPointer },
            [XiSlaveKeyboard] = new XiDevice(XiSlaveKeyboard, pointer: false, master: false, "VelaShell keyboard") { Attachment = XiMasterKeyboard },
        };
        return devices;
    }

    private bool IsKnownDevice(ushort id) => _xiDevices.ContainsKey(id);

    private bool IsPointerDevice(ushort id) => _xiDevices.TryGetValue(id, out XiDevice? d) && d.Pointer;

    private bool IsMasterDevice(ushort id) => _xiDevices.TryGetValue(id, out XiDevice? d) && d.Master;

    /// <summary>物理从设备现在挂在哪个主设备上;浮动时为 0。</summary>
    private ushort MasterOf(bool pointer) => _xiDevices[pointer ? XiSlavePointer : XiSlaveKeyboard].Attachment;

    /// <summary>物理从设备浮动了:它的事件不产生核心事件,也不经主设备报。</summary>
    private bool IsFloating(bool pointer) => MasterOf(pointer) == 0;

    private void XiChangeHierarchy(XRequestReader r)
    {
        int count = r.U8();
        r.Skip(3);
        Dictionary<ushort, uint> changed = [];
        uint flags = 0;

        void Note(ushort id, uint flag)
        {
            changed[id] = changed.GetValueOrDefault(id) | flag;
            flags |= flag;
        }

        for (int i = 0; i < count; i++)
        {
            ushort type = r.U16();
            ushort length = r.U16();
            if (length < 1 || (length - 1) * 4 > r.Remaining)
            {
                throw new XProtocolError(XErrorCode.Length);
            }
            XRequestReader b = r.Slice((length - 1) * 4);
            switch (type)
            {
                case 1:   // AddMaster
                    {
                        ushort nameLength = b.U16();
                        b.Bool();   // send_core:所有主设备都产生核心事件
                        bool enable = b.Bool();
                        string name = XWire.Latin1.GetString(b.Bytes(nameLength));
                        ushort pointerId = NextDeviceId(), keyboardId = (ushort)(pointerId + 1);
                        _xiDevices[pointerId] = new XiDevice(pointerId, pointer: true, master: true, name + " pointer")
                        {
                            Attachment = keyboardId,
                            Enabled = enable,
                        };
                        _xiDevices[keyboardId] = new XiDevice(keyboardId, pointer: false, master: true, name + " keyboard")
                        {
                            Attachment = pointerId,
                            Enabled = enable,
                        };
                        Note(pointerId, 1 | (enable ? 64u : 0));
                        Note(keyboardId, 1 | (enable ? 64u : 0));
                        break;
                    }
                case 2:   // RemoveMaster
                    {
                        ushort id = b.U16();
                        byte returnMode = b.U8();
                        b.Skip(1);
                        ushort returnPointer = b.U16(), returnKeyboard = b.U16();
                        // AttachToMaster 而没指定挂到哪(0):挂回虚拟核心设备 —— xinput remove-master 默认就这么发。
                        returnPointer = returnPointer == 0 ? XiMasterPointer : returnPointer;
                        returnKeyboard = returnKeyboard == 0 ? XiMasterKeyboard : returnKeyboard;
                        if (!_xiDevices.TryGetValue(id, out XiDevice? master) || !master.Master)
                        {
                            throw BadDevice(id);
                        }
                        XiDevice pointerMaster = master.Pointer ? master : _xiDevices[master.Attachment];
                        XiDevice keyboardMaster = master.Pointer ? _xiDevices[master.Attachment] : master;
                        if (pointerMaster.Id == XiMasterPointer)
                        {
                            throw BadDevice(id);   // 虚拟核心设备不能删除
                        }
                        if (returnMode is not (1 or 2))
                        {
                            throw new XProtocolError(XErrorCode.Value, returnMode);
                        }
                        if (returnMode == 2 && (!IsMasterDevice(returnPointer) || !IsPointerDevice(returnPointer)
                                                || !IsMasterDevice(returnKeyboard) || IsPointerDevice(returnKeyboard)
                                                || returnPointer == pointerMaster.Id || returnKeyboard == keyboardMaster.Id))
                        {
                            throw new XProtocolError(XErrorCode.Match);
                        }
                        foreach (XiDevice slave in _xiDevices.Values.Where(d => !d.Master).ToArray())
                        {
                            if (slave.Attachment == pointerMaster.Id || slave.Attachment == keyboardMaster.Id)
                            {
                                slave.Attachment = returnMode == 2 ? (slave.Pointer ? returnPointer : returnKeyboard) : (ushort)0;
                                Note(slave.Id, returnMode == 2 ? 16u : 32u);
                            }
                        }
                        _xiDevices.Remove(pointerMaster.Id);
                        _xiDevices.Remove(keyboardMaster.Id);
                        _deviceProperties.Remove(pointerMaster.Id);
                        _deviceProperties.Remove(keyboardMaster.Id);
                        Note(pointerMaster.Id, 2);
                        Note(keyboardMaster.Id, 2);
                        break;
                    }
                case 3:   // AttachSlave
                    {
                        ushort id = b.U16(), masterId = b.U16();
                        if (!_xiDevices.TryGetValue(id, out XiDevice? slave) || slave.Master)
                        {
                            throw BadDevice(id);
                        }
                        if (!_xiDevices.TryGetValue(masterId, out XiDevice? master) || !master.Master)
                        {
                            throw BadDevice(masterId);
                        }
                        if (master.Pointer != slave.Pointer)
                        {
                            throw new XProtocolError(XErrorCode.Match);
                        }
                        if (slave.Attachment != masterId)
                        {
                            slave.Attachment = masterId;
                            Note(id, 16);
                        }
                        break;
                    }
                case 4:   // DetachSlave
                    {
                        ushort id = b.U16();
                        if (!_xiDevices.TryGetValue(id, out XiDevice? slave) || slave.Master)
                        {
                            throw BadDevice(id);
                        }
                        if (slave.Attachment != 0)
                        {
                            slave.Attachment = 0;
                            Note(id, 32);
                        }
                        break;
                    }
                default:
                    throw new XProtocolError(XErrorCode.Value, type);
            }
        }
        if (flags != 0)
        {
            SendHierarchyChanged(flags, changed);
        }
    }

    private ushort NextDeviceId()
    {
        ushort id = 6;
        while (_xiDevices.ContainsKey(id) || _xiDevices.ContainsKey((ushort)(id + 1)))
        {
            id += 2;
        }
        return id;
    }

    /// <summary>HierarchyEvent:发给在根窗口上选了它的客户端,带全部设备(被删掉的也带一条,flags 说明发生了什么)。</summary>
    private void SendHierarchyChanged(uint flags, Dictionary<ushort, uint> changed)
    {
        if (!Root.AnyXi2Selects(XiHierarchyChanged))
        {
            return;
        }
        List<(ushort Id, ushort Attachment, ushort Use, bool Enabled, uint Flags)> info = [];
        foreach (XiDevice device in _xiDevices.Values)
        {
            info.Add((device.Id, device.Attachment, device.Use, device.Enabled, changed.GetValueOrDefault(device.Id)));
        }
        foreach ((ushort id, uint deviceFlags) in changed)
        {
            if (!_xiDevices.ContainsKey(id))
            {
                info.Add((id, 0, 0, false, deviceFlags));   // 已删除的主设备
            }
        }
        uint time = Now;
        foreach ((XClient client, (ulong master, ulong slave)) in Root.Xi2Selections)
        {
            if (client.Closed || ((master | slave) & (1UL << XiHierarchyChanged)) == 0)
            {
                continue;
            }
            client.GenericEvent(XInputMajor, XiHierarchyChanged, w =>
            {
                w.U16(0).U32(time).U32(flags).U16((ushort)info.Count).Zero(10);
                foreach ((ushort id, ushort attachment, ushort use, bool enabled, uint deviceFlags) in info)
                {
                    w.U16(id).U16(attachment).U8((byte)use).Bool(enabled).Zero(2).U32(deviceFlags);
                }
            });
        }
    }
}
