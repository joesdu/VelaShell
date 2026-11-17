using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using TextMateSharp.Grammars;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// LiveMarkdown 皮肤里选择器表达不了的那一部分(样式本身在 <c>MarkdownSkin.axaml</c>),
/// 聊天面板与团队控制台共用。
/// </summary>
internal static class MarkdownSkin
{
    /// <summary>当前明暗下代码块该用的高亮配色。</summary>
    public static ThemeName CodeTheme(StyledElement host)
        => host.ActualThemeVariant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

    /// <summary>
    /// 把 LiveMarkdown 的默认令牌换成 Vela 令牌的当前解析值,写进 <paramref name="host" /> 自己的资源,
    /// 并返回当前明暗对应的代码高亮配色。
    /// </summary>
    /// <remarks>
    /// 大部分观感能用选择器覆盖,但代码块头 / 体的底色只在 ControlTemplate 内以 <c>{DynamicResource}</c> 引用,
    /// 选择器够不到,只能改键;库里这些键全部落在画刷属性上,所以直接塞画刷即可。
    /// 写成直接键 —— 直接键优先于 MergedDictionaries 里的 Defaults.axaml。
    /// </remarks>
    public static ThemeName Sync(Control host)
    {
        // BorderColor 不能挪作背景用:Mermaid 拿它画全部线条(节点框/连线/分组框),
        // 给成背景色调会让整张图糊掉。代码块标题栏底色改由 axaml 的 nth-child 选择器给。
        Skin("BorderColor", "VelaBorderPrimary");              // 线条:表格/分隔线/Mermaid 描边
        Skin("BorderBrush", "VelaBorderPrimary");              // 代码块外框
        Skin("SecondaryCardBackgroundColor", "VelaBgInput");   // 代码块正文底 / Mermaid 分组头
        Skin("CardBackgroundColor", "VelaBgHover");            // 表格底 / Mermaid 节点填充
        Skin("ForegroundColor", "VelaTextPrimary");            // 正文 / Mermaid 图内文字
        Skin("CodeInlineColor", "VelaAccent");
        Skin("QuoteBorderColor", "VelaAccent");
        return CodeTheme(host);

        void Skin(string key, string velaKey)
        {
            if (host.TryFindResource(velaKey, host.ActualThemeVariant, out object? value) && value is IBrush brush)
            {
                host.Resources[key] = brush;
            }
        }
    }

    /// <summary>
    /// 只许 Markdown 里的图片走本地文件 / data: / 内嵌资源的那条样式。
    /// </summary>
    /// <remarks>
    /// 库默认第一个处理器就是 HTTP,模型写出来的图片 URL 会被直接抓取 —— 那等于给任意模型输出开了个追踪像素通道。
    /// 团队成员的报告同样是模型写的,控制台里渲染它们时一样要摘掉 HTTP。
    /// </remarks>
    public static Style LocalImagesOnly() => new(x => x.OfType<Image>().Class("Link"))
    {
        Setters =
        {
            new Setter(AsyncImageLoader.HandlersProperty, new AsyncImageLoaderHandler[]
            {
                LocalFileAsyncImageLoaderHandler.Shared,
                AvaloniaResourceAsyncImageLoaderHandler.Shared,
                DataUrlAsyncImageLoaderHandler.Shared,
                RawAsyncImageLoaderHandler.Shared
            })
        }
    };

    /// <summary>代码高亮是控件属性而非资源键:已经生成的渲染器要逐个改。</summary>
    public static void ApplyCodeTheme(Visual root, ThemeName theme)
    {
        foreach (MarkdownRenderer renderer in root.GetVisualDescendants().OfType<MarkdownRenderer>())
        {
            renderer.CodeBlockColorTheme = theme;
        }
    }
}
