using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PulseTerm.App.Services;

/// <summary>
/// 系统托盘图标(设置 → 常规 → 关闭时最小化到托盘)。项目没有 .ico 资源,图标在运行时
/// 自绘(强调色圆角方块 + ">_" 终端记号)。仅在设置开启时挂载,关闭时移除。
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
                ToolTipText = "PulseTerm",
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
        var exit = new NativeMenuItem("退出 PulseTerm");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        return new NativeMenu
        {
            Items = { show, new NativeMenuItemSeparator(), exit },
        };
    }

    /// <summary>32×32 自绘图标:深底圆角方块上画强调色 ">_"。</summary>
    private static WindowIcon CreateGeneratedIcon()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#282A36")), null,
                new RoundedRect(new Rect(0, 0, 32, 32), 7));
            var text = new FormattedText(">_", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas", weight: FontWeight.Bold), 16,
                new SolidColorBrush(Color.Parse("#00D4AA")));
            ctx.DrawText(text, new Point(5, 7));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
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
