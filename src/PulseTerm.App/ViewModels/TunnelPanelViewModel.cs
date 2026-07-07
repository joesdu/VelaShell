using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using PulseTerm.Core.Models;
using PulseTerm.Core.Tunnels;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class TunnelPanelViewModel : ReactiveObject
{
    private readonly ITunnelService _tunnelService;
    private readonly Guid _sessionId;
    private readonly Func<Task<IReadOnlyList<SessionProfile>>>? _savedProfilesProvider;

    private TunnelType _newTunnelType;
    private string _newLocalHost = "localhost";
    private int _newLocalPort;
    private string _newRemoteHost = string.Empty;
    private int _newRemotePort;
    private string _newTunnelName = string.Empty;
    private string? _errorMessage;
    private SessionProfile? _selectedTarget;

    public TunnelPanelViewModel(ITunnelService tunnelService, Guid sessionId,
        Func<Task<IReadOnlyList<SessionProfile>>>? savedProfilesProvider = null)
    {
        _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));
        _sessionId = sessionId;
        _savedProfilesProvider = savedProfilesProvider;
        SessionId = sessionId;

        Tunnels = new ObservableCollection<TunnelItemViewModel>();
        SavedTargets = new ObservableCollection<SessionProfile>();

        var canCreate = this.WhenAnyValue(
            vm => vm.NewTunnelName,
            vm => vm.NewLocalHost,
            vm => vm.NewLocalPort,
            vm => vm.NewRemoteHost,
            vm => vm.NewRemotePort,
            (name, localHost, localPort, remoteHost, remotePort) =>
                !string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(localHost) &&
                localPort >= 1 && localPort <= 65535 &&
                !string.IsNullOrWhiteSpace(remoteHost) &&
                remotePort >= 1 && remotePort <= 65535);

        CreateTunnelCommand = ReactiveCommand.CreateFromTask(CreateTunnelAsync, canCreate);
        StopTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StopTunnelAsync);
        StartTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StartTunnelAsync);
        DeleteTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(DeleteTunnelAsync);
        ResetFormCommand = ReactiveCommand.Create(() => { ErrorMessage = null; ResetForm(); });
    }

    /// <summary>Saved SSH profiles offered as forward targets (用户反馈 #4: 远程地址从资源
    /// 管理器已保存的会话中选择). Refreshed each time the panel opens.</summary>
    public ObservableCollection<SessionProfile> SavedTargets { get; }

    /// <summary>Selecting a saved profile fills the remote host (the service port stays manual —
    /// the profile's port is its SSH port, not the forwarded service's).</summary>
    public SessionProfile? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTarget, value);
            if (value is not null)
                NewRemoteHost = value.Host;
        }
    }

    /// <summary>Loads the saved profiles into <see cref="SavedTargets"/> (best-effort).</summary>
    public async Task LoadSavedTargetsAsync()
    {
        if (_savedProfilesProvider is null)
            return;

        try
        {
            var profiles = await _savedProfilesProvider();
            SavedTargets.Clear();
            foreach (var profile in profiles)
                SavedTargets.Add(profile);
        }
        catch
        {
            // The picker is a convenience; the host can still be typed manually.
        }
    }

    /// <summary>ComboBox adapter: 0 = 本地转发 (local forward), 1 = 远程转发 (remote forward).</summary>
    public int NewTunnelTypeIndex
    {
        get => NewTunnelType == TunnelType.RemoteForward ? 1 : 0;
        set
        {
            NewTunnelType = value == 1 ? TunnelType.RemoteForward : TunnelType.LocalForward;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>The SSH session these tunnels belong to.</summary>
    public Guid SessionId { get; }

    public ObservableCollection<TunnelItemViewModel> Tunnels { get; }

    public TunnelType NewTunnelType
    {
        get => _newTunnelType;
        set => this.RaiseAndSetIfChanged(ref _newTunnelType, value);
    }

    public string NewLocalHost
    {
        get => _newLocalHost;
        set => this.RaiseAndSetIfChanged(ref _newLocalHost, value);
    }

    public int NewLocalPort
    {
        get => _newLocalPort;
        set => this.RaiseAndSetIfChanged(ref _newLocalPort, value);
    }

    public string NewRemoteHost
    {
        get => _newRemoteHost;
        set => this.RaiseAndSetIfChanged(ref _newRemoteHost, value);
    }

    public int NewRemotePort
    {
        get => _newRemotePort;
        set => this.RaiseAndSetIfChanged(ref _newRemotePort, value);
    }

    public string NewTunnelName
    {
        get => _newTunnelName;
        set => this.RaiseAndSetIfChanged(ref _newTunnelName, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public bool IsFormValid =>
        !string.IsNullOrWhiteSpace(NewTunnelName) &&
        !string.IsNullOrWhiteSpace(NewLocalHost) &&
        NewLocalPort >= 1 && NewLocalPort <= 65535 &&
        !string.IsNullOrWhiteSpace(NewRemoteHost) &&
        NewRemotePort >= 1 && NewRemotePort <= 65535;

    public ReactiveCommand<Unit, Unit> CreateTunnelCommand { get; }
    public ReactiveCommand<Guid, Unit> StopTunnelCommand { get; }
    public ReactiveCommand<Guid, Unit> StartTunnelCommand { get; }
    public ReactiveCommand<Guid, Unit> DeleteTunnelCommand { get; }

    /// <summary>取消 button: clears the form and any error.</summary>
    public ReactiveCommand<Unit, Unit> ResetFormCommand { get; }

    private async Task CreateTunnelAsync(CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;

            var config = new TunnelConfig
            {
                Type = NewTunnelType,
                Name = NewTunnelName,
                LocalHost = NewLocalHost,
                LocalPort = (uint)NewLocalPort,
                RemoteHost = NewRemoteHost,
                RemotePort = (uint)NewRemotePort
            };

            TunnelInfo result;

            if (NewTunnelType == TunnelType.LocalForward)
            {
                result = await _tunnelService.CreateLocalForwardAsync(_sessionId, config, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await _tunnelService.CreateRemoteForwardAsync(_sessionId, config, ct)
                    .ConfigureAwait(false);
            }

            Tunnels.Add(new TunnelItemViewModel(result));
            ResetForm();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task StopTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            await _tunnelService.StopTunnelAsync(tunnelId, ct).ConfigureAwait(false);

            var tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel != null)
            {
                tunnel.Status = TunnelStatus.Stopped;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task StartTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            var existing = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (existing == null) return;

            var config = new TunnelConfig
            {
                Type = existing.TunnelType,
                Name = existing.Name,
                LocalHost = existing.LocalHost,
                LocalPort = existing.LocalPort,
                RemoteHost = existing.RemoteHost,
                RemotePort = existing.RemotePort
            };

            TunnelInfo result;

            if (existing.TunnelType == TunnelType.LocalForward)
            {
                result = await _tunnelService.CreateLocalForwardAsync(_sessionId, config, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await _tunnelService.CreateRemoteForwardAsync(_sessionId, config, ct)
                    .ConfigureAwait(false);
            }

            var index = Tunnels.IndexOf(existing);
            if (index >= 0)
            {
                Tunnels[index] = new TunnelItemViewModel(result);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task DeleteTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            var tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel == null) return;

            if (tunnel.Status == TunnelStatus.Active)
            {
                await _tunnelService.StopTunnelAsync(tunnelId, ct).ConfigureAwait(false);
            }

            Tunnels.Remove(tunnel);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void ResetForm()
    {
        NewTunnelName = string.Empty;
        NewLocalHost = "localhost";
        NewLocalPort = 0;
        NewRemoteHost = string.Empty;
        NewRemotePort = 0;
        SelectedTarget = null;
        NewTunnelTypeIndex = 0;
    }
}
