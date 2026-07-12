namespace VelaShell.Docking.Model;

/// <summary>
/// 一个可停靠文档(= 一个标签页)。对应原 Dock.Model 的 Document,只保留
/// 本应用实际用到的成员:浮动/固定(Pin)按产品决策永久禁用,故不建模。
/// </summary>
public abstract class DockDocument : DockElement
{
    private string _title = string.Empty;

    public string Id { get; init; } = string.Empty;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public bool CanClose { get; init; } = true;
}
