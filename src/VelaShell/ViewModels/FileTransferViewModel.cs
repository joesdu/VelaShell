using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;

namespace VelaShell.ViewModels;

/// <summary>
/// 文件传输面板(toast)的视图模型:聚合活动/历史传输项,驱动准备扫描、
/// 可取消批量传输、徽标计数与自动隐藏等交互逻辑。
/// </summary>
public class FileTransferViewModel : ReactiveObject
{
    // 可空:无参构造的宿主(单元测试/无 SFTP 服务的场景)不提供传输管理器。
    private readonly ITransferManager? _transferManager;
    private IDisposable? _autoHide;

    // 当前批次(一个文件夹/多文件的下载或上传):尚未完成的文件数,
    // 以及用于取消剩余所有文件(包括正在传输的那个)的令牌源。
    private CancellationTokenSource? _batchCts;
    private int _batchRemaining;
    private bool _hidePending;
    private bool _isPointerOver;

    // 准备阶段(上传/下载前的目录扫描):大文件夹的扫描可能持续数秒,期间面板立即弹出、
    // 徽标随发现的文件数递增,让用户知道处理已经开始。
    private bool _isPreparing;
    private int _preparingCount;

    /// <summary>
    /// 构造视图模型并初始化各命令;<paramref name="transferManager" /> 可为空
    /// (单元测试或无 SFTP 服务的宿主场景不提供传输管理器)。
    /// </summary>
    public FileTransferViewModel(ITransferManager? transferManager)
    {
        _transferManager = transferManager;
        Transfers = [];
        Transfers.CollectionChanged += OnTransfersChanged;
        CancelTransferCommand = ReactiveCommand.Create<Guid>(CancelTransfer);
        RetryTransferCommand = ReactiveCommand.Create<Guid>(RetryTransfer);
        ClearCompletedCommand = ReactiveCommand.Create(ClearCompleted);
        CancelAllCommand = ReactiveCommand.Create(CancelAll);
        HidePanelCommand = ReactiveCommand.Create(() => { IsPanelVisible = false; });
    }

    /// <summary>当前所有传输项(活动与已完成),新任务插入到列表顶部。</summary>
    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    /// <summary>进行中(传输中或排队中)的任务数量。</summary>
    public int ActiveCount => Transfers.Count(t => t.IsActive);

    /// <summary>当前是否正在运行一个可取消的传输批次。</summary>
    public bool IsBatchActive => _batchCts is not null;

    /// <summary>
    /// 头部徽标(design 9Ralg):准备阶段随扫描发现的文件数递增;
    /// 批处理期间为尚待传输的文件数(递减);其余情况为进行中的单文件传输数。
    /// </summary>
    public int PendingCount => IsPreparing
                                   ? _preparingCount
                                   : IsBatchActive
                                       ? _batchRemaining
                                       : ActiveCount;

    /// <summary>上传/下载是否仍在扫描目录以制定计划。</summary>
    public bool IsPreparing
    {
        get => _isPreparing;
        private set => this.RaiseAndSetIfChanged(ref _isPreparing, value);
    }

    /// <summary>准备阶段的状态行文案:随扫描进度动态刷新。</summary>
    public string PreparingText => Strings.Format("Msg_ScanningTransferFiles", _preparingCount);

    /// <summary>
    /// 浮窗仅在有内容且未被手动收起时存在(规范 §9):
    /// 新任务会淡入显示,点关闭(x)只是隐藏 —— 任务继续运行。
    /// </summary>
    public bool IsPanelVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>隐藏传输面板(点击关闭按钮),任务继续在后台运行。</summary>
    public ReactiveCommand<Unit, Unit> HidePanelCommand { get; }

    /// <summary>取消指定 Id 的单个传输。</summary>
    public ReactiveCommand<Guid, Unit> CancelTransferCommand { get; }

    /// <summary>重试指定 Id 的失败传输,将其重新排队。</summary>
    public ReactiveCommand<Guid, Unit> RetryTransferCommand { get; }

    /// <summary>清除列表中所有已完成或已取消的传输项。</summary>
    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

    /// <summary>
    /// 取消当前批次中所有剩余文件:中止正在传输的那个并跳过其余
    /// (规范 §9:取消剩余传输)。
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelAllCommand { get; }

    /// <summary>
    /// 进入准备(目录扫描)状态:浮窗立即弹出并显示实时文件计数,
    /// 这样选择一个大文件夹时不会再像什么都没发生。由 <see cref="BeginBatch" />
    /// (扫描完成、传输开始)或 <see cref="EndPreparing" /> 结束。
    /// </summary>
    public void BeginPreparing()
    {
        _preparingCount = 0;
        IsPreparing = true;
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(PreparingText));

