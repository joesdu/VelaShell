using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VelaShell.Services;

/// <summary>外部 Markdown:不执行 HTML、不读取图片,仅保留 https 链接。</summary>
internal static class RemoteMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UseAutoLinks().UsePipeTables().UseEmphasisExtras().UseTaskLists().Build();

    public static MarkdownDocument Parse(string body)
    {
        MarkdownDocument document = Markdown.Parse(body, Pipeline);
        foreach (LinkInline link in document.Descendants<LinkInline>().ToArray())
        {
            if (link.IsImage || !IsAllowedLink(link.Url))
            {
                while (link.FirstChild is { } child)
                {
                    child.Remove();
                    link.InsertBefore(child);
                }
                link.Remove();
            }
        }
        foreach (AutolinkInline link in document.Descendants<AutolinkInline>().ToArray())
        {
            if (link.IsEmail || !IsAllowedLink(link.Url)) link.ReplaceBy(new LiteralInline(link.Url));
        }
        return document;
    }

    public static bool IsAllowedLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
}
