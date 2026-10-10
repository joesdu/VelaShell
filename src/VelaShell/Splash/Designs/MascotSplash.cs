using SkiaSharp;

namespace VelaShell.Splash.Designs;

/// <summary>
/// 看板娘:抱着笔记本的 Q 版看板娘在左边,右边用她的口吻在气泡里说当前在做什么,
/// 底部五个猫爪印逐个点亮(猫爪是形象设定里的常用点缀)。
/// </summary>
/// <remarks>
/// <para>
/// 插画只用原图(<c>mascot/chibi-laptop.png</c>),不重绘;原图是白底,用正片叠底融进一块浅色底板。
/// 底板必须浅,否则白发和皮肤会被染色 —— 暗色主题下取主题的正文色(本来就是近白)再往白里调,
/// 亮色主题直接用终端底色,两种情况都带一点主题的色调。
/// </para>
/// <para>
/// 这是唯一一套放插画的样式,偏离了 DESIGN.md「界面里不放插画」的约定:启动画面算品牌时刻,
/// 且是用户自己选的,出厂默认仍是经典样式。
/// </para>
/// </remarks>
internal sealed class MascotSplash : SplashDesign
{
    /// <summary>猫爪弹出动画的时长。</summary>
    private const double PawPopMs = 260;

    private const float SideLeft = 286;
    private const float SideRight = 572;
    private const float BubbleTop = 118;
    private const float PawSize = 22;
    private const float PawTop = 288;

    private readonly SKPath _paw = new();
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly double[] _doneAt = new double[SplashFrame.StageCount];
    private int _lastDoneCount;

    /// <summary>构造;猫爪在 24 × 24 的格子里画好,用时再缩放。</summary>
    public MascotSplash(SplashPalette palette, SplashStrings strings, SplashResources resources)
        : base(palette, strings, resources)
    {
        _paw.AddOval(SKRect.Create(12 - 5.6f, 16.2f - 4.6f, 11.2f, 9.2f));
        _paw.AddOval(SKRect.Create(5.4f - 2.2f, 10.4f - 2.7f, 4.4f, 5.4f));
        _paw.AddOval(SKRect.Create(9.4f - 2.3f, 6.4f - 2.8f, 4.6f, 5.6f));
        _paw.AddOval(SKRect.Create(14.6f - 2.3f, 6.4f - 2.8f, 4.6f, 5.6f));
        _paw.AddOval(SKRect.Create(18.6f - 2.2f, 10.4f - 2.7f, 4.4f, 5.4f));
        Array.Fill(_doneAt, double.NaN);
    }

