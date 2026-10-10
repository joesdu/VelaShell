using SkiaSharp;

namespace VelaShell.Splash.Designs;

/// <summary>
/// 经典(出厂默认):最接近 Visual Studio 的布局 —— 左上大 Logo 与产品名,左下一行当前状态,
/// 底部一排强调色小圆点从左滑到右。不展示阶段细节。
/// </summary>
internal sealed class ClassicSplash(SplashPalette palette, SplashStrings strings, SplashResources resources)
    : SplashDesign(palette, strings, resources)
{
    /// <summary>一轮滑动的时长。</summary>
    private const double DotCycleMs = 2600;

    /// <summary>相邻两个圆点的错开时间。</summary>
    private const double DotStaggerMs = 130;

    /// <inheritdoc />
    public override SKColor BorderColor => Palette.BorderPrimary;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SplashFrame frame)
    {
        FillRect(canvas, 0, 0, SplashRenderer.Width, SplashRenderer.Height, Palette.Page);

        DrawImage(canvas, Resources.Logo, SKRect.Create(44, 50, 64, 64));
        Resources.UiSemibold.Draw(canvas, "VelaShell", 128, 48, 36, Palette.TextPrimary, lineHeight: 40);
        Resources.Ui.Draw(canvas, Text.Tagline, 128, 94, 14, Palette.TextSecondary, lineHeight: 20);

        Resources.Mono.Draw(canvas, Text.Version, SplashRenderer.Width - 24, 18, 11, Palette.TextTertiary, SKTextAlign.Right);
        Resources.Ui.Draw(canvas, Text.Status(frame), 44, 268, 12.5f, Palette.TextTertiary, lineHeight: 18);
        if (Text.Copyright.Length > 0)
        {
            Resources.Ui.Draw(canvas, Text.Copyright, SplashRenderer.Width - 24, 269, 11, Palette.TextMuted, SKTextAlign.Right, 16);
        }

        // VS 招牌的等待动画:五个点依次出发,中段放慢、两头加速,像一列被拉开又收拢的小队。
        for (int i = 0; i < 5; i++)
        {
            double phase = ((frame.ElapsedMs - i * DotStaggerMs) % DotCycleMs + DotCycleMs) % DotCycleMs / DotCycleMs;
            (double position, double opacity) = DotAt(phase);
            Fill.Color = WithOpacity(Palette.Accent, opacity);
            canvas.DrawCircle((float)(position * SplashRenderer.Width) + 2, 308, 2, Fill);
        }
    }

    /// <summary>
    /// 圆点在一轮里的位置(占卡片宽度的比例)与不透明度:
    /// 0→36% 从 -2% 走到 45%,36→64% 只走到 55%,64→100% 冲到 102%;头尾 8% 淡入淡出。
    /// </summary>
    internal static (double Position, double Opacity) DotAt(double phase)
    {
        double position = phase switch
        {
            < 0.36 => Lerp(-0.02, 0.45, phase / 0.36),
            < 0.64 => Lerp(0.45, 0.55, (phase - 0.36) / 0.28),
            _ => Lerp(0.55, 1.02, (phase - 0.64) / 0.36)
        };
        double opacity = phase switch
        {
            < 0.08 => phase / 0.08,
            > 0.92 => (1 - phase) / 0.08,
            _ => 1
        };
        return (position, opacity);
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
