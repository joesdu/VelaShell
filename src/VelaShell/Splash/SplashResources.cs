using System.Globalization;
using System.Text;
using SkiaSharp;

namespace VelaShell.Splash;

/// <summary>
/// 启动画面的字体与图片。
/// </summary>
/// <remarks>
/// <para>
/// <b>字体用系统字体,不用应用内置的 Inter / Cascadia Mono。</b>内置字体是 Avalonia 资源,
/// 要等 Avalonia 起来才读得到,而启动画面恰恰是在那之前画的。界面字用 Segoe UI,
/// 等宽字用系统的 Cascadia Mono(Windows 11 自带),没有就退到 Consolas。
/// 中日韩文字与盲文点阵(终端开机的转圈符号)由 <see cref="SplashFace" /> 逐字回退到系统里有这个字的字体。
/// </para>
/// <para>
/// 图片:Logo 作为嵌入资源编进程序集(五套样式都要);看板娘插画近 800 KB,只有一套样式用到,
/// 不进程序集,作为松散文件随应用分发(<c>splash/mascot.png</c>),选了那一套才读。
/// </para>
/// </remarks>
internal sealed class SplashResources : IDisposable
{
    /// <summary>Logo 的嵌入资源名(见 VelaShell.csproj)。</summary>
    private const string LogoResource = "VelaShell.Splash.logo.png";

    /// <summary>看板娘插画相对应用目录的路径(见 VelaShell.csproj)。</summary>
    internal const string MascotRelativePath = "splash/mascot.png";

    private readonly List<IDisposable> _owned = [];
    private SKImage? _mascot;
    private bool _mascotLoaded;

    private SplashResources(CultureInfo culture)
    {
        string[] bcp47 = [culture.Name];
        Ui = Own(new SplashFace(["Segoe UI"], SKFontStyle.Normal, bcp47));
        UiMedium = Own(new SplashFace(["Segoe UI"], new SKFontStyle(500, 5, SKFontStyleSlant.Upright), bcp47));
        UiSemibold = Own(new SplashFace(["Segoe UI"], new SKFontStyle(600, 5, SKFontStyleSlant.Upright), bcp47));
        Mono = Own(new SplashFace(["Cascadia Mono", "Consolas"], SKFontStyle.Normal, bcp47));
        MonoSemibold = Own(new SplashFace(["Cascadia Mono", "Consolas"], new SKFontStyle(600, 5, SKFontStyleSlant.Upright), bcp47));
        MonoBold = Own(new SplashFace(["Cascadia Mono", "Consolas"], SKFontStyle.Bold, bcp47));
        Logo = LoadLogo();
    }

    /// <summary>界面字(常规)。</summary>
    public SplashFace Ui { get; }

    /// <summary>界面字(中等,500)。</summary>
    public SplashFace UiMedium { get; }

    /// <summary>界面字(半粗,600)。</summary>
    public SplashFace UiSemibold { get; }

    /// <summary>等宽字(常规)。</summary>
    public SplashFace Mono { get; }

    /// <summary>等宽字(半粗,600)。</summary>
    public SplashFace MonoSemibold { get; }

    /// <summary>等宽字(粗,700)。</summary>
    public SplashFace MonoBold { get; }

    /// <summary>应用 Logo(1024²);读不到时为 <see langword="null" />,设计稿跳过不画。</summary>
    public SKImage? Logo { get; }

    /// <summary>看板娘插画;第一次访问时才读盘,读不到为 <see langword="null" />。</summary>
    public SKImage? Mascot
    {
        get
        {
            if (!_mascotLoaded)
            {
                _mascotLoaded = true;
                _mascot = LoadMascot();
            }
            return _mascot;
        }
    }

