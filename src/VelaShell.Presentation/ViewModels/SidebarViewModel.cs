using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Data;

namespace VelaShell.Presentation.ViewModels;

/// <summary>侧边栏视图模型:聚合会话树、快捷片段、最近连接以及设置/通知等入口命令。</summary>
public sealed class SidebarViewModel(
    IRecentConnectionService? recentConnectionService = null,
    QuickCommandRunnerViewModel? quickCommands = null
) : ReactiveObject
{
    /// <summary>最近连接列表的子视图模型。</summary>
    public RecentConnectionsViewModel RecentConnections { get; } = new(recentConnectionService);

    /// <summary>快捷代码片段运行区域;无应用数据存储时为 null。</summary>
    public QuickCommandRunnerViewModel? QuickCommands { get; } = quickCommands;

    /// <summary>是否在侧边栏中展示快捷命令区域。</summary>
    public bool IsQuickCommandsVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前会话树视图模型,未加载时为 null。</summary>
    public SessionTreeViewModel? SessionTree
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>打开设置的命令。</summary>
    public ReactiveCommand<Unit, Unit> SettingsCommand { get; } = ReactiveCommand.Create(() => { });

    /// <summary>打开通知的命令。</summary>
    public ReactiveCommand<Unit, Unit> NotificationsCommand { get; } =
        ReactiveCommand.Create(() => { });
}
