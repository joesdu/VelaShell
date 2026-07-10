using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace VelaShell.Themes;

/// <summary>
/// DockTabStrip.axaml 的代码后端:标签列表下拉(设计 nunbT tabListDrop)。
/// 菜单在点击时按当前打开的标签动态生成,选中即激活对应标签。
/// </summary>
public class DockTabStripResources : ResourceDictionary
{
    public DockTabStripResources()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void TabListDrop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: IDocumentDock { VisibleDockables: { Count: > 0 } dockables } dock } button)
        {
            return;
        }
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (IDockable dockable in dockables)
        {
            var item = new MenuItem { Header = dockable.Title };
            if (ReferenceEquals(dock.ActiveDockable, dockable))
            {
                // 当前激活的标签以类名标识,着色交给 DockStyles 里的 DynamicResource
                // (代码 FindResource 取不到主题字典画刷)。
                item.Classes.Add("active-tab");
            }
            IDockable captured = dockable;
            item.Click += (_, _) =>
            {
                dock.Factory?.SetActiveDockable(captured);
                dock.Factory?.SetFocusedDockable(dock, captured);
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }
}
