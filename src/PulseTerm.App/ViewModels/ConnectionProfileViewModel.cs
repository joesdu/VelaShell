using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Security;
using System.Threading.Tasks;
using PulseTerm.App.Security;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

/// <summary>“会话分组”下拉的一项;Id 为 null 表示未分组。</summary>
public sealed record GroupOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

public class ConnectionProfileViewModel : ReactiveObject
{
    private readonly IConnectionWorkflowService? _connectionWorkflowService;
    private readonly ISessionRepository? _sessionRepository;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private AuthMethod _authMethod = AuthMethod.Password;
    private SecureString? _password;
    private string? _privateKeyPath;
    private string? _privateKeyPassphrase;
    private Guid? _groupId;
    private bool _isPasswordAuth = true;
    private bool _isKeyAuth;
    private bool _isBusy;
    private string? _errorMessage;
    private bool? _lastTestSucceeded;
    private bool _rememberPassword = true;
    private bool _showPassword;
    private bool _isAdvancedVisible;
    private string _tagsText = string.Empty;
    private GroupOption? _selectedGroup;

    private readonly Guid _profileId;

    public ConnectionProfileViewModel(
        SessionProfile? existing = null,
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISessionRepository? sessionRepository = null,
        int defaultPort = 22,
        string? defaultPrivateKeyPath = null)
    {
        _connectionWorkflowService = connectionWorkflowService;
        _sessionRepository = sessionRepository;

        Groups = new ObservableCollection<GroupOption> { new(null, "未分组") };
        _selectedGroup = Groups[0];

        // 新建连接的默认值(设置 → 常规 → 连接默认值 / 密钥管理 → 默认认证密钥)。
        if (existing is null)
        {
            if (defaultPort is >= 1 and <= 65535)
                _port = defaultPort;
            if (!string.IsNullOrWhiteSpace(defaultPrivateKeyPath))
                _privateKeyPath = defaultPrivateKeyPath;
        }

        if (existing != null)
        {
            _profileId = existing.Id;
            _name = existing.Name;
            _host = existing.Host;
            _port = existing.Port;
            _username = existing.Username;
            _authMethod = existing.AuthMethod;
            _password = SecureStringConvert.FromPlaintext(existing.Password);
            _privateKeyPath = existing.PrivateKeyPath;
            _privateKeyPassphrase = existing.PrivateKeyPassphrase;
            _groupId = existing.GroupId;
            _isPasswordAuth = existing.AuthMethod == AuthMethod.Password;
            _isKeyAuth = existing.AuthMethod == AuthMethod.PrivateKey;
            _rememberPassword = existing.RememberPassword;
            _tagsText = string.Join(", ", existing.Tags);
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

        this.WhenAnyValue(x => x.SelectedGroup)
            .Subscribe(option => _groupId = option?.Id);

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
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canExecute);
        CancelCommand = ReactiveCommand.Create(() => (SessionProfile?)null);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync, canExecute);
        BrowseKeyFileCommand = ReactiveCommand.Create(() => { });
        ToggleAdvancedCommand = ReactiveCommand.Create(() => { IsAdvancedVisible = !IsAdvancedVisible; });
        TogglePasswordVisibilityCommand = ReactiveCommand.Create(() => { ShowPassword = !ShowPassword; });
    }

    /// <summary>从仓储加载分组下拉(“未分组” + 全部分组),并选中当前配置的分组。</summary>
    public async Task LoadGroupsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }

        try
        {
            var groups = await _sessionRepository.GetAllGroupsAsync();
            while (Groups.Count > 1)
            {
                Groups.RemoveAt(Groups.Count - 1);
            }

            foreach (var group in groups)
            {
                Groups.Add(new GroupOption(group.Id, group.Name));
            }

            SelectedGroup = Groups.FirstOrDefault(option => option.Id == _groupId) ?? Groups[0];
        }
        catch
        {
            // 分组加载失败时仍可保存为未分组。
        }
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
        set
        {
            this.RaiseAndSetIfChanged(ref _authMethod, value);
            this.RaisePropertyChanged(nameof(AuthMethodIndex));
        }
    }

    /// <summary>认证方式下拉的索引(0=密码认证,1=密钥认证)。</summary>
    public int AuthMethodIndex
    {
        get => AuthMethod == AuthMethod.PrivateKey ? 1 : 0;
        set => AuthMethod = value == 1 ? AuthMethod.PrivateKey : AuthMethod.Password;
    }

    /// <summary>密码以 SecureString 承载;ASCII 过滤由 <c>SecurePasswordBox</c> 输入行为负责。</summary>
    public SecureString? Password
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
        private set
        {
            this.RaiseAndSetIfChanged(ref _lastTestSucceeded, value);
            this.RaisePropertyChanged(nameof(ShowTestSuccess));
        }
    }

    /// <summary>“连接测试成功”提示可见性。</summary>
    public bool ShowTestSuccess => LastTestSucceeded == true;

    /// <summary>记住密码(AES-256 加密存储);未勾选时密码只用于本次连接。</summary>
    public bool RememberPassword
    {
        get => _rememberPassword;
        set => this.RaiseAndSetIfChanged(ref _rememberPassword, value);
    }

    public bool ShowPassword
    {
        get => _showPassword;
        set => this.RaiseAndSetIfChanged(ref _showPassword, value);
    }

    public bool IsAdvancedVisible
    {
        get => _isAdvancedVisible;
        set => this.RaiseAndSetIfChanged(ref _isAdvancedVisible, value);
    }

    /// <summary>标签,逗号分隔(高级选项)。</summary>
    public string TagsText
    {
        get => _tagsText;
        set => this.RaiseAndSetIfChanged(ref _tagsText, value);
    }

    public ObservableCollection<GroupOption> Groups { get; }

    public GroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set => this.RaiseAndSetIfChanged(ref _selectedGroup, value);
    }

    /// <summary>“连接”按钮关闭弹窗后,由宿主窗口发起连接。</summary>
    public bool ConnectAfterClose { get; private set; }

    public ReactiveCommand<Unit, SessionProfile?> SaveCommand { get; }
    public ReactiveCommand<Unit, SessionProfile?> ConnectCommand { get; }
    public ReactiveCommand<Unit, SessionProfile?> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseKeyFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAdvancedCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePasswordVisibilityCommand { get; }

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

            return await _connectionWorkflowService.SaveProfileAsync(profile);
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

    /// <summary>“连接”:保存配置并请求宿主窗口在弹窗关闭后立即连接。</summary>
    private async Task<SessionProfile?> ConnectAsync()
    {
        var profile = await SaveAsync();
        if (profile is not null)
        {
            ConnectAfterClose = true;
        }

        return profile;
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

            var result = await _connectionWorkflowService.TestConnectionAsync(BuildProfile());
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
        // 显示名称留空时用 user@host 兜底,保证列表/标签页有可读名称。
        var name = string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name.Trim();

        return new SessionProfile
        {
            Id = _profileId,
            Name = name,
            Host = Host.Trim(),
            Port = Port,
            Username = Username.Trim(),
            AuthMethod = AuthMethod,
            Password = SecureStringConvert.ToPlaintext(Password),
            RememberPassword = RememberPassword,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            GroupId = GroupId,
            Tags = TagsText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        };
    }
}
