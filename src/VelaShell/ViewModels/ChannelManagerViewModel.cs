using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Localization;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>已打开终端的分组矩阵。只在管理窗口打开期间观察结构和成员状态，不订阅终端输入。</summary>
public sealed class ChannelManagerViewModel : ReactiveObject, IDisposable
{
    private readonly MainWindowViewModel _host;
    private readonly SessionTreeViewModel? _tree;
    private readonly Dictionary<Guid, ChannelManagerRow> _tabs = [];
    private readonly Dictionary<Guid, ChannelManagerRow> _groups = [];
    private readonly Dictionary<Guid, (Guid GroupId, SessionTreeNodeViewModel Node)> _profileGroups = [];
    private readonly Dictionary<Guid, bool> _expanded = [];
    private readonly Dictionary<Guid, bool> _filterExpansion = [];
    private readonly HashSet<Guid> _selected = [];
    private readonly List<ChannelManagerRow> _roots = [];
    private readonly int[] _counts = new int[2 + Enum.GetValues<SyncInputChannel>().Length];
    private bool _indexDirty = true;
    private bool _queued;
    private bool _disposed;

    /// <summary>以工作区文档和资源管理器的真实层级为数据源。</summary>
    public ChannelManagerViewModel(MainWindowViewModel host)
    {
        _host = host;
        _tree = host.Sidebar.SessionTree;
        _tree?.StructureChanged += StructureChanged;
        host.Layout.DocumentAdded += DocumentsChanged;
        host.Layout.DocumentRemoved += DocumentsChanged;
        LocalizedStrings.Instance.PropertyChanged += LanguageChanged;
        Refresh();
    }

    /// <summary>只摊平可见行，让列表保留虚拟化能力。</summary>
    public IReadOnlyList<ChannelManagerRow> Rows { get; private set; } = [];

    /// <summary>当前筛选结果是否为空。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>当前选中的终端数量文本。</summary>
    public string SelectedText => Strings.Format("ChannelManager_Selected", _selected.Count);

    /// <summary>是否有可移动的选中终端。</summary>
    public bool CanMove => _selected.Any(id => _tabs[id].CanMove);

    /// <summary>是否有可暂停的选中终端。</summary>
    public bool CanPause => SelectedMembers().Any(t => t.IsConnected && t.IsInSyncChannel && !t.IsSyncPaused);

    /// <summary>是否有可恢复的选中终端。</summary>
    public bool CanResume => SelectedMembers().Any(t => t.IsConnected && t.IsInSyncChannel && t.IsSyncPaused);

    /// <summary>是否有可离开频道的选中终端。</summary>
    public bool CanLeave => SelectedMembers().Any(t => t.IsInSyncChannel);

    /// <summary>终端名称、主机或分组路径筛选文本。</summary>
    public string SearchText
    {
        get;
        set
        {
            value ??= string.Empty;
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            _filterExpansion.Clear();
            Refresh();
        }
    } = string.Empty;

