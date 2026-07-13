using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>终端标签的视图模型:承载标题、连接状态与激活/提醒等界面状态。</summary>
public class TabViewModel : ReactiveObject
{
    /// <summary>标签唯一标识,创建时生成。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>标签标题,默认为“新建标签”。</summary>
    public string Title
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.NewTab;

    /// <summary>会话连接状态,默认未连接。</summary>
    protected SessionStatus ConnectionStatus
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = SessionStatus.Disconnected;

    /// <summary>该标签是否为当前激活标签。</summary>
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
