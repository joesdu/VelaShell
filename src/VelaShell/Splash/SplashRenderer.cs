using SkiaSharp;
using VelaShell.Core.Models;
using VelaShell.Splash.Designs;

namespace VelaShell.Splash;

/// <summary>
/// 把一套启动画面样式画到 Skia 画布上:裁出圆角卡片、交给设计稿画内容、最后描边。
/// </summary>
/// <remarks>
/// 画布坐标一律是 600 × 340 的逻辑单位(DIP),缩放由调用方定:启动画面窗口按显示器 DPI 放大,
/// 设置页预览按控件大小缩小。同一份绘制代码两处共用,预览看到的就是启动时看到的。
/// </remarks>
internal sealed class SplashRenderer : IDisposable
{
    /// <summary>卡片宽度(逻辑单位)。</summary>
    public const float Width = 600;

    /// <summary>卡片高度(逻辑单位)。</summary>
    public const float Height = 340;

    /// <summary>卡片圆角(DESIGN.md §4.4 的大浮层取 8)。</summary>
    public const float CornerRadius = 8;

    private readonly SplashResources _resources;
    private readonly SplashDesign _design;

    private SplashRenderer(string style, SplashPalette palette, SplashStrings strings, SplashResources resources)
    {
        _resources = resources;
        Style = style;
        Palette = palette;
        _design = style switch
        {
            SplashStyles.Terminal => new TerminalSplash(palette, strings, resources),
            SplashStyles.Constellation => new ConstellationSplash(palette, strings, resources),
            SplashStyles.Prompt => new PromptSplash(palette, strings, resources),
            SplashStyles.Mascot => new MascotSplash(palette, strings, resources),
            _ => new ClassicSplash(palette, strings, resources)
        };
    }

    /// <summary>实际使用的样式(<c>none</c> 与认不出来的值按经典样式画)。</summary>
    public string Style { get; }

    /// <summary>配色。</summary>
    public SplashPalette Palette { get; }

    /// <summary>创建一个渲染器。</summary>
    /// <param name="style">样式(<see cref="SplashStyles" />)。</param>
    /// <param name="palette">配色。</param>
    /// <param name="strings">文案。</param>
    /// <param name="resources">字体与图片;所有权移交给渲染器。</param>
    /// <returns>渲染器,调用方负责释放。</returns>
    public static SplashRenderer Create(string style, SplashPalette palette, SplashStrings strings, SplashResources resources) =>
        new(SplashStyles.Normalize(style), palette, strings, resources);

    /// <summary>在 (0,0)–(600,340) 画出整张卡片。</summary>
    /// <param name="canvas">画布(调用方已设好缩放与平移)。</param>
    /// <param name="frame">这一帧的状态。</param>
    public void Render(SKCanvas canvas, SplashFrame frame)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        var card = new SKRect(0, 0, Width, Height);
        canvas.Save();
        using (var clip = new SKRoundRect(card, CornerRadius, CornerRadius))
        {
            canvas.ClipRoundRect(clip, SKClipOperation.Intersect, antialias: true);
            _design.Render(canvas, frame);
        }
        canvas.Restore();
        // 描边画在裁剪之外:半像素内缩的 1 px 线整条都在,圆角处也不会被裁成虚线。
        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = _design.BorderColor };
        canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, Width - 0.5f, Height - 0.5f), CornerRadius - 0.5f, CornerRadius - 0.5f, border);
    }

    /// <summary>
    /// 卡片投影(DESIGN.md §4.5 的窗口阴影口径:一层大而淡的环境影 + 一层贴边的接触影)。
    /// 只有原生启动画面窗口要画 —— 它是分层窗口,系统不给它阴影。
    /// </summary>
    /// <param name="canvas">画布(与 <see cref="Render" /> 同一坐标系)。</param>
    /// <param name="isDark">暗色主题下影子更重,亮色主题压淡一些。</param>
    public static void DrawShadow(SKCanvas canvas, bool isDark)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = SKColors.Black.WithAlpha((byte)(isDark ? 110 : 70));
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12);
        canvas.DrawRoundRect(new SKRect(0, 8, Width, Height + 8), CornerRadius, CornerRadius, paint);
        paint.Color = SKColors.Black.WithAlpha((byte)(isDark ? 60 : 40));
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2);
        canvas.DrawRoundRect(new SKRect(0, 1, Width, Height + 1), CornerRadius, CornerRadius, paint);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _design.Dispose();
        _resources.Dispose();
    }
}

/// <summary>
/// 一套启动画面样式的画法。坐标为 600 × 340 的逻辑单位,背景要自己铺满,描边色交给
/// <see cref="BorderColor" />,由 <see cref="SplashRenderer" /> 统一描。
/// </summary>
internal abstract class SplashDesign : IDisposable
{
    private readonly Dictionary<(SKImage Image, int Width, int Height), SKImage> _scaled = [];

