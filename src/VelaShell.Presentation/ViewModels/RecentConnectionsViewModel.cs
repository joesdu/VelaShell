using System.Collections.ObjectModel;
using System.Reactive;
using VelaShell.Core.Data;
using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 侧边栏“最近连接”列表:数据来自 SonnetDB 连接历史(<see cref="IRecentConnectionService"/>),
/// 按时间倒序、同一目标去重,最多展示 <see cref="MaxRecentConnections"/> 条。
/// </summary>
public sealed class RecentConnectionsViewModel : ReactiveObject
{
    private const int MaxRecentConnections = 10;

    private readonly IRecentConnectionService? _recentConnectionService;
    private bool _isLoading;

    public RecentConnectionsViewModel(IRecentConnectionService? recentConnectionService = null)
    {
        _recentConnectionService = recentConnectionService;
        Connections = new ObservableCollection<RecentConnectionItemViewModel>();

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        ClearCommand = ReactiveCommand.CreateFromTask(ClearAllAsync);
    }

    public ObservableCollection<RecentConnectionItemViewModel> Connections { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>快速连接头部 history 按钮:重新加载最近连接。</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    /// <summary>从连接历史重新加载列表;存储故障时保留现有内容,不影响主流程。</summary>
    public async Task RefreshAsync()
    {
        if (_recentConnectionService is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var entries = await _recentConnectionService.GetRecentAsync(MaxRecentConnections).ConfigureAwait(true);
            Connections.Clear();
            foreach (var entry in entries)
            {
                Connections.Add(new RecentConnectionItemViewModel(entry));
            }
        }
        catch
        {
            // 历史读取失败不影响侧边栏其余功能。
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ClearAllAsync()
    {
        if (_recentConnectionService is not null)
        {
            try
            {
                await _recentConnectionService.ClearAsync().ConfigureAwait(true);
            }
            catch
            {
                return;
            }
        }

        Connections.Clear();
    }
}