    /// <summary>按指定界面文化(决定中日韩字形的回退字体)创建资源。</summary>
    /// <param name="culture">界面文化。</param>
    /// <returns>资源集合,调用方负责释放。</returns>
    public static SplashResources Create(CultureInfo culture) => new(culture);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable item in _owned)
        {
            item.Dispose();
        }
        _owned.Clear();
        Logo?.Dispose();
        _mascot?.Dispose();
    }

    private T Own<T>(T item) where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    private static SKImage? LoadLogo()
    {
        try
        {
            using Stream? stream = typeof(SplashResources).Assembly.GetManifestResourceStream(LogoResource);
            return stream is null ? null : SKImage.FromEncodedData(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    private static SKImage? LoadMascot()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, MascotRelativePath);
            return File.Exists(path) ? SKImage.FromEncodedData(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// 一种字体外观(字族 + 字重),附带逐字回退:主字体里没有的字(中日韩、盲文点阵、符号)
/// 交给系统里有这个字的字体画。Skia 的 <c>DrawText</c> 自己不做回退,不处理的话这些字就是豆腐块。
/// </summary>
/// <remarks>
/// <b>字体对象(<see cref="SKTypeface" />)不归这里释放。</b>Skia 的字体管理器缓存字体,SkiaSharp 又按原生句柄
/// 只给一个托管包装 —— 两次按同名取字体、两个字各自回退到雅黑,拿到的都是同一个实例,而且与 Avalonia 用的
/// 也可能是同一个。设置页换样式时是「先建新渲染器、再放旧的」,旧的这边若把字体释放了,新的就在用一个
/// 已释放的对象。它们本来就由缓存持有、随进程存在;这里只释放自己新建的 <see cref="SKFont" /> 与画笔。
/// </remarks>
internal sealed class SplashFace : IDisposable
{
    private readonly SKTypeface _primary;
    private readonly SKFontStyle _style;
    private readonly string[] _bcp47;
    private readonly Dictionary<int, SKTypeface> _fallbacks = [];
    private readonly Dictionary<(SKTypeface Typeface, float Size), SKFont> _fonts = [];
    private readonly Dictionary<string, (SKTypeface Typeface, string Text)[]> _runs = new(StringComparer.Ordinal);
    private readonly SKPaint _paint = new() { IsAntialias = true };

    /// <summary>按首个可用的字族创建;一个都没有时用系统默认字体。</summary>
    /// <param name="families">候选字族,按优先级。</param>
    /// <param name="style">字重与字形。</param>
    /// <param name="bcp47">界面语言(决定中日韩回退字体的地区字形)。</param>
    public SplashFace(string[] families, SKFontStyle style, string[] bcp47)
    {
        _style = style;
        _bcp47 = bcp47;
        SKTypeface? chosen = null;
        foreach (string family in families)
        {
            // 系统里没有这个字族时拿到的是默认字体(共享实例,不释放,见类型注释)。
            SKTypeface candidate = SKTypeface.FromFamilyName(family, style);
            if (string.Equals(candidate.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                chosen = candidate;
                break;
            }
        }
        _primary = chosen ?? SKTypeface.FromFamilyName(null, style);
    }

    /// <summary>量一段文字的宽度。</summary>
    public float Measure(string text, float size)
    {
        float width = 0;
        foreach ((SKTypeface typeface, string run) in Runs(text))
        {
            width += Font(typeface, size).MeasureText(run);
        }
        return width;
    }

    /// <summary>
    /// 画一段单行文字,以行框顶边定位(与 CSS 的行盒一致:<paramref name="lineHeight" /> 内垂直居中)。
    /// </summary>
    /// <param name="canvas">画布。</param>
    /// <param name="text">文字。</param>
    /// <param name="x">对齐锚点的横坐标(左对齐为左边、右对齐为右边、居中为中线)。</param>
    /// <param name="top">行框顶边。</param>
    /// <param name="size">字号。</param>
    /// <param name="color">颜色。</param>
    /// <param name="align">水平对齐。</param>
    /// <param name="lineHeight">行框高度;不给时取 1.25 倍字号。</param>
    /// <returns>文字的宽度。</returns>
    public float Draw(SKCanvas canvas, string text, float x, float top, float size, SKColor color,
                      SKTextAlign align = SKTextAlign.Left, float lineHeight = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        float width = Measure(text, size);
        float left = align switch
        {
            SKTextAlign.Right => x - width,
            SKTextAlign.Center => x - width / 2,
            _ => x
        };
        float baseline = Baseline(top, size, lineHeight <= 0 ? size * 1.25f : lineHeight);
        _paint.Color = color;
        foreach ((SKTypeface typeface, string run) in Runs(text))
        {
            SKFont font = Font(typeface, size);
            canvas.DrawText(run, left, baseline, SKTextAlign.Left, font, _paint);
            left += font.MeasureText(run);
        }
        return width;
    }

    /// <summary>行框里垂直居中时的基线位置(按主字体的度量)。</summary>
    public float Baseline(float top, float size, float lineHeight)
    {
        SKFontMetrics metrics = Font(_primary, size).Metrics;
        float glyphHeight = metrics.Descent - metrics.Ascent;
        return top + (lineHeight - glyphHeight) / 2 - metrics.Ascent;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (SKFont font in _fonts.Values)
        {
            font.Dispose();
        }
        _fonts.Clear();
        // 字体对象是共享的,不释放(见类型注释)。
        _fallbacks.Clear();
        _paint.Dispose();
    }

    private SKFont Font(SKTypeface typeface, float size)
    {
        if (!_fonts.TryGetValue((typeface, size), out SKFont? font))
        {
            font = new SKFont(typeface, size)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
                Hinting = SKFontHinting.Slight
            };
            _fonts[(typeface, size)] = font;
        }
        return font;
    }

    /// <summary>按字形覆盖把文字切成若干段,每段一个字体。结果按文字缓存(启动画面上的文字几乎每帧都一样)。</summary>
    private (SKTypeface Typeface, string Text)[] Runs(string text)
    {
        if (_runs.TryGetValue(text, out (SKTypeface, string)[]? cached))
        {
            return cached;
        }
        List<(SKTypeface, string)> runs = [];
        StringBuilder current = new();
        SKTypeface? currentFace = null;
        foreach (Rune rune in text.EnumerateRunes())
        {
            SKTypeface face = FaceFor(rune.Value);
            if (currentFace is not null && !ReferenceEquals(face, currentFace))
            {
                runs.Add((currentFace, current.ToString()));
                current.Clear();
            }
            currentFace = face;
            current.Append(rune.ToString());
        }
        if (currentFace is not null && current.Length > 0)
        {
            runs.Add((currentFace, current.ToString()));
        }
        (SKTypeface, string)[] result = [.. runs];
        if (_runs.Count > 256)
        {
            _runs.Clear();
        }
        _runs[text] = result;
        return result;
    }

    private SKTypeface FaceFor(int codepoint)
    {
        if (codepoint < 0x80 || _primary.ContainsGlyph(codepoint))
        {
            return _primary;
        }
        if (!_fallbacks.TryGetValue(codepoint, out SKTypeface? face))
        {
            // 许多字回退到同一个字体时拿到的是同一个实例(见类型注释),相邻的字因此并成一段来画。
            face = SKFontManager.Default.MatchCharacter(_primary.FamilyName, _style, _bcp47, codepoint) ?? _primary;
            _fallbacks[codepoint] = face;
        }
        return face;
    }
}