    /// <summary>当前筛选项索引：0 为全部，1 为未加入，2 起对应 A–F。</summary>
    public int FilterIndex
    {
        get;
        set
        {
            if (value < 0 || value >= _counts.Length || field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            _filterExpansion.Clear();
            RaiseFilterProperties();
            Refresh();
        }
    }

    /// <summary>是否选中全部筛选项。</summary>
    public bool IsAllFilter => FilterIndex == 0;

    /// <summary>是否选中未加入筛选项。</summary>
    public bool IsNotJoinedFilter => FilterIndex == 1;

    /// <summary>是否选中频道 A 筛选项。</summary>
    public bool IsAFilter => FilterIndex == 2;

    /// <summary>是否选中频道 B 筛选项。</summary>
    public bool IsBFilter => FilterIndex == 3;

    /// <summary>是否选中频道 C 筛选项。</summary>
    public bool IsCFilter => FilterIndex == 4;

    /// <summary>是否选中频道 D 筛选项。</summary>
    public bool IsDFilter => FilterIndex == 5;

    /// <summary>是否选中频道 E 筛选项。</summary>
    public bool IsEFilter => FilterIndex == 6;

    /// <summary>是否选中频道 F 筛选项。</summary>
    public bool IsFFilter => FilterIndex == 7;

    /// <summary>全部终端筛选项及数量。</summary>
    public string FilterAllText => $"{Strings.Get("ChannelManager_All")} {_counts[0]}";

    /// <summary>未加入筛选项及数量。</summary>
    public string FilterNotJoinedText => $"{Strings.Get("ChannelManager_NotJoinedFilter")} {_counts[1]}";

    /// <summary>频道 A 筛选项及数量。</summary>
    public string FilterAText => $"A {_counts[2]}";

    /// <summary>频道 B 筛选项及数量。</summary>
    public string FilterBText => $"B {_counts[3]}";

    /// <summary>频道 C 筛选项及数量。</summary>
    public string FilterCText => $"C {_counts[4]}";

    /// <summary>频道 D 筛选项及数量。</summary>
    public string FilterDText => $"D {_counts[5]}";

    /// <summary>频道 E 筛选项及数量。</summary>
    public string FilterEText => $"E {_counts[6]}";

    /// <summary>频道 F 筛选项及数量。</summary>
    public string FilterFText => $"F {_counts[7]}";

    private void RaiseFilterProperties()
    {
        this.RaisePropertyChanged(nameof(IsAllFilter));
        this.RaisePropertyChanged(nameof(IsNotJoinedFilter));
        this.RaisePropertyChanged(nameof(IsAFilter));
        this.RaisePropertyChanged(nameof(IsBFilter));
        this.RaisePropertyChanged(nameof(IsCFilter));
        this.RaisePropertyChanged(nameof(IsDFilter));
        this.RaisePropertyChanged(nameof(IsEFilter));
        this.RaisePropertyChanged(nameof(IsFFilter));
    }

    private void RaiseFilterCounts()
    {
        this.RaisePropertyChanged(nameof(FilterAllText));
        this.RaisePropertyChanged(nameof(FilterNotJoinedText));
        this.RaisePropertyChanged(nameof(FilterAText));
        this.RaisePropertyChanged(nameof(FilterBText));
        this.RaisePropertyChanged(nameof(FilterCText));
        this.RaisePropertyChanged(nameof(FilterDText));
        this.RaisePropertyChanged(nameof(FilterEText));
        this.RaisePropertyChanged(nameof(FilterFText));
    }

    private void StructureChanged()
    {
        _indexDirty = true;
        ScheduleRefresh();
    }

    private void DocumentsChanged(DockDocument document)
    {
        if (document is TerminalDocument) ScheduleRefresh();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args) => ScheduleRefresh();

    private void TabChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TerminalTabViewModel.SyncChannel) or nameof(TerminalTabViewModel.IsSyncPaused)
            or nameof(TerminalTabViewModel.ConnectionStatus) or nameof(TerminalTabViewModel.IsConnected) or nameof(TerminalTabViewModel.Title))
            ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (_queued || _disposed) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { if (_queued && !_disposed) Refresh(); });
    }

    private void IndexTree()
    {
        _indexDirty = false;
        _profileGroups.Clear();
        _groups.Clear();
        _roots.Clear();
        void Visit(SessionTreeNodeViewModel node, ChannelManagerRow? parent)
        {
            if (!node.IsGroup)
            {
                _profileGroups[node.Id] = (parent?.Id ?? Guid.Empty, node);
                return;
            }
            ChannelManagerRow group = new(node.Id, parent, node.Name);
            _groups.Add(node.Id, group);
            (parent?.Children ?? _roots).Add(group);
            _expanded.TryAdd(node.Id, node.IsExpanded);
            foreach (SessionTreeNodeViewModel child in node.Children) Visit(child, group);
        }
        if (_tree is not null)
            foreach (SessionTreeNodeViewModel root in _tree.Nodes) Visit(root, null);
        ChannelManagerRow ungrouped = new(Guid.Empty, null, Strings.Get("Svc_Ungrouped"));
        _groups.Add(Guid.Empty, ungrouped);
        _roots.Add(ungrouped);
    }

    private bool IsFiltered => FilterIndex != 0 || !string.IsNullOrWhiteSpace(SearchText);
    private bool Match(TerminalTabViewModel tab, string label, string path)
    {
        if (FilterIndex != 0 && FilterIndex != (tab.SyncChannel is { } c ? 2 + (int)c : 1)) return false;
        string text = SearchText.Trim();
        return text.Length == 0 || label.Contains(text, StringComparison.OrdinalIgnoreCase)
            || path.Contains(text, StringComparison.OrdinalIgnoreCase)
            || tab.Profile?.Host.Contains(text, StringComparison.OrdinalIgnoreCase) == true
            || tab.Profile?.Name.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void Refresh()
    {
        if (_disposed) return;
        _queued = false;
        if (_indexDirty) IndexTree();
        TerminalTabViewModel[] open = [.. _host.TerminalTabs];
        HashSet<Guid> current = [.. open.Select(t => t.Id)];
        foreach (Guid id in _tabs.Keys.Where(id => !current.Contains(id)).ToArray())
        {
            _tabs[id].Tab!.PropertyChanged -= TabChanged;
            _tabs.Remove(id);
        }
        foreach (ChannelManagerRow group in _groups.Values) group.Members.Clear();
        ChannelManagerRow ungrouped = _groups[Guid.Empty];
        ungrouped.Name = Strings.Get("Svc_Ungrouped");
        var duplicates = open.CountBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> numbers = [with(StringComparer.OrdinalIgnoreCase)];
        HashSet<Guid> matched = [];
        Array.Clear(_counts);
        _counts[0] = open.Length;
        foreach (TerminalTabViewModel tab in open)
        {
            _counts[tab.SyncChannel is { } c ? 2 + (int)c : 1]++;
            if (!_tabs.TryGetValue(tab.Id, out ChannelManagerRow? row))
            {
                row = new ChannelManagerRow(tab);
                _tabs.Add(tab.Id, row);
                tab.PropertyChanged += TabChanged;
            }
            string label = tab.Title;
            if (duplicates[label] > 1)
            {
                int number = numbers[label] = numbers.GetValueOrDefault(label) + 1;
                label = $"{label} #{number}";
            }
            (Guid groupId, SessionTreeNodeViewModel? node) = _profileGroups.GetValueOrDefault(tab.Profile?.Id ?? Guid.Empty);
            ChannelManagerRow group = _groups[groupId];
            if (!Match(tab, label, group.Path)) continue;
            matched.Add(tab.Id);
            row.SetMember(group, label, node is null ? SessionTabIcons.For(tab.Profile) : node.TabIconId);
            group.Members.Add(row);
        }
        RaiseFilterCounts();
        _selected.IntersectWith(matched);
        List<ChannelManagerRow> rows = [];
        void Flatten(ChannelManagerRow group, bool visible)
        {
            // 父组的批量操作针对过滤后命中的后代，折叠只改变显示。
            group.Targets.Clear();
            int header = rows.Count;
            if (visible) rows.Add(group);
            bool expanded = IsFiltered ? _filterExpansion.GetValueOrDefault(group.Id, true) : _expanded.GetValueOrDefault(group.Id, true);
            group.IsExpanded = expanded;
            foreach (ChannelManagerRow child in group.Children)
            {
                Flatten(child, visible && expanded);
                group.Targets.AddRange(child.Targets);
            }
            foreach (ChannelManagerRow member in group.Members.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase))
            {
                group.Targets.Add(member.Id);
                if (visible && expanded) rows.Add(member);
            }
            if (group.Targets.Count == 0 && visible) rows.RemoveAt(header);
            group.Label = $"{group.Name} ({group.Targets.Count})";
        }
        foreach (ChannelManagerRow root in _roots) Flatten(root, true);
        if (!Rows.SequenceEqual(rows))
        {
            Rows = rows;
            this.RaisePropertyChanged(nameof(Rows));
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
        UpdateSelection();
    }

    private IEnumerable<TerminalTabViewModel> SelectedMembers() => _selected.Select(id => _tabs[id].Tab!);

    private void UpdateSelection()
    {
        foreach (ChannelManagerRow row in _tabs.Values) row.IsChecked = _selected.Contains(row.Id);
        foreach (ChannelManagerRow group in _groups.Values)
        {
            int count = group.Targets.Count(_selected.Contains);
            group.IsChecked = count == 0 ? false : count == group.Targets.Count ? true : null;
        }
        this.RaisePropertyChanged(nameof(SelectedText));
        this.RaisePropertyChanged(nameof(CanMove));
        this.RaisePropertyChanged(nameof(CanPause));
        this.RaisePropertyChanged(nameof(CanResume));
        this.RaisePropertyChanged(nameof(CanLeave));
    }

    /// <summary>切换一行或分组后代的选中状态。</summary>
    /// <param name="row">要切换的行。</param>
    public void ToggleSelection(ChannelManagerRow row)
    {
        if (_queued) Refresh();
        if (!Rows.Contains(row)) return;
        IEnumerable<Guid> ids = row.IsGroup ? row.Targets : [row.Id];
        if (row.IsChecked == true) _selected.ExceptWith(ids);
        else _selected.UnionWith(ids);
        UpdateSelection();
    }

    /// <summary>切换分组的展开状态。</summary>
    /// <param name="row">要切换的分组行。</param>
    public void ToggleExpanded(ChannelManagerRow row)
    {
        if (!row.IsGroup) return;
        (IsFiltered ? _filterExpansion : _expanded)[row.Id] = !row.IsExpanded;
        Refresh();
    }

    /// <summary>将指定行或当前选中终端移动到目标频道。</summary>
    /// <param name="channel">目标频道。</param>
    /// <param name="row">可选的单个目标行。</param>
    public void Move(SyncInputChannel channel, ChannelManagerRow? row = null)
    {
        if (_queued) Refresh();
        if (row is not null && !Rows.Contains(row)) return;
        IEnumerable<TerminalTabViewModel> targets = row?.Tab is { } tab ? [tab] : SelectedMembers();
        foreach (TerminalTabViewModel target in targets)
            if (target.IsConnected && target.SyncChannel != channel) target.MoveToSyncChannel(channel);
        Refresh();
    }

    /// <summary>暂停或恢复当前选中终端的频道同步。</summary>
    /// <param name="paused">是否暂停。</param>
    public void SetPaused(bool paused)
    {
        if (_queued) Refresh();
        foreach (TerminalTabViewModel target in SelectedMembers())
            if (target.IsConnected) target.SetSyncPaused(paused);
        Refresh();
    }

    /// <summary>让当前选中终端离开各自的同步频道。</summary>
    public void Leave()
    {
        if (_queued) Refresh();
        foreach (TerminalTabViewModel target in SelectedMembers())
            if (target.IsInSyncChannel) target.LeaveSyncChannel();
        Refresh();
    }

    /// <summary>解除窗口生命周期内注册的事件监听。</summary>
    public void Dispose()
    {
        _disposed = true;
        _host.Layout.DocumentAdded -= DocumentsChanged;
        _host.Layout.DocumentRemoved -= DocumentsChanged;
        LocalizedStrings.Instance.PropertyChanged -= LanguageChanged;
        _tree?.StructureChanged -= StructureChanged;
        foreach (ChannelManagerRow row in _tabs.Values) row.Tab!.PropertyChanged -= TabChanged;
    }
}

