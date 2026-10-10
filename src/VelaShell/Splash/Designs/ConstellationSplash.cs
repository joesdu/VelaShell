using SkiaSharp;

namespace VelaShell.Splash.Designs;

/// <summary>
/// 船帆座:Vela 是南天的船帆座。背景是缓慢闪烁的星点,启动每推进一段就点亮一颗主星、连上一段星线,
/// 主窗口出来时星座刚好连成闭环;星线同时也像一条条 SSH 连接。亮起的星旁标着拜耳命名。
/// </summary>
/// <remarks>星图是示意的:七颗主星的相对位置按星座的大致轮廓摆,不按真实赤经赤纬投影。</remarks>
internal sealed class ConstellationSplash : SplashDesign
{
    /// <summary>七颗主星:位置(逻辑坐标)、拜耳命名、标注相对星点的偏移。</summary>
    private static readonly (float X, float Y, string Name, float Dx, float Dy)[] Stars =
    [
        (318, 214, "γ Vel", -40, 8),
        (388, 284, "δ Vel", -42, 4),
        (494, 278, "κ Vel", 10, 8),
        (556, 206, "φ Vel", -12, 12),
        (530, 118, "μ Vel", 10, -4),
        (436, 58, "λ Vel", 10, -12),
        (372, 112, "ψ Vel", -44, -6)
    ];

    private readonly (float X, float Y, float Size, double Period, double Phase)[] _dust;
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _glow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) };

    /// <summary>构造;背景星点用固定种子生成,每次启动位置都一样。</summary>
    public ConstellationSplash(SplashPalette palette, SplashStrings strings, SplashResources resources)
        : base(palette, strings, resources)
    {
        int seed = 7;
        double Next()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0;
        }
        _dust = new (float, float, float, double, double)[52];
        for (int i = 0; i < _dust.Length; i++)
        {
            float x = (float)Math.Round(Next() * 590 + 4);
            float y = (float)Math.Round(Next() * 330 + 4);
            float size = Next() > 0.82 ? 2 : 1;
            double period = (2.4 + Next() * 3) * 1000;
            double phase = Next() * 4000;
            _dust[i] = (x, y, size, period, phase);
        }
    }

    /// <inheritdoc />
    public override SKColor BorderColor => Palette.BorderPrimary;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SplashFrame frame)
    {
        double t = frame.ElapsedMs;
        FillRect(canvas, 0, 0, SplashRenderer.Width, SplashRenderer.Height, Palette.Page);

        // 背景星点:0.12 ↔ 0.75 之间缓慢呼吸,各自的周期与相位都不同。
        foreach ((float x, float y, float size, double period, double phase) in _dust)
        {
            double wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * ((t + phase) % period) / period);
            Fill.Color = WithOpacity(Palette.TextTertiary, 0.12 + 0.63 * wave);
            canvas.DrawCircle(x + size / 2, y + size / 2, size / 2, Fill);
        }

        int n = Stars.Length;
        double progress = frame.IsDone ? 1 : frame.Progress;

        // 星线:第 k 段在总进度走过 k/n 之后开始画,走到 (k+1)/n 时连上下一颗星。
        _line.Color = WithOpacity(Palette.Accent, 0.55);
        for (int k = 0; k < n; k++)
        {
            double part = Math.Clamp((progress - (double)k / n) * n, 0, 1);
            if (part <= 0)
            {
                continue;
            }
            var from = Stars[k];
            var to = Stars[(k + 1) % n];
            canvas.DrawLine(from.X, from.Y,
                            (float)(from.X + (to.X - from.X) * part),
                            (float)(from.Y + (to.Y - from.Y) * part), _line);
        }

        // 主星:进度越过它的门槛后在几十毫秒内亮起(半径、颜色、光晕一起过渡),亮了再标名字。
        for (int k = 0; k < n; k++)
        {
            var star = Stars[k];
            double lit = frame.IsDone ? 1 : Math.Clamp((progress - (double)k / n) * n * 4 + (k == 0 ? 1 : 0), 0, 1);
            if (lit > 0)
            {
                _glow.Color = WithOpacity(Palette.Accent, 0.55 * lit);
                canvas.DrawCircle(star.X, star.Y, 5, _glow);
                Fill.Color = WithOpacity(Palette.Accent, (frame.IsDone ? 0.3 : 0.18) * lit);
                canvas.DrawCircle(star.X, star.Y, 7.5f, Fill);
            }
            Fill.Color = Mix(Palette.TextMuted, Palette.Accent, lit);
            canvas.DrawCircle(star.X, star.Y, (float)(2 + 1.5 * lit), Fill);
            if (lit > 0)
            {
                Resources.Mono.Draw(canvas, star.Name, star.X + star.Dx, star.Y + star.Dy, 10,
                                    WithOpacity(Palette.TextTertiary, 0.9 * lit), lineHeight: 13);
            }
        }

        DrawImage(canvas, Resources.Logo, SKRect.Create(36, 40, 40, 40));
        Resources.UiSemibold.Draw(canvas, "VelaShell", 88, 40, 28, Palette.TextPrimary, lineHeight: 40);
        Resources.Mono.Draw(canvas, Text.Version + " · Vela " + Text.ConstellationName, 36, 90, 11, Palette.TextTertiary, lineHeight: 16);

        Resources.Ui.Draw(canvas, Text.Status(frame), 36, 268, 13, frame.IsDone ? Palette.Accent : Palette.TextSecondary, lineHeight: 18);
        int step = frame.IsDone ? SplashFrame.StageCount : Math.Max(1, frame.Current + 1);
        Resources.Mono.Draw(canvas, $"{step} / {SplashFrame.StageCount}", 36, 291, 11, Palette.TextTertiary, lineHeight: 16);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _line.Dispose();
        _glow.Dispose();
        base.Dispose();
    }
}
