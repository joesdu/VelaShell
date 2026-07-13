namespace VelaShell.Docking.Model;

/// <summary>
/// 一个可停靠文档(= 一个标签页)。对应原 Dock.Model 的 Document,只保留
/// 本应用实际用到的成员:浮动/固定(Pin)按产品决策永久禁用,故不建模。
/// </summary>
public abstract class DockDocument : DockElement
{
    public string Id { get; init; } = string.Empty;

    public string Title
    {
        get;
        set => SetField(ref field, value);
    } = string.Empty;

    public bool CanClose { get; init; } = true;
}
