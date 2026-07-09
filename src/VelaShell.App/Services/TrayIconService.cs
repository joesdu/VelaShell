using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace VelaShell.App.Services;

/// <summary>
/// 系统托盘图标(设置 → 常规 → 关闭时最小化到托盘)。使用应用图标资源
/// (Assets/velashell.ico)。仅在设置开启时挂载,关闭时移除。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Application _app;
    private TrayIcon? _trayIcon;

    public TrayIconService(Application app) => _app = app;

    /// <summary>点击托盘图标 / “显示主窗口”菜单。</summary>
    public event Action? ShowRequested;

    /// <summary>“退出”菜单:调用方负责绕过托盘拦截真正退出。</summary>
    public event Action? ExitRequested;

    public bool IsActive => _trayIcon is not null;

    public void SetEnabled(bool enabled)
    {
        if (enabled == IsActive)
            return;

        if (enabled)
        {
            _trayIcon = new TrayIcon
            {
                Icon = CreateGeneratedIcon(),
                ToolTipText = "VelaShell",
                Menu = BuildMenu(),
            };
            _trayIcon.Clicked += (_, _) => ShowRequested?.Invoke();
            TrayIcon.SetIcons(_app, [_trayIcon]);
        }
        else
        {
            Dispose();
        }
    }

    private NativeMenu BuildMenu()
    {
        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) => ShowRequested?.Invoke();
        var exit = new NativeMenuItem("退出 VelaShell");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        return new NativeMenu
        {
            Items = { show, new NativeMenuItemSeparator(), exit },
        };
    }

    /// <summary>托盘图标:加载应用图标资源(Assets/velashell.ico)。</summary>
    private static WindowIcon CreateGeneratedIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://VelaShell.App/Assets/velashell.ico"));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        if (_trayIcon is null)
            return;

        TrayIcon.SetIcons(_app, []);
        _trayIcon.Dispose();
        _trayIcon = null;
    }
}