/// <summary>矩阵的一行：分组汇总或唯一的终端实例。</summary>
public sealed class ChannelManagerRow : ReactiveObject
{
    internal ChannelManagerRow(Guid id, ChannelManagerRow? parent, string name)
    {
        Id = id;
        Name = name;
        Path = parent is null ? name : $"{parent.Path} > {name}";
        Indent = new Thickness(parent is null ? 0 : parent.Indent.Left + 14, 0, 0, 0);
    }

    internal ChannelManagerRow(TerminalTabViewModel tab) { Id = tab.Id; Tab = tab; }
    internal Guid Id { get; }
    internal string Name { get; set; } = string.Empty;
    internal string Path { get; } = string.Empty;
    internal TerminalTabViewModel? Tab { get; }
    internal List<ChannelManagerRow> Children { get; } = [];
    internal List<ChannelManagerRow> Members { get; } = [];
    internal List<Guid> Targets { get; } = [];
    /// <summary>该行对应的单选按钮组名称。</summary>
    public string RadioGroup => Id.ToString();

    /// <summary>该行是否为分组行。</summary>
    public bool IsGroup => Tab is null;

    /// <summary>分组是否展开。</summary>
    public bool IsExpanded { get; internal set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>该行的选中状态；分组行可为不确定状态。</summary>
    public bool? IsChecked { get; internal set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>用于显示的行名称。</summary>
    public string Label { get; internal set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;

    /// <summary>该行相对于矩阵名称列的缩进。</summary>
    public Thickness Indent { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>该终端是否允许移动频道。</summary>
    public bool CanMove { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>该终端当前连接或频道状态。</summary>
    public string Status { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;

    /// <summary>该终端当前所属频道字母。</summary>
    public string Channel { get; private set; } = string.Empty;

    /// <summary>该终端是否在频道 A。</summary>
    public bool InA => Channel == "A";

    /// <summary>该终端是否在频道 B。</summary>
    public bool InB => Channel == "B";

    /// <summary>该终端是否在频道 C。</summary>
    public bool InC => Channel == "C";

    /// <summary>该终端是否在频道 D。</summary>
    public bool InD => Channel == "D";

    /// <summary>该终端是否在频道 E。</summary>
    public bool InE => Channel == "E";

    /// <summary>该终端是否在频道 F。</summary>
    public bool InF => Channel == "F";

    /// <summary>资源管理器中配置的自定义图标 id。</summary>
    public string? TabIconId { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    internal void SetMember(ChannelManagerRow group, string label, string? iconId)
    {
        TerminalTabViewModel tab = Tab!;
        Label = label;
        TabIconId = iconId;
        Indent = new Thickness(group.Indent.Left + 14, 0, 0, 0);
        CanMove = tab.IsConnected;
        Status = !CanMove ? (tab.ConnectionStatus == SessionStatus.Connecting ? Strings.Connecting : Strings.Disconnected)
            : !tab.IsInSyncChannel ? Strings.Get("ChannelManager_NotJoined")
            : tab.IsSyncPaused ? Strings.Get("ChannelManager_Paused") : Strings.Get("ChannelManager_Syncing");
        if (Channel == tab.SyncChannelLetter) return;
        Channel = tab.SyncChannelLetter;
        this.RaisePropertyChanged(nameof(Channel));
        this.RaisePropertyChanged(nameof(InA));
        this.RaisePropertyChanged(nameof(InB));
        this.RaisePropertyChanged(nameof(InC));
        this.RaisePropertyChanged(nameof(InD));
        this.RaisePropertyChanged(nameof(InE));
        this.RaisePropertyChanged(nameof(InF));
    }
}
