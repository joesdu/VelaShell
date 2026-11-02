using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>快捷命令目录视图模型:管理分组、内置命令与自定义命令。</summary>
/// <remarks>
/// 内置命令也能改、能删(#555),但目录本身不动,用户的改动是叠在上面的一层:
/// 删掉的记进隐藏清单,改过的换成一条同标识的自定义命令(<see cref="QuickCommandViewModel.IsBuiltInOverride" />),
/// 「恢复默认」就是把这一层揭掉。
/// </remarks>
public class QuickCommandsViewModel : ReactiveObject
{
    private readonly IQuickCommandRepository _repository;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<Guid, bool> _expansionBeforeSearch = [];

    /// <summary>用户删掉的内置命令;目录里已经没有的标识(更新的版本才有的)也原样留着,保存时照写回去。</summary>
    private readonly List<Guid> _hiddenBuiltInIds = [];

    private bool _loaded;
    private bool _searchActive;

    /// <summary>创建快捷命令视图模型。</summary>
    public QuickCommandsViewModel(IQuickCommandRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        AllCommands = [];
        Groups = [];
        FilteredGroups = [];
        FilteredCommands = [];
        Categories = [];
        AddCommandCommand = ReactiveCommand.Create(AddCommand);
        DeleteCommandCommand = ReactiveCommand.CreateFromTask<QuickCommandViewModel>(
            DeleteCommandAsync
        );
        SaveNewCommandCommand = ReactiveCommand.CreateFromTask(SaveNewCommandAsync);
        CancelAddCommand = ReactiveCommand.Create(CancelAdd);
        BeginEditCommand = ReactiveCommand.Create<QuickCommandViewModel>(BeginEdit);
        SaveEditCommand = ReactiveCommand.CreateFromTask(SaveEditAsync);
        CancelEditCommand = ReactiveCommand.Create(CancelEdit);
        RestoreBuiltInCommand = ReactiveCommand.CreateFromTask<QuickCommandViewModel>(
            RestoreBuiltInAsync
        );
        RestoreAllBuiltInsCommand = ReactiveCommand.CreateFromTask(RestoreAllBuiltInsAsync);
        this.WhenAnyValue(viewModel => viewModel.SearchQuery).Subscribe(_ => ApplyFilter());
        BuildFromData(new() { Groups = QuickCommandGroupCatalog.CreateSystemGroups() });
    }

    /// <summary>全部命令(内置 + 自定义)。</summary>
    public ObservableCollection<QuickCommandViewModel> AllCommands { get; }

    /// <summary>全部分组。</summary>
    public ObservableCollection<QuickCommandGroupViewModel> Groups { get; }

    /// <summary>当前搜索条件下可见的分组。</summary>
    public ObservableCollection<QuickCommandGroupViewModel> FilteredGroups { get; }

    /// <summary>兼容旧绑定与命令补全测试的扁平筛选结果。</summary>
    public ObservableCollection<QuickCommandViewModel> FilteredCommands { get; }

    /// <summary>新增/编辑时可选择的分组名称。</summary>
    public ObservableCollection<string> Categories { get; }

    /// <summary>搜索关键字。</summary>
    public string SearchQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>迁移或加载错误;非空时只展示内置命令并禁止覆盖未知版本。</summary>
    public string ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否删过或改过内置命令 —— 有才显示「恢复内置命令」。</summary>
    public bool HasBuiltInChanges
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>设置页当前是否显示新增片段表单。</summary>
    public bool IsAddingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>新增或编辑片段的名称。</summary>
    public string NewName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑片段的分组名称。</summary>
    public string NewCategory
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑片段的命令正文。</summary>
    public string NewCommandText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>新增或编辑片段是否在发送后按回车。</summary>
    public bool NewExecuteAfterSend
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>新增或编辑片段的说明。</summary>
    public string NewDescription
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>当前正在编辑的片段(内置命令也可以;保存时换成同标识的自定义命令)。</summary>
    public QuickCommandViewModel? EditingCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>打开新增片段表单。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddCommandCommand { get; }

    /// <summary>删除指定片段;内置命令记进隐藏清单。</summary>
    public ReactiveCommand<QuickCommandViewModel, RxVoid> DeleteCommandCommand { get; }

    /// <summary>保存新增片段。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveNewCommandCommand { get; }

    /// <summary>取消新增片段。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelAddCommand { get; }

    /// <summary>开始编辑指定片段。</summary>
    public ReactiveCommand<QuickCommandViewModel, RxVoid> BeginEditCommand { get; }

    /// <summary>保存当前片段编辑。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveEditCommand { get; }

