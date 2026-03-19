using FluentAssertions;
using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.Presentation.Tests.ViewModels;

public sealed class TabBarViewModelTests
{
    [Fact]
    public void AddTab_AddsToCollection()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();

        vm.Tabs.Should().HaveCount(1);
    }

    [Fact]
    public void AddTab_SetsAsActive()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();

        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab.Should().BeSameAs(vm.Tabs[0]);
    }

    [Fact]
    public void CloseTab_RemovesFromCollection()
    {
        var vm = new TabBarViewModel();
        vm.AddTabCommand.Execute().Subscribe();
        var tab = vm.Tabs[0];

        vm.CloseTabCommand.Execute(tab).Subscribe();

        vm.Tabs.Should().BeEmpty();
    }
}