    /// <inheritdoc />
    public override SKColor BorderColor => Palette.BorderPrimary;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SplashFrame frame)
    {
        double t = frame.ElapsedMs;
        FillRect(canvas, 0, 0, SplashRenderer.Width, SplashRenderer.Height, Palette.Page);
        DrawArtPanel(canvas);

        DrawImage(canvas, Resources.Logo, SKRect.Create(SideLeft, 34, 32, 32));
        Resources.UiSemibold.Draw(canvas, "VelaShell", SideLeft + 42, 34, 24, Palette.TextPrimary, lineHeight: 32);
        Resources.Mono.Draw(canvas, Text.Version, SideLeft + 42, 72, 11, Palette.TextTertiary, lineHeight: 16);

        string say = frame.IsDone ? Text.MascotReady : frame.Current >= 0 ? Text.MascotLines[frame.Current] : Text.MascotHello;
        DrawBubble(canvas, say);
        Resources.Ui.Draw(canvas, Text.Status(frame).TrimEnd('…'), SideLeft, 174, 12, Palette.TextTertiary, lineHeight: 18);

        DrawPaws(canvas, frame, t);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _paw.Dispose();
        _stroke.Dispose();
        base.Dispose();
    }

    private void DrawArtPanel(SKCanvas canvas)
    {
        var panel = SKRect.Create(16, 16, 240, 308);
        SKColor tone = Palette.IsDark ? Mix(Palette.TextPrimary, SKColors.White, 0.75) : Palette.Terminal;
        Fill.Color = tone;
        canvas.DrawRoundRect(panel, 10, 10, Fill);
        SKImage? art = Resources.Mascot;
        if (art is null)
        {
            return;
        }
        canvas.Save();
        using (var clip = new SKRoundRect(panel, 10, 10))
        {
            canvas.ClipRoundRect(clip, SKClipOperation.Intersect, antialias: true);
            // 宽 252、左移 2:左右各裁掉一点 —— 原图是从三连图里拆出来的,两侧边缘残着邻图的尾巴和星芒。
            float height = 252f * art.Height / art.Width;
            DrawImage(canvas, art, SKRect.Create(14, panel.Bottom - height, 252, height), SKBlendMode.Multiply);
        }
        canvas.Restore();
    }

    private void DrawBubble(SKCanvas canvas, string say)
    {
        float maxText = SideRight - SideLeft - 34;
        float size = 14;
        while (size > 11 && Resources.Ui.Measure(say, size) > maxText)
        {
            size -= 0.5f;
        }
        float width = Math.Min(Resources.Ui.Measure(say, size), maxText) + 32;
        var bubble = SKRect.Create(SideLeft, BubbleTop, width, 46);
        Fill.Color = Palette.Surface;
        canvas.DrawRoundRect(bubble, 12, 12, Fill);
        _stroke.Color = Palette.BorderSecondary;
        canvas.DrawRoundRect(SKRect.Inflate(bubble, -0.5f, -0.5f), 11.5f, 11.5f, _stroke);

        // 小尾巴指向左边的看板娘:先用底色盖住那一截描边,再描两条外边。
        using var tail = new SKPath();
        tail.MoveTo(SideLeft + 1.5f, BubbleTop + 9.5f);
        tail.LineTo(SideLeft - 6.5f, BubbleTop + 17);
        tail.LineTo(SideLeft + 1.5f, BubbleTop + 24.5f);
        tail.Close();
        canvas.DrawPath(tail, Fill);
        using var edge = new SKPath();
        edge.MoveTo(SideLeft + 0.5f, BubbleTop + 9.5f);
        edge.LineTo(SideLeft - 6.5f, BubbleTop + 17);
        edge.LineTo(SideLeft + 0.5f, BubbleTop + 24.5f);
        canvas.DrawPath(edge, _stroke);

        Resources.Ui.Draw(canvas, say, SideLeft + 16, BubbleTop + 12, size, Palette.TextPrimary, lineHeight: 22);
    }

    private void DrawPaws(SKCanvas canvas, SplashFrame frame, double t)
    {
        if (frame.DoneCount < _lastDoneCount)
        {
            // 预览循环重播了:弹出动画的起点作废。
            Array.Fill(_doneAt, double.NaN);
        }
        _lastDoneCount = frame.DoneCount;

        for (int i = 0; i < frame.Stages.Length; i++)
        {
            SplashStage stage = frame.Stages[i];
            float scale = 1;
            float rotation = 0;
            SKColor color;
            switch (stage.State)
            {
                case SplashStageState.Done:
                    if (double.IsNaN(_doneAt[i]))
                    {
                        // 画面出现前就已完成的阶段(运行时)不弹,直接亮着。
                        _doneAt[i] = t < 50 ? t - PawPopMs : t;
                    }
                    // 「按」一下:原地放大到 1.3 倍、歪一下头再落回去。不从无到有地弹出来 ——
                    // 那样它在「进行中」时还亮着,一完成反倒先消失一下,看着像闪了一帧。
                    double bump = Math.Sin(Math.PI * Math.Clamp((t - _doneAt[i]) / PawPopMs, 0, 1));
                    scale = (float)(1 + 0.3 * bump);
                    rotation = (float)(-14 * bump);
                    color = Palette.Accent;
                    break;
                case SplashStageState.Running:
                    double wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (t % 900) / 900);
                    color = WithOpacity(Palette.Accent, 0.35 + 0.65 * wave);
                    break;
                default:
                    color = Palette.BorderSecondary;
                    break;
            }
            float left = SideLeft + i * (PawSize + 12);
            canvas.Save();
            canvas.Translate(left + PawSize / 2, PawTop + PawSize / 2);
            canvas.RotateDegrees(rotation);
            canvas.Scale(scale * PawSize / 24);
            canvas.Translate(-12, -12);
            Fill.Color = color;
            canvas.DrawPath(_paw, Fill);
            canvas.Restore();
        }
    }
}
