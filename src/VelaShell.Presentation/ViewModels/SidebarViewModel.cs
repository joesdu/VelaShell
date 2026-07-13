using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Data;

namespace VelaShell.Presentation.ViewModels;

/// <summary>侧边栏视图模型:聚合最近连接、会话树以及设置/通知等入口命令。</summary>
public sealed class SidebarViewModel(IRecentConnectionService? recentConnectionService = null) : ReactiveObject
{
    /// <summary>最近连接列表的子视图模型。</summary>
    public RecentConnectionsViewModel RecentConnections { get; } = new(recentConnectionService);

    /// <summary>当前会话树视图模型,未加载时为 null。</summary>
    public SessionTreeViewModel? SessionTree
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>打开设置的命令。</summary>
    public ReactiveCommand<Unit, Unit> SettingsCommand { get; } = ReactiveCommand.Create(() => { });

    /// <summary>打开通知的命令。</summary>
    public ReactiveCommand<Unit, Unit> NotificationsCommand { get; } = ReactiveCommand.Create(() => { });
}
