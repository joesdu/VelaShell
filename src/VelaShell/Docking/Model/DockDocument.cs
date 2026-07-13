namespace VelaShell.Docking.Model;

/// <summary>
/// 一个可停靠文档(= 一个标签页)。对应原 Dock.Model 的 Document,只保留
/// 本应用实际用到的成员:浮动/固定(Pin)按产品决策永久禁用,故不建模。
/// </summary>
public abstract class DockDocument : DockElement
{
    /// <summary>文档的唯一标识,用于在工作区内定位与去重。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>标签页显示的标题;变更时触发属性通知以刷新界面。</summary>
    public string Title
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>是否允许用户关闭该文档标签,默认允许。</summary>
    public bool CanClose { get; init; } = true;
}
