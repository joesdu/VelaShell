using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// X 程序的托盘图标(F12)画成宿主自己的托盘图标:服务端当托盘管理器(<see cref="X11ServerOptions.SystemTray" />),图标停靠进来时
/// 交来嵌入窗口的句柄 —— 这里读它的像素做图标(随损伤更新,攒一下再读),悬停提示用图标的名字,经 SSH 转发来的按设置在前面标出来源
/// (与窗口标题同一个开关,<paramref name="showSource" />;原先托盘提示不标,远端程序挂一个盾牌图标、提示写「Windows 安全中心」就看不出是远端的);
/// 单击送一次左键,菜单里的「菜单」送一次右键(程序据此弹出自己的 X 菜单)。只在 UI 线程上用。
/// </summary>
internal sealed class XTrayIcons(Func<XTopLevelWindow, X11Server?> server, Func<bool> showSource)
{
    /// <summary>损伤之后隔这么久再读像素换图标:动画图标一秒几十帧地重画,托盘图标不必跟那么紧。</summary>
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<XTopLevelWindow, TrayIcon> _icons = [];

    /// <summary>每个图标的名字(服务端给的,没加来源):开关变了重算提示用。</summary>
    private readonly Dictionary<XTopLevelWindow, string> _titles = [];
    private readonly HashSet<XTopLevelWindow> _stale = [];
    private DispatcherTimer? _timer;

    /// <summary>此刻挂着的图标数(测试看)。</summary>
    internal int Count => _icons.Count;

    /// <summary>这个句柄是托盘图标吗。</summary>
    public bool Contains(XTopLevelWindow handle) => _icons.ContainsKey(handle);

    /// <summary>图标停靠进来了:建一个宿主的托盘图标。</summary>
    public void Add(XTopLevelWindow handle, string title)
    {
        if (_icons.ContainsKey(handle) || !handle.IsAlive || Application.Current is not { } app)
        {
            return;
        }
        NativeMenuItem click = new(Strings.Get("XServer_TrayClick"));
        click.Click += (_, _) => Click(handle, 1);
        NativeMenuItem menu = new(Strings.Get("XServer_TrayMenu"));
        menu.Click += (_, _) => Click(handle, 3);
        TrayIcon tray = new()
        {
            ToolTipText = ToolTip(handle, title),
            Menu = new NativeMenu { Items = { click, menu } },
            Icon = Render(handle),
            IsVisible = true,
        };
        tray.Clicked += (_, _) => Click(handle, 1);
        _icons[handle] = tray;
        _titles[handle] = title;
        IconsOf(app).Add(tray);
    }

    /// <summary>「标出来源」开关变了:按它重算每个图标的悬停提示。</summary>
    public void RefreshToolTips()
    {
        foreach ((XTopLevelWindow handle, TrayIcon tray) in _icons)
        {
            tray.ToolTipText = ToolTip(handle, _titles.GetValueOrDefault(handle, ""));
        }
    }

    /// <summary>悬停提示:图标的名字(没有时用来源本身),经 SSH 转发来的按设置在前面标出来源。</summary>
    private string? ToolTip(XTopLevelWindow handle, string title)
    {
        string? label = handle.Snapshot.ClientLabel;
        string text = title.Length > 0 ? XNativeWindow.WithSource(label, title, showSource()) : showSource() && !string.IsNullOrEmpty(label) ? label : "";
        return text.Length > 0 ? text : null;
    }

    /// <summary>图标没了:收掉对应的托盘图标。</summary>
    public void Remove(XTopLevelWindow handle)
    {
        if (!_icons.Remove(handle, out TrayIcon? tray))
        {
            return;
        }
        _titles.Remove(handle);
        _stale.Remove(handle);
        if (Application.Current is { } app)
        {
            IconsOf(app).Remove(tray);
        }
        tray.Dispose();
    }

    /// <summary>服务端停了:全部收掉。</summary>
    public void Clear()
    {
        foreach (XTopLevelWindow handle in _icons.Keys.ToArray())
        {
            Remove(handle);
        }
        _timer?.Stop();
    }

    /// <summary>图标重画了:过一会儿重读像素换图标(同一段时间里的多次损伤只读一次)。</summary>
    public void Damaged(XTopLevelWindow handle)
    {
        if (!_icons.ContainsKey(handle))
        {
            return;
        }
        _stale.Add(handle);
        _timer ??= new DispatcherTimer(RefreshDelay, DispatcherPriority.Background, (_, _) => RefreshStale());
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void RefreshStale()
    {
        _timer?.Stop();
        foreach (XTopLevelWindow handle in _stale)
        {
            if (_icons.TryGetValue(handle, out TrayIcon? tray) && Render(handle) is { } icon)
            {
                tray.Icon = icon;
            }
        }
        _stale.Clear();
    }

    /// <summary>托盘图标集合挂在应用上;别的托盘图标(VelaShell 自己的)也在里面,只加减自己的。</summary>
    private static TrayIcons IconsOf(Application app)
    {
        if (TrayIcon.GetIcons(app) is { } icons)
        {
            return icons;
        }
        TrayIcons created = [];
        TrayIcon.SetIcons(app, created);
        return created;
    }

    /// <summary>在图标中间送一次单击(先挪指针过去,程序按指针位置弹菜单)。</summary>
    private void Click(XTopLevelWindow handle, int button)
    {
        if (server(handle) is not { } x || !handle.IsAlive)
        {
            return;
        }
        XTopLevelSnapshot snapshot = handle.Snapshot;
        int cx = snapshot.Width / 2, cy = snapshot.Height / 2;
        x.InjectPointerMotion(handle, cx, cy);
        x.InjectPointerButton(handle, cx, cy, button, pressed: true);
        x.InjectPointerButton(handle, cx, cy, button, pressed: false);
    }

    /// <summary>读嵌入窗口的像素做成图标(没有 alpha 的当不透明;有 alpha 的本来就是预乘的 ARGB)。读不到为 null。</summary>
    private static WindowIcon? Render(XTopLevelWindow handle)
    {
        WriteableBitmap? bitmap = null;
        bool opaque = !handle.Snapshot.HasAlpha;
        handle.ReadPixels((pixels, width, height) =>
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using ILockedFramebuffer frame = bitmap.Lock();
            uint[] row = new uint[width];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<uint> source = pixels.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    row[x] = opaque ? source[x] | 0xFF000000u : source[x];
                }
                Marshal.Copy((int[])(object)row, 0, frame.Address + (y * frame.RowBytes), width);
            }
        });
        return bitmap is null ? null : new WindowIcon(bitmap);
    }
}
