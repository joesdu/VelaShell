using Avalonia.Controls;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Docking;

/// <summary>
/// 插件面板的停靠文档:内容是宿主按插件声明式界面树渲染出的控件
/// (docs/plugins/dev-guide.md §UI)。与其它文档一样可拖拽到任意分栏位置。
/// </summary>
public sealed class PluginDocument : DockDocument, IDockViewProvider
{
    private readonly Control _view;

    /// <summary>用已渲染好的内容视图初始化插件停靠文档。</summary>
    /// <param name="id">文档 id。</param>
    /// <param name="title">标签标题。</param>
    /// <param name="pluginId">所属插件 id。</param>
    /// <param name="view">插件交出的内容视图。</param>
    /// <param name="icon">插件在 <c>PanelOptions.Icon</c> 里自报的图标;没给就是通用插头。</param>
    public PluginDocument(
        string id, string title, string pluginId, Control view, PluginSdk.PluginIcon? icon = null)
    {
        Id = id;
        Title = title;
        PluginId = pluginId;
        // 算一次存下来:插件那段路径每次读都重新解析没有意义,而标签图标一辈子不变。
        TabIcon = Services.ConnectionIcon.ForPanel(icon);
        _view = view;
    }

    /// <summary>所属插件 id(标签提示用)。</summary>
    public string PluginId { get; }

    /// <summary>
    /// 标签页上的图标:插件自报的那个,没给就是通用插头。
    /// 宿主不认识这个面板是干什么的 —— 也不该认识。
    /// </summary>
    public Services.TabIcon? TabIcon { get; }

    /// <summary>标签的悬停提示。</summary>
    public string Tooltip => $"{Title} · {PluginId}";

    /// <summary>返回缓存的内容视图(面板内容更新走视图内部,不重建控件)。</summary>
    public Control CreateView() => _view;
}
