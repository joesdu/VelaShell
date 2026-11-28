using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;

namespace VelaShell.Plugin.Ai.Ui;

internal static partial class ProviderProxyLinks
{
    public static Border CreateNotice(Loc loc)
    {
        var message = new TextBlock { Classes = { "body" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        SetNotice(message, loc["SetupApiKeyOnlyNotice"]);
        return new Border
        {
            Classes = { "provider-notice" },
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { new TextBlock { Classes = { "provider-notice-title" }, Text = loc["NoticeTitle"] }, message }
            }
        };
    }

    public static void SetNotice(TextBlock hint, string text)
    {
        var inlines = new InlineCollection();
        foreach (string part in GatewayRegex().Split(text))
        {
            string? url = part switch
            {
                "Magpie" => "https://usemagpie.ai",
                "Astra" => "https://astra-gate.si",
                _ => null
            };
            if (url is null)
            {
                inlines.Add(new Run(part));
                continue;
            }
            var link = new InlineLinkButton
            {
                Content = part,
                NavigateUri = new Uri(url),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                MinHeight = 0,
                MinWidth = 0
            };
            link.Bind(TemplatedControl.FontSizeProperty, hint.GetObservable(TextBlock.FontSizeProperty));
            ToolTip.SetTip(link, url);
            inlines.Add(new InlineUIContainer { Child = link });
        }
        hint.Inlines = inlines;
    }

    private sealed class InlineLinkButton : HyperlinkButton
    {
        protected override Type StyleKeyOverride => typeof(HyperlinkButton);

        protected override Size MeasureOverride(Size availableSize)
        {
            Size size = base.MeasureOverride(availableSize);
            // InlineUIContainer otherwise aligns the bottom of the button to the surrounding baseline.
            if (Presenter?.Child is TextBlock label)
                TextBlock.SetBaselineOffset(this, label.TextLayout.TextLines[0].Baseline);
            return size;
        }
    }

    [GeneratedRegex("(Magpie|Astra)")]
    private static partial Regex GatewayRegex();
}
