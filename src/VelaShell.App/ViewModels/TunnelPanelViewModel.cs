using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Reactive;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Tunnels;

namespace VelaShell.App.ViewModels;

/// <summary>
/// 隧道管理面板(设计 B3Rth),以服务器为中心:从已保存会话中选一台服务器,
/// 创建/启动隧道时后台自动建立专用 SSH 连接(不打开终端标签,用户反馈 #5)。
/// 隧道生命周期独立于终端会话;该服务器最后一条隧道删除后,后台连接自动断开。
/// </summary>
public class TunnelPanelViewModel : ReactiveObject, IDisposable
{
    private readonly Func<SessionProfile, CancellationToken, Task<Guid>>? _backgroundConnector;

    /// <summary>面板持有的后台隧道连接:profileId → sessionId。</summary>
    private readonly Dictionary<Guid, Guid> _hostSessions = new();

    private readonly Func<Guid, bool>? _isSessionAlive;

    /// <summary>每台服务器的隧道条目缓存:切换服务器/后台连接掉线后配置仍在,可一键重启。</summary>
    private readonly Dictionary<Guid, ObservableCollection<TunnelItemViewModel>> _itemsByProfile = new();

    private readonly DispatcherTimer? _liveTimer;
    private readonly Func<Task<IReadOnlyList<SessionProfile>>>? _savedProfilesProvider;
    private readonly Func<Guid, Task>? _sessionDisconnector;
    private readonly ITunnelService _tunnelService;
    private Guid? _editingTunnelId;
    private bool _isConnectingHost;

    private SessionProfile? _selectedServer;
    private ObservableCollection<TunnelItemViewModel> _tunnels;

