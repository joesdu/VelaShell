using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.Presentation.ViewModels;

public sealed class SessionTreeViewModel : ReactiveObject
{
    private readonly ISessionRepository _repository;
    private readonly Dictionary<Guid, SessionProfile> _sessionCache = new();
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

    /// <summary>把选中的会话移动到指定分组节点(参数为“移动到分组”子菜单项)。</summary>
    public ReactiveCommand<SessionTreeNodeViewModel, Unit> MoveToGroupCommand { get; }

    public void AddSession(SessionProfile session)
    {
        _sessionCache[session.Id] = session;

        var groupNode = Nodes.FirstOrDefault(node => node.IsGroup && node.Id == session.GroupId);
        if (groupNode is not null)
        {
            groupNode.Children.Add(new SessionTreeNodeViewModel(session.Id, session.Name, false));
        }

        HasNoSessions = !Nodes.Any(group => group.Children.Count > 0);
    }

    public void MoveSessionToGroup(Guid sessionId, Guid targetGroupId)
    {
        SessionTreeNodeViewModel? sourceNode = null;
        SessionTreeNodeViewModel? sourceGroup = null;

        foreach (var group in Nodes)
        {
            var child = group.Children.FirstOrDefault(node => node.Id == sessionId);
            if (child is null)
            {
                continue;
            }

            sourceNode = child;
            sourceGroup = group;
            break;
        }

        if (sourceNode is null || sourceGroup is null)
        {
            return;
        }

        sourceGroup.Children.Remove(sourceNode);

        var targetGroup = Nodes.FirstOrDefault(node => node.IsGroup && node.Id == targetGroupId);
        targetGroup?.Children.Add(sourceNode);

        if (_sessionCache.TryGetValue(sessionId, out var session))
        {
            // Guid.Empty 是“未分组”虚拟节点:落库必须存 null,否则下次加载时会话会
            // 因找不到分组而从树里消失。
            session.GroupId = targetGroupId == Guid.Empty ? null : targetGroupId;
            _ = _repository.SaveSessionAsync(session);
        }
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

        foreach (var group in groups.OrderBy(item => item.SortOrder))
        {
            var groupNode = new SessionTreeNodeViewModel(group.Id, group.Name, true);

            if (byGroup.TryGetValue(group.Id, out var members))
            {
                foreach (var session in members.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _sessionCache[session.Id] = session;
                    groupNode.Children.Add(new SessionTreeNodeViewModel(session.Id, session.Name, false));
                }
            }

            Nodes.Add(groupNode);
            GroupNodes.Add(groupNode);
        }

        if (ungrouped.Count > 0)
        {
            var ungroupedNode = new SessionTreeNodeViewModel(Guid.Empty, "未分组", true);
            foreach (var session in ungrouped.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                _sessionCache[session.Id] = session;
                ungroupedNode.Children.Add(new SessionTreeNodeViewModel(session.Id, session.Name, false));
            }

            Nodes.Add(ungroupedNode);
        }

        // “移动到分组”子菜单始终提供“未分组”落点(树里未必有该虚拟节点)。
        GroupNodes.Add(new SessionTreeNodeViewModel(Guid.Empty, "未分组", true));

        HasNoSessions = !Nodes.Any(group => group.Children.Count > 0);
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

        foreach (var group in Nodes)
        {
            var child = group.Children.FirstOrDefault(node => node.Id == sessionId);
            if (child is null)
            {
                continue;
            }

            group.Children.Remove(child);
            break;
        }

        SelectedNode = null;
        HasNoSessions = !Nodes.Any(group => group.Children.Count > 0);
    }
}
