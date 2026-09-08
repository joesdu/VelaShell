using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 选区高亮的对比度整定:把「配色方案给的选区色」换算成**真正看得见**的填充色。
/// <para>
/// 起因是选区色此前按半透明叠加绘制(暗色 35%、亮色 25%)。可各家方案(Dracula #44475A、
/// Solarized base2 #EEE8D5、GitHub Light #CFE4FB……)的选区色本来就是照「不透明填充」设计的
/// —— 它们与自家背景的差本就只有一线,再乘上 0.25~0.35 的不透明度,实测对比度只剩
/// 1.05:1 ~ 1.21:1,肉眼等同于没画。用户拖完一段以为压根没选中,正是这么来的。
/// </para>
/// <para>
/// 所以这里做两件事:选区底<b>不透明</b>绘制;并且保证它与终端底之间至少拉开
/// <see cref="MinLightnessDelta" /> 的感知明度差 —— 不够就把它往远离背景的方向推
/// (暗底推向白、亮底推向黑),够了就原样用方案自己的色,不夺方案的设计。
/// </para>
/// <para>
/// 判据用 CIE L*(感知明度)而非 WCAG 对比度:后者在暗端被压缩得厉害,同一个阈值在暗色主题上
/// 只挪一点点、在亮色主题上却会把选区压成一条灰带,明暗两套没法用同一个数说话。
/// </para>
/// </summary>
internal static class SelectionContrast
{
    /// <summary>
    /// 选区底与终端底之间必须拉开的感知明度差(CIE L*,0–100 标度)。
    /// 14 是照着公认「看得见但不喧宾夺主」的那档取的:VS Code 的选区在自家明暗主题上分别是
    /// 20.8 与 15.9,取稍低一档,既托住 Solarized(原生只有 4.8)这类过淡的方案,
    /// 又不会去动 Dracula / Monokai / Tokyo Night 这些本就够用的。
    /// </summary>
    internal const double MinLightnessDelta = 14.0;

    /// <summary>
    /// 选中格的前景与选区底之间的最小感知亮度差(0–1 标度)。
    /// 低于此值就把前景换成 <see cref="ReadableForeground" /> 给的兜底色 —— 选区底一旦不透明,
    /// 原本压在终端底上读得清的暗色前景(Solarized Dark 的 ansi black #073642 是最极端的一例)
    /// 就可能贴到明度相近的选区底上,选中即消失。
    /// </summary>
    internal const double MinForegroundDelta = 0.18;

    /// <summary>
    /// 把方案给的选区色整定成实际绘制用的填充色:强制不透明,并保证与
    /// <paramref name="background" /> 至少差 <see cref="MinLightnessDelta" /> 的 L*。
    /// </summary>
    /// <param name="selection">配色方案 / 用户设置里的选区色(其 alpha 被忽略)。</param>
    /// <param name="background">终端默认背景色。</param>
    public static Rgba Fill(Rgba selection, Rgba background)
    {
        double backgroundLightness = Lightness(background);
        if (Math.Abs(Lightness(selection) - backgroundLightness) >= MinLightnessDelta)
        {
            return Opaque(selection);
        }

        // 推的方向由背景明暗定,不由选区色定:暗底上把选区提亮、亮底上把它压深,才是
        // 各家终端一致的观感。混合目标取纯白/纯黑,是最省事又能保住原色相的 tint/shade。
        byte target = backgroundLightness < 50 ? (byte)0xFF : (byte)0x00;

        // 二分出**刚好够**的混合比例:够看见就停手,不多推一分。上界 1.0 必然满足
        // (背景 L* < 50 时推到白至少差 50,反之推到黑至少差 50),所以搜索必然收敛。
        double low = 0.0;
        double high = 1.0;
        for (int i = 0; i < 12; i++)
        {
            double mid = (low + high) / 2;
            if (Math.Abs(Lightness(Mix(selection, target, mid)) - backgroundLightness) >= MinLightnessDelta)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }
        return Opaque(Mix(selection, target, high));
    }

    /// <summary>
    /// 前景与选区底撞明度时的兜底前景色:优先用终端默认前景(观感上最不突兀),
    /// 它自己也撞的话才退到纯白/纯黑里较远的那个。
    /// </summary>
    public static Rgba ReadableForeground(Rgba selectionFill, Rgba defaultForeground)
    {
        double fill = Brightness(selectionFill);
        if (Math.Abs(Brightness(defaultForeground) - fill) >= MinForegroundDelta)
        {
            return defaultForeground;
        }
        return fill < 0.5 ? Rgba.FromRgb(0xFF, 0xFF, 0xFF) : Rgba.FromRgb(0x00, 0x00, 0x00);
    }

    /// <summary>
    /// 感知亮度(0–1)。用的是 <c>0.299/0.587/0.114</c> 这套直接压在 sRGB 通道上的老式加权,
    /// 而非 <see cref="Lightness" /> 那条要开方的正经路子:它在**每个选中格**上都要算一次,
    /// 精度够用即可,不能带 <c>Math.Pow</c> 进渲染热路径。
    /// </summary>
    public static double Brightness(Rgba color) =>
        ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;

    /// <summary>CIE L*(0–100)。只在换主题 / 换配色时算,可以走完整的 sRGB 线性化。</summary>
    internal static double Lightness(Rgba color)
    {
        double y = (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
        return y > 0.008856 ? (116 * Math.Cbrt(y)) - 16 : 903.3 * y;
    }

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static Rgba Mix(Rgba color, byte target, double t) =>
        Rgba.FromRgb(Lerp(color.R, target, t), Lerp(color.G, target, t), Lerp(color.B, target, t));

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * t)), 0, 255);

    private static Rgba Opaque(Rgba color) => Rgba.FromRgb(color.R, color.G, color.B);
}