    public TunnelPanelViewModel(
        ITunnelService tunnelService,
        Func<Task<IReadOnlyList<SessionProfile>>>? savedProfilesProvider = null,
        Func<SessionProfile, CancellationToken, Task<Guid>>? backgroundConnector = null,
        Func<Guid, bool>? isSessionAlive = null,
        Func<Guid, Task>? sessionDisconnector = null)
    {
        _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));
        _savedProfilesProvider = savedProfilesProvider;
        _backgroundConnector = backgroundConnector;
        _isSessionAlive = isSessionAlive;
        _sessionDisconnector = sessionDisconnector;
        Servers = [];
        _tunnels = [];
        IObservable<bool> canCreate = this.WhenAnyValue(vm => vm.SelectedServer,
            vm => vm.NewLocalHost,
            vm => vm.NewLocalPort,
            vm => vm.NewRemoteHost,
            vm => vm.NewRemotePort,
            vm => vm.NewTunnelTypeIndex,
            vm => vm.ForwardToServerLoopback,
            (server, localHost, localPort, remoteHost, remotePort, typeIndex, _) =>
                server is not null &&
                !string.IsNullOrWhiteSpace(localHost) &&
                localPort is >= 1 and <= 65535 &&
                (typeIndex == 2 ||
                 (!string.IsNullOrWhiteSpace(remoteHost) && remotePort is >= 1 and <= 65535)));
        CreateTunnelCommand = ReactiveCommand.CreateFromTask(SubmitAsync, canCreate);
        StopTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StopTunnelAsync);
        StartTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StartTunnelAsync);
        DeleteTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(DeleteTunnelAsync);
        EditTunnelCommand = ReactiveCommand.Create<Guid>(BeginEdit);
        // 取消:编辑模式下退出编辑并清空表单;普通状态下表单本就多为默认值,单纯清空
        // 看起来"点了没反应"(用户反馈 #6)——此时按对话框惯例直接收起面板。
        ResetFormCommand = ReactiveCommand.Create(() =>
        {
            ErrorMessage = null;
            bool wasEditing = IsEditing;
            ResetForm();
            if (!wasEditing)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        // 每 5 秒刷新条目(运行时长/服务侧状态与错误)并核对后台连接是否还活着;
        // 无 Avalonia 应用(单元测试)时跳过。
        if (Application.Current is not null)
        {
            _liveTimer = new()
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _liveTimer.Tick += (_, _) => RefreshLiveState();
            _liveTimer.Start();
        }
    }

    /// <summary>隧道走哪台服务器:全部已保存会话。</summary>
    public ObservableCollection<SessionProfile> Servers { get; }

    public SessionProfile? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (ReferenceEquals(_selectedServer, value))
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _selectedServer, value);
            OnServerChanged();
        }
    }

    /// <summary>当前服务器的隧道条目(按服务器缓存,切换不丢配置)。</summary>
    public ObservableCollection<TunnelItemViewModel> Tunnels
    {
        get => _tunnels;
        private set => this.RaiseAndSetIfChanged(ref _tunnels, value);
    }

    /// <summary>服务器行下方的连接状态说明。</summary>
    public string ServerStatusText
    {
        get
        {
            if (SelectedServer is null)
            {
                return "请选择服务器";
            }
            if (_isConnectingHost)
            {
                return "正在后台连接…";
            }
            return ResolveLiveSession(SelectedServer.Id) is not null
                       ? "已连接 • 后台隧道通道(独立于终端会话)"
                       : "未连接 • 创建或启动隧道时自动连接";
        }
    }

    public bool IsServerConnected => SelectedServer is not null && ResolveLiveSession(SelectedServer.Id) is not null;

    /// <summary>ComboBox adapter: 0 = 本地转发, 1 = 远程转发, 2 = 动态转发 (SOCKS)。</summary>
    public int NewTunnelTypeIndex
    {
        get =>
            NewTunnelType switch
            {
                TunnelType.RemoteForward  => 1,
                TunnelType.DynamicForward => 2,
                _                         => 0
            };
        set
        {
            NewTunnelType = value switch
            {
                1 => TunnelType.RemoteForward,
                2 => TunnelType.DynamicForward,
                _ => TunnelType.LocalForward
            };
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsRemoteTargetVisible));
        }
    }

    /// <summary>动态转发(SOCKS)没有固定目标,隐藏目标主机/端口输入。</summary>
    public bool IsRemoteTargetVisible => NewTunnelType != TunnelType.DynamicForward;

    /// <summary>
    /// 默认转发到"服务器本机":目标主机锁定为 127.0.0.1(从服务器视角,
    /// 用户反馈 #5 —— 填服务器公网 IP 会被服务器自己拒绝)。取消勾选可填内网第三方主机。
    /// </summary>
    public bool ForwardToServerLoopback
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value)
            {
                NewRemoteHost = "127.0.0.1";
            }
            this.RaisePropertyChanged(nameof(IsRemoteHostEditable));
        }
    } = true;

    public bool IsRemoteHostEditable => !ForwardToServerLoopback;

    public TunnelType NewTunnelType
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string NewLocalHost
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1";

    public int NewLocalPort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string NewRemoteHost
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1";

    public int NewRemotePort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string NewTunnelName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string? ErrorMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>非空 = 表单处于"编辑既有隧道"模式;提交按钮显示"保存"。</summary>
    public bool IsEditing => _editingTunnelId is not null;

    public string FormTitle => IsEditing ? "编辑隧道" : "新建隧道";

    public string SubmitButtonText => IsEditing ? "保存" : "创建";

    public ReactiveCommand<Unit, Unit> CreateTunnelCommand { get; }

    public ReactiveCommand<Guid, Unit> StopTunnelCommand { get; }

    public ReactiveCommand<Guid, Unit> StartTunnelCommand { get; }

    public ReactiveCommand<Guid, Unit> DeleteTunnelCommand { get; }

    /// <summary>把某条隧道的配置填回表单进入编辑模式;保存时按新配置重建。</summary>
    public ReactiveCommand<Guid, Unit> EditTunnelCommand { get; }

    /// <summary>取消 button: clears the form and any error, and leaves edit mode.</summary>
    public ReactiveCommand<Unit, Unit> ResetFormCommand { get; }

    /// <summary>收起面板。</summary>
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public void Dispose() => _liveTimer?.Stop();

    /// <summary>面板右上角关闭按钮(设计 B3Rth tunCloseBtn),由宿主收起面板。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>打开面板时调用:刷新服务器列表并(可选)预选某台服务器。</summary>
    public async Task OpenAsync(Guid? preferredProfileId = null)
    {
        await LoadServersAsync();
        SessionProfile? preferred = preferredProfileId is { } id
                                        ? Servers.FirstOrDefault(p => p.Id == id)
                                        : null;
        if (preferred is not null)
        {
            SelectedServer = preferred;
        }
        else if (SelectedServer is null)
        {
            SelectedServer = Servers.FirstOrDefault();
        }
        RefreshServerStatus();
    }

    private async Task LoadServersAsync()
    {
        if (_savedProfilesProvider is null)
        {
            return;
        }
        try
        {
            IReadOnlyList<SessionProfile> profiles = await _savedProfilesProvider();
            Guid? selectedId = SelectedServer?.Id;
            Servers.Clear();
            foreach (SessionProfile profile in profiles)
            {
                Servers.Add(profile);
            }
            if (selectedId is { } sid)
            {
                _selectedServer = Servers.FirstOrDefault(p => p.Id == sid);
            }
            this.RaisePropertyChanged(nameof(SelectedServer));
        }
        catch
        {
            // 列表刷新失败保留旧数据。
        }
    }

    // ---- 服务器/会话解析 ----

    /// <summary>面板持有的、仍然活着的后台会话;掉线的顺手清掉映射。</summary>
    private Guid? ResolveLiveSession(Guid profileId)
    {
        if (!_hostSessions.TryGetValue(profileId, out Guid sessionId))
        {
            return null;
        }
        if (_isSessionAlive?.Invoke(sessionId) == false)
        {
            _hostSessions.Remove(profileId);
            return null;
        }
        return sessionId;
    }

    /// <summary>取得(或后台建立)到指定服务器的隧道连接。</summary>
    private async Task<Guid> EnsureSessionAsync(SessionProfile profile, CancellationToken ct)
    {
        if (ResolveLiveSession(profile.Id) is { } alive)
        {
            return alive;
        }
        if (_backgroundConnector is null)
        {
            throw new InvalidOperationException("后台连接服务未配置。");
        }
        _isConnectingHost = true;
        RefreshServerStatus();
        try
        {
            Guid sessionId = await _backgroundConnector(profile, ct);
            _hostSessions[profile.Id] = sessionId;
            return sessionId;
        }
        finally
        {
            _isConnectingHost = false;
            RefreshServerStatus();
        }
    }

    /// <summary>该服务器最后一条隧道删除后,后台连接没有存在的必要,自动断开。</summary>
    private async Task ReleaseHostIfUnusedAsync(Guid profileId)
    {
        if (!_hostSessions.TryGetValue(profileId, out Guid sessionId))
        {
            return;
        }
        bool hasTunnels;
        try
        {
            hasTunnels = _tunnelService.GetActiveTunnels(sessionId).Items.Any();
        }
        catch
        {
            hasTunnels = false;
        }
        if (hasTunnels)
        {
            return;
        }
        _hostSessions.Remove(profileId);
        if (_sessionDisconnector is not null)
        {
            try
            {
                await _sessionDisconnector(sessionId);
            }
            catch
            {
                // 会话可能已经掉线。
            }
        }
        RefreshServerStatus();
    }

    private void OnServerChanged()
    {
        ErrorMessage = null;
        if (IsEditing)
        {
            ResetForm();
        }
        if (SelectedServer is { } server)
        {
            if (!_itemsByProfile.TryGetValue(server.Id, out ObservableCollection<TunnelItemViewModel>? items))
            {
                items = [];
                _itemsByProfile[server.Id] = items;

                // 找回该服务器后台会话上已建的隧道(面板重开/重建的场景)。
                if (ResolveLiveSession(server.Id) is { } sessionId)
                {
                    try
                    {
                        IReadOnlyList<TunnelInfo> existing = _tunnelService.GetActiveTunnels(sessionId).Items;
                        foreach (TunnelInfo info in existing.OrderBy(t => t.CreatedAt))
                        {
                            items.Add(new(info));
                        }
                    }
                    catch
                    {
                        // 找不回历史隧道不影响面板可用性。
                    }
                }
            }
            Tunnels = items;
        }
        else
        {
            Tunnels = [];
        }
        RefreshServerStatus();
    }

    private void RefreshServerStatus()
    {
        this.RaisePropertyChanged(nameof(ServerStatusText));
        this.RaisePropertyChanged(nameof(IsServerConnected));
    }

    /// <summary>时钟:刷新条目运行时长与服务侧状态;后台会话掉线时把条目标为已停止。</summary>
    private void RefreshLiveState()
    {
        if (SelectedServer is { } server && _hostSessions.TryGetValue(server.Id, out Guid sessionId) && _isSessionAlive?.Invoke(sessionId) == false)
        {
            _hostSessions.Remove(server.Id);
            foreach (TunnelItemViewModel tunnel in Tunnels)
            {
                tunnel.Status = TunnelStatus.Stopped;
            }
        }
        foreach (TunnelItemViewModel tunnel in Tunnels)
        {
            tunnel.RefreshLive();
        }
        RefreshServerStatus();
    }

    // ---- 表单/条目操作 ----

    private void BeginEdit(Guid tunnelId)
    {
        TunnelItemViewModel? item = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        if (item is null)
        {
            return;
        }
        _editingTunnelId = tunnelId;
        NewTunnelName = item.Config.Name;
        NewLocalHost = item.Config.LocalHost;
        NewLocalPort = (int)item.Config.LocalPort;
        NewTunnelTypeIndex = item.TunnelType switch
        {
            TunnelType.RemoteForward  => 1,
            TunnelType.DynamicForward => 2,
            _                         => 0
        };
        ForwardToServerLoopback = item.TunnelType != TunnelType.DynamicForward && item.Config.RemoteHost is "" or "127.0.0.1" or "localhost";
        if (!ForwardToServerLoopback)
        {
            NewRemoteHost = item.Config.RemoteHost;
        }
        NewRemotePort = (int)item.Config.RemotePort;
        ErrorMessage = null;
        RaiseEditingChanged();
    }

    /// <summary>创建新隧道,或保存编辑(旧隧道先停止移除,再按新配置重建)。</summary>
    private async Task SubmitAsync(CancellationToken ct)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            TunnelConfig config = BuildConfig();
            if (_editingTunnelId is { } editingId)
            {
                TunnelItemViewModel? existing = Tunnels.FirstOrDefault(t => t.Id == editingId);
                int index = existing is null ? -1 : Tunnels.IndexOf(existing);
                bool wasActive = existing?.IsActive ?? true;
                await _tunnelService.RemoveTunnelAsync(editingId, ct).ConfigureAwait(true);
                if (wasActive)
                {
                    Guid sessionId = await EnsureSessionAsync(server, ct).ConfigureAwait(true);
                    TunnelInfo result = await CreateOnServiceAsync(sessionId, config, ct).ConfigureAwait(true);
                    ReplaceOrAdd(index, new(result));
                }
                else
                {
                    // 停止状态下编辑:只更新配置,不自动拉起。
                    var stopped = new TunnelInfo
                    {
                        Id = Guid.NewGuid(),
                        Config = config,
                        Status = TunnelStatus.Stopped,
                        SessionId = ResolveLiveSession(server.Id) ?? Guid.Empty,
                        CreatedAt = DateTime.UtcNow,
                        BytesTransferred = 0
                    };
                    ReplaceOrAdd(index, new(stopped));
                }
            }
            else
            {
                Guid sessionId = await EnsureSessionAsync(server, ct).ConfigureAwait(true);
                TunnelInfo result = await CreateOnServiceAsync(sessionId, config, ct).ConfigureAwait(true);
                Tunnels.Add(new(result));
            }
            ResetForm();
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    private void ReplaceOrAdd(int index, TunnelItemViewModel item)
    {
        if (index >= 0 && index < Tunnels.Count)
        {
            Tunnels[index] = item;
        }
        else
        {
            Tunnels.Add(item);
        }
    }

    private TunnelConfig BuildConfig()
    {
        bool isDynamic = NewTunnelType == TunnelType.DynamicForward;
        bool loopback = !isDynamic && ForwardToServerLoopback;
        return new()
        {
            Type = NewTunnelType,
            // 别名可选(设计 B3Rth):留空时用路由描述兜底,列表里始终有可读名称。
            Name = NewTunnelName.Trim(),
            LocalHost = NewLocalHost.Trim(),
            LocalPort = (uint)NewLocalPort,
            RemoteHost = isDynamic
                             ? string.Empty
                             : loopback
                                 ? "127.0.0.1"
                                 : NewRemoteHost.Trim(),
            RemotePort = isDynamic ? 0u : (uint)NewRemotePort
        };
    }

    private Task<TunnelInfo> CreateOnServiceAsync(Guid sessionId, TunnelConfig config, CancellationToken ct) =>
        config.Type switch
        {
            TunnelType.RemoteForward  => _tunnelService.CreateRemoteForwardAsync(sessionId, config, ct),
            TunnelType.DynamicForward => _tunnelService.CreateDynamicForwardAsync(sessionId, config, ct),
            _                         => _tunnelService.CreateLocalForwardAsync(sessionId, config, ct)
        };

    private async Task StopTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            await _tunnelService.StopTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            TunnelItemViewModel? tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel != null)
            {
                tunnel.Status = TunnelStatus.Stopped;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    /// <summary>启动一条已停止的隧道:必要时先后台连上服务器,再按原配置重建转发。</summary>
    private async Task StartTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            TunnelItemViewModel? existing = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (existing == null)
            {
                return;
            }

            // 服务侧还留着停止状态的旧记录,先清掉再重建,避免列表里越积越多。
            await _tunnelService.RemoveTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            Guid sessionId = await EnsureSessionAsync(server, ct).ConfigureAwait(true);
            TunnelInfo result = await CreateOnServiceAsync(sessionId, existing.Config, ct).ConfigureAwait(true);
            int index = Tunnels.IndexOf(existing);
            ReplaceOrAdd(index, new(result));
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    private async Task DeleteTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            TunnelItemViewModel? tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel == null)
            {
                return;
            }
            await _tunnelService.RemoveTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            Tunnels.Remove(tunnel);
            if (_editingTunnelId == tunnelId)
            {
                ResetForm();
            }
            if (SelectedServer is { } server)
            {
                await ReleaseHostIfUnusedAsync(server.Id).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    /// <summary>把服务层异常翻译成用户能看懂的提示。</summary>
    private string FriendlyError(Exception ex) =>
        ex switch
        {
            OperationCanceledException => "已取消。",
            InvalidOperationException when ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                => "与服务器的连接不可用,请重试(将自动重连)。",
            SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => $"本地端口 {NewLocalPort} 已被占用,请换一个端口。",
            _                                                                    => ex.Message
        };

    private void ResetForm()
    {
        _editingTunnelId = null;
        NewTunnelName = string.Empty;
        NewLocalHost = "127.0.0.1";
        NewLocalPort = 0;
        NewRemotePort = 0;
        NewTunnelTypeIndex = 0;
        ForwardToServerLoopback = true; // 同时把目标主机复位为 127.0.0.1
        RaiseEditingChanged();
    }

    private void RaiseEditingChanged()
    {
        this.RaisePropertyChanged(nameof(IsEditing));
        this.RaisePropertyChanged(nameof(FormTitle));
        this.RaisePropertyChanged(nameof(SubmitButtonText));
    }
}
