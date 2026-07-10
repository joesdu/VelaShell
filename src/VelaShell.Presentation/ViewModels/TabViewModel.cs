using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

public class TabViewModel : ReactiveObject
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.NewTab;

    protected SessionStatus ConnectionStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = SessionStatus.Disconnected;

    public bool IsActive
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 后台标签收到 BEL 时点亮的提醒标记(设置 → 终端 → 标签闪烁提醒);
    /// 切换到该标签时由宿主清除。
    /// </summary>
    public bool HasBellAlert
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
