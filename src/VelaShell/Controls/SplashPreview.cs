using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using SkiaSharp;
using VelaShell.Splash;

namespace VelaShell.Controls;

/// <summary>
/// 设置页里的启动画面实时预览:按「一般」速度循环播放一次模拟启动。
/// </summary>
/// <remarks>
/// <para>
/// 画法与真正的启动画面是同一份代码(<see cref="SplashRenderer" />),只是画布换成了 Avalonia 的
/// Skia 画布、时间线换成了 <see cref="SplashSimulation" /> —— 预览里看到的就是下次启动时看到的。
/// 主题与强调色取设置页里正在编辑的值(还没保存也算),界面语言取当前语言。
/// </para>
/// <para>
/// 自定义绘制在渲染线程上执行,渲染器又会在 UI 线程上随样式/主题切换而重建:两边经同一把锁交接,
/// 已释放的渲染器不会再被画。不可见(切到别的设置页、窗口关了)时不再请求动画帧。
/// </para>
/// </remarks>
public sealed class SplashPreview : Control
{
    /// <summary>启动画面样式(<c>Core.Models.SplashStyles</c>)。</summary>
    public static readonly StyledProperty<string?> SplashStyleProperty =
        AvaloniaProperty.Register<SplashPreview, string?>(nameof(SplashStyle));

    /// <summary>主题 Id(含 <c>system</c>)。</summary>
    public static readonly StyledProperty<string?> ThemeIdProperty =
        AvaloniaProperty.Register<SplashPreview, string?>(nameof(ThemeId));

    /// <summary>强调色覆盖;空 = 跟随主题。</summary>
    public static readonly StyledProperty<string?> AccentColorProperty =
        AvaloniaProperty.Register<SplashPreview, string?>(nameof(AccentColor));

    private readonly Lock _gate = new();
    private readonly SplashSimulation _simulation = new();
    private SplashRenderer? _renderer;
    private long _startedAt = Stopwatch.GetTimestamp();
    private bool _animating;

    static SplashPreview()
    {
        ClipToBoundsProperty.OverrideDefaultValue<SplashPreview>(true);
        // 高度由宽度按比例算出(见 MeasureOverride);默认的 Stretch 会被父容器拉高,比例就不对了。
        VerticalAlignmentProperty.OverrideDefaultValue<SplashPreview>(Avalonia.Layout.VerticalAlignment.Top);
    }

    /// <summary>启动画面样式。</summary>
    public string? SplashStyle
    {
        get => GetValue(SplashStyleProperty);
        set => SetValue(SplashStyleProperty, value);
    }

    /// <summary>主题 Id。</summary>
    public string? ThemeId
    {
        get => GetValue(ThemeIdProperty);
        set => SetValue(ThemeIdProperty, value);
    }

    /// <summary>强调色覆盖。</summary>
    public string? AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        // 固定 600:340 的比例;宽度不受限时按原尺寸。
        double width = double.IsInfinity(availableSize.Width) ? SplashRenderer.Width : Math.Min(availableSize.Width, SplashRenderer.Width);
        return new Size(width, width * SplashRenderer.Height / SplashRenderer.Width);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        double t = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds % SplashSimulation.CycleMs;
        context.Custom(new DrawOperation(this, new Rect(Bounds.Size), _simulation.At(t)));
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SplashStyleProperty
            || change.Property == ThemeIdProperty
            || change.Property == AccentColorProperty)
        {
            Rebuild(restart: change.Property == SplashStyleProperty);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Rebuild(restart: true);
        _animating = true;
        RequestNextFrame();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _animating = false;
        lock (_gate)
        {
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    /// <summary>「跟随系统」时系统明暗翻转,预览跟着换配色。</summary>
    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => Rebuild(restart: false);

    private void Rebuild(bool restart)
    {
        if (VisualRoot is null)
        {
            return;
        }
        SplashRenderer? next = null;
        try
        {
            SplashPalette palette = SplashPalette.Resolve(ThemeId, AccentColor, ActualThemeVariant != ThemeVariant.Light);
            next = SplashRenderer.Create(SplashStyle ?? "", palette, SplashStrings.Load(), SplashResources.Create(CultureInfo.CurrentUICulture));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Splash] Preview renderer failed: {ex}");
        }
        lock (_gate)
        {
            _renderer?.Dispose();
            _renderer = next;
        }
        if (restart)
        {
            _startedAt = Stopwatch.GetTimestamp();
        }
        InvalidateVisual();
    }

    private void RequestNextFrame()
    {
        if (!_animating || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        topLevel.RequestAnimationFrame(_ =>
        {
            if (!_animating)
            {
                return;
            }
            if (IsEffectivelyVisible)
            {
                InvalidateVisual();
            }
            RequestNextFrame();
        });
    }

    /// <summary>渲染线程上的那一次绘制。</summary>
    private sealed class DrawOperation(SplashPreview owner, Rect bounds, SplashFrame frame) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }
            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            lock (owner._gate)
            {
                if (owner._renderer is not { } renderer)
                {
                    return;
                }
                canvas.Save();
                float scale = (float)(bounds.Width / SplashRenderer.Width);
                canvas.Scale(scale);
                renderer.Render(canvas, frame);
                canvas.Restore();
            }
        }

        public void Dispose()
        {
        }
    }
}
