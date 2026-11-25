using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void DiffDocument_FoldsKeepSyntaxAndNavigationAcrossThemeChanges()
    {
        OnUi(async () =>
        {
            string before = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"int value{i} = 1;"));
            string after = before.Replace("value12 = 1", "value12 = 2", StringComparison.Ordinal)
                .Replace("value30 = 1", "value30 = 3", StringComparison.Ordinal);
            var view = new FileDiffDocumentView(FullDocument("local:w", "a.cs", before, after), new Loc("en"));
            var window = new Window { Width = 1000, Height = 500, Content = view, RequestedThemeVariant = ThemeVariant.Light };
            window.Show();
            try
            {
                RaiseClick(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiffInlineToggle"));
                DiffView diff = view.InlineView!;
                int[] collapsed = [.. view.ChangeStarts];
                Assert.HasCount(2, collapsed);
                RaiseClick(diff.Rows.Children.OfType<ToggleButton>().First());
                int[] expanded = [.. collapsed.Select(i => i + 8)];
                Assert.AreSequenceEqual(expanded, view.ChangeStarts.ToArray());

                window.RequestedThemeVariant = ThemeVariant.Dark;
                await PumpAsync(3);
                Assert.AreSequenceEqual(expanded, view.ChangeStarts.ToArray());
                Assert.IsTrue(diff.Rows.Children.OfType<ToggleButton>().First().IsChecked == true);
                TextBlock firstLine = diff.Rows.Children[1].GetVisualDescendants().OfType<TextBlock>().Last();
                Assert.AreEqual("int value1 = 1;", string.Concat(firstLine.Inlines!.OfType<Run>().Select(r => r.Text)));
                Assert.IsTrue(firstLine.Inlines!.OfType<Run>().Any(r => r.Foreground is not null));
                foreach ((int index, string digit) in new[] { (expanded[0], "1"), (expanded[0] + 1, "2") })
                {
                    TextBlock code = diff.Rows.Children[index].GetVisualDescendants().OfType<TextBlock>().Last();
                    Run marked = code.Inlines!.OfType<Run>().Single(r => r.TextDecorations is { Count: > 0 });
                    Assert.AreEqual(digit, marked.Text);
                }

                RaiseClick(diff.Rows.Children.OfType<ToggleButton>().First());
                Assert.AreSequenceEqual(collapsed, view.ChangeStarts.ToArray());
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ToolDiff_FoldingIsSectionLocal_AndLargeGapCanBeCollapsedAfterTruncation()
    {
        OnUi(() =>
        {
            string[] prefix = [.. Enumerable.Range(1, DiffView.MaxRenderedLines + 8).Select(i => $"row {i}")];
            LineDiffResult first = LineDiff.Compute("a\nold", "a\nnew");
            LineDiffResult second = LineDiff.Compute([.. prefix, "old"], [.. prefix, "new"]);
            var view = new DiffView(new ToolFileDiff("a.txt", [first, second], false), new Loc("en"), DiffViewMode.Inline, context: 0);
            int collapsed = view.Rows.Children.Count;
            ToggleButton[] folds = [.. view.Rows.Children.OfType<ToggleButton>()];
            Assert.HasCount(2, folds);
            RaiseClick(folds[1]);
            Assert.HasCount(DiffView.MaxRenderedLines + 1, view.Rows.Children);
            Assert.IsFalse(folds[0].IsChecked == true);
            Assert.IsTrue(view.Rows.Children[^1].Classes.Contains("diffGap"));
            Assert.IsFalse(view.Rows.Children[^1] is ToggleButton);
            RaiseClick(folds[1]);
            Assert.HasCount(collapsed, view.Rows.Children);
            Assert.IsTrue(view.Rows.Children[^1].Classes.Contains("diffAdded"));
            return Task.CompletedTask;
        });
    }
}
