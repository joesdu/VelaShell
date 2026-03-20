using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class ConnectionProfileViewModel : ReactiveObject
{
    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private AuthMethod _authMethod = AuthMethod.Password;
    private string? _password;
    private string? _privateKeyPath;
    private string? _privateKeyPassphrase;
    private Guid? _groupId;
    private bool _isPasswordAuth = true;
    private bool _isKeyAuth;
    private bool _isBusy;
    private string? _errorMessage;
    private bool? _lastTestSucceeded;

    private readonly Guid _profileId;

    public ConnectionProfileViewModel(SessionProfile? existing = null, IConnectionWorkflowService? connectionWorkflowService = null)
    {
        _connectionWorkflowService = connectionWorkflowService;

        if (existing != null)
        {
            _profileId = existing.Id;
            _name = existing.Name;
            _host = existing.Host;
            _port = existing.Port;
            _username = existing.Username;
            _authMethod = existing.AuthMethod;
            _password = existing.Password;
            _privateKeyPath = existing.PrivateKeyPath;
            _privateKeyPassphrase = existing.PrivateKeyPassphrase;
            _groupId = existing.GroupId;
            _isPasswordAuth = existing.AuthMethod == AuthMethod.Password;
            _isKeyAuth = existing.AuthMethod == AuthMethod.PrivateKey;
        }
        else
        {
            _profileId = Guid.NewGuid();
        }

        this.WhenAnyValue(x => x.AuthMethod)
            .Subscribe(method =>
            {
                IsPasswordAuth = method == AuthMethod.Password;
                IsKeyAuth = method == AuthMethod.PrivateKey;
            });

        var canExecute = this.WhenAnyValue(
            x => x.Host,
            x => x.Username,
            x => x.Port,
            x => x.IsBusy,
            (host, username, port, isBusy) =>
                !isBusy &&
                !string.IsNullOrWhiteSpace(host) &&
                !string.IsNullOrWhiteSpace(username) &&
                port >= 1 && port <= 65535);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canExecute);
        CancelCommand = ReactiveCommand.Create(() => (SessionProfile?)null);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync, canExecute);
        BrowseKeyFileCommand = ReactiveCommand.Create(() => { });
    }

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => this.RaiseAndSetIfChanged(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    public AuthMethod AuthMethod
    {
        get => _authMethod;
        set => this.RaiseAndSetIfChanged(ref _authMethod, value);
    }

    public string? Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    public string? PrivateKeyPath
    {
        get => _privateKeyPath;
        set => this.RaiseAndSetIfChanged(ref _privateKeyPath, value);
    }

    public string? PrivateKeyPassphrase
    {
        get => _privateKeyPassphrase;
        set => this.RaiseAndSetIfChanged(ref _privateKeyPassphrase, value);
    }

    public Guid? GroupId
    {
        get => _groupId;
        set => this.RaiseAndSetIfChanged(ref _groupId, value);
    }

    public bool IsPasswordAuth
    {
        get => _isPasswordAuth;
        private set => this.RaiseAndSetIfChanged(ref _isPasswordAuth, value);
    }

    public bool IsKeyAuth
    {
        get => _isKeyAuth;
        private set => this.RaiseAndSetIfChanged(ref _isKeyAuth, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public bool? LastTestSucceeded
    {
        get => _lastTestSucceeded;
        private set => this.RaiseAndSetIfChanged(ref _lastTestSucceeded, value);
    }

    public ReactiveCommand<Unit, SessionProfile?> SaveCommand { get; }
    public ReactiveCommand<Unit, SessionProfile?> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseKeyFileCommand { get; }

    private async Task<SessionProfile?> SaveAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var profile = BuildProfile();
            if (_connectionWorkflowService is null)
            {
                return profile;
            }

            return await _connectionWorkflowService.SaveProfileAsync(profile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestConnectionAsync()
    {
        if (_connectionWorkflowService is null)
        {
            LastTestSucceeded = null;
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var result = await _connectionWorkflowService.TestConnectionAsync(BuildProfile()).ConfigureAwait(false);
            LastTestSucceeded = result.Success;
            ErrorMessage = result.ErrorMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private SessionProfile BuildProfile()
    {
        return new SessionProfile
        {
            Id = _profileId,
            Name = Name,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthMethod = AuthMethod,
            Password = Password,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            GroupId = GroupId
        };
    }
}
