using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using static VelaShell.Splash.SplashNative;

namespace VelaShell.Splash;

/// <summary>
/// 启动画面的原生窗口:无边框的分层窗口(<c>WS_EX_LAYERED</c>),内容由 Skia 直接画进一块 DIB,
/// 再用 <c>UpdateLayeredWindow</c> 按逐像素 alpha 贴上屏幕。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是分层窗口</b>:圆角与投影都要自己画(分层窗口不吃 DWM 的圆角与阴影),换来的是 Windows 10 / 11
/// 上长得一模一样,透明的投影区域还自动点击穿透;整体透明度也顺手拿来做淡入淡出。
/// </para>
/// <para>
/// <b>线程</b>:整个窗口活在启动画面自己的线程上,有自己的消息循环 —— 主线程此时正忙着初始化 Avalonia,
/// 一连几秒不处理消息,画面若挂在主线程上就会卡住不动。除 <see cref="RequestClose" /> 外,
/// 所有成员只能在创建它的线程上调用。
/// </para>
/// <para>
/// <b>不抢焦点</b>:用 <c>SW_SHOWNOACTIVATE</c> 显示。进程从资源管理器拿到的「可以把窗口带到前台」的权利
/// 原样留给主窗口;启动期间用户切去别的窗口,画面也不会把焦点抢回来。
/// </para>
/// </remarks>
internal sealed unsafe class Win32SplashWindow : IDisposable
{
    private const string ClassName = "VelaShell.Splash";
    private const nuint TimerId = 1;

    /// <summary>别的线程请求关闭时投递的消息(开始淡出,淡出完再销毁)。</summary>
    private const uint WmRequestClose = WM_APP + 1;

    /// <summary>卡片四周留给投影的边距(逻辑单位)。</summary>
    private const float ShadowMargin = 32;

    private const double FadeInMs = 120;
    private const double FadeOutMs = 160;

    /// <summary>窗口过程按线程找到自己的实例:一个线程至多一个启动画面窗口。</summary>
    [ThreadStatic]
    private static Win32SplashWindow? _current;

    private readonly SplashRenderer _renderer;
    private readonly Func<SplashFrame> _frames;
    private readonly float _scale;
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private readonly nint _hwnd;
    private readonly nint _memoryDc;
    private readonly nint _dib;
    private readonly nint _previousBitmap;
    private readonly SKSurface _surface;
    private readonly nint _bigIcon;
    private readonly nint _smallIcon;
    private readonly long _shownAt;
    private long _closingAt = -1;
    private bool _destroyed;
    private bool _disposed;
    private int _frameCount;
    private double _renderMsTotal;
    private double _renderMsMax;

