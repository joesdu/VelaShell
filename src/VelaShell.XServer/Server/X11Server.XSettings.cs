// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   XSETTINGS Specification 0.5(freedesktop.org)—— §「Protocol」(管理器占有 _XSETTINGS_S<screen> 选区,
//   设置放在属主窗口的 _XSETTINGS_SETTINGS 属性里)、§「Format」(byte-order、serial、N_SETTINGS,
//   每项:类型、名字、last-change-serial、值)、§「Registered settings」(Xft/DPI 以 1024 为单位等)
//   ICCCM 2.0 §2.8「Manager Selections」
//
//   服务端自己当 XSETTINGS 管理器:真实桌面总有一个(gnome-settings-daemon 之类),GTK / Qt 启动时会去找;
//   找不到时它们照样能跑,但会先踩一次 BadWindow / BadAtom。这里只发布字体渲染相关的几项。
//   有别的客户端(真正的设置守护进程)来抢这个选区时照常让出。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private int _dpi;
    private int _scale = 1;
    private uint _xsettingsSerial;

    private void InitXSettings()
    {
        _dpi = _options.Dpi;
        _scale = Math.Max(1, _options.ScaleFactor);
        _selections[Intern("_XSETTINGS_S0")] = (SelectionWindow, null, 0);
        PublishDisplaySettings();
    }

    /// <summary>
    /// 宿主的 DPI / 缩放变了(窗口挪到了另一台显示器、用户改了系统缩放):更新 XSETTINGS(Xft/DPI、Gdk/WindowScalingFactor、
    /// Gdk/UnscaledDPI)与根窗口的 RESOURCE_MANAGER(Xft.dpi)。GTK 立刻按新值重排;Xlib / Xft 与 Qt 程序在下次启动时生效。
    /// </summary>
    /// <param name="dpi">每英寸像素数(实际像素,如 2 倍缩放的 192)。</param>
    /// <param name="scale">整数缩放倍数(GTK 的窗口缩放)。</param>
    public void SetDisplayScale(int dpi, int scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        Post(null, () =>
        {
            _dpi = dpi;
            _scale = scale;
            PublishDisplaySettings();
        });
    }

    /// <summary>写 _XSETTINGS_SETTINGS(serial 递增)与 RESOURCE_MANAGER,并发 PropertyNotify。</summary>
    private void PublishDisplaySettings()
    {
        _xsettingsSerial++;
        uint settings = Intern("_XSETTINGS_SETTINGS");
        SelectionWindow.Properties[settings] = new XProperty(settings, 8, BuildXSettings());
        SendPropertyNotify(SelectionWindow, settings, deleted: false);

        uint resources = Intern("RESOURCE_MANAGER");
        string text = $"Xft.dpi:\t{_dpi}\nXft.antialias:\t1\nXft.hinting:\t1\nXft.hintstyle:\thintslight\nXft.rgba:\tnone\n";
        Root.Properties[resources] = new XProperty(XAtom.String, 8, XWire.Latin1.GetBytes(text));
        SendPropertyNotify(Root, resources, deleted: false);
    }

    private byte[] BuildXSettings()
    {
        List<byte> data = [0, 0, 0, 0];   // byte-order:LSBFirst,3 字节空
        AddU32(data, _xsettingsSerial);
        (string Name, object Value)[] items =
        [
            ("Xft/DPI", _dpi * 1024),
            ("Xft/Antialias", 1),
            ("Xft/Hinting", 1),
            ("Xft/HintStyle", "hintslight"),
            ("Xft/RGBA", "none"),
            ("Gdk/WindowScalingFactor", _scale),
            ("Gdk/UnscaledDPI", _dpi * 1024 / _scale),
        ];
        AddU32(data, (uint)items.Length);
        foreach ((string name, object value) in items)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            data.Add(value is string ? (byte)1 : (byte)0);   // 0 整数,1 字符串
            data.Add(0);
            data.Add((byte)nameBytes.Length);
            data.Add((byte)(nameBytes.Length >> 8));
            AddPadded(data, nameBytes);
            AddU32(data, _xsettingsSerial);   // last-change-serial
            if (value is string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                AddU32(data, (uint)bytes.Length);
                AddPadded(data, bytes);
            }
            else
            {
                AddU32(data, unchecked((uint)(int)value));
            }
        }
        return [.. data];

        static void AddU32(List<byte> list, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            list.AddRange(b);
        }

        static void AddPadded(List<byte> list, byte[] bytes)
        {
            list.AddRange(bytes);
            for (int i = bytes.Length; i % 4 != 0; i++)
            {
                list.Add(0);
            }
        }
    }
}
