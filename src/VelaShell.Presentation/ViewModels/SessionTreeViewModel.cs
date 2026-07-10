using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

public sealed class SessionTreeViewModel : ReactiveObject
{
    private readonly ISessionRepository _repository;
    private readonly Dictionary<Guid, SessionProfile> _sessionCache = new();

    /// <summary>各配置最近一次上报的连接状态;重建树(LoadTreeAsync)后重放,状态圆点
    /// 与「活跃/连接中」标签才不会因刷新而回到断开态。</summary>
    private readonly Dictionary<Guid, SessionStatus> _statusCache = new();

    private SessionTreeNodeViewModel? _selectedNode;
    private bool _hasNoSessions;

    public SessionTreeViewModel(ISessionRepository repository)
    {
        _repository = repository;
        Nodes = new ObservableCollection<SessionTreeNodeViewModel>();
        _hasNoSessions = true;

        LoadCommand = ReactiveCommand.CreateFromTask(LoadTreeAsync);

        var hasSelectedSession = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false });

        ConnectCommand = ReactiveCommand.Create(() => RaiseForSelected(ConnectRequested), hasSelectedSession);
        EditSessionCommand = ReactiveCommand.Create(() => RaiseForSelected(EditRequested), hasSelectedSession);
        DeleteSessionCommand = ReactiveCommand.CreateFromTask(DeleteSelectedSessionAsync, hasSelectedSession);
        DuplicateSessionCommand = ReactiveCommand.CreateFromTask(DuplicateSelectedSessionAsync, hasSelectedSession);
        OpenSftpCommand = ReactiveCommand.Create(() => RaiseForSelected(OpenSftpRequested), hasSelectedSession);
        PortForwardCommand = ReactiveCommand.Create(() => RaiseForSelected(PortForwardRequested), hasSelectedSession);
        DisconnectCommand = ReactiveCommand.Create(() => RaiseForSelected(DisconnectRequested), hasSelectedSession);
        DiagnoseCommand = ReactiveCommand.Create(() => RaiseForSelected(DiagnoseRequested), hasSelectedSession);
        MoveToGroupCommand = ReactiveCommand.Create<SessionTreeNodeViewModel>(MoveSelectedToGroup);
    }

    /// <summary>右键“连接”或双击会话时触发,由宿主发起 SSH 连接。</summary>
    public event Action<SessionProfile>? ConnectRequested;

    /// <summary>右键“编辑”时触发,由宿主打开连接配置弹窗。</summary>
    public event Action<SessionProfile>? EditRequested;

    /// <summary>右键“打开 SFTP”:由宿主连接会话并展开文件浏览面板。</summary>
    public event Action<SessionProfile>? OpenSftpRequested;

    /// <summary>右键“端口转发”:由宿主打开隧道管理面板。</summary>
    public event Action<SessionProfile>? PortForwardRequested;

    /// <summary>右键“断开连接”:由宿主断开该会话已连接的终端标签。</summary>
    public event Action<SessionProfile>? DisconnectRequested;

    /// <summary>右键“连接诊断”:由宿主打开连接诊断中心(设计 RGXg1)。</summary>
    public event Action<SessionProfile>? DiagnoseRequested;

    private void RaiseForSelected(Action<SessionProfile>? handler)
    {
        if (SelectedNode is { IsGroup: false } node && _sessionCache.TryGetValue(node.Id, out var session))
        {
            handler?.Invoke(session);
        }
    }

    /// <summary>视图双击会话行时调用:选中并触发连接。</summary>
    public void RequestConnect(Guid sessionId)
    {
        if (_sessionCache.TryGetValue(sessionId, out var session))
        {
            ConnectRequested?.Invoke(session);
        }
    }

    public ObservableCollection<SessionTreeNodeViewModel> Nodes { get; }

    public bool HasNoSessions
    {
        get => _hasNoSessions;
        private set => this.RaiseAndSetIfChanged(ref _hasNoSessions, value);
    }

    public string EmptyStateMessage => "Add your first connection";

    public SessionTreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
    }

    /// <summary>分组节点(供“移动到分组”子菜单绑定);随 LoadTreeAsync 同步。</summary>
    public ObservableCollection<SessionTreeNodeViewModel> GroupNodes { get; } = new();

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }

    public ReactiveCommand<Unit, Unit> EditSessionCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteSessionCommand { get; }

    /// <summary>复制选中的连接为“<名称> (副本)”并落库。</summary>
    public ReactiveCommand<Unit, Unit> DuplicateSessionCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSftpCommand { get; }

    public ReactiveCommand<Unit, Unit> PortForwardCommand { get; }

    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    public ReactiveCommand<Unit, Unit> DiagnoseCommand { get; }

    /// <summary>把选中的会话移动到指定分组节点(参数为“移动到分组”子菜单项)。</summary>
    public ReactiveCommand<SessionTreeNodeViewModel, Unit> MoveToGroupCommand { get; }

    public void AddSession(SessionProfile session)
    {
        _sessionCache[session.Id] = session;

        var sessionNode = new SessionTreeNodeViewModel(session.Id, session.Name, false);
        if (session.GroupId is null)
        {
            // 未分组会话直接挂树根(设计 FrJPu),不再有“未分组”目录。
            sessionNode.IsRootLevel = true;
            Nodes.Add(sessionNode);
        }
        else
        {
            var groupNode = Nodes.FirstOrDefault(node => node.IsGroup && node.Id == session.GroupId);
            if (groupNode is null)
            {
                return;
            }

            groupNode.Children.Add(sessionNode);
        }

        RefreshHasNoSessions();
    }

    public void MoveSessionToGroup(Guid sessionId, Guid targetGroupId)
    {
        var sourceNode = FindSessionNode(sessionId, out var sourceGroup);
        if (sourceNode is null)
        {
            return;
        }

        if (sourceGroup is not null)
        {
            sourceGroup.Children.Remove(sourceNode);
        }
        else
        {
            Nodes.Remove(sourceNode);
        }

        if (targetGroupId == Guid.Empty)
        {
            // “未分组”落点 = 树根(设计 FrJPu)。
            sourceNode.IsRootLevel = true;
            Nodes.Add(sourceNode);
        }
        else
        {
            var targetGroup = Nodes.FirstOrDefault(node => node.IsGroup && node.Id == targetGroupId);
            if (targetGroup is not null)
            {
                sourceNode.IsRootLevel = false;
                targetGroup.Children.Add(sourceNode);
            }
        }

        if (_sessionCache.TryGetValue(sessionId, out var session))
        {
            // Guid.Empty 是“未分组”落点:落库必须存 null,否则下次加载时会话会
            // 因找不到分组而从树里消失。
            session.GroupId = targetGroupId == Guid.Empty ? null : targetGroupId;
            _ = _repository.SaveSessionAsync(session);
        }
    }

    /// <summary>宿主上报某配置的连接状态,驱动状态圆点与「活跃/连接中/离线」标签。</summary>
    public void SetSessionStatus(Guid sessionId, SessionStatus status)
    {
        _statusCache[sessionId] = status;

        var node = FindSessionNode(sessionId, out _);
        if (node is not null)
        {
            node.Status = status;
        }
    }

    /// <summary>在树根与各分组下查找会话节点;<paramref name="parentGroup"/> 为 null 表示根级。</summary>
    private SessionTreeNodeViewModel? FindSessionNode(Guid sessionId, out SessionTreeNodeViewModel? parentGroup)
    {
        foreach (var node in Nodes)
        {
            if (node.IsGroup)
            {
                var child = node.Children.FirstOrDefault(item => item.Id == sessionId);
                if (child is not null)
                {
                    parentGroup = node;
                    return child;
                }
            }
            else if (node.Id == sessionId)
            {
                parentGroup = null;
                return node;
            }
        }

        parentGroup = null;
        return null;
    }

    private void RefreshHasNoSessions()
    {
        HasNoSessions = !Nodes.Any(node => !node.IsGroup || node.Children.Count > 0);
    }

    private void MoveSelectedToGroup(SessionTreeNodeViewModel? targetGroup)
    {
        if (targetGroup is not { IsGroup: true } || SelectedNode is not { IsGroup: false } node)
        {
            return;
        }

        MoveSessionToGroup(node.Id, targetGroup.Id);
    }

    private async Task DuplicateSelectedSessionAsync()
    {
        if (SelectedNode is not { IsGroup: false } node || !_sessionCache.TryGetValue(node.Id, out var source))
        {
            return;
        }

        var copy = new SessionProfile
        {
            Name = source.Name + " (副本)",
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            AuthMethod = source.AuthMethod,
            Password = source.Password,
            RememberPassword = source.RememberPassword,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase,
            GroupId = source.GroupId,
            Tags = new List<string>(source.Tags),
            JumpHostProfileId = source.JumpHostProfileId,
        };

        await _repository.SaveSessionAsync(copy);
        await LoadTreeAsync();
    }

    private async Task LoadTreeAsync()
    {
        Nodes.Clear();
        GroupNodes.Clear();
        _sessionCache.Clear();

        // 以会话的 GroupId 为唯一事实来源分组;无分组的会话归入“未分组”节点。
        var groups = await _repository.GetAllGroupsAsync();
        var sessions = await _repository.GetAllSessionsAsync();
        var byGroup = sessions
            .Where(session => session.GroupId is not null)
            .GroupBy(session => session.GroupId!.Value)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());
        var ungrouped = sessions.Where(session => session.GroupId is null).ToList();

        var groupIndex = 0;
        foreach (var group in groups.OrderBy(item => item.SortOrder))
        {
            var groupNode = new SessionTreeNodeViewModel(group.Id, group.Name, true)
            {
                // 文件夹图标按设计 FrJPu 以 warning/info/accent 轮换配色。
                GroupColorIndex = groupIndex++ % 3,
            };

            if (byGroup.TryGetValue(group.Id, out var members))
            {
                foreach (var session in members.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    groupNode.Children.Add(CreateSessionNode(session, isRootLevel: false));
                }
            }

            Nodes.Add(groupNode);
            GroupNodes.Add(groupNode);
        }

        // 未分组会话直接挂在树根(设计 FrJPu),不再收进“未分组”目录。
        foreach (var session in ungrouped.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Nodes.Add(CreateSessionNode(session, isRootLevel: true));
        }

        // “移动到分组”子菜单始终提供“未分组”落点(即移回树根)。
        GroupNodes.Add(new SessionTreeNodeViewModel(Guid.Empty, "未分组", true));

        RefreshHasNoSessions();
    }

    private SessionTreeNodeViewModel CreateSessionNode(SessionProfile session, bool isRootLevel)
    {
        _sessionCache[session.Id] = session;
        var node = new SessionTreeNodeViewModel(session.Id, session.Name, false)
        {
            IsRootLevel = isRootLevel,
        };

        if (_statusCache.TryGetValue(session.Id, out var status))
        {
            node.Status = status;
        }

        return node;
    }

    private async Task DeleteSelectedSessionAsync()
    {
        if (SelectedNode is null || SelectedNode.IsGroup)
        {
            return;
        }

        var sessionId = SelectedNode.Id;
        await _repository.DeleteSessionAsync(sessionId);
        _sessionCache.Remove(sessionId);
        _statusCache.Remove(sessionId);

        var node = FindSessionNode(sessionId, out var parentGroup);
        if (node is not null)
        {
            if (parentGroup is not null)
            {
                parentGroup.Children.Remove(node);
            }
            else
            {
                Nodes.Remove(node);
            }
        }

        SelectedNode = null;
        RefreshHasNoSessions();
    }
}
