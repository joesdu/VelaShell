using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests;

/// <summary>
/// 状态栏的选区计数与编码热切入口。
/// </summary>
[TestClass]
[TestCategory("StatusBar")]
public sealed class StatusBarSelectionTests
{
    [TestMethod]
    public void NoSelection_HidesTheSegment()
    {
        using var vm = new StatusBarViewModel();

        Assert.AreEqual(0, vm.SelectionLength);
        Assert.IsFalse(vm.HasSelection, "没有选区时那一段不该占位。");
    }

    [TestMethod]
    public void SettingALength_ShowsTheSegment_AndRaisesTheDerivedProperties()
    {
        using var vm = new StatusBarViewModel();
        // 只收具名通知。`PropertyName == null` 在 INotifyPropertyChanged 的约定里是
        // 「全部属性都变了」,它既满足不了下面那两条按名字断言,也不该把 changed 的元素类型
        // 拖成 string? —— MSTest 4.4.1 起 Assert.Contains 的约束是 TCollection : IEnumerable<T>,
        // T 由 nameof(...) 推成非空 string,传 List<string?> 会因可空性不匹配报 CS8631,
        // 而 CI 的构建带 -warnaserror。
        List<string> changed = [];
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name)
            {
                changed.Add(name);
            }
        };

        vm.SelectionLength = 42;

        Assert.IsTrue(vm.HasSelection);
        Assert.Contains(nameof(StatusBarViewModel.HasSelection), changed);
        Assert.Contains(nameof(StatusBarViewModel.SelectionLabel), changed);
        Assert.Contains("42", vm.SelectionLabel, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ClearingTheSelection_HidesTheSegmentAgain()
    {
        using var vm = new StatusBarViewModel { SelectionLength = 7 };

        vm.SelectionLength = 0;

        Assert.IsFalse(vm.HasSelection);
    }

    [TestMethod]
    public void AvailableEncodings_DefaultsToEmpty_UntilTheHostInjectsThem()
    {
        // 宿主没注入时菜单是空的,而不是崩掉或显示一份写死的副本。
        using var vm = new StatusBarViewModel();

        Assert.IsEmpty(vm.AvailableEncodings);
        Assert.IsNull(vm.ChangeEncodingCommand);
    }
}
