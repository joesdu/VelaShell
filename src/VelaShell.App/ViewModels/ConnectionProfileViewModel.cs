using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Security;
using ReactiveUI;
using VelaShell.App.Security;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;

namespace VelaShell.App.ViewModels;

/// <summary>“会话分组”下拉的一项;Id 为 null 表示未分组。</summary>
public sealed record GroupOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

public class ConnectionProfileViewModel : ReactiveObject
{
    /// <summary>“未分组”选项/输入的显示名;输入等于该名或留空即保存为未分组。</summary>
    private const string UngroupedName = "未分组";

    private readonly IConnectionWorkflowService? _connectionWorkflowService;

    private readonly Guid _profileId;
    private readonly ISessionRepository? _sessionRepository;
    private AuthMethod _authMethod = AuthMethod.Password;
    private Guid? _groupId;
    private string _host = string.Empty;
    private bool _isKeyAuth;
    private bool _isPasswordAuth = true;
    private Guid? _jumpHostProfileId;
    private string _name = string.Empty;
    private SecureString? _password;
    private int _port = 22;
    private string? _privateKeyPassphrase;
    private string? _privateKeyPath;
    private bool _rememberPassword = true;
    private GroupOption? _selectedGroup;
    private GroupOption? _selectedJumpHost;
    private string _tagsText = string.Empty;
    private string _username = string.Empty;

