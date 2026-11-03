using System.ComponentModel;
using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>A normal field or a tree of grouped Boolean fields; values stay in PluginFields.</summary>
public sealed class PluginFormRow : ReactiveObject, IDisposable
{
    private PluginFormRow(PluginProtocolFieldViewModel? field, PluginSelectionNode? group)
    {
        Field = field;
        FieldItems = field is null ? [] : [field];
        Group = group;
        SelectionRoots = group is null ? [] : [group];
        group?.PropertyChanged += OnGroupChanged;
    }

    /// <summary>The existing editor for an ungrouped field.</summary>
    public PluginProtocolFieldViewModel? Field { get; }

    /// <summary>Empty for categories so the field template is never instantiated with null data.</summary>
    public IReadOnlyList<PluginProtocolFieldViewModel> FieldItems { get; }

    /// <summary>The category root, if this row represents a selection tree.</summary>
    public PluginSelectionNode? Group { get; }

    /// <summary>Tree root collection for the view.</summary>
    public IReadOnlyList<PluginSelectionNode> SelectionRoots { get; }

    /// <summary>Whether this row uses the existing field template.</summary>
    public bool IsField => Field is not null;

    /// <summary>Hide categories with no visible fields, including categories on other pages.</summary>
    public bool IsGroupVisible => Group?.IsVisible == true;

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginSelectionNode.IsVisible))
        {
            this.RaisePropertyChanged(nameof(IsGroupVisible));
        }
    }

    internal static IReadOnlyList<PluginFormRow> Build(IEnumerable<PluginProtocolFieldViewModel> fields)
    {
        PluginProtocolFieldViewModel[] all = [.. fields];
        var groups = all.Where(field => field.IsToggle && !string.IsNullOrWhiteSpace(field.Field.SelectionGroup?.Key))
            .GroupBy(field => (field.PageKey, field.Field.SelectionGroup!.Key))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var rows = new List<PluginFormRow>();
        var added = new HashSet<(string Page, string Group)>();
        foreach (PluginProtocolFieldViewModel field in all)
        {
            if (field.IsToggle && field.Field.SelectionGroup is { } group && !string.IsNullOrWhiteSpace(group.Key))
            {
                if (added.Add((field.PageKey, group.Key)))
                {
                    rows.Add(new(null, new PluginSelectionNode(group.Label, groups[(field.PageKey, group.Key)])));
                }
            }
            else
            {
                rows.Add(new(field, null));
            }
        }
        return rows;
    }

    /// <summary>Release observers attached to the original fields.</summary>
    public void Dispose()
    {
        if (Group is not null)
        {
            Group.PropertyChanged -= OnGroupChanged;
            Group.Dispose();
        }
    }
}