        // 面板立即可见;挂起中的自动隐藏作废(新一轮任务开始了)。
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    /// <summary>更新准备扫描过程中迄今发现的文件实时计数。</summary>
    public void UpdatePreparingCount(int discovered)
    {
        if (!IsPreparing)
        {
            return;
        }
        _preparingCount = discovered;
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(PreparingText));
    }

    /// <summary>
    /// 不启动批次即离开准备状态(计划为空、取消或出错)。当
    /// <see cref="BeginBatch" /> 已接管时为无操作。若扫描未产出可显示内容则再次隐藏浮窗。
    /// </summary>
    public void EndPreparing()
    {
        if (!IsPreparing)
        {
            return;
        }
        _preparingCount = 0;
        IsPreparing = false;
        this.RaisePropertyChanged(nameof(PendingCount));
        if (Transfers.Count == 0)
        {
            IsPanelVisible = false;
        }
        else
        {
            NotifyTaskSettled();
        }
    }

    /// <summary>
    /// 开始一个包含 <paramref name="totalFiles" /> 个传输的可取消批次。随后
    /// 头部显示剩余计数,并提供触发 <paramref name="cts" /> 的取消控件。
    /// </summary>
    public void BeginBatch(int totalFiles, CancellationTokenSource cts)
    {
        // 准备阶段结束,徽标从"已发现"切换为"剩余"。
        _isPreparing = false;
        _preparingCount = 0;
        this.RaisePropertyChanged(nameof(IsPreparing));
        _batchCts = cts;
        _batchRemaining = totalFiles;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>将当前批次中的一个文件标记为完成,并递减剩余计数。</summary>
    public void NotifyBatchItemSettled()
    {
        if (_batchRemaining > 0)
        {
            _batchRemaining--;
        }
        this.RaisePropertyChanged(nameof(PendingCount));
    }

    /// <summary>结束当前批次;浮窗恢复为空闲徽标并恢复自动隐藏。</summary>
    public void EndBatch()
    {
        _batchCts = null;
        _batchRemaining = 0;
        this.RaisePropertyChanged(nameof(IsBatchActive));
        this.RaisePropertyChanged(nameof(PendingCount));

        // 批次结束后重新评估自动隐藏(批次期间曾抑制了它)。
        NotifyTaskSettled();
    }

    private void CancelAll()
    {
        // Cancel() 内联运行传输的取消回调;加保护,使行为异常的回调绝不会从取消按钮处让应用崩溃。
        try
        {
            _batchCts?.Cancel();
        }
        catch
        {
            // 尽力而为:批次已在拆除中。
        }

        // 立即反映取消状态;正在运行的文件会随其流关闭而逐步结束。
        foreach (TransferItemViewModel item in Transfers.Where(t => t.IsActive).ToList())
        {
            item.Status = TransferStatus.Cancelled;
        }
    }

    /// <summary>
    /// 重新打开传输浮窗(通过工具栏“传输历史”按钮),以便查看过往与
    /// 正在进行的传输。取消任何待定的自动隐藏,并保持显示直到用户用 x 收起。
    /// </summary>
    public void ShowPanel()
    {
        _autoHide?.Dispose();
        _autoHide = null;
        _hidePending = false;
        IsPanelVisible = true;
    }

    /// <summary>
    /// 传输完成通知用的临时展开:面板可见,但不像 <see cref="ShowPanel" /> 那样锁定——
    /// 自动隐藏倒计时照常进行(指针悬停时照常暂停)。修复完成通知把面板钉死在界面上、
    /// 只能手动关闭的问题。
    /// </summary>
    public void ShowPanelTransient()
    {
        IsPanelVisible = true;
        NotifyTaskSettled();
    }

    private void OnTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        if (Transfers.Count > 0)
        {
            IsPanelVisible = true;
        }
        else
        {
            IsPanelVisible = false;
        }
    }

    /// <summary>
    /// 任务完成时调用;一旦没有活动项,浮窗会停留约 3 秒显示完成状态,
    /// 随后淡出(规范 §9.4)。指针在浮窗内时暂停倒计时 —— 仅当指针离开后才开始(或重启)计时。
    /// </summary>
    public void NotifyTaskSettled()
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(PendingCount));
        _autoHide?.Dispose();
        _autoHide = null;

        // 批次有剩余文件时、任一单文件传输仍在进行时、或扫描正在规划下一批次时,保持浮窗开启。
        if (ActiveCount > 0 || IsBatchActive || IsPreparing)
        {
            _hidePending = false;
            return;
        }
        _hidePending = true;
        if (!_isPointerOver)
        {
            ScheduleAutoHide();
        }
    }

    /// <summary>
    /// 由视图在指针进入/离开时调用:进入时暂停任何待定的自动隐藏,
    /// 以便用户查看结果;离开时恢复 3 秒倒计时。
    /// </summary>
    public void SetPointerOver(bool isOver)
    {
        _isPointerOver = isOver;
        if (isOver)
        {
            _autoHide?.Dispose();
            _autoHide = null;
            return;
        }
        if (_hidePending && ActiveCount == 0)
        {
            ScheduleAutoHide();
        }
    }

    private void ScheduleAutoHide()
    {
        _autoHide = DispatcherTimer.RunOnce(() =>
        {
            if (ActiveCount != 0 || _isPointerOver)
            {
                return;
            }
            IsPanelVisible = false;
            _hidePending = false;
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>新增一个传输任务;插入列表顶部,使进行中的传输无需滚动即可看到。</summary>
    public void AddTransfer(TransferTask task)
    {
        var item = new TransferItemViewModel(task);
        // 新任务出现在顶部,这样进行中的上传无需滚动即可看到。
        Transfers.Insert(0, item);
    }

    /// <summary>按 Id 查找传输项,未找到时返回 <see langword="null" />。</summary>
    public TransferItemViewModel? FindTransfer(Guid transferId) => Transfers.FirstOrDefault(t => t.Id == transferId);

    private void CancelTransfer(Guid transferId)
    {
        TransferItemViewModel? item = FindTransfer(transferId);
        if (item == null)
        {
            return;
        }
        item.Status = TransferStatus.Cancelled;
        _transferManager?.CancelTransferAsync(transferId);
    }

    private void RetryTransfer(Guid transferId)
    {
        TransferItemViewModel? item = FindTransfer(transferId);
        if (item is not { Status: TransferStatus.Failed })
        {
            return;
        }
        item.Status = TransferStatus.Queued;
    }

    private void ClearCompleted()
    {
        var completed = Transfers.Where(t =>
            t.Status is TransferStatus.Completed or TransferStatus.Cancelled).ToList();
        foreach (TransferItemViewModel item in completed)
        {
            Transfers.Remove(item);
        }
    }
}
