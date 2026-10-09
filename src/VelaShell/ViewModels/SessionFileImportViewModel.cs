using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Security;

namespace VelaShell.ViewModels;

/// <summary>「放到哪里」下拉里的一项。</summary>
/// <param name="FromFile">true = 按文件里的分组放。</param>
/// <param name="GroupId">放进的分组;null 且 <paramref name="FromFile" /> 为 false = 未分组。</param>
/// <param name="Name">显示名。</param>
public sealed record SessionImportGroupTarget(bool FromFile, Guid? GroupId, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>「重复时」下拉里的一项。</summary>
/// <param name="Conflict">处理方式。</param>
/// <param name="Name">显示名。</param>
public sealed record SessionImportConflictOption(SessionImportConflict Conflict, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// 导入连接文件对话框(#571):VelaShell JSON 或 CSV。先预览 —— 每一行是新建、与已有连接重复还是有问题 ——
/// 再按用户选的「重复时怎么办」「放到哪里」写入。
/// </summary>
/// <remarks>
/// 默认值都取最不会出事的那一个:重复的跳过、按文件里的分组放、带加密机密的文件不解锁就不导密码。
/// 用户什么都不改直接点「导入」,结果只会是「多了几条新连接」,不会有任何已有连接被改掉。
/// </remarks>
public sealed class SessionFileImportViewModel : ReactiveObject
{
    private readonly ISessionArchiveService _service;
    private readonly Guid? _initialTarget;
    private SessionImportPlan? _plan;

    /// <summary>构造。</summary>
    /// <param name="service">导入导出服务。</param>
    /// <param name="document">解析好的文件。</param>
    /// <param name="targetGroupId">从分组菜单「导入到此分组」进来时的分组;null = 按文件里的分组。</param>
    public SessionFileImportViewModel(ISessionArchiveService service, SessionImportDocument document, Guid? targetGroupId = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _initialTarget = targetGroupId;
        ConflictOptions =
        [
            new(SessionImportConflict.Skip, Strings.Get("SessImport_ConflictSkip")),
            new(SessionImportConflict.Overwrite, Strings.Get("SessImport_ConflictOverwrite")),
            new(SessionImportConflict.KeepBoth, Strings.Get("SessImport_ConflictKeepBoth"))
        ];
        SelectedConflict = ConflictOptions[0];
        GroupTargets = [new(true, null, Strings.Get("SessImport_TargetFromFile")), new(false, null, Strings.Get("Svc_Ungrouped"))];
        SelectedGroupTarget = GroupTargets[0];

        IObservable<bool> canImport = this.WhenAnyValue(x => x.SelectedCount, x => x.IsBusy)
            .Select(static t => t.Property1 > 0 && !t.Property2);
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync, canImport);
        IObservable<bool> canUnlock = this.WhenAnyValue(x => x.SecretsUnlocked, x => x.Passphrase)
            .Select(t => Document.HasEncryptedSecrets && !t.Property1 && t.Property2 is { Length: > 0 });
        UnlockCommand = ReactiveCommand.Create(Unlock, canUnlock);
        SelectAllCommand = ReactiveCommand.Create(() => SetAllSelected(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAllSelected(false));
        CancelCommand = ReactiveCommand.Create(() => { });
    }

    /// <summary>解析好的文件。</summary>
    public SessionImportDocument Document { get; }

    /// <summary>对话框标题。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string Title => Strings.Get("SessImport_Title");

    /// <summary>文件名。</summary>
    public string FileName => Document.FileName;

    /// <summary>格式与导出时间(「VelaShell JSON · 导出于 2026-10-09 16:00」/「CSV」)。</summary>
    public string FormatLabel => Document.Format == SessionFileFormat.Csv
        ? "CSV"
        : Document.ExportedAtUtc is { } exported
            ? Strings.Format("SessImport_JsonExportedFmt", exported.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture))
            : "VelaShell JSON";

    /// <summary>整份文件层面的提示(忽略了的列、解不开的加密方式),一行一条;没有时为空串。</summary>
    public string DocumentWarnings => string.Join(Environment.NewLine, Document.Warnings);

    /// <summary>是否有整份文件层面的提示。</summary>
    public bool HasDocumentWarnings => Document.Warnings.Count > 0;

    /// <summary>文件是否带着加密的敏感信息。</summary>
    public bool HasEncryptedSecrets => Document.HasEncryptedSecrets;

    /// <summary>敏感信息是否已用口令解开。</summary>
    public bool SecretsUnlocked
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowUnlockFields));
            this.RaisePropertyChanged(nameof(SecretsStatus));
        }
    }

    /// <summary>口令输入框是否显示(有加密的敏感信息、还没解开)。</summary>
    public bool ShowUnlockFields => HasEncryptedSecrets && !SecretsUnlocked;

    /// <summary>导出时设的口令。</summary>
    public SecureString? Passphrase
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            UnlockError = null;
        }
    }

    /// <summary>口令不对时的说明。</summary>
    public string? UnlockError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>敏感信息那一栏的说明:没解开时导入不含密码,解开后带上了几条。</summary>
    public string SecretsStatus => SecretsUnlocked
        ? Strings.Format("SessImport_SecretsUnlockedFmt", Items.Count(static i => i.HasSecret))
        : Strings.Get("SessImport_SecretsLocked");

    /// <summary>「重复时」的选项。</summary>
    public IReadOnlyList<SessionImportConflictOption> ConflictOptions { get; }

    /// <summary>选中的「重复时」。</summary>
    public SessionImportConflictOption SelectedConflict
    {
        get;
        set
        {
            if (value is null)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _bulkUpdate = true;
            try
            {
                foreach (SessionFileImportItemViewModel item in Items)
                {
                    item.ApplyConflict(value.Conflict);
                }
            }
            finally
            {
                _bulkUpdate = false;
            }
            RecomputeCounts();
            RefreshGroupLabels();
        }
    }

    /// <summary>批量改勾选时先不逐行重算统计,改完算一次(几百行时逐行算是平方级的)。</summary>
    private bool _bulkUpdate;

    /// <summary>「放到哪里」的选项:按文件里的分组 / 未分组 / 本机已有的分组。</summary>
    public ObservableCollection<SessionImportGroupTarget> GroupTargets { get; }

    /// <summary>选中的「放到哪里」。</summary>
    public SessionImportGroupTarget SelectedGroupTarget
    {
        get;
        set
        {
            if (value is null)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RefreshGroupLabels();
        }
    }

    /// <summary>预览行。</summary>
    public ObservableCollection<SessionFileImportItemViewModel> Items { get; } = [];

    /// <summary>文件里的连接总数。</summary>
    public int TotalCount => Items.Count;

    /// <summary>新连接数。</summary>
    public int NewCount => Items.Count(static i => !i.HasError && !i.IsDuplicate);

    /// <summary>与本机重复的数量。</summary>
    public int DuplicateCount => Items.Count(static i => !i.HasError && i.IsDuplicate);

    /// <summary>有问题、不能导入的数量。</summary>
    public int ErrorCount => Items.Count(static i => i.HasError);

    /// <summary>勾上的数量。</summary>
    public int SelectedCount
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>摘要主标题。</summary>
    public string Headline =>
        IsBusy && _plan is null ? Strings.Get("SessImport_Loading")
        : SelectedCount == 0 ? Strings.Get("SessImport_NothingSelected")
        : Strings.Format("SessImport_HeadlineFmt", SelectedCount);

    /// <summary>摘要副标题:新建 / 重复 / 有问题各几条。</summary>
    public string Detail => Strings.Format("SessImport_DetailFmt", NewCount, DuplicateCount, ErrorCount);

    /// <summary>导入按钮文案(带数量)。</summary>
    public string ImportButtonText => SelectedCount > 0
        ? Strings.Format("SessImport_ImportCountFmt", SelectedCount)
        : Strings.Get("SessImport_ImportButton");

    /// <summary>是否正在比对或写入。</summary>
    public bool IsBusy
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Headline));
        }
    } = true;

    /// <summary>是否已经开始往库里写(写到一半失败也算):对话框关掉后宿主据此决定要不要重读资源管理器。</summary>
    public bool HasWritten { get; private set; }

    /// <summary>写入失败时的说明。</summary>
    public string? ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>执行导入;没有勾选或失败时为 null。</summary>
    public ReactiveCommand<RxVoid, SessionFileImportOutcome?> ImportCommand { get; }

    /// <summary>用口令解开敏感信息。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UnlockCommand { get; }

    /// <summary>全选(能勾的)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectAllCommand { get; }

    /// <summary>全不选。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectNoneCommand { get; }

    /// <summary>取消。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>对话框打开时调用:与本机数据比对,生成预览行与「放到哪里」的分组选项。</summary>
    /// <remarks>
    /// 多次调用只比对一次、返回同一个任务:窗口的 <c>Opened</c> 在从托盘恢复等场合会重发,
    /// 每发一次就再追加一遍预览行与分组选项,列表里每条连接就成了两条。
    /// </remarks>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private Task? _initialization;

    private async Task InitializeCoreAsync()
    {
        IsBusy = true;
        try
        {
            _plan = await _service.PlanAsync(Document).ConfigureAwait(true);
            foreach (ServerGroup group in _plan.ExistingGroups.OrderBy(static g => g.SortOrder))
            {
                GroupTargets.Add(new SessionImportGroupTarget(false, group.Id, group.Name));
            }
            if (_initialTarget is { } target && GroupTargets.FirstOrDefault(t => t.GroupId == target) is { } preset)
            {
                SelectedGroupTarget = preset;
            }
            foreach (SessionImportPlanItem planItem in _plan.Items)
            {
                var item = new SessionFileImportItemViewModel(planItem);
                item.ApplyConflict(SelectedConflict.Conflict);
                item.PropertyChanged += (_, e) =>
                {
                    if (!_bulkUpdate && e.PropertyName == nameof(SessionFileImportItemViewModel.IsSelected))
                    {
                        RecomputeCounts();
                    }
                };
                Items.Add(item);
            }
            RefreshGroupLabels();
        }
        finally
        {
            IsBusy = false;
            RecomputeCounts();
        }
    }

    private void Unlock()
    {
        if (SessionArchiveJson.TryUnlock(Document, SecureStringConvert.ToPlaintext(Passphrase) ?? string.Empty))
        {
            SecretsUnlocked = true;
            UnlockError = null;
            foreach (SessionFileImportItemViewModel item in Items)
            {
                item.RefreshSecret();
            }
            this.RaisePropertyChanged(nameof(SecretsStatus));
        }
        else
        {
            UnlockError = Strings.Get("SessImport_WrongPassphrase");
        }
    }

    private async Task<SessionFileImportOutcome?> ImportAsync()
    {
        if (_plan is null)
        {
            return null;
        }
        IsBusy = true;
        ErrorMessage = null;
        // 从这一刻起库里可能已经有改动:哪怕最后失败、对话框以「取消」收场,宿主也得重读一遍资源管理器,
        // 否则树里缓存的还是旧对象,接下来置顶 / 移动分组会把旧值整条写回去,等于悄悄撤销了这次导入。
        HasWritten = true;
        try
        {
            SessionImportGroupTarget target = SelectedGroupTarget;
            return await _service.ImportAsync(_plan, new SessionImportOptions
            {
                Conflict = SelectedConflict.Conflict,
                UseFileGroups = target.FromFile,
                TargetGroupId = target.GroupId,
                SelectedIndices = [.. Items.Where(static i => i.IsSelected).Select(static i => i.Item.Candidate.Index)]
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // 写库失败(SonnetDB 抛什么类型这里并不知道)留在对话框里说清楚,不能让它逃出命令:
            // ReactiveCommand 里没接住的异常会走到 ReactiveUI 的默认处理器,直接把应用带走。
            // 已经写进去的那部分不回滚 —— 每条都是完整的一条,重新导一次、选「跳过」就能补齐剩下的。
            ErrorMessage = Strings.Format("SessImport_Failed", ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetAllSelected(bool selected)
    {
        _bulkUpdate = true;
        try
        {
            foreach (SessionFileImportItemViewModel item in Items)
            {
                item.IsSelected = selected;
            }
        }
        finally
        {
            _bulkUpdate = false;
        }
        RecomputeCounts();
    }

    /// <summary>按「放到哪里」回填每一行的分组标签:已有的分组写名字,会新建的分组注明「新建」。</summary>
    private void RefreshGroupLabels()
    {
        if (_plan is null)
        {
            return;
        }
        SessionImportGroupTarget target = SelectedGroupTarget;
        var byId = _plan.ExistingGroups.GroupBy(static g => g.Id).ToDictionary(static g => g.Key, static g => g.First().Name);
        var names = _plan.ExistingGroups.Select(static g => g.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string ungrouped = Strings.Get("Svc_Ungrouped");
        foreach (SessionFileImportItemViewModel item in Items)
        {
            if (!target.FromFile)
            {
                item.GroupLabel = target.Name;
                continue;
            }
            SessionImportCandidate candidate = item.Item.Candidate;
            if (candidate.FileGroupId is { } fileGroup && byId.TryGetValue(fileGroup, out string? localName))
            {
                item.GroupLabel = localName;
                continue;
            }
            if (Document.Format == SessionFileFormat.Csv
                && !candidate.Columns.HasFlag(SessionCsvColumns.Group)
                && item.Item.Existing is { } existing
                && SelectedConflict.Conflict == SessionImportConflict.Overwrite)
            {
                // CSV 没有分组列、又是覆盖:分组不动,显示它现在所在的那一组。
                item.GroupLabel = existing.GroupId is { } current && byId.TryGetValue(current, out string? currentName) ? currentName : ungrouped;
                continue;
            }
            string? name = candidate.GroupName?.Trim();
            item.GroupLabel = string.IsNullOrEmpty(name) ? ungrouped
                : names.Contains(name) ? name
                : Strings.Format("SessImport_NewGroupFmt", name);
        }
    }

    private void RecomputeCounts()
    {
        SelectedCount = Items.Count(static i => i.IsSelected);
        this.RaisePropertyChanged(nameof(TotalCount));
        this.RaisePropertyChanged(nameof(NewCount));
        this.RaisePropertyChanged(nameof(DuplicateCount));
        this.RaisePropertyChanged(nameof(ErrorCount));
        this.RaisePropertyChanged(nameof(Headline));
        this.RaisePropertyChanged(nameof(Detail));
        this.RaisePropertyChanged(nameof(ImportButtonText));
    }
}
