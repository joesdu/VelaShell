using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace VelaShell.Services;

/// <summary>
/// 系统托盘图标(设置 → 常规 → 关闭时最小化到托盘)。使用应用图标资源
/// (Assets/velashell.ico)。仅在设置开启时挂载,关闭时移除。
/// </summary>
public sealed class TrayIconService(Application app) : IDisposable
{
    private TrayIcon? _trayIcon;

    public bool IsActive => _trayIcon is not null;

    public void Dispose()
    {
        if (_trayIcon is null)
        {
            return;
        }
        TrayIcon.SetIcons(app, []);
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    /// <summary>点击托盘图标 / “显示主窗口”菜单。</summary>
    public event Action? ShowRequested;

    /// <summary>“退出”菜单:调用方负责绕过托盘拦截真正退出。</summary>
    public event Action? ExitRequested;

    public void SetEnabled(bool enabled)
    {
        if (enabled == IsActive)
        {
            return;
        }
        if (enabled)
        {
            _trayIcon = new()
            {
                Icon = CreateGeneratedIcon(),
                ToolTipText = "VelaShell",
                Menu = BuildMenu()
            };
            _trayIcon.Clicked += (_, _) => ShowRequested?.Invoke();
            TrayIcon.SetIcons(app, [_trayIcon]);
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
        return new()
        {
            Items = { show, new NativeMenuItemSeparator(), exit }
        };
    }

    /// <summary>托盘图标:加载应用图标资源(Assets/velashell.ico)。</summary>
    private static WindowIcon CreateGeneratedIcon()
    {
        using Stream stream = AssetLoader.Open(new("avares://VelaShell/Assets/velashell.ico"));
        return new(stream);
    }
}
