using SkiaSharp;

namespace VelaShell.Splash.Designs;

/// <summary>
/// 提示符:一开始只有 Logo 里的 <c>&gt;_</c>,接着光标往右走、把 <c>VelaShell</c> 敲出来 —— Logo 本身就是动画。
/// 下方一行状态加五格方块进度,正在进行的那格呼吸闪烁。
/// </summary>
internal sealed class PromptSplash(SplashPalette palette, SplashStrings strings, SplashResources resources)
    : SplashDesign(palette, strings, resources)
{
    private const string Name = "VelaShell";

    /// <summary>开始敲名字的时刻(先让 <c>&gt;_</c> 单独亮一下,认出这是 Logo)。</summary>
    private const double TypeStartMs = 450;

    /// <summary>每敲一个字符的时间。</summary>
    private const double TypeMs = 85;

    private const float GlyphSize = 46;
    private const float LineTop = 120;
    private const float RowTop = 200;
    private const float Cell = 8;
    private const float CellGap = 4;

    /// <inheritdoc />
    public override SKColor BorderColor => Palette.BorderSecondary;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SplashFrame frame)
    {
        double t = frame.ElapsedMs;
        FillRect(canvas, 0, 0, SplashRenderer.Width, SplashRenderer.Height, Palette.Terminal);

        // 行宽按 12 个字符定死再居中:名字一个字一个字长出来时,左边的 > 不跟着挪。
        float ch = Resources.MonoBold.Measure("0", GlyphSize);
        float x = (SplashRenderer.Width - 12 * ch) / 2 - 0.5f * ch;
        x += Resources.MonoBold.Draw(canvas, "> ", x, LineTop, GlyphSize, Palette.Accent, lineHeight: GlyphSize);
        int typed = (int)Math.Clamp(Math.Floor((t - TypeStartMs) / TypeMs), 0, Name.Length);
        x += Resources.MonoSemibold.Draw(canvas, Name[..typed], x, LineTop, GlyphSize, Palette.TextPrimary, lineHeight: GlyphSize);
        bool typing = typed is > 0 and < 9;
        if (typing || BlinkOn(t))
        {
            Resources.MonoBold.Draw(canvas, "_", x, LineTop, GlyphSize, Palette.Accent, lineHeight: GlyphSize);
        }

        string status = Text.Status(frame);
        float statusWidth = Resources.Ui.Measure(status, 13);
        float cellsWidth = SplashFrame.StageCount * Cell + (SplashFrame.StageCount - 1) * CellGap;
        float left = (SplashRenderer.Width - (statusWidth + 14 + cellsWidth)) / 2;
        Resources.Ui.Draw(canvas, status, left, RowTop, 13, frame.IsDone ? Palette.Accent : Palette.TextSecondary, lineHeight: 20);
        float cellLeft = left + statusWidth + 14;
        for (int i = 0; i < frame.Stages.Length; i++)
        {
            SplashStage stage = frame.Stages[i];
            SKColor color = stage.State switch
            {
                SplashStageState.Waiting => Palette.BorderSecondary,
                SplashStageState.Running => WithOpacity(Palette.Accent, 0.45 + 0.55 * Math.Abs(Math.Sin(t / 260))),
                _ => Palette.Accent
            };
            Fill.Color = color;
            canvas.DrawRoundRect(SKRect.Create(cellLeft + i * (Cell + CellGap), RowTop + 6, Cell, Cell), 1, 1, Fill);
        }

        Resources.Mono.Draw(canvas, Text.Version, SplashRenderer.Width - 18, SplashRenderer.Height - 30, 11, Palette.TextMuted, SKTextAlign.Right, 16);
    }
}
