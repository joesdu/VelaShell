using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using VelaShell.Plugin.Ai.Agent;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private readonly HashSet<ChangedFileEntry> _reviewing = [];
    private readonly HashSet<ChangedFileList> _batchReviews = [];
    private ChangedFileList? _confirmDiscardList;
    private (ChangedFileEntry Entry, int Revision)[] _confirmDiscardEntries = [];
    private readonly HashSet<FileDiffDocumentView> _fallbackDiffViews = [];

    private IEnumerable<FileDiffDocumentView> ReviewViews() => _diffDocuments.Values.Where(p => p.Panel.IsOpen)
        .Select(p => p.View).Concat(_fallbackDiffViews).Distinct();

    Task<string?> IFileDiffHost.ReviewAsync(ChangedFileEntry entry, int revision, bool keep) => ReviewFileAsync(entry, revision, keep);

    private async Task<string?> ReviewFileAsync(ChangedFileEntry entry, int revision, bool keep)
    {
        ChangedFileList list = entry.Owner;
        if (!list.Entries.Contains(entry) || revision != entry.Revision || !_reviewing.Add(entry)) return "DiffReviewConflict";
        var views = ReviewViews().Where(view => view.Document.RawLocation == entry.Location && view.Document.Path == entry.Path).ToArray();
        bool locked = false;
        try
        {
            if (views.Any(view => view.IsDirty || view.IsBusy)) return "DiffReviewDraft";
            foreach (var view in views) view.SetBusy(true);
            locked = true;
            if (!keep)
            {
                if (!entry.CanRestore) return "DiffReviewUnavailable";
                string? error = await FileTexts.RestoreAsync(entry.Snapshot());
                if (error is not null) return error;
            }
            if (revision != entry.Revision || !list.Remove(entry)) return "DiffReviewConflict";
            foreach (var view in ReviewViews().ToArray()) view.FinishReview(entry, keep);
            return null;
        }
        finally
        {
            _reviewing.Remove(entry);
            if (locked) foreach (var view in views) view.SetBusy(false);
            if (list == _active.ChangedFiles && !_batchReviews.Contains(list)) RefreshChangedFiles();
        }
    }

    private async Task ReviewAllAsync(ChangedFileList list, (ChangedFileEntry Entry, int Revision)[] entries, bool keep)
    {
        if (!_batchReviews.Add(list)) return;
        _confirmDiscardList = null;
        _confirmDiscardEntries = [];
        RefreshChangedFiles();
        try
        {
            foreach (var (entry, revision) in entries.Reverse())
            {
                if (!list.Entries.Contains(entry)) continue;
                string? error = await ReviewFileAsync(entry, revision, keep);
                if (error is not null)
                {
                    entry.ReviewError = _loc[error];
                    _changedFilesExpanded = true;
                }
            }
        }
        finally
        {
            _batchReviews.Remove(list);
            RefreshChangedFiles();
        }
    }

    private Button ReviewButton(string name, string label, Action click)
    {
        var button = new Button { Name = name, Content = _loc[label], Height = 24, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        button[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        AutomationProperties.SetName(button, _loc[label]);
        button.Click += (_, e) => { e.Handled = true; click(); };
        return button;
    }

    private StackPanel BatchActions(ChangedFileList list)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsEnabled = !_batchReviews.Contains(list) };
        if (_confirmDiscardList == list)
        {
            actions.Children.Add(ReviewButton("ChangedFilesConfirmDiscard", "DiffConfirmDiscard", () => _ = ReviewAllAsync(list, _confirmDiscardEntries, keep: false)));
            actions.Children.Add(ReviewButton("ChangedFilesCancelDiscard", "Cancel", () => { _confirmDiscardList = null; _confirmDiscardEntries = []; RefreshChangedFiles(); }));
        }
        else
        {
            actions.Children.Add(ReviewButton("ChangedFilesKeepAll", "DiffKeepAll", () => _ = ReviewAllAsync(list, [.. list.Entries.Select(e => (e, e.Revision))], keep: true)));
            actions.Children.Add(ReviewButton("ChangedFilesDiscardAll", "DiffDiscardAll", () =>
            {
                _confirmDiscardList = list;
                _confirmDiscardEntries = [.. list.Entries.Select(e => (e, e.Revision))];
                RefreshChangedFiles();
            }));
        }
        return actions;
    }
}
