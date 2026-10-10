// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   XSETTINGS Specification 0.5(freedesktop.org)—— §「Protocol」(管理器占有 _XSETTINGS_S<screen> 选区,
//   设置放在属主窗口的 _XSETTINGS_SETTINGS 属性里)、§「Format」(byte-order、serial、N_SETTINGS,
//   每项:类型、名字、last-change-serial、值)、§「Registered settings」(Xft/DPI 以 1024 为单位等)
//   ICCCM 2.0 §2.8「Manager Selections」;根窗口的 RESOURCE_MANAGER(Xlib 资源文件的格式:每条「名字: 值」一行,行尾反斜杠续行)
//
//   服务端自己当 XSETTINGS 管理器:真实桌面总有一个(gnome-settings-daemon 之类),GTK / Qt 启动时会去找;
//   找不到时它们照样能跑,但会先踩一次 BadWindow / BadAtom。这里只发布字体渲染相关的几项。
//   有别的客户端(真正的设置守护进程)来抢这个选区时照常让出。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private int _dpi;
    private int _scale = 1;
    private uint _xsettingsSerial;

    private void InitXSettings()
    {
        _dpi = _options.Dpi;
        _scale = _options.ScaleFactor;
        if (!Rootful)   // 单窗口模式:XSETTINGS 归远端桌面的设置守护进程(xfsettingsd、gsd-xsettings)
        {
            _selections[new SelectionSlot(Intern("_XSETTINGS_S0"), null)] = (SelectionWindow, null, 0);
        }
        PublishDisplaySettings();
    }

    /// <summary>宿主的 DPI / 缩放变了(见 <see cref="SetDisplayScale" />):重新发布 XSETTINGS 与 RESOURCE_MANAGER。</summary>
    private void ApplyDisplayScale(int dpi, int scale)
    {
        bool dpiChanged = dpi != _dpi;
        _dpi = dpi;
        _scale = scale;
        PublishDisplaySettings();
        if (dpiChanged)
        {
            // 屏幕的毫米数按 DPI 换算:变了要告诉 RANDR 的客户端(原先只有布局变化才发 ScreenChangeNotify)。
            _layoutTime = Now;
            NotifyRandRChange(screenOnly: true);
        }
    }

    /// <summary>写 _XSETTINGS_SETTINGS(serial 递增)与 RESOURCE_MANAGER,并发 PropertyNotify。</summary>
    private void PublishDisplaySettings()
    {
        _xsettingsSerial++;
        uint settings = Intern("_XSETTINGS_SETTINGS");
        StoreServerProperty(SelectionWindow, settings, new XProperty(settings, 8, BuildXSettings()));
        SendPropertyNotify(SelectionWindow, settings, deleted: false);

        uint resources = Intern("RESOURCE_MANAGER");
        string text = $"Xft.dpi:\t{_dpi}\nXft.antialias:\t1\nXft.hinting:\t1\nXft.hintstyle:\thintslight\nXft.rgba:\tnone\n";
        if (Root.Properties.GetValueOrDefault(resources) is { Format: 8 } existing)
        {
            text = MergeResources(XWire.Latin1.GetString(existing.Data), text);
        }
        StoreServerProperty(Root, resources, new XProperty(XAtom.String, 8, XWire.Latin1.GetBytes(text)));
        SendPropertyNotify(Root, resources, deleted: false);
    }

    /// <summary>服务端在 RESOURCE_MANAGER 里管的那几项(换 DPI 时只换它们)。</summary>
    private static readonly string[] ManagedResources = ["Xft.dpi", "Xft.antialias", "Xft.hinting", "Xft.hintstyle", "Xft.rgba"];

    /// <summary>
    /// 根窗口上已有的资源(用户 <c>xrdb -merge</c> 进去的 xterm / Emacs / Motif 设置)留着,只把 <see cref="ManagedResources" /> 那几项
    /// 换成 <paramref name="ours" />。原先换一次 DPI 就整份覆盖,用户的资源全丢。按逻辑行处理(行尾的反斜杠接着下一行),注释照留。
    /// </summary>
    private static string MergeResources(string existing, string ours)
    {
        StringBuilder merged = new(existing.Length + ours.Length);
        int start = 0;
        while (start < existing.Length)
        {
            int end = start;
            while (true)
            {
                int newline = existing.IndexOf('\n', end);
                if (newline < 0)
                {
                    end = existing.Length;
                    break;
                }
                end = newline + 1;
                if (newline == start || existing[newline - 1] != '\\')
                {
                    break;
                }
            }
            ReadOnlySpan<char> entry = existing.AsSpan(start, end - start);
            start = end;
            int colon = entry.IndexOf(':');
            if (colon >= 0 && ManagedResources.AsSpan().Contains(entry[..colon].Trim().ToString()))
            {
                continue;
            }
            if (!entry.IsWhiteSpace())
            {
                merged.Append(entry);
                if (entry[^1] != '\n')
                {
                    merged.Append('\n');
                }
            }
        }
        return merged.Append(ours).ToString();
    }

    private byte[] BuildXSettings()
    {
        List<byte> data = [0, 0, 0, 0];   // byte-order:LSBFirst,3 字节空
        AddU32(data, _xsettingsSerial);
        List<(string Name, object Value)> items =
        [
            ("Xft/DPI", _dpi * 1024),
            ("Xft/Antialias", 1),
            ("Xft/Hinting", 1),
            ("Xft/HintStyle", "hintslight"),
            ("Xft/RGBA", "none"),
            ("Gdk/WindowScalingFactor", _scale),
            ("Gdk/UnscaledDPI", _dpi * 1024 / _scale),
        ];
        if (_options.InputMethodName is not null)
        {
            // 服务端当着 XIM 输入法服务端:请 GTK 2/3 用 XIM 输入模块(环境变量 GTK_IM_MODULE 设了的照它)。没有 XMODIFIERS 时
            // Xlib 用本地的组合键处理,与 GTK 自己的简单输入法一样能打字。
            items.Add(("Gtk/IMModule", "xim"));
        }
        AddU32(data, (uint)items.Count);
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
