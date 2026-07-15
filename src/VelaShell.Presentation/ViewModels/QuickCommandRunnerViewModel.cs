using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 快捷命令运行区域:维护已连接终端目标、选择状态与执行请求
/// </summary>
public sealed class QuickCommandRunnerViewModel : ReactiveObject
{
    /// <summary>
    /// 创建运行区域并复用共享的片段目录
    /// </summary>
    public QuickCommandRunnerViewModel(QuickCommandsViewModel library)
    {
        Library = library ?? throw new ArgumentNullException(nameof(library));
        RunCommand = ReactiveCommand.Create<QuickCommandViewModel>(
            Run,
            this.WhenAnyValue(x => x.CanRun)
        );
        SelectAllCommand = ReactiveCommand.Create(SelectAll);
        ClearSelectionCommand = ReactiveCommand.Create(ClearSelection);
    }

    /// <summary>
    /// 设置页与左栏共享的片段目录
    /// </summary>
    public QuickCommandsViewModel Library { get; }

    /// <summary>
    /// 当前所有已连接、可接收命令的终端
    /// </summary>
    public ObservableCollection<QuickCommandTargetViewModel> Targets { get; } = [];

    /// <summary>
    /// 当前活动终端的标签标识;未显式选择目标时作为默认值
    /// </summary>
    public Guid? CurrentTargetId
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 是否存在可选终端
    /// </summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>
    /// 显式选中的终端数量
    /// </summary>
    public int SelectedTargetCount => Targets.Count(target => target.IsSelected);

    /// <summary>
    /// 是否已显式选择至少一个终端
    /// </summary>
    public bool HasSelectedTargets => SelectedTargetCount > 0;

    /// <summary>当前是否存在有效的显式或默认执行目标。</summary>
    public bool CanRun => ResolveTargetIds().Count > 0;

    /// <summary>运行指定代码片段。</summary>
    public ReactiveCommand<QuickCommandViewModel, Unit> RunCommand { get; }

    /// <summary>选择全部已连接终端。</summary>
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }

    /// <summary>清空显式选择,恢复“当前终端”默认语义。</summary>
    public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }

    /// <summary>宿主收到后负责把命令映射到真实终端并发送。</summary>
    public event EventHandler<QuickCommandExecutionRequest>? ExecutionRequested;

    /// <summary>
    /// 用最新的已连接终端快照刷新选择列表;仍存在的终端保留勾选状态,
    /// 已关闭或断开的终端自动从列表和选择中移除。
    /// </summary>
    public void UpdateTargets(IEnumerable<(Guid Id, string DisplayName)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        HashSet<Guid> selectedIds = Targets
            .Where(target => target.IsSelected)
            .Select(target => target.Id)
            .ToHashSet();
        foreach (QuickCommandTargetViewModel target in Targets)
        {
            target.PropertyChanged -= OnTargetPropertyChanged;
        }
        Targets.Clear();
        foreach ((Guid id, string displayName) in targets.DistinctBy(target => target.Id))
        {
            var target = new QuickCommandTargetViewModel(id, displayName)
            {
                IsSelected = selectedIds.Contains(id),
            };
            target.PropertyChanged += OnTargetPropertyChanged;
            Targets.Add(target);
        }
        if (CurrentTargetId is { } currentId && Targets.All(target => target.Id != currentId))
        {
            CurrentTargetId = null;
        }
        RaiseTargetStateChanged();
    }

    /// <summary>设置未显式选择目标时使用的当前终端。</summary>
    public void SetCurrentTarget(Guid? targetId)
    {
        CurrentTargetId = targetId is { } id && Targets.Any(target => target.Id == id) ? id : null;
        RaiseTargetStateChanged();
    }

    private void Run(QuickCommandViewModel command)
    {
        IReadOnlyList<Guid> targetIds = ResolveTargetIds();
        if (targetIds.Count == 0 || string.IsNullOrWhiteSpace(command.CommandText))
        {
            return;
        }
        ExecutionRequested?.Invoke(this, new(command.CommandText, targetIds));
    }

    private IReadOnlyList<Guid> ResolveTargetIds()
    {
        Guid[] selected = Targets
            .Where(target => target.IsSelected)
            .Select(target => target.Id)
            .ToArray();
        if (selected.Length > 0)
        {
            return selected;
        }
        return CurrentTargetId is { } currentId && Targets.Any(target => target.Id == currentId)
            ? [currentId]
            : [];
    }

    private void SelectAll()
    {
        foreach (QuickCommandTargetViewModel target in Targets)
        {
            target.IsSelected = true;
        }
        RaiseTargetStateChanged();
    }

    private void ClearSelection()
    {
        foreach (QuickCommandTargetViewModel target in Targets)
        {
            target.IsSelected = false;
        }
        RaiseTargetStateChanged();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCommandTargetViewModel.IsSelected))
        {
            RaiseTargetStateChanged();
        }
    }

    private void RaiseTargetStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasTargets));
        this.RaisePropertyChanged(nameof(SelectedTargetCount));
        this.RaisePropertyChanged(nameof(HasSelectedTargets));
        this.RaisePropertyChanged(nameof(CanRun));
    }
}