    /// <summary>构造。</summary>
    protected SplashDesign(SplashPalette palette, SplashStrings strings, SplashResources resources)
    {
        Palette = palette;
        Text = strings;
        Resources = resources;
    }

    /// <summary>配色。</summary>
    protected SplashPalette Palette { get; }

    /// <summary>文案。</summary>
    protected SplashStrings Text { get; }

    /// <summary>字体与图片。</summary>
    protected SplashResources Resources { get; }

    /// <summary>通用填充画笔(抗锯齿)。用前设好颜色;改了别的属性要自己复原。</summary>
    protected SKPaint Fill { get; } = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    /// <summary>卡片描边色。</summary>
    public abstract SKColor BorderColor { get; }

    /// <summary>画一帧。</summary>
    /// <param name="canvas">画布(已裁成圆角卡片)。</param>
    /// <param name="frame">这一帧的状态。</param>
    public abstract void Render(SKCanvas canvas, SplashFrame frame);

    /// <summary>填一个纯色矩形。</summary>
    protected void FillRect(SKCanvas canvas, float left, float top, float width, float height, SKColor color)
    {
        Fill.Color = color;
        canvas.DrawRect(left, top, width, height, Fill);
    }

    /// <summary>
    /// 把图片画进目标矩形。缩小时先按画布当前的像素缩放预先缩好一份(逐级减半再三次插值)并缓存 ——
    /// 直接把 1024² 的 Logo 每帧缩到 64 px,既慢又会出锯齿。
    /// </summary>
    protected void DrawImage(SKCanvas canvas, SKImage? image, SKRect dest, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        if (image is null)
        {
            return;
        }
        float scale = Math.Max(0.01f, canvas.TotalMatrix.ScaleX);
        int width = Math.Max(1, (int)Math.Round(dest.Width * scale));
        int height = Math.Max(1, (int)Math.Round(dest.Height * scale));
        if (!_scaled.TryGetValue((image, width, height), out SKImage? scaled))
        {
            if (_scaled.Count >= 16)
            {
                // 预览控件被拖着改大小时每个尺寸都会缓存一份,封个顶。
                foreach (SKImage stale in _scaled.Values)
                {
                    stale.Dispose();
                }
                _scaled.Clear();
            }
            scaled = Downscale(image, width, height);
            _scaled[(image, width, height)] = scaled;
        }
        using var paint = new SKPaint { IsAntialias = true, BlendMode = blend };
        canvas.DrawImage(scaled, dest, new SKSamplingOptions(SKFilterMode.Linear), paint);
    }

    /// <summary>按 0–1 的不透明度调一个颜色的 alpha。</summary>
    protected static SKColor WithOpacity(SKColor color, double opacity) =>
        color.WithAlpha((byte)Math.Clamp(Math.Round(color.Alpha * opacity), 0, 255));

    /// <summary>两色线性插值(含 alpha)。</summary>
    protected static SKColor Mix(SKColor from, SKColor to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new SKColor(
            (byte)Math.Round(from.Red + (to.Red - from.Red) * t),
            (byte)Math.Round(from.Green + (to.Green - from.Green) * t),
            (byte)Math.Round(from.Blue + (to.Blue - from.Blue) * t),
            (byte)Math.Round(from.Alpha + (to.Alpha - from.Alpha) * t));
    }

    /// <summary>方波闪烁(光标):周期 <paramref name="periodMs" />,前半亮后半灭。</summary>
    protected static bool BlinkOn(double elapsedMs, double periodMs = 1060) => elapsedMs % periodMs < periodMs / 2;

    /// <inheritdoc />
    public virtual void Dispose()
    {
        foreach (SKImage image in _scaled.Values)
        {
            image.Dispose();
        }
        _scaled.Clear();
        Fill.Dispose();
    }

    private static SKImage Downscale(SKImage source, int width, int height)
    {
        SKImage current = source;
        bool owned = false;
        // 逐级减半:三次插值只看 4×4 邻域,一步缩十几倍会丢细节、出锯齿。
        while (current.Width / 2 >= width && current.Height / 2 >= height)
        {
            SKImage half = Resize(current, current.Width / 2, current.Height / 2, new SKSamplingOptions(SKFilterMode.Linear));
            if (owned)
            {
                current.Dispose();
            }
            current = half;
            owned = true;
        }
        SKImage result = Resize(current, width, height, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (owned)
        {
            current.Dispose();
        }
        return result;
    }

    private static SKImage Resize(SKImage source, int width, int height, SKSamplingOptions sampling)
    {
        var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (SKPixmap pixmap = bitmap.PeekPixels())
        {
            source.ScalePixels(pixmap, sampling);
        }
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }
}
