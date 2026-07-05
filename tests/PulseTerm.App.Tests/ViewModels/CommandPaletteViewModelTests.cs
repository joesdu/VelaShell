using FluentAssertions;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Tests.ViewModels;

public class CommandPaletteViewModelTests
{
    private static CommandPaletteViewModel CreateVm(out int[] runCount, params (string cat, string title)[] items)
    {
        var counts = new int[items.Length];
        runCount = counts;
        var built = new List<CommandPaletteItem>();
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            built.Add(new CommandPaletteItem(items[i].cat, items[i].title, () => counts[index]++));
        }
        return new CommandPaletteViewModel(() => built);
    }

    [Fact]
    [Trait("Category", "CommandPalette")]
    public void Open_LoadsItemsGroupedByCategory_AndSelectsFirst()
    {
        var vm = CreateVm(out _, ("会话", "web-01"), ("会话", "db-01"), ("命令", "打开设置"));
        vm.Open();

        vm.IsOpen.Should().BeTrue();
        vm.Groups.Should().HaveCount(2);
        vm.Groups[0].Category.Should().Be("会话");
        vm.Groups[0].Items.Should().HaveCount(2);
        vm.ResultCount.Should().Be(3);
        vm.SelectedItem!.Title.Should().Be("web-01");
        vm.SelectedItem.IsSelected.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "CommandPalette")]
    public void Query_FiltersItems_CaseInsensitiveAndFuzzy()
    {
        var vm = CreateVm(out _, ("命令", "打开设置"), ("命令", "新建 SSH 连接"), ("会话", "web-prod-01"));
        vm.Open();

        vm.Query = "web";
        vm.ResultCount.Should().Be(1);
        vm.SelectedItem!.Title.Should().Be("web-prod-01");

        // Fuzzy subsequence: "ssh" matches "新建 SSH 连接".
        vm.Query = "ssh";
        vm.ResultCount.Should().Be(1);
        vm.SelectedItem!.Title.Should().Be("新建 SSH 连接");
    }

    [Fact]
    [Trait("Category", "CommandPalette")]
    public void MoveDownAndUp_WrapsSelection()
    {
        var vm = CreateVm(out _, ("命令", "a"), ("命令", "b"), ("命令", "c"));
        vm.Open();

        vm.SelectedItem!.Title.Should().Be("a");
        vm.MoveDown();
        vm.SelectedItem!.Title.Should().Be("b");
        vm.MoveUp();
        vm.MoveUp();
        vm.SelectedItem!.Title.Should().Be("c"); // wrapped past the top
    }

    [Fact]
    [Trait("Category", "CommandPalette")]
    public void ExecuteSelected_InvokesActionAndCloses()
    {
        var vm = CreateVm(out var runs, ("命令", "a"), ("命令", "b"));
        vm.Open();
        vm.MoveDown(); // select "b"

        vm.ExecuteSelected();

        runs[1].Should().Be(1);
        runs[0].Should().Be(0);
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "CommandPalette")]
    public void Activate_SelectsThenRunsItem()
    {
        var vm = CreateVm(out var runs, ("命令", "a"), ("命令", "b"));
        vm.Open();
        var target = vm.Groups[0].Items[1];

        vm.Activate(target);

        runs[1].Should().Be(1);
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "CommandPalette")]
    public void Open_RefreshesFromProvider()
    {
        var source = new List<CommandPaletteItem>
        {
            new("命令", "one", () => { }),
        };
        var vm = new CommandPaletteViewModel(() => source);

        vm.Open();
        vm.ResultCount.Should().Be(1);

        source.Add(new CommandPaletteItem("命令", "two", () => { }));
        vm.Open();
        vm.ResultCount.Should().Be(2);
    }
}
