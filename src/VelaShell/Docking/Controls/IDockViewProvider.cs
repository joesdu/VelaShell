using Avalonia.Controls;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 由文档自己提供内容视图(取代原 Dock 的 IDataTemplate 间接层)。
/// <see cref="DockWorkspaceControl" /> 对每个文档只调用一次并缓存结果,
/// 切换标签复用同一控件实例 —— 多标签切换流畅度的关键(原 ControlRecycling 的职责)。
/// </summary>
public interface IDockViewProvider
{
    Control CreateView();
}
