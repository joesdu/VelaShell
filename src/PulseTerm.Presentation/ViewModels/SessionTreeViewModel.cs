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

        ConnectCommand = ReactiveCommand.Create<Unit>(_ => { }, hasSelectedSession);
        EditSessionCommand = ReactiveCommand.Create<Unit>(_ => { }, hasSelectedSession);
        DeleteSessionCommand = ReactiveCommand.CreateFromTask(DeleteSelectedSessionAsync, hasSelectedSession);
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

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }

    public ReactiveCommand<Unit, Unit> EditSessionCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteSessionCommand { get; }

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
            session.GroupId = targetGroupId;
            _ = _repository.SaveSessionAsync(session);
        }
    }

    private async Task LoadTreeAsync()
    {
        Nodes.Clear();
        _sessionCache.Clear();

        var groups = await _repository.GetAllGroupsAsync();
        foreach (var group in groups.OrderBy(item => item.SortOrder))
        {
            var groupNode = new SessionTreeNodeViewModel(group.Id, group.Name, true);

            foreach (var sessionId in group.Sessions)
            {
                var session = await _repository.GetSessionAsync(sessionId);
                if (session is null)
                {
                    continue;
                }

                _sessionCache[session.Id] = session;
                groupNode.Children.Add(new SessionTreeNodeViewModel(session.Id, session.Name, false));
            }

            Nodes.Add(groupNode);
        }

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
