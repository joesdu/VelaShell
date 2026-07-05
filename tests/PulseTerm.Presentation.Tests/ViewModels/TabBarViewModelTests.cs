using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.Presentation.Tests.ViewModels;

[TestClass]
public sealed class TabBarViewModelTests
{
    [TestMethod]
    public void AddTab_AddsToCollection()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();

        Assert.AreEqual(1, vm.Tabs.Count());
    }

    [TestMethod]
    public void AddTab_SetsAsActive()
    {
        var vm = new TabBarViewModel();

        vm.AddTabCommand.Execute().Subscribe();

        Assert.IsNotNull(vm.ActiveTab);
        Assert.AreSame(vm.Tabs[0], vm.ActiveTab);
    }

    [TestMethod]
    public void CloseTab_RemovesFromCollection()
    {
        var vm = new TabBarViewModel();
        vm.AddTabCommand.Execute().Subscribe();
        var tab = vm.Tabs[0];

        vm.CloseTabCommand.Execute(tab).Subscribe();

        Assert.AreEqual(0, vm.Tabs.Count());
    }
}
