using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace VelaShell.ViewModels;

/// <summary>A checkbox tree node backed by the original connection fields.</summary>
public sealed class PluginSelectionNode : ReactiveObject, IDisposable
{
    private readonly PluginProtocolFieldViewModel? _field;
    private readonly Action? _changed;

    internal PluginSelectionNode(string label, IReadOnlyList<PluginProtocolFieldViewModel> fields)
    {
        Label = label;
        Children = [.. fields.Select(field => new PluginSelectionNode(field, Refresh))];
        ToggleCommand = ReactiveCommand.Create(Toggle);
    }

    private PluginSelectionNode(PluginProtocolFieldViewModel field, Action changed)
    {
        _field = field;
        _changed = changed;
        Label = field.Label;
        Children = [];
        ToggleCommand = ReactiveCommand.Create(Toggle);
        field.PropertyChanged += OnFieldChanged;
    }

    /// <summary>Localized category or device name.</summary>
    public string Label { get; }

    /// <summary>Devices in this category; empty for a device.</summary>
    public IReadOnlyList<PluginSelectionNode> Children { get; }

    /// <summary>Whether this is a category.</summary>
    public bool IsGroup => _field is null;

    /// <summary>Field hint, exposed as a tooltip to keep device rows compact.</summary>
    public string? Hint => _field?.Field.Hint;

    /// <summary>Page and conditional visibility are inherited from the original fields.</summary>
    public bool IsVisible => _field?.IsRowVisible ?? Children.Any(child => child.IsVisible);

    /// <summary>All, none, or some of the currently visible devices are selected.</summary>
    public bool? IsChecked
    {
        get
        {
            if (_field is not null) { return _field.Toggle; }
            int count = 0, selected = 0;
            foreach (PluginSelectionNode child in Children)
            {
                if (!child.IsVisible) { continue; }
                count++;
                if (child.IsChecked == true) { selected++; }
            }
            return selected == 0 ? false : selected == count ? true : null;
        }
    }

    /// <summary>Selection count remains visible when the category is collapsed.</summary>
    public string SelectionCount => IsGroup
        ? $"{Children.Count(child => child.IsVisible && child.IsChecked == true)}/{Children.Count(child => child.IsVisible)}"
        : string.Empty;

    /// <summary>Categories initially collapse to keep large device lists manageable.</summary>
    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>A partially selected category selects all on the next click.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleCommand { get; }

    private void Toggle()
    {
        bool value = IsChecked != true;
        if (_field is not null)
        {
            _field.Toggle = value;
            return;
        }
        // Snapshot before changing values: a plugin may condition one field on another.
        PluginSelectionNode[] visible = [.. Children.Where(child => child.IsVisible)];
        foreach (PluginSelectionNode child in visible)
        {
            if (child._field is { } field) { field.Toggle = value; }
        }
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginProtocolFieldViewModel.Text) or nameof(PluginProtocolFieldViewModel.IsRowVisible))
        {
            Refresh();
            _changed?.Invoke();
        }
    }

    private void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsVisible));
        this.RaisePropertyChanged(nameof(IsChecked));
        this.RaisePropertyChanged(nameof(SelectionCount));
    }

    /// <summary>Detach field observers when the form is rebuilt.</summary>
    public void Dispose()
    {
        _field?.PropertyChanged -= OnFieldChanged;
        foreach (PluginSelectionNode child in Children) { child.Dispose(); }
        ToggleCommand.Dispose();
    }
}
