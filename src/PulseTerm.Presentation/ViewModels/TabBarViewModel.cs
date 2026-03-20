using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;

namespace PulseTerm.Presentation.ViewModels;

public sealed class TabBarViewModel : ReactiveObject
{
    private TabViewModel? _activeTab;

    public TabBarViewModel()
    {
        Tabs = new ObservableCollection<TabViewModel>();

        AddTabCommand = ReactiveCommand.Create(AddTab);
        CloseTabCommand = ReactiveCommand.Create<TabViewModel>(CloseTab);
        SelectTabCommand = ReactiveCommand.Create<TabViewModel>(tab => ActiveTab = tab);
        CloseActiveTabCommand = ReactiveCommand.Create(CloseActiveTab);
        NextTabCommand = ReactiveCommand.Create(NextTab);
        PreviousTabCommand = ReactiveCommand.Create(PreviousTab);
    }

    public ObservableCollection<TabViewModel> Tabs { get; }

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab is not null)
            {
                _activeTab.IsActive = false;
            }

            this.RaiseAndSetIfChanged(ref _activeTab, value);

            if (_activeTab is not null)
            {
                _activeTab.IsActive = true;
            }
        }
    }

    public ReactiveCommand<Unit, Unit> AddTabCommand { get; }

    public ReactiveCommand<TabViewModel, Unit> CloseTabCommand { get; }

    public ReactiveCommand<TabViewModel, Unit> SelectTabCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseActiveTabCommand { get; }

    public ReactiveCommand<Unit, Unit> NextTabCommand { get; }

    public ReactiveCommand<Unit, Unit> PreviousTabCommand { get; }

    public void AddTab(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        Tabs.Add(tab);
        ActiveTab = tab;
    }

    private void AddTab()
    {
        var tab = new TabViewModel();
        AddTab(tab);
    }

    private void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);

        if (ActiveTab != tab)
        {
            return;
        }

        ActiveTab = Tabs.Count == 0
            ? null
            : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    private void CloseActiveTab()
    {
        if (ActiveTab is not null)
        {
            CloseTab(ActiveTab);
        }
    }

    private void NextTab()
    {
        if (Tabs.Count == 0 || ActiveTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + 1) % Tabs.Count];
    }

    private void PreviousTab()
    {
        if (Tabs.Count == 0 || ActiveTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index - 1 + Tabs.Count) % Tabs.Count];
    }
}