    /// <summary>取消当前片段编辑。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelEditCommand { get; }

    /// <summary>把一条改过的内置命令恢复成目录里的原样。</summary>
    public ReactiveCommand<QuickCommandViewModel, RxVoid> RestoreBuiltInCommand { get; }

    /// <summary>恢复全部内置命令:删掉的回来,改过的还原。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RestoreAllBuiltInsCommand { get; }

    /// <summary>加载并迁移自定义快捷命令。</summary>
    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }
        await _loadGate.WaitAsync();
        try
        {
            if (_loaded)
            {
                return;
            }
            QuickCommandLoadResult result = await _repository.LoadAsync();
            ErrorMessage = result.Error ?? string.Empty;
            BuildFromData(result.Data);
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// 把一条命令拖到 <paramref name="target" /> 分组里、<paramref name="insertBefore" /> 之前
    /// (null = 排到组尾)。拖进别的分组的内置命令换成同标识的自定义命令 —— 内置命令的分组由目录定死。
    /// </summary>
    /// <returns>顺序确有变化并已保存时为 true;落回原处、目标无效或处于只读状态时为 false。</returns>
    public async Task<bool> MoveCommandAsync(
        QuickCommandViewModel command,
        QuickCommandGroupViewModel target,
        QuickCommandViewModel? insertBefore
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        if (
            !string.IsNullOrEmpty(ErrorMessage)
            || ReferenceEquals(command, insertBefore)
            || FindGroupOf(command) is not { } source
            || !Groups.Contains(target)
            || (insertBefore is not null && !target.Commands.Contains(insertBefore))
        )
        {
            return false;
        }
        if (ReferenceEquals(source, target))
        {
            // 落回原处(自己的前面或后面一格)等于没动,不把分组白白记成「排过序」。
            int from = source.Commands.IndexOf(command);
            int before = insertBefore is null
                ? source.Commands.Count
                : source.Commands.IndexOf(insertBefore);
            if (before == from || before == from + 1)
            {
                return false;
            }
        }

        if (command.IsBuiltIn && !ReferenceEquals(source, target))
        {
            command = ReplaceWithOverride(command, source);
        }
        source.Commands.Remove(command);
        int index = insertBefore is null
            ? target.Commands.Count
            : target.Commands.IndexOf(insertBefore);
        target.Commands.Insert(index, command);
        if (!ReferenceEquals(source, target))
        {
            command.GroupId = target.Id;
            command.Category = target.Name;
            RenumberCustomCommands(source);
        }
        target.HasCustomOrder = true;
        RenumberCustomCommands(target);
        RefreshBuiltInChanges();
        ApplyFilter();
        await SaveCustomCommandsAsync();
        return true;
    }

    /// <summary>
    /// 把分组拖到 <paramref name="insertBefore" /> 之前(null = 排到「未分组」之前)。
    /// 「未分组」固定垫底,既不能拖,也不能有分组排到它后面。
    /// </summary>
    /// <returns>顺序确有变化并已保存时为 true。</returns>
    public async Task<bool> MoveGroupAsync(
        QuickCommandGroupViewModel group,
        QuickCommandGroupViewModel? insertBefore
    )
    {
        ArgumentNullException.ThrowIfNull(group);
        if (
            !string.IsNullOrEmpty(ErrorMessage)
            || group.IsDefault
            || ReferenceEquals(group, insertBefore)
            || !Groups.Contains(group)
            || (insertBefore is not null && !Groups.Contains(insertBefore))
        )
        {
            return false;
        }
        int from = Groups.IndexOf(group);
        int to = insertBefore is null || insertBefore.IsDefault
            ? IndexOfDefaultGroup()
            : Groups.IndexOf(insertBefore);
        if (to == from || to == from + 1)
        {
            return false;
        }
        Groups.Move(from, to > from ? to - 1 : to);
        int order = 0;
        foreach (QuickCommandGroupViewModel item in Groups.Where(item => !item.IsDefault))
        {
            item.Model.SortOrder = order++;
        }
        RefreshCategories();
        ApplyFilter();
        await SaveCustomCommandsAsync();
        return true;
    }

    private void BuildFromData(QuickCommandData data)
    {
        var expansion = Groups.ToDictionary(
            group => group.Id,
            group => group.IsExpanded
        );
        EditingCommand = null;
        Groups.Clear();
        AllCommands.Clear();
        _hiddenBuiltInIds.Clear();
        _hiddenBuiltInIds.AddRange(data.HiddenBuiltInIds ?? []);

        foreach (
            QuickCommandGroup group in data
                .Groups.OrderBy(group => group.Kind == QuickCommandGroupKind.Default)
                .ThenBy(group => group.SortOrder)
                .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            string displayName =
                group.Kind == QuickCommandGroupKind.Default
                    ? Strings.Get("QuickCmd_Ungrouped")
                    : group.Name;
            var groupViewModel = new QuickCommandGroupViewModel(
                QuickCommandGroupCatalog.Clone(group),
                displayName
            )
            {
                IsExpanded = expansion.GetValueOrDefault(group.Id, true),
            };
            Groups.Add(groupViewModel);
        }

        foreach (QuickCommand command in QuickCommandCatalog.VisibleBuiltIns(data))
        {
            AddToGroup(new(command));
        }
        foreach (
            QuickCommand command in data
                .Commands.OrderBy(command => command.SortOrder)
                .ThenBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            command.IsBuiltIn = false;
            AddToGroup(new(command));
        }
        foreach (QuickCommandGroupViewModel group in Groups.Where(group => group.HasCustomOrder))
        {
            ApplyCustomOrder(group);
        }
        RefreshCategories();
        RefreshBuiltInChanges();
        ApplyFilter();
    }

    private void AddToGroup(QuickCommandViewModel command)
    {
        QuickCommandGroupViewModel group =
            Groups.FirstOrDefault(item => item.Id == command.GroupId)
            ?? Groups.First(item => item.Id == QuickCommandGroupCatalog.DefaultGroupId);
        if (group.Id != command.GroupId && !command.IsBuiltIn)
        {
            command.GroupId = group.Id;
        }
        command.Category = group.Name;
        group.Commands.Add(command);
        AllCommands.Add(command);
    }

    /// <summary>按分组上存的顺序重排;表里没有的命令保持默认的相对顺序接在后面(OrderBy 是稳定排序)。</summary>
    private static void ApplyCustomOrder(QuickCommandGroupViewModel group)
    {
        var rank = new Dictionary<Guid, int>();
        for (int index = 0; index < group.Model.CommandOrder.Count; index++)
        {
            rank.TryAdd(group.Model.CommandOrder[index], index);
        }
        QuickCommandViewModel[] ordered =
        [
            .. group.Commands.OrderBy(command =>
                rank.TryGetValue(command.Id, out int position) ? position : int.MaxValue
            ),
        ];
        group.Commands.Clear();
        foreach (QuickCommandViewModel command in ordered)
        {
            group.Commands.Add(command);
        }
    }

    /// <summary>当下的界面状态折成一份要落盘的文档。</summary>
    private QuickCommandData Snapshot() =>
        new()
        {
            Groups =
            [
                .. Groups.Select(group =>
                {
                    QuickCommandGroup model = QuickCommandGroupCatalog.Clone(group.Model);
                    model.CommandOrder = group.HasCustomOrder
                        ? [.. group.Commands.Select(command => command.Id)]
                        : [];
                    return model;
                }),
            ],
            Commands =
            [
                .. AllCommands
                    .Where(command => !command.IsBuiltIn)
                    .Select(command => command.ToModel()),
            ],
            HiddenBuiltInIds = [.. _hiddenBuiltInIds],
        };

    private async Task SaveCustomCommandsAsync()
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        await _repository.SaveAsync(Snapshot());
    }

    private void AddCommand()
    {
        IsAddingCommand = true;
        NewName = string.Empty;
        NewCategory = Strings.Get("QuickCmd_Ungrouped");
        NewCommandText = string.Empty;
        NewExecuteAfterSend = false;
        NewDescription = string.Empty;
    }

    private async Task SaveNewCommandAsync()
    {
        string commandText = QuickCommandText.Normalize(NewCommandText);
        if (
            !string.IsNullOrEmpty(ErrorMessage)
            || string.IsNullOrWhiteSpace(NewName)
            || commandText.Length == 0
        )
        {
            return;
        }
        QuickCommandGroupViewModel group = ResolveOrCreateGroup(NewCategory);
        var command = new QuickCommandViewModel(
            new()
            {
                GroupId = group.Id,
                Name = NewName.Trim(),
                CommandText = commandText,
                ExecuteAfterSend = NewExecuteAfterSend,
                Description = NewDescription.Trim(),
                SortOrder = NextCustomSortOrder(group),
            }
        )
        {
            Category = group.Name,
        };
        group.Commands.Add(command);
        AllCommands.Add(command);
        IsAddingCommand = false;
        RefreshCategories();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private async Task DeleteCommandAsync(QuickCommandViewModel command)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        // 内置命令(原样的或改过的)删掉就是「不再显示」;改过的那条若只删自定义的一层,
        // 目录里的原样就冒出来了 —— 用户要的是删掉,不是恢复默认。
        if (QuickCommandCatalog.IsBuiltInId(command.Id) && !_hiddenBuiltInIds.Contains(command.Id))
        {
            _hiddenBuiltInIds.Add(command.Id);
        }
        AllCommands.Remove(command);
        FindGroupOf(command)?.Commands.Remove(command);
        if (ReferenceEquals(EditingCommand, command))
        {
            EditingCommand = null;
        }
        RefreshBuiltInChanges();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private void BeginEdit(QuickCommandViewModel command)
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        EditingCommand = command;
        NewName = command.Name;
        NewCategory = command.Category;
        NewCommandText = command.CommandText;
        NewExecuteAfterSend = command.ExecuteAfterSend;
        NewDescription = command.Description;
    }

    private async Task SaveEditAsync()
    {
        string name = NewName.Trim();
        string commandText = QuickCommandText.Normalize(NewCommandText);
        string description = NewDescription.Trim();
        if (
            EditingCommand is null
            || !string.IsNullOrEmpty(ErrorMessage)
            || name.Length == 0
            || commandText.Length == 0
        )
        {
            return;
        }

        QuickCommandViewModel command = EditingCommand;
        QuickCommandGroupViewModel previous =
            FindGroupOf(command)
            ?? Groups.First(group => group.Id == QuickCommandGroupCatalog.DefaultGroupId);
        QuickCommandGroupViewModel next = ResolveOrCreateGroup(NewCategory);
        if (command.IsBuiltIn)
        {
            if (
                ReferenceEquals(previous, next)
                && name == command.Name
                && commandText == command.CommandText
                && NewExecuteAfterSend == command.ExecuteAfterSend
                && description == command.Description
            )
            {
                // 什么都没改:它仍是原样的内置命令,不留一条一模一样的自定义副本。
                EditingCommand = null;
                RefreshCategories();
                return;
            }
            command = ReplaceWithOverride(command, previous);
            // 原地换成了自定义命令:不记下组内顺序的话,重启后它会掉到组里那些内置命令的后面。
            // 换到别的分组的不用记,它马上就离开这一组了。
            previous.HasCustomOrder |= ReferenceEquals(previous, next);
        }
        command.Name = name;
        command.CommandText = commandText;
        command.ExecuteAfterSend = NewExecuteAfterSend;
        command.Description = description;
        if (!ReferenceEquals(previous, next))
        {
            previous.Commands.Remove(command);
            command.GroupId = next.Id;
            command.SortOrder = NextCustomSortOrder(next);
            next.Commands.Add(command);
            RenumberCustomCommands(previous);
        }
        command.Category = next.Name;
        EditingCommand = null;
        RefreshCategories();
        RefreshBuiltInChanges();
        ApplyFilter();
        await SaveCustomCommandsAsync();
    }

    private void CancelEdit() => EditingCommand = null;

    private void CancelAdd() => IsAddingCommand = false;

    private async Task RestoreBuiltInAsync(QuickCommandViewModel command)
    {
        if (!string.IsNullOrEmpty(ErrorMessage) || !command.IsBuiltInOverride)
        {
            return;
        }
        // 去掉顶替它的那条、重建一遍。分组上存的顺序认的是标识,原样那条与顶替它的同一个标识,
        // 所以原地改过的内置命令恢复后仍在原来的位置;挪到别的分组的,回到目录定的分组。
        QuickCommandData data = Snapshot();
        data.Commands.RemoveAll(item => item.Id == command.Id);
        data.HiddenBuiltInIds.Remove(command.Id);
        BuildFromData(data);
        await SaveCustomCommandsAsync();
    }

    private async Task RestoreAllBuiltInsAsync()
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }
        QuickCommandData data = Snapshot();
        data.Commands.RemoveAll(item => QuickCommandCatalog.IsBuiltInId(item.Id));
        data.HiddenBuiltInIds.Clear();
        BuildFromData(data);
        await SaveCustomCommandsAsync();
    }

    /// <summary>
    /// 把一条原样的内置命令就地换成同标识、同内容的自定义命令(内置命令的实例只读),返回换上的那条。
    /// </summary>
    private QuickCommandViewModel ReplaceWithOverride(
        QuickCommandViewModel builtIn,
        QuickCommandGroupViewModel group
    )
    {
        var replacement = new QuickCommandViewModel(
            new()
            {
                Id = builtIn.Id,
                GroupId = group.Id,
                Name = builtIn.Name,
                CommandText = builtIn.CommandText,
                ExecuteAfterSend = builtIn.ExecuteAfterSend,
                Description = builtIn.Description,
            }
        )
        {
            Category = group.Name,
        };
        int index = group.Commands.IndexOf(builtIn);
        if (index >= 0)
        {
            group.Commands[index] = replacement;
        }
        int all = AllCommands.IndexOf(builtIn);
        if (all >= 0)
        {
            AllCommands[all] = replacement;
        }
        RenumberCustomCommands(group);
        return replacement;
    }

    /// <summary>
    /// 自定义命令的 <see cref="QuickCommand.SortOrder" /> 按它在组里的位置重写:
    /// 本版本按分组上的顺序表排,这一步是给不认识顺序表的旧版本看的,让它们至少排得一致。
    /// </summary>
    private static void RenumberCustomCommands(QuickCommandGroupViewModel group)
    {
        for (int index = 0; index < group.Commands.Count; index++)
        {
            group.Commands[index].SortOrder = index;
        }
    }

    private static int NextCustomSortOrder(QuickCommandGroupViewModel group) =>
        group.Commands.Where(command => !command.IsBuiltIn)
            .Select(command => command.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private QuickCommandGroupViewModel? FindGroupOf(QuickCommandViewModel command) =>
        Groups.FirstOrDefault(group => group.Commands.Contains(command));

    private int IndexOfDefaultGroup()
    {
        for (int index = 0; index < Groups.Count; index++)
        {
            if (Groups[index].IsDefault)
            {
                return index;
            }
        }
        return Groups.Count;
    }

    private void RefreshBuiltInChanges() =>
        HasBuiltInChanges =
            _hiddenBuiltInIds.Any(QuickCommandCatalog.IsBuiltInId)
            || AllCommands.Any(command => command.IsBuiltInOverride);

    private QuickCommandGroupViewModel ResolveOrCreateGroup(string name)
    {
        string normalized = name.Trim();
        if (
            string.IsNullOrEmpty(normalized)
            || string.Equals(
                normalized,
                Strings.Get("QuickCmd_Ungrouped"),
                StringComparison.CurrentCultureIgnoreCase
            )
        )
        {
            return Groups.First(group => group.Id == QuickCommandGroupCatalog.DefaultGroupId);
        }
        QuickCommandGroupViewModel? existing = Groups.FirstOrDefault(group =>
            string.Equals(group.Name, normalized, StringComparison.CurrentCultureIgnoreCase)
        );
        if (existing is not null)
        {
            return existing;
        }

        int sortOrder =
            Groups
                .Where(group => group.Kind != QuickCommandGroupKind.Default)
                .Select(group => group.Model.SortOrder)
                .DefaultIfEmpty(-1)
                .Max() + 1;
        var created = new QuickCommandGroupViewModel(
            new()
            {
                Id = QuickCommandGroupCatalog.IdForName(normalized),
                Name = normalized,
                SortOrder = sortOrder,
                Kind = QuickCommandGroupKind.User,
            },
            normalized
        );
        Groups.Insert(IndexOfDefaultGroup(), created);
        return created;
    }

    private void ApplyFilter()
    {
        string query = SearchQuery.Trim();
        bool active = query.Length > 0;
        if (active && !_searchActive)
        {
            _expansionBeforeSearch.Clear();
            foreach (QuickCommandGroupViewModel group in Groups)
            {
                _expansionBeforeSearch[group.Id] = group.IsExpanded;
            }
        }
        else if (!active && _searchActive)
        {
            foreach (QuickCommandGroupViewModel group in Groups)
            {
                group.IsExpanded = _expansionBeforeSearch.GetValueOrDefault(group.Id, true);
            }
            _expansionBeforeSearch.Clear();
        }
        _searchActive = active;

        FilteredGroups.Clear();
        FilteredCommands.Clear();
        foreach (QuickCommandGroupViewModel group in Groups)
        {
            group.FilteredCommands.Clear();
            foreach (QuickCommandViewModel command in group.Commands)
            {
                if (!active || Matches(command, query))
                {
                    group.FilteredCommands.Add(command);
                    FilteredCommands.Add(command);
                }
            }
            if (group.FilteredCommands.Count > 0)
            {
                if (active)
                {
                    group.IsExpanded = true;
                }
                FilteredGroups.Add(group);
            }
        }
    }

    private static bool Matches(QuickCommandViewModel command, string query) =>
        command.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || command.CommandText.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void RefreshCategories()
    {
        Categories.Clear();
        foreach (QuickCommandGroupViewModel group in Groups)
        {
            Categories.Add(group.Name);
        }
    }
}
