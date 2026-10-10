using System.Globalization;
using SkiaSharp;

namespace VelaShell.Splash.Designs;

/// <summary>
/// 终端开机:启动画面本身就是一个迷你终端。提示符敲下 <c>velashell</c>,接着逐行打出真实的启动阶段与耗时,
/// 底边一条细线是总进度。万一卡住,用户能看出停在哪一步,反馈问题截个图就够。
/// </summary>
/// <remarks>第二行是 SSH 协议的版本标识串(RFC 4253 §4.2 的格式),给懂行的人的一个小彩蛋。</remarks>
internal sealed class TerminalSplash(SplashPalette palette, SplashStrings strings, SplashResources resources)
    : SplashDesign(palette, strings, resources)
{
    private const string Command = "velashell";

    /// <summary>每敲一个字符的时间。</summary>
    private const double TypeMs = 45;

    /// <summary>开场(敲命令 + 打版本串)的时长;阶段行在这之后才出现。</summary>
    private const double IntroMs = 500;

    private const float FontSize = 13.5f;
    private const float LineHeight = 24;
    private const float Left = 25;
    private const float Top = 48;

    /// <summary>盲文点阵转圈符号,每 80 ms 换一格。</summary>
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    /// <inheritdoc />
    public override SKColor BorderColor => Palette.BorderSecondary;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SplashFrame frame)
    {
        double t = frame.ElapsedMs;
        SplashFace mono = Resources.Mono;
        FillRect(canvas, 0, 0, SplashRenderer.Width, SplashRenderer.Height, Palette.Terminal);

        // 标题栏:与应用自己的 28–30 px 标题栏同一个调子。
        FillRect(canvas, 0, 0, SplashRenderer.Width, 30, Palette.Page);
        FillRect(canvas, 0, 29, SplashRenderer.Width, 1, Palette.BorderPrimary);
        DrawImage(canvas, Resources.Logo, SKRect.Create(15, 7, 16, 16));
        Resources.UiMedium.Draw(canvas, "VelaShell", 39, 0, 12, Palette.TextSecondary, lineHeight: 30);
        mono.Draw(canvas, Text.Version, SplashRenderer.Width - 15, 0, 11, Palette.TextTertiary, SKTextAlign.Right, 30);

        // 提示符与命令。
        float x = Left;
        x += mono.Draw(canvas, "vela", x, Top, FontSize, Palette.Success, lineHeight: LineHeight);
        x += mono.Draw(canvas, "@", x, Top, FontSize, Palette.TextTertiary, lineHeight: LineHeight);
        x += mono.Draw(canvas, "local", x, Top, FontSize, Palette.Info, lineHeight: LineHeight);
        x += mono.Draw(canvas, " ", x, Top, FontSize, Palette.TextPrimary, lineHeight: LineHeight);
        x += mono.Draw(canvas, "~", x, Top, FontSize, Palette.Accent, lineHeight: LineHeight);
        x += mono.Draw(canvas, " $ ", x, Top, FontSize, Palette.TextTertiary, lineHeight: LineHeight);
        int typed = (int)Math.Clamp(Math.Floor(t / TypeMs), 0, Command.Length);
        x += mono.Draw(canvas, Command[..typed], x, Top, FontSize, Palette.TextPrimary, lineHeight: LineHeight);
        if (t < IntroMs)
        {
            mono.Draw(canvas, "▌", x, Top, FontSize, Palette.Accent, lineHeight: LineHeight);
        }

        float y = Top + LineHeight;
        if (t >= IntroMs - 80)
        {
            mono.Draw(canvas, "SSH-2.0-VelaShell_" + Text.Version.TrimStart('v'), Left, y, FontSize, Palette.TextMuted, lineHeight: LineHeight);
        }
        y += LineHeight;
        if (t < IntroMs)
        {
            DrawProgress(canvas, frame.Progress);
            return;
        }

        float ch = mono.Measure("0", FontSize);
        for (int i = 0; i < frame.Stages.Length; i++)
        {
            SplashStage stage = frame.Stages[i];
            if (stage.State == SplashStageState.Waiting)
            {
                continue;
            }
            bool done = stage.State == SplashStageState.Done;
            string status = done ? "ok" : Spinner[(int)(t / 80) % Spinner.Length];
            string time = done
                ? stage.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + "s"
                : stage.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            mono.Draw(canvas, "[", Left, y, FontSize, Palette.TextTertiary, lineHeight: LineHeight);
            mono.Draw(canvas, status, Left + 3 * ch, y, FontSize, done ? Palette.Success : Palette.Accent, SKTextAlign.Center, LineHeight);
            mono.Draw(canvas, "]", Left + 5 * ch, y, FontSize, Palette.TextTertiary, lineHeight: LineHeight);
            mono.Draw(canvas, Text.Stages[i], Left + 6 * ch + 12, y, FontSize, done ? Palette.TextSecondary : Palette.TextPrimary, lineHeight: LineHeight);
            mono.Draw(canvas, time, SplashRenderer.Width - Left, y, FontSize, Palette.TextTertiary, SKTextAlign.Right, LineHeight);
            y += LineHeight;
        }

        if (frame.IsDone)
        {
            float w = mono.Draw(canvas, "✓ " + Text.Ready + " ", Left, y, FontSize, Palette.Success, lineHeight: LineHeight);
            if (BlinkOn(t))
            {
                mono.Draw(canvas, "▌", Left + w, y, FontSize, Palette.Accent, lineHeight: LineHeight);
            }
        }

        DrawProgress(canvas, frame.Progress);
    }

    /// <summary>底边 2 px 的总进度线。</summary>
    private void DrawProgress(SKCanvas canvas, double progress)
    {
        FillRect(canvas, 0, SplashRenderer.Height - 3, SplashRenderer.Width, 2, Palette.BorderPrimary);
        FillRect(canvas, 0, SplashRenderer.Height - 3, (float)(SplashRenderer.Width * progress), 2, Palette.Accent);
    }
}