    /// <summary>在当前线程上创建窗口(还不显示)。</summary>
    /// <param name="renderer">渲染器。</param>
    /// <param name="frames">每帧取一次状态。</param>
    public Win32SplashWindow(SplashRenderer renderer, Func<SplashFrame> frames)
    {
        _renderer = renderer;
        _frames = frames;

        // 放在鼠标所在的那块屏幕上:用户在哪块屏幕上点的图标,就在哪块屏幕上给反馈。
        POINT cursor;
        GetCursorPos(&cursor);
        nint monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        MONITORINFO info = new() { CbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, &info);
        uint dpiX = 96, dpiY = 96;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY) != 0 || dpiX == 0)
        {
            dpiX = 96;
        }
        _scale = dpiX / 96f;
        _width = (int)Math.Ceiling((SplashRenderer.Width + 2 * ShadowMargin) * _scale);
        _height = (int)Math.Ceiling((SplashRenderer.Height + 2 * ShadowMargin) * _scale);
        RECT work = info.RcWork;
        _x = work.Left + (work.Right - work.Left - _width) / 2;
        _y = work.Top + (work.Bottom - work.Top - _height) / 2;

        nint instance = GetModuleHandleW(0);
        fixed (char* className = ClassName)
        {
            WNDCLASSEXW windowClass = new()
            {
                CbSize = (uint)sizeof(WNDCLASSEXW),
                LpfnWndProc = &WindowProc,
                HInstance = instance,
                // 鼠标移到画面上是「后台忙」的箭头:与系统给刚启动的程序的那个光标同一个意思。
                HCursor = LoadCursorW(0, IDC_APPSTARTING),
                LpszClassName = className
            };
            // 同进程里第二次创建(理论上不会)时类已存在,注册失败照样能用。
            RegisterClassExW(&windowClass);
        }

        _current = this;
        // 有任务栏按钮(WS_EX_APPWINDOW):启动时任务栏上立刻出现 VelaShell,本身就是「在打开了」的信号。
        _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_APPWINDOW, ClassName, "VelaShell", WS_POPUP,
                                _x, _y, _width, _height, 0, 0, instance, 0);
        if (_hwnd == 0)
        {
            _current = null;
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        }

        BITMAPINFOHEADER header = new()
        {
            BiSize = (uint)sizeof(BITMAPINFOHEADER),
            BiWidth = _width,
            BiHeight = -_height, // 自上而下,与 Skia 的行序一致
            BiPlanes = 1,
            BiBitCount = 32
        };
        nint screenDc = GetDC(0);
        _memoryDc = CreateCompatibleDC(screenDc);
        void* bits;
        _dib = CreateDIBSection(screenDc, &header, DIB_RGB_COLORS, &bits, 0, 0);
        ReleaseDC(0, screenDc);
        if (_dib == 0)
        {
            throw new InvalidOperationException("CreateDIBSection failed.");
        }
        _previousBitmap = SelectObject(_memoryDc, _dib);
        // 分层窗口要的正是预乘 alpha 的 BGRA,Skia 直接画进 DIB 的内存,一次拷贝都不用。
        _surface = SKSurface.Create(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul), (nint)bits, _width * 4)
                   ?? throw new InvalidOperationException("SKSurface.Create failed.");

        (_bigIcon, _smallIcon) = (ExtractIcon(32), ExtractIcon(16));
        if (_bigIcon != 0)
        {
            SendMessageW(_hwnd, WM_SETICON, ICON_BIG, _bigIcon);
        }
        if (_smallIcon != 0)
        {
            SendMessageW(_hwnd, WM_SETICON, ICON_SMALL, _smallIcon);
        }
        _shownAt = Stopwatch.GetTimestamp();
    }

    /// <summary>显示窗口并跑消息循环,直到窗口销毁(淡出结束,或用户 Alt+F4)。</summary>
    public void Run()
    {
        Present();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SetTimer(_hwnd, TimerId, 15, 0);
        MSG message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    /// <summary>请求关闭(开始淡出)。可在任意线程调用;窗口已销毁时无效。</summary>
    public void RequestClose() => PostMessageW(_hwnd, WmRequestClose, 0, 0);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_destroyed)
        {
            DestroyWindow(_hwnd);
        }
        // 一行自诊断:画面开了多久、画了多少帧、每帧多贵。启动慢的报告里有它,就能看出画面本身拖没拖后腿。
        Trace.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[Splash] style={_renderer.Style} scale={_scale:0.##} open={Stopwatch.GetElapsedTime(_shownAt).TotalMilliseconds:0} ms "
            + $"frames={_frameCount} render avg={(_frameCount == 0 ? 0 : _renderMsTotal / _frameCount):0.0} ms max={_renderMsMax:0.0} ms"));
        _surface.Dispose();
        SelectObject(_memoryDc, _previousBitmap);
        DeleteObject(_dib);
        DeleteDC(_memoryDc);
        if (_bigIcon != 0)
        {
            DestroyIcon(_bigIcon);
        }
        if (_smallIcon != 0)
        {
            DestroyIcon(_smallIcon);
        }
        UnregisterClassW(ClassName, GetModuleHandleW(0));
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    /// <summary>画一帧并贴上屏幕;淡出走完时销毁窗口。</summary>
    private void Present()
    {
        double opacity = Math.Clamp(Stopwatch.GetElapsedTime(_shownAt).TotalMilliseconds / FadeInMs, 0, 1);
        if (_closingAt >= 0)
        {
            double fadeOut = 1 - Stopwatch.GetElapsedTime(_closingAt).TotalMilliseconds / FadeOutMs;
            if (fadeOut <= 0)
            {
                DestroyWindow(_hwnd);
                return;
            }
            opacity = Math.Min(opacity, fadeOut);
        }

        long renderStart = Stopwatch.GetTimestamp();
        SKCanvas canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale(_scale);
        canvas.Translate(ShadowMargin, ShadowMargin);
        SplashRenderer.DrawShadow(canvas, _renderer.Palette.IsDark);
        _renderer.Render(canvas, _frames());
        canvas.Restore();
        canvas.Flush();

        POINT position = new() { X = _x, Y = _y };
        SIZE size = new() { Cx = _width, Cy = _height };
        POINT source = default;
        BLENDFUNCTION blend = new()
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = (byte)Math.Round(opacity * 255),
            AlphaFormat = AC_SRC_ALPHA
        };
        UpdateLayeredWindow(_hwnd, 0, &position, &size, _memoryDc, &source, 0, &blend, ULW_ALPHA);

        double renderMs = Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
        _frameCount++;
        _renderMsTotal += renderMs;
        _renderMsMax = Math.Max(_renderMsMax, renderMs);
    }

    /// <summary>从本程序的 exe 里取指定逻辑尺寸的图标(任务栏按钮用);exe 不是 VelaShell 自己时不取。</summary>
    private nint ExtractIcon(int logicalSize)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || !string.Equals(Path.GetFileNameWithoutExtension(exe), "VelaShell", StringComparison.OrdinalIgnoreCase))
        {
            // `dotnet VelaShell.dll` 跑起来时 exe 是 dotnet.exe,拿到的会是 .NET 的图标。
            return 0;
        }
        int size = (int)Math.Round(logicalSize * _scale);
        nint icon;
        uint id;
        return PrivateExtractIconsW(exe, 0, size, size, &icon, &id, 1, 0) == 1 ? icon : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            Win32SplashWindow? self = _current;
            if (self is not null && self._hwnd == hwnd)
            {
                switch (message)
                {
                    case WM_TIMER:
                        self.Present();
                        return 0;
                    case WmRequestClose:
                        if (self._closingAt < 0)
                        {
                            self._closingAt = Stopwatch.GetTimestamp();
                        }
                        return 0;
                    case WM_DESTROY:
                        self._destroyed = true;
                        KillTimer(hwnd, TimerId);
                        PostQuitMessage(0);
                        return 0;
                }
            }
        }
        catch (Exception ex)
        {
            // 异常绝不能穿过非托管边界(那会直接终止进程):记一笔、收掉画面。
            // 画不出来的画面留着只会每 15 ms 再失败一次,而启动本身不受它影响。
            Trace.WriteLine($"[Splash] Window procedure failed, closing the splash screen: {ex}");
            DestroyWindow(hwnd);
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