    public ConnectionProfileViewModel(
        SessionProfile? existing = null,
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISessionRepository? sessionRepository = null,
        int defaultPort = 22,
        string? defaultPrivateKeyPath = null)
    {
        _connectionWorkflowService = connectionWorkflowService;
        _sessionRepository = sessionRepository;
        Groups = [new(null, UngroupedName)];
        _selectedGroup = Groups[0];
        JumpHostOptions = [new(null, "直连(不使用跳板)")];
        _selectedJumpHost = JumpHostOptions[0];

        // 新建连接的默认值(设置 → 常规 → 连接默认值 / 密钥管理 → 默认认证密钥)。
        if (existing is null)
        {
            if (defaultPort is >= 1 and <= 65535)
            {
                _port = defaultPort;
            }
            if (!string.IsNullOrWhiteSpace(defaultPrivateKeyPath))
            {
                _privateKeyPath = defaultPrivateKeyPath;
            }
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
            _jumpHostProfileId = existing.JumpHostProfileId;
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

        // Skip(1):WhenAnyValue 订阅时会立即用当前值(默认“未分组”/“直连”)触发一次,
        // 不跳过会把上面刚从 existing 读入的 _groupId/_jumpHostProfileId 冲成 null——
        // 表现为编辑窗口跳板机回显丢失、保存后配置被静默清掉(用户反馈 #1)。
        this.WhenAnyValue(x => x.SelectedGroup)
            .Skip(1)
            .Subscribe(option =>
            {
                _groupId = option?.Id;
                if (option is not null)
                {
                    GroupText = option.Name;
                }
            });
        this.WhenAnyValue(x => x.SelectedJumpHost)
            .Skip(1)
            .Subscribe(option => _jumpHostProfileId = option?.Id);
        IObservable<bool> canExecute = this.WhenAnyValue(x => x.Host,
            x => x.Username,
            x => x.Port,
            x => x.IsBusy,
            (host, username, port, isBusy) =>
                !isBusy &&
                !string.IsNullOrWhiteSpace(host) &&
                !string.IsNullOrWhiteSpace(username) &&
                port is >= 1 and <= 65535);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canExecute);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canExecute);
        CancelCommand = ReactiveCommand.Create(() => (SessionProfile?)null);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync, canExecute);
        BrowseKeyFileCommand = ReactiveCommand.Create(() => { });
        ToggleAdvancedCommand = ReactiveCommand.Create(() => { IsAdvancedVisible = !IsAdvancedVisible; });
        TogglePasswordVisibilityCommand = ReactiveCommand.Create(() => { ShowPassword = !ShowPassword; });
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

    /// <summary>跳板主机下拉:“直连” + 除自身外的全部已保存配置。</summary>
    public ObservableCollection<GroupOption> JumpHostOptions { get; }

    public GroupOption? SelectedJumpHost
    {
        get => _selectedJumpHost;
        set => this.RaiseAndSetIfChanged(ref _selectedJumpHost, value);
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
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? ErrorMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool? LastTestSucceeded
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
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
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsAdvancedVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
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

    /// <summary>
    /// 分组框的可编辑文本:既可从下拉选已有分组,也可直接输入新分组名;
    /// 保存时由 <see cref="ResolveGroupFromTextAsync" /> 解析(不存在则建组归属)。
    /// </summary>
    public string GroupText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = UngroupedName;

    /// <summary>“连接”按钮关闭弹窗后,由宿主窗口发起连接。</summary>
    public bool ConnectAfterClose { get; private set; }

    public ReactiveCommand<Unit, SessionProfile?> SaveCommand { get; }

    public ReactiveCommand<Unit, SessionProfile?> ConnectCommand { get; }

    public ReactiveCommand<Unit, SessionProfile?> CancelCommand { get; }

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }

    public ReactiveCommand<Unit, Unit> BrowseKeyFileCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleAdvancedCommand { get; }

    public ReactiveCommand<Unit, Unit> TogglePasswordVisibilityCommand { get; }

    /// <summary>
    /// 从仓储加载分组下拉(“未分组” + 全部分组),并选中当前配置的分组;
    /// 同时装填跳板主机下拉(“直连” + 其余已保存配置)。
    /// </summary>
    public async Task LoadGroupsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            List<ServerGroup> groups = await _sessionRepository.GetAllGroupsAsync();
            while (Groups.Count > 1)
            {
                Groups.RemoveAt(Groups.Count - 1);
            }
            foreach (ServerGroup group in groups)
            {
                Groups.Add(new(group.Id, group.Name));
            }
            SelectedGroup = Groups.FirstOrDefault(option => option.Id == _groupId) ?? Groups[0];
        }
        catch
        {
            // 分组加载失败时仍可保存为未分组。
        }
        await LoadJumpHostOptionsAsync();
    }

    /// <summary>跳板主机候选 = 除自身外的全部已保存配置(跳板需已存凭据才能免交互连上)。</summary>
    private async Task LoadJumpHostOptionsAsync()
    {
        if (_sessionRepository is null)
        {
            return;
        }
        try
        {
            List<SessionProfile> profiles = await _sessionRepository.GetAllSessionsAsync();
            while (JumpHostOptions.Count > 1)
            {
                JumpHostOptions.RemoveAt(JumpHostOptions.Count - 1);
            }
            foreach (SessionProfile profile in profiles
                                               .Where(p => p.Id != _profileId)
                                               .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                JumpHostOptions.Add(new(profile.Id, profile.Name));
            }
            SelectedJumpHost = JumpHostOptions.FirstOrDefault(option => option.Id == _jumpHostProfileId) ?? JumpHostOptions[0];
        }
        catch
        {
            // 跳板列表加载失败时仍可按直连保存。
        }
    }

    private async Task<SessionProfile?> SaveAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await ResolveGroupFromTextAsync();
            SessionProfile profile = BuildProfile();
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
        SessionProfile? profile = await SaveAsync();
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
            ConnectionTestResult result = await _connectionWorkflowService.TestConnectionAsync(BuildProfile());
            LastTestSucceeded = result.Success;
            ErrorMessage = result.ErrorMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把分组框文本解析为 GroupId:留空/“未分组”→ null;命中已有分组(不区分
    /// 大小写)→ 其 Id;否则新建分组落库并归属。无仓储(设计时)只回退未分组,
    /// 避免产生指向不存在分组的悬空 Id。
    /// </summary>
    private async Task ResolveGroupFromTextAsync()
    {
        string text = GroupText.Trim();
        if (text.Length == 0 || text == UngroupedName)
        {
            SelectedGroup = Groups[0];
            return;
        }
        GroupOption? existing = Groups.FirstOrDefault(option => option.Id is not null && string.Equals(option.Name, text, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedGroup = existing;
            return;
        }
        if (_sessionRepository is null)
        {
            SelectedGroup = Groups[0];
            return;
        }
        var group = new ServerGroup
        {
            Name = text,
            SortOrder = Groups.Count - 1 // 排在已有分组之后(下拉含“未分组”占位,故 -1)。
        };
        await _sessionRepository.SaveGroupAsync(group);
        var option = new GroupOption(group.Id, group.Name);
        Groups.Add(option);
        SelectedGroup = option;
    }

    private SessionProfile BuildProfile()
    {
        // 显示名称留空时用 user@host 兜底,保证列表/标签页有可读名称。
        string name = string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name.Trim();
        return new()
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
            JumpHostProfileId = _jumpHostProfileId
        };
    }
}
