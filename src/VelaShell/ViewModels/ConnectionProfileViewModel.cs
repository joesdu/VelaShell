using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.Infrastructure.Ssh;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>“会话分组”下拉的一项;Id 为 null 表示未分组。</summary>
public sealed record GroupOption(Guid? Id, string Name)
{
    /// <summary>下拉项以分组名称展示。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 「凭据来源」下拉的一项(#550):在本配置中填写,或某条共享凭据。
/// </summary>
/// <param name="Reference">引用;在本配置中填写时为 null。</param>
/// <param name="Display">下拉里显示的文字。</param>
/// <param name="Credential">指向的共享凭据;在本配置中填写,或引用已失效时为 null。</param>
public sealed record CredentialSourceOption(CredentialReference? Reference, string Display, SharedCredential? Credential)
{
    /// <summary>「在本配置中填写」。</summary>
    public static CredentialSourceOption Inline() => new(null, Strings.Get("Profile_CredSourceInline"), null);

    /// <summary>一条共享凭据:名称后面跟着用户名,下拉里一眼能分出同名的两条。</summary>
    public static CredentialSourceOption Shared(SharedCredential credential) =>
        new(CredentialReference.ForShared(credential.Id),
            credential.Username.Length > 0 ? $"{credential.Name} ({credential.Username})" : credential.Name,
            credential);

    /// <summary>引用指向的凭据已不在了:照样摆出来,让用户看见并改选,而不是悄悄落回「在本配置中填写」。</summary>
    public static CredentialSourceOption Missing(CredentialReference reference) =>
        new(reference, Strings.Get("Profile_CredSourceMissing"), null);

    /// <summary>下拉项以显示文字展示。</summary>
    public override string ToString() => Display;
}

/// <summary>新建/编辑连接配置对话框的视图模型:承载表单字段、认证方式切换、分组与跳板主机选择,并提供保存、连接、测试等命令。</summary>
public partial class ConnectionProfileViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// “未分组”选项/输入的显示名;输入等于该名或留空即保存为未分组。
    /// 属性而非 static readonly:后者按类型加载时的语言冻结,换语言后新开的
    /// 对话框仍显示旧语言;VM 每次打开对话框新建,属性取值即当前语言。
    /// </summary>
    private static string UngroupedName => Strings.Get("Msg_Ungrouped");

    private readonly IConnectionWorkflowService? _connectionWorkflowService;

    /// <summary>复制成功回执(「已复制」)的停留时长。</summary>
    private static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromSeconds(2);

    /// <summary>回执退回的定时器句柄;连点时先取消上一个。</summary>
    private IDisposable? _copyFeedbackReset;

    private readonly Guid _profileId;

    // ---- 对话框里没有、但属于这条配置的状态:编辑时原样带回 ----
    // 保存是按界面字段新建一条配置再整条覆盖落盘,不带回来就等于清掉 ——
    // 改个端口,置顶没了、这条连接从「最近连接」里掉到最后。
    private readonly bool _isPinned;
    private readonly DateTime? _lastConnectedAt;
    private readonly List<Guid>? _autoStartTunnelIds;
    // 仓储更新时本就保留库里的收藏;这里再带上,是让保存交回去的那份配置(调用方拿它刷新缓存、接着连)也不缺收藏。
    private readonly List<RemoteDirectoryBookmark> _directoryBookmarks = [];

    private readonly ISessionRepository? _sessionRepository;

    // ---- 凭据来源(#550) ----
    private readonly ISharedCredentialRepository? _sharedCredentials;

    /// <summary>当前的凭据引用;null = 在本配置中填写。下拉还没加载出来时也以它为准。</summary>
    private CredentialReference? _credentialSource;
    private CredentialSourceOption _selectedCredentialSource;
    private AuthMethod _authMethod = AuthMethod.Password;
    private string? _certificatePath;
    private SystemKeyReference? _keyStoreKey;
    private readonly ISystemKeyStore? _systemKeyStore;
    private bool _systemKeysLoaded;
    private ConnectionType _connectionType = ConnectionType.SSH;
    private FtpEncryptionMode _ftpEncryption = FtpEncryptionMode.Auto;
    private bool _ftpPassive = true;
    private bool _ftpAnonymous;
    private readonly string? _ftpTrustedThumbprint;
    private readonly int _ftpMaxConnections = new FtpSettings().MaxConnections;
    private string? _ftpInitialRemotePath;
    private int? _ftpServerUtcOffsetMinutes;
    private Guid? _groupId;
    private string _host = string.Empty;
    private bool _isCertAuth;
    private bool _isKeyAuth;
    private bool _isPasswordAuth = true;
    private Guid? _jumpHostProfileId;
    private string _name = string.Empty;
    private string? _notes;
    private string? _tabIcon;
    private SecureString? _password;
    private int _port = 22;
    private string? _privateKeyPassphrase;
    private string? _privateKeyPath;
    private string? _postAuthCommand;
    private int _postAuthCommandDelaySeconds = new SessionProfile().PostAuthCommandDelaySeconds;
    private bool _rememberPassword = true;
    private GroupOption? _selectedGroup;
    private GroupOption? _selectedJumpHost;
    private string _tagsText = string.Empty;
    private string _username = string.Empty;

    // ---- 会话级终端覆盖项(F-06);null / -1 = 跟随全局 ----
    private string? _overrideEncoding;
    private string? _overrideTerminalType;
    private string? _overrideTabColor;
    private string? _overrideStartupDirectory;
    private int _overrideKeepAliveSeconds = -1;
    private int _antiIdleSeconds;

    // ---- SSH 协议层可选能力(压缩 / agent 转发 / X11 转发);默认全关 ----
    private bool _sshCompression;
    private bool _sshObscureKeystrokeTiming;
    private bool _sshAgentForwarding;
    private bool _sshAgentForwardRestrict;
    private bool _sshAgentForwardConfirm;
    private string? _sshAgentEndpoint;
    private readonly List<string> _savedAgentForwardKeys = [];
    private bool _agentForwardKeysLoaded;
    private readonly ISshKeyService? _keyService;
    private bool _sshX11Forwarding;
    private string? _sshX11Display;
    private bool _sshX11Trusted = new SshSessionOptions().X11Trusted;
    private bool _sshLegacyAlgorithms;
    private bool _sshCustomAlgorithms;
    private string? _sshKexAlgorithms;
    private string? _sshHostKeyAlgorithms;
    private string? _sshCiphers;
    private string? _sshMacs;

    // ---- 插件协议 ----
    private readonly PluginProtocolRegistry? _protocolRegistry;
    private string? _pluginProtocolId;
    private PluginConnectionForm? _pluginForm;
    // 套用变体**之前**的那份。变体是随字段值实时切换的,每次都要从这份原始形态重新套,
    // 否则连着切两次方言就会把上一次的改写叠在下一次上面(SQLite 的"数据库文件"标签
    // 会粘在随后选中的 MySQL 上)。
    private PluginConnectionForm? _pluginBaseForm;

    /// <summary>编辑既有配置时读入的插件设置;表单还没渲染出来就保存的话原样带回。</summary>
    private Dictionary<string, string>? _pluginStored;
    private Dictionary<string, string>? _pluginStoredSecrets;

    /// <summary>
    /// 最近一次**真正渲染过表单**的协议 id。「换没换协议」必须以它为准而不是
    /// <see cref="PluginProtocolId" /> —— 后者会被切回内建协议时置 null。
    /// </summary>
    private string? _loadedProtocolId;

    /// <summary>
    /// 「已保存的 SSH 配置」下拉的候选,随跳板机候选一起加载。
    /// 空列表也要有第一项("不经跳板机"),否则那个下拉在没有任何 SSH 配置时是空的,
    /// 用户分不清"没得选"与"还没加载"。
    /// </summary>
    private List<PluginSessionChoice> _sshSessionChoices = [];

    /// <summary>创建视图模型;传入 <paramref name="existing" /> 时进入编辑模式回显字段,否则新建并应用默认端口/默认密钥路径。</summary>
    public ConnectionProfileViewModel(
        SessionProfile? existing = null,
        IConnectionWorkflowService? connectionWorkflowService = null,
        ISessionRepository? sessionRepository = null,
        int defaultPort = 22,
        string? defaultPrivateKeyPath = null,
        PluginProtocolRegistry? protocolRegistry = null,
        ISshKeyService? keyService = null,
        ISharedCredentialRepository? sharedCredentials = null,
        ISystemKeyStore? systemKeyStore = null)
    {
        // 最先定:编辑态下构造函数末尾那次表单装载可能同步跑完(插件已注册时没有真正的让出点),
        // 装载完成时定步骤(T1)要读它。
        IsEditing = existing is not null;
        _protocolRegistry = protocolRegistry;
        _systemKeyStore = systemKeyStore;
        _keyService = keyService;
        _sharedCredentials = sharedCredentials;
        CredentialSources = [CredentialSourceOption.Inline()];
        _selectedCredentialSource = CredentialSources[0];
        _connectionWorkflowService = connectionWorkflowService;
        _sessionRepository = sessionRepository;
        Groups = [new(null, UngroupedName)];
        _selectedGroup = Groups[0];
        JumpHostOptions = [new(null, Strings.Get("Msg_DirectConnection"))];
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
            _isPinned = existing.IsPinned;
            _lastConnectedAt = existing.LastConnectedAt;
            _autoStartTunnelIds = existing.AutoStartTunnelIds;
            _directoryBookmarks = [.. (existing.DirectoryBookmarks ?? []).Select(static bookmark => bookmark.Clone())];
            _connectionType = existing.ConnectionType;
            _name = existing.Name;
            _host = existing.Host;
            _port = existing.Port;
            _username = existing.Username;
            _authMethod = existing.AuthMethod;
            _password = SecureStringConvert.FromPlaintext(existing.Password);
            _privateKeyPath = existing.PrivateKeyPath;
            _privateKeyPassphrase = existing.PrivateKeyPassphrase;
            _certificatePath = existing.CertificatePath;
            _keyStoreKey = existing.KeyStoreKey;
            _credentialSource = existing.CredentialSource;
            _groupId = existing.GroupId;
            _isPasswordAuth = existing.AuthMethod == AuthMethod.Password;
            _isKeyAuth = existing.AuthMethod == AuthMethod.PrivateKey;
            _isCertAuth = existing.AuthMethod == AuthMethod.Certificate;
            _rememberPassword = existing.RememberPassword;
            _tagsText = string.Join(", ", existing.Tags);
            _notes = existing.Notes;
            _jumpHostProfileId = existing.JumpHostProfileId;
            _postAuthCommand = existing.PostAuthCommand;
            _postAuthCommandDelaySeconds = existing.PostAuthCommandDelaySeconds;
            if (existing.Ftp is { } ftp)
            {
                _ftpEncryption = ftp.EncryptionMode;
                _ftpPassive = ftp.DataConnectionMode == FtpDataConnectionMode.Passive;
                _ftpAnonymous = ftp.Anonymous;
                _ftpTrustedThumbprint = ftp.TrustedCertificateThumbprint;
                _ftpMaxConnections = ftp.MaxConnections;
                _ftpInitialRemotePath = ftp.InitialRemotePath;
                _ftpServerUtcOffsetMinutes = ftp.ServerUtcOffsetMinutes;
            }
            _pluginProtocolId = existing.PluginProtocolId;
            _pluginStored = existing.PluginSettings;
            _pluginStoredSecrets = existing.PluginSecrets;
            if (existing.Terminal is { } overrides)
            {
                _overrideEncoding = overrides.Encoding;
                _overrideTerminalType = overrides.TerminalType;
                _overrideTabColor = overrides.TabColor;
                _tabIcon = SessionTabIcons.Normalize(overrides.TabIcon);
                _overrideStartupDirectory = overrides.StartupDirectory;
                _overrideKeepAliveSeconds = overrides.KeepAliveSeconds ?? -1;
                _antiIdleSeconds = overrides.AntiIdleSeconds ?? 0;
            }
            if (existing.Ssh is { } ssh)
            {
                _sshCompression = ssh.Compression;
                _sshObscureKeystrokeTiming = ssh.ObscureKeystrokeTiming;
                _sshAgentForwarding = ssh.AgentForwarding;
                _sshAgentForwardRestrict = ssh.AgentForwardKeys is not null;
                _savedAgentForwardKeys = ssh.AgentForwardKeys is { } keys ? [.. keys] : [];
                _sshAgentForwardConfirm = ssh.AgentForwardConfirm;
                _sshAgentEndpoint = ssh.AgentEndpoint;
                _sshX11Forwarding = ssh.X11Forwarding;
                _sshX11Display = ssh.X11Display;
                _sshX11Trusted = ssh.X11Trusted;
                _sshLegacyAlgorithms = ssh.LegacyAlgorithms;
                _sshCustomAlgorithms = ssh.HasCustomAlgorithms;
                _sshKexAlgorithms = ssh.KexAlgorithms;
                _sshHostKeyAlgorithms = ssh.HostKeyAlgorithms;
                _sshCiphers = ssh.Ciphers;
                _sshMacs = ssh.Macs;
            }
        }
        else
        {
            _profileId = Guid.NewGuid();
        }
        // 编辑一条已经限定了密钥的配置:清单一打开就要摆出来(勾选状态来自存过的那些)。
        if (_sshAgentForwarding && _sshAgentForwardRestrict)
        {
            _ = LoadAgentForwardKeysAsync();
        }
        this.WhenAnyValue(x => x.AuthMethod)
            .Subscribe(method =>
            {
                IsPasswordAuth = method == AuthMethod.Password;
                IsKeyAuth = method == AuthMethod.PrivateKey;
                IsCertAuth = method == AuthMethod.Certificate;
                // 系统密钥库与 Tailscale 一样不走共享凭据:引用的材料在这台电脑的密钥库里,共享不了。
                if (method is AuthMethod.Tailscale or AuthMethod.KeyStore)
                {
                    _credentialSource = null;
                    _selectedCredentialSource = CredentialSources[0];
                    this.RaisePropertyChanged(nameof(SelectedCredentialSource));
                }
                this.RaisePropertyChanged(nameof(IsTailscaleAuth));
                this.RaisePropertyChanged(nameof(ShowsPrivateKeyFields));
                this.RaisePropertyChanged(nameof(IsAgentAuth));
                this.RaisePropertyChanged(nameof(IsKeyStoreAuth));
                this.RaisePropertyChanged(nameof(ShowKeyStoreFields));
                this.RaisePropertyChanged(nameof(ShowKeyStoreAuthOption));
                if (method == AuthMethod.KeyStore)
                {
                    _ = LoadSystemKeysAsync();
                }
                // 口令框也跟着认证方式显隐 —— 原先漏发了这一条,切到私钥页时口令框会留在原地。
                this.RaisePropertyChanged(nameof(ShowPasswordField));
                RaiseCredentialSourceDependents();
            });

        // Skip(1):WhenAnyValue 订阅时会立即用当前值(默认“未分组”/“直连”)触发一次,
        // 不跳过会把上面刚从 existing 读入的 _groupId/_jumpHostProfileId 冲成 null——
        // 表现为编辑窗口跳板机回显丢失、保存后配置被静默清掉(#1)。
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
        // 「必须填用户名」这条并非对所有协议成立:FTP 匿名登录,以及声明了
        // AnonymousAccess 的插件协议(如 S3 的公开只读桶)都不需要。
        // 保存/连接按钮不能因为这个永远灰着(串口调研里踩过同一个坑)。
        IObservable<bool> canExecute = this.WhenAnyValue(x => x.Host,
            x => x.Username,
            x => x.Port,
            x => x.IsBusy,
            x => x.FtpAnonymous,
            x => x.ConnectionType,
            x => x.AllowsAnonymous,
            x => x.PluginUnavailable,
            x => x.SshAlgorithmsError,
            x => x.CredentialSourceError,
            x => x.SharedCredentialProvidesUsername,
            (host, username, port, isBusy, anonymous, type, pluginAnonymous, pluginUnavailable, algorithmsError,
             credentialSourceError, usernameFromCredential) =>
                !isBusy &&
                // 算法清单写错了连不上,存下来也只是把错误推迟到连接那一刻。
                algorithmsError is null &&
                // 选了一条用不上的共享凭据(FTP 配了私钥凭据):存下去就是一条注定连不上的配置。
                credentialSourceError is null &&
                IsHostWellFormed(host) &&
                (!string.IsNullOrWhiteSpace(username) ||
                 // 共享凭据自带用户名时,连接自己可以不填(#550)。
                 usernameFromCredential ||
                 (type == ConnectionType.FTP && anonymous) ||
                 // 插件不可用时也放行:否则一条「用户名为空的 S3 配置」在禁用插件后
                 // 保存/连接/测试三个按钮同时灰死,连改个名字都存不下去。
                 (type == ConnectionType.Plugin && (pluginAnonymous || pluginUnavailable))) &&
                port is >= 1 and <= 65535);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canExecute);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canExecute);
        CancelCommand = ReactiveCommand.Create(() => (SessionProfile?)null);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync, canExecute);
        AllowAlgorithmsCommand = ReactiveCommand.CreateFromTask(
            AllowAlgorithmsAndRetestAsync,
            this.WhenAnyValue(x => x.AlgorithmRemedy, x => x.IsBusy, (remedy, busy) => remedy is not null && !busy));
        CopyErrorCommand = ReactiveCommand.CreateFromTask(CopyErrorAsync, this.WhenAnyValue(x => x.ErrorMessage, message => !string.IsNullOrWhiteSpace(message)));
        SelectConnectionTypeCommand = ReactiveCommand.Create<ConnectionType>(SelectConnectionType);
        SelectPluginProtocolCommand = ReactiveCommand.CreateFromTask<string>(SelectPluginProtocolAsync);
        // 刷新期间不让再点:枚举串口要读注册表 / 扫 sysfs,连点会把候选项列表反复重建。
        RefreshHostChoicesCommand = ReactiveCommand.CreateFromTask(
            RefreshHostChoicesAsync,
            this.WhenAnyValue(x => x.IsHostRefreshing, refreshing => !refreshing));
        // 「选类型」这一步的命令(见 ConnectionProfileViewModel.TypePicker.cs)。
        // 方法体里另有同样的守卫:canExecute 只管按钮灰不灰,直接 Execute 不看它。
        SelectTypeCardCommand = ReactiveCommand.Create<string>(
            SelectTypeCard,
            this.WhenAnyValue(x => x.IsPickingType));
        ConfirmTypeCardCommand = ReactiveCommand.Create<string>(
            ConfirmTypeCard,
            this.WhenAnyValue(x => x.IsPickingType, x => x.IsBusy, (picking, busy) => picking && !busy));
        NextCommand = ReactiveCommand.Create(
            ConfirmTypeStep,
            this.WhenAnyValue(x => x.IsPickingType, x => x.SelectedTypeValue, x => x.IsBusy,
                (picking, selected, busy) => picking && selected is not null && !busy));
        BackCommand = ReactiveCommand.Create(
            () =>
            {
                if (ShowBackButton)
                {
                    ReturnToTypeStep();
                }
            },
            this.WhenAnyValue(x => x.ShowBackButton, x => x.IsBusy, (show, busy) => show && !busy));
        ChangeTypeCommand = ReactiveCommand.Create(
            () =>
            {
                if (ShowChangeTypeLink)
                {
                    ReturnToTypeStep();
                }
            },
            this.WhenAnyValue(x => x.ShowChangeTypeLink, x => x.IsBusy, (show, busy) => show && !busy));
        // 表单是插件异步装载出来的,字段进集合的那一刻就得带上当前所在的分页 ——
        // 只在切换分页时下发是不够的(先渲染后切页,「常规」页会把高级字段全画出来)。
        // 必须在下面那句可能触发装载的 SelectPluginProtocolAsync 之前接线。
        // 订阅也挂在这里,而不是只在 SelectPluginProtocolAsync 的建行处:字段进集合有
        // 两条路(插件装载后成批加,以及测试/将来的代码直接 Add),漏掉任一条就会出现
        // "改了部署形态,主节点名不出现"这种只在一条路上复现的怪毛病。
        PluginFields.CollectionChanged += OnPluginFieldsChanged;
        PluginProtocols.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasPluginProtocols));
            this.RaisePropertyChanged(nameof(PluginSectionTitle));
        };
        // 页签上的「有非默认值」圆点、页脚的连接目标预览都是从别的属性推出来的:
        // 逐个 setter 补发通知会漏(已经漏过一回标签),统一在这里按属性名转发。
        PropertyChanged += OnOwnPropertyChanged;
        LoadPluginProtocols();
        // 页签不是一次性快照:插件发现跑在后台线程,对话框可能先于它打开;
        // 插件管理器又是非模态的,开着对话框也能启用/禁用插件。
        _protocolRegistry?.Changed += OnProtocolsChanged;
        if (_connectionType == ConnectionType.Plugin && _pluginProtocolId is { Length: > 0 } existingProtocol)
        {
            // 编辑既有插件协议配置:进对话框就把表单渲染出来(会触发该插件的惰性激活)。
            // 不等它(构造函数不能等),但把这次装载留在 PendingPluginLoad 上,用例据此等装载完成。
            PendingPluginLoad = SelectPluginProtocolAsync(existingProtocol);
        }
        BrowseKeyFileCommand = ReactiveCommand.Create(() => { });
        BrowseCertificateFileCommand = ReactiveCommand.Create(() => { });
        SelectSectionCommand = ReactiveCommand.Create<ConnectionProfileSection>(section => SelectedSection = section);
        SelectPluginPageCommand = ReactiveCommand.Create<string>(SelectPluginPage);
        SelectAuthMethodCommand = ReactiveCommand.Create<AuthMethod>(method => AuthMethod = method);
        NewSharedCredentialCommand = ReactiveCommand.CreateFromTask(NewSharedCredentialAsync);
        OpenPluginManagerCommand = ReactiveCommand.Create(() => OpenPluginManager?.Invoke());
        TogglePasswordVisibilityCommand = ReactiveCommand.Create(() => { ShowPassword = !ShowPassword; });
        RefreshSystemKeysCommand = ReactiveCommand.CreateFromTask(
            () => LoadSystemKeysAsync(force: true), this.WhenAnyValue(x => x.IsLoadingSystemKeys, loading => !loading));
        CopySystemKeyPublicKeyCommand = ReactiveCommand.CreateFromTask(
            CopySystemKeyPublicKeyAsync, this.WhenAnyValue(x => x.SelectedSystemKey, key => key?.PublicKeyLine is { Length: > 0 }));
    }

    /// <summary>连接显示名称;留空时保存时以 user@host 兜底。</summary>
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    /// <summary>
    /// 当前连接协议;SSH 为默认值,SFTP 复用 SSH 认证但不创建终端,FTP 走独立的 FTP/FTPS 栈。
    /// 未知值降级为 SSH(白名单同 <see cref="SessionProfile.ConnectionType" />)。
    /// </summary>
    public ConnectionType ConnectionType
    {
        get => _connectionType;
        private set
        {
            ConnectionType normalized = Enum.IsDefined(value) ? value : ConnectionType.SSH;
            if (_connectionType == normalized)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _connectionType, normalized);
            this.RaisePropertyChanged(nameof(IsSshSelected));
            this.RaisePropertyChanged(nameof(IsSftpSelected));
            this.RaisePropertyChanged(nameof(IsFtpSelected));
            this.RaisePropertyChanged(nameof(IsPluginSelected));
            this.RaisePropertyChanged(nameof(TabIconDefaultKey));
            this.RaisePropertyChanged(nameof(RequiresSshAuth));
            this.RaisePropertyChanged(nameof(SshAlgorithmsError));
            this.RaisePropertyChanged(nameof(SupportsPostAuthCommand));
            this.RaisePropertyChanged(nameof(ShowFtpPlaintextWarning));

            this.RaisePropertyChanged(nameof(ShowPasswordField));
            this.RaisePropertyChanged(nameof(HostLabel));
            this.RaisePropertyChanged(nameof(HostPlaceholder));
            this.RaisePropertyChanged(nameof(UsernameLabel));
            this.RaisePropertyChanged(nameof(PasswordLabel));
            // FTP / 插件只认密码类的共享凭据:换协议要重判一次凭据来源能不能用。
            RaiseCredentialSourceDependents();
        }
    }

    /// <summary>协议标签是否选中 SSH。</summary>
    public bool IsSshSelected => ConnectionType == ConnectionType.SSH;

    /// <summary>协议标签是否选中 SFTP。</summary>
    public bool IsSftpSelected => ConnectionType == ConnectionType.SFTP;

    /// <summary>协议标签是否选中 FTP / FTPS。</summary>
    public bool IsFtpSelected => ConnectionType == ConnectionType.FTP;

    /// <summary>协议标签是否选中某个插件协议。</summary>
    public bool IsPluginSelected => ConnectionType == ConnectionType.Plugin;

    /// <summary>
    /// 是否需要 SSH 认证表单(私钥、口令、跳板)。FTP 只有用户名/口令与匿名登录,
    /// 插件协议(S3 之类)一般也只有一对密钥,把这些 SSH 专属项隐掉,
    /// 避免用户以为它们也能用私钥。
    /// </summary>
    public bool RequiresSshAuth => ConnectionType is not (ConnectionType.FTP or ConnectionType.Plugin);

    /// <summary>
    /// 「主机」输入框的标签。协议可以改写它:S3 填的是服务端点而不是一台主机,
    /// 沿用「主机名/IP」会让人以为要填某台服务器的地址 —— 同一个输入框,换个说法即可,
    /// 不必再开一个字段。文案由插件给(它才知道自己的领域词汇)。
    /// </summary>
    public string HostLabel => _pluginForm?.HostLabel ?? Strings.Get("Profile_HostIp");

    /// <summary>「主机」输入框的占位提示。</summary>
    public string HostPlaceholder => _pluginForm?.HostPlaceholder ?? "192.168.1.100";

    /// <summary>
    /// 是否显示「端口」那一栏。声明了 <c>NoEndpoint</c> 的连接类型收起它 ——
    /// 目标不是一个 <c>host:port</c> 时(串口是一根线、SQLite 是磁盘上一个文件),
    /// 那一栏填什么都不会被用上,摆着只会让用户以为它有意义,而且还留着上一个协议的残值。
    /// <para>
    /// 只收显示,不改判定:端口的**取值**照旧参与保存/连接按钮的可用性 ——
    /// 收起一栏不该顺手把按钮堵死。
    /// </para>
    /// </summary>
    public bool ShowPortField => _pluginForm?.ShowsPort != false;

    /// <summary>
    /// 是否显示「跳板主机」那一行。没有端点的表单(<see cref="ShowPortField" /> 为 false,即变体或描述符声明了
    /// <c>NoEndpoint</c>:SQLite 是一个本地文件、串口是一根线)收起它 —— 目标不是 <c>host:port</c>,
    /// 就谈不上经跳板连过去。
    /// <para>
    /// 只收显示,不清值:选过的跳板照旧随配置保存,切回有端点的类型时原样回来。
    /// 工作台连接时建不建 SSH 隧道也不看这一行,而是按变体生效的能力位判定
    /// (<c>WorkspaceVariants.EffectiveFeatures</c>,见 <c>MainWindowViewModel.EstablishWorkspaceTunnelAsync</c>)。
    /// </para>
    /// </summary>
    public bool ShowJumpHostField => ShowPortField;

    /// <summary>「主机」那一栏是否渲染成**只读**下拉。</summary>
    public bool HostIsChoice => _pluginForm is { HostIsChoice: true, HostAllowsCustomValue: false };

    /// <summary>
    /// 「主机」那一栏是否渲染成**可编辑**下拉。
    /// <para>
    /// 串口就是这一档:端口是可枚举的(所以该给下拉),但枚举不到的也必须填得进去 ——
    /// 没插的适配器、容器里映射进来的 <c>/dev/ttyS10</c>、还没装驱动的板子。
    /// </para>
    /// </summary>
    public bool HostIsEditableChoice => _pluginForm is { HostIsChoice: true, HostAllowsCustomValue: true };

    /// <summary>「主机」那一栏是否就是个普通文本框(SSH / SFTP / FTP / S3 / Telnet 都是)。</summary>
    public bool HostIsText => _pluginForm?.HostIsChoice != true;

    /// <summary>「主机」下拉旁边要不要给刷新按钮(候选项是向插件现取的)。</summary>
    public bool HostIsDynamicChoice => _pluginForm?.HostIsDynamic == true;

    /// <summary>「主机」下拉的候选项。</summary>
    public ObservableCollection<PluginChoiceItem> HostChoices { get; } = [];

    /// <summary>正在向插件索取「主机」候选项(刷新按钮期间不可点)。</summary>
    public bool IsHostRefreshing
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 「主机」下拉当前选中的项。可编辑形态下对不上候选项就是**没有选中项**,
    /// 当前值由文本框自己显示 —— 在那里回落到第一项,等于把用户存的那个
    /// "这次没枚举到"的端口悄悄换成别的。
    /// </summary>
    public PluginChoiceItem? SelectedHostChoice
    {
        get
        {
            PluginChoiceItem? exact = HostChoices.FirstOrDefault(choice => choice.Value == Host);
            return exact ?? (HostIsChoice ? HostChoices.FirstOrDefault() : null);
        }
        set
        {
            if (value is not null)
            {
                Host = value.Value;
            }
        }
    }

    /// <summary>「用户名」输入框的标签(插件协议可改写,如 Access Key ID)。</summary>
    public string UsernameLabel => _pluginForm?.UsernameLabel ?? Strings.Get("Username");

    /// <summary>「密码」输入框的标签(插件协议可改写,如 Secret Access Key)。</summary>
    public string PasswordLabel => _pluginForm?.PasswordLabel ?? Strings.Get("Password");

    /// <summary>
    /// 当前协议是否允许不填凭据直接连(匿名访问)。
    /// **必须是可观察的存储属性**:它参与保存/连接按钮的可用性判定,而那个判定是
    /// <c>WhenAnyValue</c> 组合器 —— 纯计算属性发的 PropertyChanged 驱动不了它,
    /// 结果就是"编辑一条已存的匿名 S3 配置,按钮灰着,得先随便改个字段才亮"。
    /// </summary>
    public bool AllowsAnonymous
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>加密方式下拉的下标:0 自动、1 显式 FTPS、2 隐式 FTPS、3 不加密。</summary>
    public int FtpEncryptionIndex
    {
        get => _ftpEncryption switch
        {
            FtpEncryptionMode.Explicit => 1,
            FtpEncryptionMode.Implicit => 2,
            FtpEncryptionMode.None => 3,
            _ => 0
        };
        set => FtpEncryption = value switch
        {
            1 => FtpEncryptionMode.Explicit,
            2 => FtpEncryptionMode.Implicit,
            3 => FtpEncryptionMode.None,
            _ => FtpEncryptionMode.Auto
        };
    }

    /// <summary>是否提示「明文 FTP 不加密」。选了不加密才提示;FTPS 不提示。</summary>
    public bool ShowFtpPlaintextWarning => IsFtpSelected && FtpEncryption == FtpEncryptionMode.None;

    /// <summary>
    /// 是否显示用户名/口令两栏。声明了 <see cref="ProtocolFeatures.NoCredentials" /> 的
    /// 插件协议(Telnet 这种登录发生在带内的)一律收起 —— 摆着两个填了也发不出去的框,
    /// 只会让用户以为填上就能自动登录。
    /// </summary>
    public bool ShowCredentialFields => _pluginForm?.ShowsCredentials != false;

    /// <summary>
    /// 插件按协议或变体把用户名/口令两栏收起来了(Telnet 在带内登录,SQLite 是个文件)。
    /// <para>
    /// 这时框里还留着的用户名口令是收起之前填的 —— SSH 页签上顺手填的,或编辑态「更换」之前那一档(MySQL)的,
    /// 用户已经看不见。照存的话,每次打开都会拿"有用户名、没口令"弹一个填了也没用的登录框,
    /// 存着口令的还会原样交给插件。与凭据来源那一栏收起时不存引用(#550)同一个理由。
    /// 插件不可用(表单没装载出来)时两栏照旧显示,这里为 false:不知道这一档要不要凭据,就不动已存的。
    /// </para>
    /// </summary>
    private bool HidesPluginCredentials => ConnectionType == ConnectionType.Plugin && !ShowCredentialFields;

    /// <summary>是否显示口令输入框:匿名 FTP 与无凭据协议不需要口令。</summary>
    public bool ShowPasswordField => IsPasswordAuth && ShowCredentialFields && !(IsFtpSelected && FtpAnonymous);

    /// <summary>FTP 加密方式;仅 <see cref="IsFtpSelected" /> 时有意义。</summary>
    public FtpEncryptionMode FtpEncryption
    {
        get => _ftpEncryption;
        set
        {
            this.RaiseAndSetIfChanged(ref _ftpEncryption, value);
            this.RaisePropertyChanged(nameof(FtpEncryptionIndex));
            this.RaisePropertyChanged(nameof(ShowFtpPlaintextWarning));
            // 隐式 FTPS 与明文/显式的默认端口不同,用户没手动改过端口时跟着切,省一次踩坑。
            if (value == FtpEncryptionMode.Implicit && Port == FtpSettings.DefaultPort)
            {
                Port = FtpSettings.DefaultImplicitPort;
            }
            else if (value != FtpEncryptionMode.Implicit && Port == FtpSettings.DefaultImplicitPort)
            {
                Port = FtpSettings.DefaultPort;
            }
        }
    }

    /// <summary>FTP 是否使用被动模式(默认开;主动模式在客户端 NAT 后基本不可用)。</summary>
    public bool FtpPassive
    {
        get => _ftpPassive;
        set => this.RaiseAndSetIfChanged(ref _ftpPassive, value);
    }

    /// <summary>FTP 是否匿名登录;开启后不再要求填写用户名。</summary>
    public bool FtpAnonymous
    {
        get => _ftpAnonymous;
        set
        {
            this.RaiseAndSetIfChanged(ref _ftpAnonymous, value);
            this.RaisePropertyChanged(nameof(ShowPasswordField));
            RaiseCredentialSourceDependents();
        }
    }

    /// <summary>可选的插件协议页签(来自已安装插件的清单声明;插件未激活时也在)。</summary>
    public ObservableCollection<PluginProtocolTabViewModel> PluginProtocols { get; } = [];

    /// <summary>当前插件协议的设置表单字段(选中协议并完成激活后才有内容)。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginFields { get; } = [];

    /// <summary>当前选中的插件协议 id;非插件协议时为 null。</summary>
    public string? PluginProtocolId
    {
        get => _pluginProtocolId;
        private set => this.RaiseAndSetIfChanged(ref _pluginProtocolId, value);
    }

    /// <summary>
    /// 表单是否还在等插件激活。协议页签在发现期就画出来了(不装载程序集),
    /// 用户点到它才触发惰性激活 —— 这中间有一小段"页签在、字段还没到"的状态。
    /// </summary>
    public bool IsPluginLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 编辑既有插件配置时,构造函数里发起的那次表单装载;新建配置时为已完成的任务。
    /// <para>
    /// 只给用例用:装载是异步的(可能触发插件惰性激活),而"打开一条已存配置时表单长什么样"
    /// 必须在装载完成后才能判定。等它而不是轮询 <see cref="PluginFields" />。
    /// </para>
    /// </summary>
    internal Task PendingPluginLoad { get; } = Task.CompletedTask;

    /// <summary>
    /// 目标主机地址(主机名或 IP)。协议可以改写它承载的东西 —— 串口在这一栏装设备名
    /// (<c>COM3</c> / <c>/dev/ttyUSB0</c>),与 PuTTY 把 <i>Host Name</i> 换成
    /// <i>Serial line</i> 是同一个取舍。
    /// </summary>
    public string Host
    {
        get => _host;
        set
        {
            this.RaiseAndSetIfChanged(ref _host, value);
            // 下拉形态下当前值与选中项是同一件事;不补这一发,程序化改 Host
            // (读入既有配置、刷新后归位)时下拉显示的还是上一项。
            this.RaisePropertyChanged(nameof(SelectedHostChoice));
            this.RaisePropertyChanged(nameof(HostError));
        }
    }

    /// <summary>SSH 端口,有效范围 1–65535,默认 22。</summary>
    public int Port
    {
        get => _port;
        set
        {
            this.RaiseAndSetIfChanged(ref _port, value);
            this.RaisePropertyChanged(nameof(PortError));
        }
    }

    /// <summary>登录用户名。</summary>
    public string Username
    {
        get => _username;
        set
        {
            this.RaiseAndSetIfChanged(ref _username, value);
            this.RaisePropertyChanged(nameof(UsernameError));
        }
    }

    /// <summary>认证方式;变更时同步刷新 <see cref="AuthMethodIndex" /> 与两条路径校验。</summary>
    public AuthMethod AuthMethod
    {
        get => _authMethod;
        set
        {
            this.RaiseAndSetIfChanged(ref _authMethod, value);
            this.RaisePropertyChanged(nameof(AuthMethodIndex));
            this.RaisePropertyChanged(nameof(PrivateKeyPathError));
            this.RaisePropertyChanged(nameof(CertificatePathError));
        }
    }

    /// <summary>认证方式的序号(0=密码,1=私钥,2=证书,3=Agent,4=Tailscale),即枚举值本身。</summary>
    /// <remarks>
    /// 序号与 <see cref="Core.Models.AuthMethod" /> 的枚举值一一对应,所以这里就是一次强转。
    /// 越界值(手改过的配置文件)落回密码认证 —— 与从前那句"非 1 即 0"的兜底口径一致。
    /// </remarks>
    public int AuthMethodIndex
    {
        get => (int)AuthMethod;
        set => AuthMethod = Enum.IsDefined((AuthMethod)value) ? (AuthMethod)value : AuthMethod.Password;
    }

    /// <summary>密码以 SecureString 承载;ASCII 过滤由 <c>SecurePasswordBox</c> 输入行为负责。</summary>
    public SecureString? Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    /// <summary>私钥文件路径(密钥认证与证书认证都用它签名)。</summary>
    public string? PrivateKeyPath
    {
        get => _privateKeyPath;
        set
        {
            this.RaiseAndSetIfChanged(ref _privateKeyPath, value);
            this.RaisePropertyChanged(nameof(PrivateKeyPathError));
        }
    }

    /// <summary>OpenSSH 用户证书文件路径(证书认证时使用,通常叫 <c>*-cert.pub</c>)。</summary>
    public string? CertificatePath
    {
        get => _certificatePath;
        set
        {
            this.RaiseAndSetIfChanged(ref _certificatePath, value);
            this.RaisePropertyChanged(nameof(CertificatePathError));
        }
    }

    // ---- 逐字段的内联校验 ----
    //
    // 保存 / 连接 / 测试三个按钮本来就按"主机非空 + 用户名非空 + 端口范围"灰着,
    // 但用户只看到按钮点不动,看不到**为什么** —— 尤其端口这种要退到 1-65535 才亮的。
    // 这几条把原因写在字段下方。注意它们只负责"说清楚",按钮的可用性判定仍在
    // 构造函数里那条 canExecute 上,不在这里重复一遍逻辑。

    /// <summary>主机为空、或方括号写法不成立时的提示;没问题为 null。</summary>
    public string? HostError =>
        string.IsNullOrWhiteSpace(Host) ? HostRequiredMessage()
        : !IsHostWellFormed(Host) ? Strings.Get("Profile_ErrHostBrackets")
        : null;

    /// <summary>
    /// 主机留空时的提示。当前变体改写了主机标签时跟着标签说(SQLite:「请填写数据库文件」)——
    /// 标签写着「数据库文件」、提示却说「请填写主机」,用户要愣一下该填什么。
    /// 没有变体、或变体没给 <see cref="WorkspaceVariant.HostLabel" /> 时与原来一样。
    /// </summary>
    private string HostRequiredMessage() =>
        CurrentVariant()?.HostLabel is { } label && !string.IsNullOrWhiteSpace(label)
            ? Strings.Format("Profile_ErrHostRequiredFormat", label)
            : Strings.Get("Profile_ErrHostRequired");

    /// <summary>主机栏能不能用:非空;以 <c>[</c> 开头的必须是完整的 <c>[IPv6]</c> 或 <c>[IPv6]:端口</c>。</summary>
    private static bool IsHostWellFormed(string? host)
    {
        string value = host?.Trim() ?? string.Empty;
        return value.Length > 0 && (!value.StartsWith('[') || HostAddress.TrySplit(value, out _, out _));
    }

    /// <summary>
    /// 实际要连的主机与端口。主机栏写成 <c>[2001:db8::1]</c> / <c>[2001:db8::1]:2222</c> 的(从别处抄来的常是这样),
    /// 去掉方括号,方括号后面的端口取代端口栏(#611);配置里存裸地址(见 <see cref="HostAddress" />)。
    /// </summary>
    /// <remarks>
    /// 只认方括号这一种写法,别的原样(去两端空白):主机栏在串口协议下装的是设备名,插件协议的主机写法由插件定;
    /// 不带方括号的 IPv6 本来就是裸地址,不用动。
    /// </remarks>
    private (string Host, int Port) EffectiveEndpoint()
    {
        string host = _host.Trim();
        if (host.StartsWith('[') && HostAddress.TrySplit(host, out string bare, out int? port))
        {
            return (bare, ShowPortField && port is { } explicitPort ? explicitPort : _port);
        }
        return (host, _port);
    }

    /// <summary>端口越界时的提示;没问题为 null。</summary>
    public string? PortError =>
        Port is >= 1 and <= 65535 ? null : Strings.Get("Profile_ErrPortRange");

    /// <summary>
    /// 用户名为空时的提示;没问题(或本连接类型允许匿名)为 null。
    /// </summary>
    public string? UsernameError =>
        !string.IsNullOrWhiteSpace(Username)
        || SharedCredentialProvidesUsername
        || (ConnectionType == ConnectionType.FTP && FtpAnonymous)
        || (ConnectionType == ConnectionType.Plugin && (AllowsAnonymous || PluginUnavailable))
            ? null
            : Strings.Get("Profile_ErrUserRequired");

    /// <summary>
    /// 私钥文件不存在时的提示;没问题为 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一条是**新增的校验**,不只是把已有门槛写出来:填错私钥路径原先要等到连接失败、
    /// 从一句笼统的认证错误里猜。
    /// </para>
    /// <para>
    /// 两种认证下"留空"的含义不一样,所以不能合成一个判断:密钥认证留空是合法的
    /// (含义是"用默认密钥"),证书认证留空则是硬错 —— 证书只是 CA 的背书,签名始终由私钥出,
    /// 少了它连不上,而失败信息只会是一句笼统的 publickey 被拒。
    /// </para>
    /// </remarks>
    public string? PrivateKeyPathError
    {
        get
        {
            if (AuthMethod is not (AuthMethod.PrivateKey or AuthMethod.Certificate))
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(PrivateKeyPath))
            {
                return AuthMethod == AuthMethod.Certificate ? Strings.Get("Profile_ErrCertKeyRequired") : null;
            }
            return FileExistsSafe(PrivateKeyPath) ? null : Strings.Get("Profile_ErrKeyMissing");
        }
    }

    /// <summary>
    /// 证书文件缺失或不存在时的提示;没问题为 null。
    /// </summary>
    /// <remarks>
    /// 与私钥那条不同,证书认证下空路径也算错:私钥留空还能退回默认密钥,
    /// 证书没有任何默认位置可退。系统密钥库认证的证书是可选的:留空就是按公钥认证,填了才要文件在。
    /// </remarks>
    public string? CertificatePathError
    {
        get
        {
            if (AuthMethod is not (AuthMethod.Certificate or AuthMethod.KeyStore))
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(CertificatePath))
            {
                return AuthMethod == AuthMethod.Certificate ? Strings.Get("Profile_ErrCertRequired") : null;
            }
            return FileExistsSafe(CertificatePath) ? null : Strings.Get("Profile_ErrCertMissing");
        }
    }

    private static bool FileExistsSafe(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 路径非法或不可访问:当作"存在",把判断留给真正的连接过程 ——
            // 在这里报错反而会挡住用了特殊路径(UNC、符号链接)的用户。
            return true;
        }
    }

    /// <summary>私钥口令(私钥受密码保护时使用)。</summary>
    public string? PrivateKeyPassphrase
    {
        get => _privateKeyPassphrase;
        set => this.RaiseAndSetIfChanged(ref _privateKeyPassphrase, value);
    }

    /// <summary>所属分组的 Id;null 表示未分组。</summary>
    public Guid? GroupId
    {
        get => _groupId;
        set => this.RaiseAndSetIfChanged(ref _groupId, value);
    }

    /// <summary>跳板主机下拉:“直连” + 除自身外的全部已保存配置。</summary>
    public ObservableCollection<GroupOption> JumpHostOptions { get; }

    /// <summary>当前选中的跳板主机项;null 项表示直连。</summary>
    public GroupOption? SelectedJumpHost
    {
        get => _selectedJumpHost;
        set => this.RaiseAndSetIfChanged(ref _selectedJumpHost, value);
    }

    /// <summary>当前是否为密码认证;由认证方式派生,控制密码相关字段的可见性。</summary>
    public bool IsPasswordAuth
    {
        get => _isPasswordAuth;
        private set => this.RaiseAndSetIfChanged(ref _isPasswordAuth, value);
    }

    /// <summary>当前是否为密钥认证;由认证方式派生,控制私钥相关字段的可见性。</summary>
    public bool IsKeyAuth
    {
        get => _isKeyAuth;
        private set => this.RaiseAndSetIfChanged(ref _isKeyAuth, value);
    }

    /// <summary>当前是否为证书认证;由认证方式派生,控制证书文件字段的可见性。</summary>
    public bool IsCertAuth
    {
        get => _isCertAuth;
        private set => this.RaiseAndSetIfChanged(ref _isCertAuth, value);
    }

    /// <summary>私钥与口令两个字段是否可见:密钥认证与证书认证都要用到它们。</summary>
    /// <remarks>
    /// 证书页不是另起一套表单,而是密钥页再加一个证书文件字段 —— 证书凭据的
    /// 「证书 + 匹配私钥」本就是两件套,签名始终由私钥出。
    /// 共用同一块面板,校验与浏览按钮也就只有一份,不会两边各改各的。
    /// </remarks>
    public bool ShowsPrivateKeyFields => IsKeyAuth || IsCertAuth;

    /// <summary>是否正忙(保存/连接/测试进行中);用于禁用命令与显示进度。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次操作的错误信息;无错误时为 null。</summary>
    public string? ErrorMessage
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasFeedback));
            this.RaisePropertyChanged(nameof(HasError));
            // 换了一条错误(或清空)就收回上一条的「已复制」:那句回执是对具体那段文本说的。
            ErrorCopied = false;
            // 一键放开是对上一次测试失败说的:错误换了(新一轮测试、保存失败),它也跟着作废。
            AlgorithmRemedy = null;
        }
    }

    /// <summary>
    /// 上一次「测试」败在算法协商上、而对端有本版实现了只是没放开的算法:反馈条上的「放开这几个算法并重新测试」按它来。
    /// </summary>
    public SshAlgorithmRemedy? AlgorithmRemedy
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasAlgorithmRemedy));
            this.RaisePropertyChanged(nameof(AlgorithmRemedyTip));
        }
    }

    /// <summary>反馈条上要不要出「放开这几个算法并重新测试」。</summary>
    public bool HasAlgorithmRemedy => AlgorithmRemedy is not null;

    /// <summary>那颗按钮的悬停说明:往哪一类清单里加哪几个。</summary>
    public string AlgorithmRemedyTip => AlgorithmRemedy is { } remedy
        ? SshAlgorithmRemedyFlow.ButtonTip(remedy, SshAlgorithmRemedyFollowUp.Retest)
        : string.Empty;

    /// <summary>「放开这几个算法并重新测试」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AllowAlgorithmsCommand { get; }

    /// <summary>
    /// 视图注入的确认框(标题、正文、确认键文字;确认返回 true),与终端标签上那颗按钮问的是同一句。
    /// 未挂时(headless)不改 —— 这一步会降低那条连接的安全性,没人点头不能做。
    /// </summary>
    public Func<string, string, string, Task<bool>>? AllowAlgorithmsConfirmer { get; set; }

    /// <summary>是否有可复制的错误信息 —— 复制按钮据此出现。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>刚复制过错误信息;按钮短暂显示「已复制」,随后自己退回。</summary>
    public bool ErrorCopied
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次连接测试结果;null 表示尚未测试,变更时同步刷新 <see cref="ShowTestSuccess" />。</summary>
    public bool? LastTestSucceeded
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowTestSuccess));
            this.RaisePropertyChanged(nameof(HasFeedback));
        }
    }

    /// <summary>“连接测试成功”提示可见性。</summary>
    public bool ShowTestSuccess => LastTestSucceeded == true;

    /// <summary>
    /// 反馈条(钉在按钮上方那一条)是否可见 —— 有话说才占位置,免得空着一条边框。
    /// </summary>
    public bool HasFeedback => ShowTestSuccess || !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>记住密码(AES-256 加密存储);未勾选时密码只用于本次连接。</summary>
    public bool RememberPassword
    {
        get => _rememberPassword;
        set => this.RaiseAndSetIfChanged(ref _rememberPassword, value);
    }

    // ---- 凭据来源(#550) ----
    //
    // 选了共享凭据,这条配置就不再存自己的认证材料:认证方式、密码、私钥那几栏收起,
    // 连接那一刻向凭据去取。用户名照常可填 —— 填了覆盖凭据里的,留空就跟着凭据走。

    /// <summary>「凭据来源」下拉:第一项「在本配置中填写」,其后是全部共享凭据。</summary>
    public ObservableCollection<CredentialSourceOption> CredentialSources { get; }

    /// <summary>当前选中的凭据来源。</summary>
    public CredentialSourceOption SelectedCredentialSource
    {
        get => _selectedCredentialSource;
        set
        {
            // ComboBox 在 ItemsSource 重建的瞬间会回写一个 null,不能让它把引用清掉。
            if (value is null || ReferenceEquals(value, _selectedCredentialSource)
                || ((IsTailscaleAuth || IsKeyStoreAuth) && value.Reference is not null))
            {
                return;
            }
            SharedCredential? previous = _selectedCredentialSource.Credential;
            this.RaiseAndSetIfChanged(ref _selectedCredentialSource, value);
            _credentialSource = value.Reference;
            if (value.Reference is null && previous is not null)
            {
                // 从共享凭据切回「在本配置中填写」:把凭据拷进表单,而不是丢给用户一排空框 ——
                // 用户多半只是想让这一条不再跟着凭据走,不是想重新输一遍密码。
                CopyCredentialIntoForm(previous);
            }
            // 选中的凭据自带的用户名与表单里的相同:清掉表单里那一份,让这条连接跟着凭据走 ——
            // 否则凭据日后改了用户名,这条连接还钉在旧名字上。
            if (value.Credential is { Username.Length: > 0 } credential
                && string.Equals(Username.Trim(), credential.Username, StringComparison.Ordinal))
            {
                Username = string.Empty;
            }
            RaiseCredentialSourceDependents();
        }
    }

    /// <summary>是否使用共享凭据(而不是在本配置中填写)。</summary>
    public bool UsesSharedCredential => _credentialSource is not null;

    /// <summary>
    /// 是否显示「凭据来源」那一栏:没有凭据仓储(设计期、单测)不显示;
    /// 匿名 FTP 与无凭据协议(Telnet)用不上任何凭据,也不显示。
    /// </summary>
    public bool ShowCredentialSourceSelector => _sharedCredentials is not null && CredentialSourceApplies;

    /// <summary>这种连接用不用得上凭据来源:匿名 FTP 与无凭据协议(Telnet)用不上。</summary>
    /// <remarks>与仓储在不在无关 —— 没有仓储只是选不了,已有的引用照样要存回去。</remarks>
    private bool CredentialSourceApplies =>
        ShowCredentialFields && !IsTailscaleAuth && !IsKeyStoreAuth && !(IsFtpSelected && FtpAnonymous);

    /// <summary>认证方式分段按钮是否显示:用共享凭据时认证方式由凭据决定。</summary>
    public bool ShowAuthMethodSelector => RequiresSshAuth && !UsesSharedCredential;

    /// <summary>口令框(与「记住密码」)是否显示:用共享凭据时不显示 —— 密码在凭据里。</summary>
    public bool ShowInlinePassword => ShowPasswordField && !UsesSharedCredential;

    /// <summary>私钥 / 证书那几栏是否显示:用共享凭据时不显示。</summary>
    public bool ShowInlineKeyFields => ShowsPrivateKeyFields && !UsesSharedCredential;

    /// <summary>Agent 认证那行说明是否显示:用共享凭据时不显示。</summary>
    public bool ShowAgentHint => IsAgentAuth && !UsesSharedCredential;

    /// <summary>Tailscale SSH 使用本机身份,只需要远端系统用户名。</summary>
    public bool ShowTailscaleHint => RequiresSshAuth && IsTailscaleAuth;

    /// <summary>选中的共享凭据自带用户名:这时连接自己的用户名可以留空。</summary>
    public bool SharedCredentialProvidesUsername =>
        !IsTailscaleAuth && UsesSharedCredential && _selectedCredentialSource.Credential is { Username.Length: > 0 };

    /// <summary>用户名框的占位提示:跟着凭据走时写明会用哪个用户名。</summary>
    public string UsernamePlaceholder => SharedCredentialProvidesUsername
        ? Strings.Format("Profile_CredUsernameInherit", _selectedCredentialSource.Credential!.Username)
        : "root";

    /// <summary>选中的共享凭据的摘要(用户名 · 认证方式),显示在下拉下方。</summary>
    public string? SharedCredentialSummary => UsesSharedCredential && _selectedCredentialSource.Credential is { } credential
        ? Strings.Format("Profile_CredSummary",
            credential.Username.Length > 0 ? credential.Username : Strings.Get("SharedCred_UsernameFromConnection"),
            credential.AuthMethod switch
            {
                AuthMethod.PrivateKey => Strings.Get("Profile_KeyAuth"),
                AuthMethod.Certificate => Strings.Get("Profile_CertAuth"),
                AuthMethod.Agent => Strings.Get("Profile_AgentAuth"),
                AuthMethod.Tailscale => Strings.Get("Profile_TailscaleAuth"),
                _ => Strings.Get("Profile_PasswordAuth")
            })
        : null;

    /// <summary>
    /// 凭据来源的问题:引用已失效,或选的凭据这种连接用不上(FTP 与插件协议只认密码)。没问题为 null。
    /// </summary>
    public string? CredentialSourceError
    {
        get
        {
            if (!UsesSharedCredential || !CredentialSourceApplies)
            {
                return null;
            }
            if (_selectedCredentialSource.Credential is not { } credential)
            {
                // 下拉还没加载出来时不报:那一刻不知道它在不在。
                return _sharedCredentialsLoaded ? Strings.Get("Profile_ErrCredMissing") : null;
            }
            if (credential.AuthMethod == AuthMethod.Tailscale)
            {
                return Strings.Get("SharedCred_ErrTailscaleUnsupported");
            }
            return ConnectionType is ConnectionType.FTP or ConnectionType.Plugin && credential.AuthMethod != AuthMethod.Password
                ? Strings.Get("Profile_ErrCredNotPassword")
                : null;
        }
    }

    /// <summary>共享凭据下拉是否已经加载过(决定「引用已失效」能不能报)。</summary>
    private bool _sharedCredentialsLoaded;

    /// <summary>
    /// 「新建…」:由视图注入,打开共享凭据编辑框(预填当前表单里的凭据),返回新建的那一条;取消为 null。
    /// </summary>
    [Reactive]
    public partial Func<SharedCredential, Task<SharedCredential?>>? CreateSharedCredential { get; set; }

    /// <summary>从当前表单里的凭据新建一条共享凭据并选中它。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NewSharedCredentialCommand { get; }

    /// <summary>加载共享凭据下拉,并选中当前引用的那一条。</summary>
    public async Task LoadSharedCredentialsAsync()
    {
        if (_sharedCredentials is null)
        {
            return;
        }
        List<SharedCredential> credentials;
        try
        {
            credentials = await _sharedCredentials.GetAllAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 读不出来时只剩「在本配置中填写」可选;已有的引用原样保留,保存时不会被清掉。
            return;
        }
        CredentialReference? current = _credentialSource;
        while (CredentialSources.Count > 1)
        {
            CredentialSources.RemoveAt(CredentialSources.Count - 1);
        }
        foreach (SharedCredential credential in credentials)
        {
            CredentialSources.Add(CredentialSourceOption.Shared(credential));
        }
        CredentialSourceOption selected = CredentialSources[0];
        if (current is not null)
        {
            selected = CredentialSources.FirstOrDefault(option => option.Reference == current)
                       ?? CredentialSourceOption.Missing(current);
            if (selected.Credential is null)
            {
                CredentialSources.Add(selected);
            }
        }
        _sharedCredentialsLoaded = true;
        _selectedCredentialSource = selected;
        _credentialSource = selected.Reference;
        this.RaisePropertyChanged(nameof(SelectedCredentialSource));
        RaiseCredentialSourceDependents();
    }

    /// <summary>用表单里现有的凭据预填一条共享凭据,新建成功后选中它。</summary>
    private async Task NewSharedCredentialAsync()
    {
        if (CreateSharedCredential is not { } create)
        {
            return;
        }
        bool keyBased = AuthMethod is AuthMethod.PrivateKey or AuthMethod.Certificate;
        var draft = new SharedCredential
        {
            Name = Username.Trim(),
            Username = Username.Trim(),
            AuthMethod = RequiresSshAuth ? AuthMethod : AuthMethod.Password,
            Password = AuthMethod == AuthMethod.Password || !RequiresSshAuth ? SecureStringConvert.ToPlaintext(Password) : null,
            PrivateKeyPath = keyBased ? PrivateKeyPath : null,
            PrivateKeyPassphrase = keyBased ? PrivateKeyPassphrase : null,
            CertificatePath = AuthMethod == AuthMethod.Certificate ? CertificatePath : null
        };
        if (await create(draft) is not { } created)
        {
            return;
        }
        var option = CredentialSourceOption.Shared(created);
        CredentialSources.Add(option);
        SelectedCredentialSource = option;
    }

    private void CopyCredentialIntoForm(SharedCredential credential)
    {
        if (credential.AuthMethod == AuthMethod.Tailscale)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Username))
        {
            Username = credential.Username;
        }
        // FTP 与插件协议的认证方式下拉是收起的,只认密码:别把它切到一个界面上切不回来的私钥页。
        AuthMethod = RequiresSshAuth ? credential.AuthMethod : AuthMethod.Password;
        Password = SecureStringConvert.FromPlaintext(credential.Password);
        PrivateKeyPath = credential.PrivateKeyPath;
        PrivateKeyPassphrase = credential.PrivateKeyPassphrase;
        CertificatePath = credential.CertificatePath;
    }

    private void RaiseCredentialSourceDependents()
    {
        this.RaisePropertyChanged(nameof(UsesSharedCredential));
        this.RaisePropertyChanged(nameof(ShowCredentialSourceSelector));
        this.RaisePropertyChanged(nameof(ShowAuthMethodSelector));
        this.RaisePropertyChanged(nameof(ShowInlinePassword));
        this.RaisePropertyChanged(nameof(ShowInlineKeyFields));
        this.RaisePropertyChanged(nameof(ShowAgentHint));
        this.RaisePropertyChanged(nameof(ShowTailscaleHint));
        this.RaisePropertyChanged(nameof(ShowKeyStoreFields));
        this.RaisePropertyChanged(nameof(SharedCredentialProvidesUsername));
        this.RaisePropertyChanged(nameof(UsernamePlaceholder));
        this.RaisePropertyChanged(nameof(SharedCredentialSummary));
        this.RaisePropertyChanged(nameof(CredentialSourceError));
        this.RaisePropertyChanged(nameof(UsernameError));
    }

    /// <summary>是否明文显示密码。</summary>
    public bool ShowPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ---- 分页 ----
    //
    // 原先是一列到底的表单加一个「高级选项」折叠开关:SSH 展开后有三十来行,
    // 窗口被钳在 768 高里只能一路滚;插件协议的调优字段也收在同一个开关后面。
    // 现在按主题分页,哪几页出现由协议决定,「常规」恒在。

    /// <summary>右侧当前显示的分页。设成当前协议没有的那一页时落回「常规」。</summary>
    public ConnectionProfileSection SelectedSection
    {
        get;
        set
        {
            ConnectionProfileSection section = IsSectionAvailable(value) ? value : ConnectionProfileSection.General;
            if (field == section)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, section);
            this.RaisePropertyChanged(nameof(IsGeneralSection));
            this.RaisePropertyChanged(nameof(IsTerminalSection));
            this.RaisePropertyChanged(nameof(IsSshOptionsSection));
            this.RaisePropertyChanged(nameof(IsForwardingSection));
            this.RaisePropertyChanged(nameof(IsAdvancedSection));
            this.RaisePropertyChanged(nameof(IsPluginPageSection));
            SyncPluginPageSelection();
            // 插件字段按页键分页:「常规」、插件声明的自定义页、「高级」(见 ConnectionProfileViewModel.PluginPages.cs)。
            ApplyPluginFieldVisibility();
        }
    }

    /// <summary>当前是否在「常规」页。</summary>
    public bool IsGeneralSection => SelectedSection == ConnectionProfileSection.General;

    /// <summary>当前是否在「终端」页。</summary>
    public bool IsTerminalSection => SelectedSection == ConnectionProfileSection.Terminal;

    /// <summary>当前是否在「SSH 选项」页。</summary>
    public bool IsSshOptionsSection => SelectedSection == ConnectionProfileSection.SshOptions;

    /// <summary>当前是否在「转发」页。</summary>
    public bool IsForwardingSection => SelectedSection == ConnectionProfileSection.Forwarding;

    /// <summary>当前是否在「高级」页。</summary>
    public bool IsAdvancedSection => SelectedSection == ConnectionProfileSection.Advanced;

    /// <summary>「终端」页:认证后命令与会话级终端覆盖都只对有 shell 的 SSH 成立。</summary>
    public bool ShowTerminalSection => SupportsPostAuthCommand;

    /// <summary>「SSH 选项」页:压缩与算法协商对 SSH 与 SFTP 都有意义(同一条 SSH 连接)。</summary>
    public bool ShowSshOptionsSection => RequiresSshAuth;

    /// <summary>「转发」页:两个转发只挂在交互式 shell 上,只对 SSH 出现。</summary>
    public bool ShowForwardingSection => SupportsPostAuthCommand;

    /// <summary>「高级」页:FTP 的默认打开路径,或插件声明了标 <c>IsAdvanced</c> 的字段。</summary>
    public bool ShowAdvancedSection => IsFtpSelected || AdvancedPluginFieldCount > 0;

    /// <summary>切换分页。</summary>
    public ReactiveCommand<ConnectionProfileSection, RxVoid> SelectSectionCommand { get; }

    /// <summary>
    /// 「高级」页签上的字段数,如 <c>6</c>;没有插件高级字段(FTP、内建协议)时为空串。
    /// <para>
    /// 没有这个数,S3 这类协议的六七个调优字段在用户眼里就是"设置项没了" ——
    /// 分页省下的高度,不该用"找不到东西"来换。
    /// </para>
    /// </summary>
    public string AdvancedBadge => AdvancedPluginFieldCount is var count and > 0
        ? count.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>是否要显示 <see cref="AdvancedBadge" />。</summary>
    public bool HasAdvancedBadge => AdvancedPluginFieldCount > 0;

    /// <summary>
    /// 「高级」页上要画的插件字段数(显示条件不成立的不算:点进去也看不到它)。
    /// 按页键数:进了自定义页的字段哪怕标着 IsAdvanced 也不算(页键优先)。
    /// </summary>
    private int AdvancedPluginFieldCount => PluginFields.Count(f => IsFormRowOn(f, PluginFormPage.AdvancedKey));

    /// <summary>「常规」页上有没有插件字段要画(没有时连带那一节的标题一起收起)。</summary>
    public bool ShowPluginGeneralFields =>
        IsPluginSelected && (IsPluginLoading || PluginFields.Any(f => IsFormRowOn(f, PluginFormPage.GeneralKey)));

    /// <summary>「常规」页上插件字段那一节的标题,如「S3 设置」。</summary>
    public string PluginSectionTitle =>
        Strings.Format("Profile_SecPluginSettings",
            PluginProtocols.FirstOrDefault(p => p.Id == PluginProtocolId)?.DisplayName ?? string.Empty);

    // 页签旁的圆点:这一页有没有改过默认值。编辑既有配置时,填过的东西不能藏在别的页签后面
    // 让人以为配置丢了 —— 原先的做法是把折叠区自动展开,分页之后改成在页签上点一个点。

    /// <summary>「终端」页是否有非默认值。</summary>
    public bool IsTerminalSectionModified =>
        !string.IsNullOrWhiteSpace(_postAuthCommand)
        || _overrideEncoding is not null
        || _overrideTerminalType is not null
        || !string.IsNullOrWhiteSpace(_overrideTabColor)
        || !string.IsNullOrWhiteSpace(_overrideStartupDirectory)
        || _overrideKeepAliveSeconds >= 0
        || _antiIdleSeconds > 0;

    /// <summary>「SSH 选项」页是否有非默认值。</summary>
    public bool IsSshOptionsSectionModified =>
        _sshCompression || (SupportsPostAuthCommand && _sshObscureKeystrokeTiming) || _sshLegacyAlgorithms || _sshCustomAlgorithms
        || !SshAgentEndpoints.IsDefault(_sshAgentEndpoint);

    /// <summary>「转发」页是否有非默认值。</summary>
    public bool IsForwardingSectionModified => _sshAgentForwarding || _sshX11Forwarding;

    /// <summary>「高级」页是否有非默认值。</summary>
    public bool IsAdvancedSectionModified =>
        IsFtpSelected
            ? !string.IsNullOrWhiteSpace(_ftpInitialRemotePath) || _ftpServerUtcOffsetMinutes is not null
            // 按页键认「高级」页上的字段:进了自定义页的不给「高级」点圆点(自定义页不做圆点)。
            : PluginFields.Any(f => string.Equals(f.PageKey, PluginFormPage.AdvancedKey, StringComparison.Ordinal)
                                    && !string.Equals(f.Text, f.Field.DefaultValue ?? string.Empty, StringComparison.Ordinal));

    private bool IsSectionAvailable(ConnectionProfileSection section) => section switch
    {
        ConnectionProfileSection.Terminal => ShowTerminalSection,
        ConnectionProfileSection.SshOptions => ShowSshOptionsSection,
        ConnectionProfileSection.Forwarding => ShowForwardingSection,
        ConnectionProfileSection.Advanced => ShowAdvancedSection,
        ConnectionProfileSection.PluginPage => IsPluginPageAvailable,
        _ => true
    };

    /// <summary>
    /// 协议或插件字段一变,哪几页出现就可能跟着变:补发通知,当前页没了就回到「常规」——
    /// 否则从 SSH 的「转发」页切到 FTP,右边是一片空白、页签上又没有一个是选中的。
    /// </summary>
    private void RefreshSectionAvailability()
    {
        this.RaisePropertyChanged(nameof(ShowTerminalSection));
        this.RaisePropertyChanged(nameof(ShowSshOptionsSection));
        this.RaisePropertyChanged(nameof(ShowForwardingSection));
        this.RaisePropertyChanged(nameof(ShowAdvancedSection));
        // 按键时序混淆只算 SSH 的:切到 SFTP 它不存,页签上的圆点也不该还亮着。
        this.RaisePropertyChanged(nameof(IsSshOptionsSectionModified));
        if (!IsSectionAvailable(SelectedSection))
        {
            SelectedSection = ConnectionProfileSection.General;
        }
    }

    // ---- 页脚 ----

    /// <summary>
    /// 页脚左侧的连接目标预览:SSH 是 <c>user@host:port</c>,SFTP / FTP 带上 scheme;
    /// 主机还空着时为空串(整块收起)。随输入实时更新,填完一眼就能核对要连的是哪。
    /// </summary>
    public string EndpointPreview
    {
        get
        {
            (string host, int port) = EffectiveEndpoint();
            if (host.Length == 0)
            {
                return string.Empty;
            }
            bool showsUser = ShowCredentialFields && !(IsFtpSelected && _ftpAnonymous);
            string user = showsUser ? _username.Trim() : string.Empty;
            // IPv6 字面量带端口要加方括号,否则 ::1:22 读不出哪段是端口。
            string target = ShowPortField
                ? HostAddress.FormatTarget(user, host, port)
                : user.Length > 0 ? $"{user}@{host}" : host;
            return ConnectionType switch
            {
                ConnectionType.SFTP => "sftp://" + target,
                ConnectionType.FTP => (_ftpEncryption is FtpEncryptionMode.Explicit or FtpEncryptionMode.Implicit ? "ftps://" : "ftp://") + target,
                _ => target
            };
        }
    }

    /// <summary>是否有已安装的插件协议(协议栏里「插件」那一组的标题据此出现)。</summary>
    public bool HasPluginProtocols => PluginProtocols.Count > 0;

    /// <summary>
    /// 打开插件管理器。由主窗口注入(对话框层够不着它);没注入(单测、独立打开)时
    /// 协议栏底部那一行链接不出现。插件管理器是非模态的,在那边启用了新协议,
    /// 这边的协议栏经注册表的 <c>Changed</c> 事件当场补上。
    /// </summary>
    public Action? OpenPluginManager
    {
        get;
        set
        {
            field = value;
            this.RaisePropertyChanged(nameof(CanOpenPluginManager));
        }
    }

    /// <summary>协议栏底部「获取更多协议…」是否出现。</summary>
    public bool CanOpenPluginManager => OpenPluginManager is not null;

    /// <summary>打开插件管理器。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenPluginManagerCommand { get; }

    /// <summary>这些属性一变,页签上的圆点要重算。</summary>
    private static readonly FrozenSet<string> TerminalSectionInputs = new[]
    {
        nameof(PostAuthCommand), nameof(OverrideEncoding), nameof(OverrideTerminalType), nameof(OverrideTabColor),
        nameof(OverrideStartupDirectory), nameof(OverrideKeepAliveSeconds), nameof(AntiIdleSeconds)
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SshOptionsSectionInputs =
        new[] { nameof(SshCompression), nameof(SshObscureKeystrokeTiming), nameof(SshLegacyAlgorithms), nameof(SshCustomAlgorithms), nameof(SshAgentEndpoint) }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ForwardingSectionInputs =
        new[] { nameof(SshAgentForwarding), nameof(SshX11Forwarding) }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>这些属性一变,页脚的连接目标预览要重算。</summary>
    private static readonly FrozenSet<string> EndpointInputs = new[]
    {
        nameof(Host), nameof(Port), nameof(Username), nameof(ConnectionType), nameof(FtpEncryption),
        nameof(FtpAnonymous), nameof(ShowPortField), nameof(ShowCredentialFields)
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>把派生属性的通知集中转发(见构造函数里的说明)。</summary>
    private void OnOwnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name is null)
        {
            return;
        }
        if (TerminalSectionInputs.Contains(name))
        {
            this.RaisePropertyChanged(nameof(IsTerminalSectionModified));
        }
        else if (SshOptionsSectionInputs.Contains(name))
        {
            this.RaisePropertyChanged(nameof(IsSshOptionsSectionModified));
        }
        else if (ForwardingSectionInputs.Contains(name))
        {
            this.RaisePropertyChanged(nameof(IsForwardingSectionModified));
        }
        else if (name is nameof(FtpInitialRemotePath) or nameof(SelectedFtpTimeZone))
        {
            this.RaisePropertyChanged(nameof(IsAdvancedSectionModified));
        }
        if (EndpointInputs.Contains(name))
        {
            this.RaisePropertyChanged(nameof(EndpointPreview));
        }
        if (name == nameof(ConnectionType))
        {
            this.RaisePropertyChanged(nameof(IsAdvancedSectionModified));
            this.RaisePropertyChanged(nameof(ShowPluginGeneralFields));
            RefreshSectionAvailability();
        }
        else if (name is nameof(IsPluginLoading) or nameof(PluginProtocolId))
        {
            this.RaisePropertyChanged(nameof(ShowPluginGeneralFields));
            this.RaisePropertyChanged(nameof(PluginSectionTitle));
        }
    }

    /// <summary>标签,逗号分隔。</summary>
    public string TagsText
    {
        get => _tagsText;
        set => this.RaiseAndSetIfChanged(ref _tagsText, value);
    }

    /// <summary>
    /// 备注(#549,「常规」页「整理」一节的多行文本框);留空 = 没有备注。
    /// 资源管理器里悬停这条连接时显示它。
    /// </summary>
    public string? Notes
    {
        get => _notes;
        set => this.RaiseAndSetIfChanged(ref _notes, value);
    }

    /// <summary>
    /// 标签页图标(「常规」页「整理」一节的图标字段,<see cref="SessionTabIcons" /> 里的 id);null = 跟协议。
    /// </summary>
    /// <remarks>
    /// 放在「常规」页而不是颜色所在的「终端」页:图标对每一种协议都画(SFTP / FTP / 插件标签也有),
    /// 而「终端」页只有 SSH 才有。存进 <see cref="TerminalOverrides.TabIcon" />,与标签颜色同一处。
    /// </remarks>
    public string? TabIcon
    {
        get => _tabIcon;
        set => this.RaiseAndSetIfChanged(ref _tabIcon, SessionTabIcons.Normalize(value));
    }

    /// <summary>跟协议时图标字段上画的那枚(按当前选的协议;插件协议是通用插头)。</summary>
    public string? TabIconDefaultKey =>
        ConnectionIcon.ResourceKeyFor(new SessionProfile { ConnectionType = ConnectionType });

    /// <summary>
    /// 本条配置专属的「认证后执行命令」(「终端」页);留空 = 不执行。
    /// 与设置里那条全局的「连接后执行命令」互不影响,两处都配就都执行(先全局后本条)。
    /// </summary>
    public string? PostAuthCommand
    {
        get => _postAuthCommand;
        set => this.RaiseAndSetIfChanged(ref _postAuthCommand, value);
    }

    /// <summary>注入上面那条命令前的等待秒数(0~60)。</summary>
    public int PostAuthCommandDelaySeconds
    {
        get => _postAuthCommandDelaySeconds;
        set => this.RaiseAndSetIfChanged(
            ref _postAuthCommandDelaySeconds,
            Math.Clamp(value, 0, SessionProfile.MaxPostAuthCommandDelaySeconds));
    }

    /// <summary>
    /// 延迟输入框的上限;界面直接绑它,免得两处各写一个 60。
    /// 实例属性而非 static:编译绑定不解析实例路径上的静态成员,写成 static 那个
    /// <c>Maximum</c> 会静默绑空,输入框上限就没了。
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public int MaxPostAuthCommandDelaySeconds => SessionProfile.MaxPostAuthCommandDelaySeconds;

    /// <summary>
    /// 「认证后执行命令」这一栏是否出现。只对 SSH 成立 —— 命令是往 shell 通道里注入的,
    /// 而 SFTP / FTP / 对象存储这些连接根本没有终端,摆一个永远不会执行的输入框只会骗人。
    /// </summary>
    public bool SupportsPostAuthCommand => ConnectionType == ConnectionType.SSH;

    // ---- SSH 协议层可选能力 ----
    //
    // 压缩对 SSH 与 SFTP 都有意义(两者走同一条 SSH 连接),显示条件跟 RequiresSshAuth;
    // 两个转发只挂在交互式 shell 上,SFTP 配置没有 shell,显示条件跟 SupportsPostAuthCommand。

    /// <summary>当前是否选了 SSH Agent 认证;控制那一行说明的可见性。</summary>
    public bool IsAgentAuth => AuthMethod == AuthMethod.Agent;

    /// <summary>当前是否选了 Tailscale SSH 认证。</summary>
    public bool IsTailscaleAuth => AuthMethod == AuthMethod.Tailscale;

    // ---- 系统密钥库(Windows CNG:证书存储、TPM、智能卡里导不出来的钥) ----

    /// <summary>当前是否选了系统密钥库认证。</summary>
    public bool IsKeyStoreAuth => AuthMethod == AuthMethod.KeyStore;

    /// <summary>
    /// 认证方式里要不要有「系统密钥库」那一段:只有 Windows 有;别的平台上不显示 ——
    /// 除非这条配置本来就是它(从 Windows 同步过来),那就照样显示选中的那一段,而不是让它看起来像什么都没选。
    /// </summary>
    public bool ShowKeyStoreAuthOption => (_systemKeyStore?.IsSupported ?? OperatingSystem.IsWindows()) || IsKeyStoreAuth;

    /// <summary>选钥那几栏是否显示(SSH / SFTP 且选了系统密钥库;它不走共享凭据)。</summary>
    public bool ShowKeyStoreFields => RequiresSshAuth && IsKeyStoreAuth;

    /// <summary>这台电脑没有系统密钥库(配置是从 Windows 同步过来的):选钥那一栏换成一句说明。</summary>
    public bool KeyStoreUnsupportedHere => !(_systemKeyStore?.IsSupported ?? OperatingSystem.IsWindows());

    /// <summary>密钥库里能用的钥;存过的那一把即使这台电脑上找不到也列着(标明找不到),免得打开再保存就悄悄丢了。</summary>
    public ObservableCollection<SystemKeyChoice> SystemKeyChoices { get; } = [];

    /// <summary>选中的钥。</summary>
    public SystemKeyChoice? SelectedSystemKey
    {
        get;
        set
        {
            // ComboBox 在 ItemsSource 重建的瞬间会回写一个 null,不能让它把选好的钥清掉。
            if (value is null)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _keyStoreKey = value.Reference;
        }
    }

    /// <summary>正在列密钥库里的钥。</summary>
    public bool IsLoadingSystemKeys
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>列完了,一把能用的都没有(也没有存过的):给一行怎么建钥的说明。</summary>
    public bool HasNoSystemKeys
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>重新列一遍(插了智能卡、刚在 TPM 里建了钥之后)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshSystemKeysCommand { get; }

    /// <summary>复制选中那把钥的公钥(OpenSSH 一行),贴进服务器的 authorized_keys。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopySystemKeyPublicKeyCommand { get; }

    /// <summary>
    /// 列密钥库里的钥:第一次选到系统密钥库(或打开一条本来就是它的配置)时列一次,之后只在点「刷新」时再列。
    /// 存过的那一把排在最前,找到了就换成带证书主题、位置与指纹的那一行。
    /// </summary>
    internal async Task LoadSystemKeysAsync(bool force = false)
    {
        if ((_systemKeysLoaded && !force) || IsLoadingSystemKeys)
        {
            return;
        }
        _systemKeysLoaded = true;
        IsLoadingSystemKeys = true;
        IReadOnlyList<SystemKeyInfo> found = [];
        try
        {
            if (_systemKeyStore is { IsSupported: true } store)
            {
                found = await store.ListKeysAsync();
            }
        }
        catch (Exception ex)
        {
            // 列不出来就只剩存过的那一把,照样能保存。
            System.Diagnostics.Trace.WriteLine($"[KeyStore] Listing keys failed: {ex.Message}");
        }
        finally
        {
            IsLoadingSystemKeys = false;
        }

        // 由界面线程上的属性 setter / 命令发起、全程不 ConfigureAwait(false),回到这里时仍在界面线程。
        SystemKeyReference? current = _keyStoreKey;
        List<SystemKeyChoice> choices = [.. found.Select(static key => SystemKeyChoice.Found(key))];
        SystemKeyChoice? selected = current is null ? null : choices.FirstOrDefault(c => c.Matches(current));
        if (current is not null && selected is null)
        {
            selected = SystemKeyChoice.Missing(current);
            choices.Insert(0, selected);
        }
        SystemKeyChoices.Clear();
        foreach (SystemKeyChoice choice in choices)
        {
            SystemKeyChoices.Add(choice);
        }
        HasNoSystemKeys = SystemKeyChoices.Count == 0;
        if (selected is not null)
        {
            SelectedSystemKey = selected;
        }
    }

    private async Task CopySystemKeyPublicKeyAsync()
    {
        if (CopyToClipboard is { } copy && SelectedSystemKey?.PublicKeyLine is { Length: > 0 } line)
        {
            await copy(line);
        }
    }

    /// <summary>认证方式分段按钮:点哪段切到哪种认证。</summary>
    public ReactiveCommand<AuthMethod, RxVoid> SelectAuthMethodCommand { get; }

    /// <summary>启用压缩(高延迟 / 低带宽链路)。</summary>
    public bool SshCompression
    {
        get => _sshCompression;
        set => this.RaiseAndSetIfChanged(ref _sshCompression, value);
    }

    /// <summary>
    /// 按键时序混淆(OpenSSH 的 <c>ObscureKeystrokeTiming</c>);只挂在交互式 shell 上,显示条件跟 <see cref="SupportsPostAuthCommand" />。
    /// </summary>
    public bool SshObscureKeystrokeTiming
    {
        get => _sshObscureKeystrokeTiming;
        set => this.RaiseAndSetIfChanged(ref _sshObscureKeystrokeTiming, value);
    }

    // ---- 算法清单(连老设备)----
    //
    // 与压缩同一个显示条件(SSH 与 SFTP 都有):两者走同一条 SSH 连接。
    // 「默认」取决于有没有放开老算法,所以老算法开关一变,四个提示与校验都要跟着重算。

    /// <summary>放开老算法(<c>diffie-hellman-group14-sha1</c>、<c>ssh-rsa</c>、<c>hmac-sha1</c>),追加在默认之后。</summary>
    public bool SshLegacyAlgorithms
    {
        get => _sshLegacyAlgorithms;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshLegacyAlgorithms, value);
            RaiseAlgorithmsChanged();
        }
    }

    /// <summary>展开四个自定义清单;关着时不存(与 X11 关着时不存显示地址同一个理由)。</summary>
    public bool SshCustomAlgorithms
    {
        get => _sshCustomAlgorithms;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshCustomAlgorithms, value);
            this.RaisePropertyChanged(nameof(SshAlgorithmsError));
        }
    }

    /// <summary>密钥交换算法清单(OpenSSH 写法);空 = 默认。</summary>
    public string? SshKexAlgorithms
    {
        get => _sshKexAlgorithms;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshKexAlgorithms, value);
            this.RaisePropertyChanged(nameof(SshAlgorithmsError));
        }
    }

    /// <summary>主机密钥算法清单(OpenSSH 写法);空 = 默认。</summary>
    public string? SshHostKeyAlgorithms
    {
        get => _sshHostKeyAlgorithms;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshHostKeyAlgorithms, value);
            this.RaisePropertyChanged(nameof(SshAlgorithmsError));
        }
    }

    /// <summary>加密算法清单(OpenSSH 写法);空 = 默认。</summary>
    public string? SshCiphers
    {
        get => _sshCiphers;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshCiphers, value);
            this.RaisePropertyChanged(nameof(SshAlgorithmsError));
        }
    }

    /// <summary>MAC 算法清单(OpenSSH 写法);空 = 默认。</summary>
    public string? SshMacs
    {
        get => _sshMacs;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshMacs, value);
            this.RaisePropertyChanged(nameof(SshAlgorithmsError));
        }
    }

    /// <summary>密钥交换输入框的悬停提示:此刻的默认清单与另可加的算法。</summary>
    public string SshKexAlgorithmsTip => AlgorithmTip(SshAlgorithmKind.KeyExchange);

    /// <summary>主机密钥输入框的悬停提示。</summary>
    public string SshHostKeyAlgorithmsTip => AlgorithmTip(SshAlgorithmKind.HostKey);

    /// <summary>加密输入框的悬停提示。</summary>
    public string SshCiphersTip => AlgorithmTip(SshAlgorithmKind.Cipher);

    /// <summary>MAC 输入框的悬停提示。</summary>
    public string SshMacsTip => AlgorithmTip(SshAlgorithmKind.Mac);

    /// <summary>
    /// 四个清单里第一处写错的地方(带类别);都成立、或清单没展开时为 null。不为 null 时保存 / 连接 / 测试都灰着。
    /// </summary>
    public string? SshAlgorithmsError
    {
        get
        {
            if (!_sshCustomAlgorithms || !RequiresSshAuth)
            {
                return null;
            }
            foreach ((SshAlgorithmKind kind, string? spec) in AlgorithmSpecs())
            {
                if (SshAlgorithmPreferences.Validate(kind, spec, _sshLegacyAlgorithms) is { } error)
                {
                    return Strings.Format("Ssh_AlgoSpecInvalid", SshAlgorithmPreferences.Label(kind), error);
                }
            }
            return null;
        }
    }

    private (SshAlgorithmKind Kind, string? Spec)[] AlgorithmSpecs() =>
    [
        (SshAlgorithmKind.KeyExchange, _sshKexAlgorithms),
        (SshAlgorithmKind.HostKey, _sshHostKeyAlgorithms),
        (SshAlgorithmKind.Cipher, _sshCiphers),
        (SshAlgorithmKind.Mac, _sshMacs),
    ];

    private string AlgorithmTip(SshAlgorithmKind kind)
    {
        IReadOnlyList<string> defaults = SshAlgorithmPreferences.Defaults(kind, _sshLegacyAlgorithms);
        string[] more = [.. SshAlgorithmPreferences.Available(kind).Where(a => !defaults.Contains(a, StringComparer.Ordinal))];
        string tip = Strings.Format("Profile_SshAlgoTipDefaults", string.Join(", ", defaults));
        return more.Length == 0 ? tip : $"{tip}\n{Strings.Format("Profile_SshAlgoTipMore", string.Join(", ", more))}";
    }

    private void RaiseAlgorithmsChanged()
    {
        this.RaisePropertyChanged(nameof(SshKexAlgorithmsTip));
        this.RaisePropertyChanged(nameof(SshHostKeyAlgorithmsTip));
        this.RaisePropertyChanged(nameof(SshCiphersTip));
        this.RaisePropertyChanged(nameof(SshMacsTip));
        this.RaisePropertyChanged(nameof(SshAlgorithmsError));
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>把本机 ssh-agent 转发给远端。</summary>
    public bool SshAgentForwarding
    {
        get => _sshAgentForwarding;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshAgentForwarding, value);
            if (value && _sshAgentForwardRestrict)
            {
                _ = LoadAgentForwardKeysAsync();
            }
        }
    }

    /// <summary>
    /// 只转发下面勾选的密钥;关着时 agent 里的钥全部可见(与 <c>ssh -A</c> 一致)。
    /// </summary>
    public bool SshAgentForwardRestrict
    {
        get => _sshAgentForwardRestrict;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshAgentForwardRestrict, value);
            if (value)
            {
                _ = LoadAgentForwardKeysAsync();
            }
        }
    }

    /// <summary>远端每次请求签名时弹窗确认。</summary>
    public bool SshAgentForwardConfirm
    {
        get => _sshAgentForwardConfirm;
        set => this.RaiseAndSetIfChanged(ref _sshAgentForwardConfirm, value);
    }

    /// <summary>
    /// 这条连接用哪个 ssh-agent(<see cref="SshSessionOptions.AgentEndpoint" />);空 = 系统默认。
    /// 「SSH Agent」认证与 agent 转发都用它。
    /// </summary>
    public string? SshAgentEndpoint
    {
        get => _sshAgentEndpoint;
        set
        {
            this.RaiseAndSetIfChanged(ref _sshAgentEndpoint, value);
            this.RaisePropertyChanged(nameof(SshAgentEndpointError));
        }
    }

    /// <summary>
    /// 端点在这台机器上用不了的原因;没填或写得对时为 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 只提示、不拦保存:配置会随云同步到别的机器,在 macOS 上写的套接字路径拿到 Windows 上看就「不是完整路径」,
    /// 为此让这边连别的字段都存不下去不合理;连不上时连接报错会点名这个端点。
    /// </remarks>
    public string? SshAgentEndpointError => SshAgentEndpoints.Check(_sshAgentEndpoint) switch
    {
        SshAgentEndpointProblem.PipeNotSupported => Strings.Get("Profile_SshAgentEndpointPipeNotSupported"),
        SshAgentEndpointProblem.PipeNameMissing => Strings.Get("Profile_SshAgentEndpointPipeNameMissing"),
        SshAgentEndpointProblem.NotFullPath => Strings.Get("Profile_SshAgentEndpointNotFullPath"),
        SshAgentEndpointProblem.TooLong => Strings.Format("Profile_SshAgentEndpointTooLong", SshAgentEndpoints.MaxSocketPathBytes(OperatingSystem.IsMacOS())),
        _ => null
    };

    /// <summary>端点输入框的占位文字:按平台给一个例子。</summary>
    public static string SshAgentEndpointPlaceholder =>
        Strings.Get(OperatingSystem.IsWindows() ? "Profile_SshAgentEndpointPlaceholderWindows" : "Profile_SshAgentEndpointPlaceholderUnix");

    /// <summary>「只转发指定密钥」的候选清单。</summary>
    public ObservableCollection<AgentForwardKeyChoice> AgentForwardKeyChoices { get; } = [];

    /// <summary>候选清单是空的(agent 没在跑、~/.ssh 下也没有公钥);界面据此给一行说明。</summary>
    public bool HasNoAgentForwardKeyChoices
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 填候选清单:配置里存过的 → agent 里的 → ~/.ssh 下的,按指纹去重。只在第一次需要时加载。
    /// </summary>
    /// <remarks>
    /// 存过的钥即使已经不在 agent 与 ~/.ssh 里也列出来并保持勾选 —— 否则打开再保存一次,
    /// 限定就悄悄少了一把(更糟的是少到零,那条会话就不再转发)。
    /// agent 没在跑时 <see cref="ISshKeyService.ListAgentKeysAsync" /> 返回空,不当错误。
    /// </remarks>
    internal async Task LoadAgentForwardKeysAsync()
    {
        if (_agentForwardKeysLoaded)
        {
            return;
        }
        _agentForwardKeysLoaded = true;

        HashSet<string> saved = [with(StringComparer.Ordinal)];
        List<AgentForwardKeyChoice> choices = [];
        foreach (string line in _savedAgentForwardKeys)
        {
            if (AgentForwardKeyChoice.TryCreate(line, null, Strings.Get("Profile_SshAgentKeySourceSaved")) is { } choice
                && saved.Add(choice.Fingerprint))
            {
                choice.IsSelected = true;
                choices.Add(choice);
            }
        }

        List<(string Line, string Label, string Source)> discovered = [];
        if (_keyService is { } keys)
        {
            try
            {
                // 按连接指定了 agent 端点时列的是那个 agent 里的钥(转发的就是它)。
                foreach (SshKeyInfo key in await keys.ListAgentKeysAsync(SshAgentEndpoints.Normalize(_sshAgentEndpoint)))
                {
                    discovered.Add((key.PublicKeyLine!, key.Name, Strings.Get("Profile_SshAgentKeySourceAgent")));
                }
                foreach (SshKeyInfo key in await keys.ListKeysAsync())
                {
                    if (key.PublicKeyLine is { } line)
                    {
                        discovered.Add((line, key.Name, Strings.Get("Profile_SshAgentKeySourceFile")));
                    }
                }
            }
            catch
            {
                // 列不出来就只剩存过的那些,照样能保存。
            }
        }

        foreach ((string line, string label, string source) in discovered)
        {
            if (AgentForwardKeyChoice.TryCreate(line, label, source) is not { } choice)
            {
                continue;
            }
            int existing = choices.FindIndex(c => c.Fingerprint == choice.Fingerprint);
            if (existing < 0)
            {
                choices.Add(choice);
            }
            else if (saved.Contains(choice.Fingerprint) && choices[existing].Source == Strings.Get("Profile_SshAgentKeySourceSaved"))
            {
                // 存过、现在又找到了:换成带名字与来源的那一行,勾选照旧。
                choice.IsSelected = true;
                choices[existing] = choice;
            }
        }

        // 由界面线程上的属性 setter 发起、全程不 ConfigureAwait(false),回到这里时仍在界面线程。
        AgentForwardKeyChoices.Clear();
        foreach (AgentForwardKeyChoice choice in choices)
        {
            AgentForwardKeyChoices.Add(choice);
        }
        HasNoAgentForwardKeyChoices = AgentForwardKeyChoices.Count == 0;
    }

    /// <summary>
    /// 保存时实际写进配置的限定:没打开限定为 <see langword="null" />;清单还没加载过就原样带回存过的那些。
    /// </summary>
    private List<string>? SelectedAgentForwardKeys() =>
        !_sshAgentForwardRestrict ? null
        : _agentForwardKeysLoaded ? [.. AgentForwardKeyChoices.Where(c => c.IsSelected).Select(c => c.PublicKeyLine)]
        : [.. _savedAgentForwardKeys];

    /// <summary>X11 转发;关着时显示地址与受信任两项收起。</summary>
    public bool SshX11Forwarding
    {
        get => _sshX11Forwarding;
        set => this.RaiseAndSetIfChanged(ref _sshX11Forwarding, value);
    }

    /// <summary>本机 X 显示;留空 = <c>DISPLAY</c> 环境变量,再没有就是 <c>localhost:0.0</c>。</summary>
    public string? SshX11Display
    {
        get => _sshX11Display;
        set => this.RaiseAndSetIfChanged(ref _sshX11Display, value);
    }

    /// <summary>受信任的 X11 转发(<c>ssh -Y</c>)。</summary>
    public bool SshX11Trusted
    {
        get => _sshX11Trusted;
        set => this.RaiseAndSetIfChanged(ref _sshX11Trusted, value);
    }

    /// <summary>X11 显示地址输入框的占位:没配 <c>DISPLAY</c> 时实际会用的那个值。</summary>
    public static string SshX11DisplayPlaceholder =>
        Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } display
            ? display
            : SshSessionOptions.DefaultX11Display;

    // ---- 会话级终端覆盖项(F-06) ----

    /// <summary>
    /// 「跟随全局」在各下拉里的占位项。
    /// </summary>
    /// <remarks>
    /// 下拉不能只放具体取值:那样用户一旦选过就再也回不到"跟随全局",而清空一个
    /// <c>ComboBox</c> 在界面上并不是一个显而易见的动作。显式给一项,选它即写回 null。
    /// </remarks>
    public string FollowGlobalOption { get; } = Strings.Get("Profile_FollowGlobal");

    /// <summary>可选编码,首项为「跟随全局」。</summary>
    public string[] EncodingOptions { get; } = [Strings.Get("Profile_FollowGlobal"), .. TerminalEncodings.All];

    /// <summary>可选终端类型,首项为「跟随全局」;其余与设置页的全局下拉同一张表。</summary>
    public string[] TerminalTypeOptions { get; } =
        [Strings.Get("Profile_FollowGlobal"), .. TerminalTypes.All];

    /// <summary>会话级编码覆盖;「跟随全局」= 不覆盖。</summary>
    public string OverrideEncoding
    {
        get => _overrideEncoding ?? FollowGlobalOption;
        set => this.RaiseAndSetIfChanged(ref _overrideEncoding, Normalize(value));
    }

    /// <summary>会话级终端类型覆盖;「跟随全局」= 不覆盖。</summary>
    public string OverrideTerminalType
    {
        get => _overrideTerminalType ?? FollowGlobalOption;
        set => this.RaiseAndSetIfChanged(ref _overrideTerminalType, Normalize(value));
    }

    /// <summary>
    /// 标签页颜色(<c>#RRGGBB</c>);留空 = 按配置 id 自动配色。
    /// </summary>
    /// <remarks>
    /// 「生产标红、测试标绿」是运维最常提的诉求,而自动配色恰恰保证同一批机器颜色各不相同 ——
    /// 两个目标没法用同一套规则同时满足。
    /// </remarks>
    public string? OverrideTabColor
    {
        get => _overrideTabColor;
        set => this.RaiseAndSetIfChanged(ref _overrideTabColor, value);
    }

    /// <summary>登录后自动切换到的目录;留空 = 不切换。</summary>
    public string? OverrideStartupDirectory
    {
        get => _overrideStartupDirectory;
        set => this.RaiseAndSetIfChanged(ref _overrideStartupDirectory, value);
    }

    /// <summary>
    /// 会话级保活心跳间隔(秒);<c>-1</c> = 跟随全局。
    /// </summary>
    /// <remarks>
    /// 用 <c>-1</c> 而不是 null 表示"跟随",是因为承载它的是 <c>NumericUpDown</c> ——
    /// 那个控件的空值语义已经被 <c>NumericInputGuard</c> 占去表示"正在输入中",
    /// 再让它兼表"跟随全局"就分不清了。<c>-1</c> 在界面上显示为最小值旁的一格,
    /// 提示文字说明它的含义。
    /// </remarks>
    public int OverrideKeepAliveSeconds
    {
        get => _overrideKeepAliveSeconds;
        set => this.RaiseAndSetIfChanged(
            ref _overrideKeepAliveSeconds,
            Math.Clamp(value, FollowGlobalKeepAlive, TerminalOverrides.MaxKeepAliveSeconds));
    }

    /// <summary>保活输入框里代表「跟随全局」的哨兵值。</summary>
    /// <remarks>同 <see cref="MaxPostAuthCommandDelaySeconds" />:编译绑定只解析实例成员。</remarks>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public int FollowGlobalKeepAlive => -1;

    /// <summary>保活输入框的上限,供界面绑定。</summary>
    /// <remarks>同上,不能写成 static,否则 <c>Maximum</c> 会静默绑空、上限消失。</remarks>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public int MaxKeepAliveSeconds => TerminalOverrides.MaxKeepAliveSeconds;

    /// <summary>
    /// 本会话的防空闲注入间隔(秒);<c>0</c> = 关闭。
    /// </summary>
    /// <remarks>
    /// 这一项没有「跟随全局」可选,因此 0 就是关闭,不必像保活那样借一个 <c>-1</c> 当哨兵:
    /// 会按空闲踢人的只是特定那几台机器,一个全局开关反而会把注入撒到不需要它的会话上。
    /// 它与上面的保活是两件事——保活防的是链路被回收,这一项防的是服务端 shell 嫌你闲。
    /// </remarks>
    public int AntiIdleSeconds
    {
        get => _antiIdleSeconds;
        set => this.RaiseAndSetIfChanged(
            ref _antiIdleSeconds,
            Math.Clamp(value, 0, TerminalOverrides.MaxAntiIdleSeconds));
    }

    /// <summary>防空闲输入框的上限,供界面绑定。</summary>
    /// <remarks>同上,不能写成 static。</remarks>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public int MaxAntiIdleSeconds => TerminalOverrides.MaxAntiIdleSeconds;

    /// <summary>下拉选中「跟随全局」时归一化为 null。</summary>
    private string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == FollowGlobalOption ? null : value;

    /// <summary>
    /// FTP / FTPS 连上后远程面板默认打开的目录(「高级」页);留空 = 沿用登录工作目录。
    /// 上传目标常年是同一个 <c>/var/www/html</c>,而 FTP 给的登录目录往往就是根,
    /// 每连一次手点四五层纯属重复劳动。
    /// </summary>
    public string? FtpInitialRemotePath
    {
        get => _ftpInitialRemotePath;
        set => this.RaiseAndSetIfChanged(ref _ftpInitialRemotePath, value);
    }

    /// <summary>
    /// 「服务器时区」下拉候选(「高级」页,仅 FTP / FTPS):「与本机相同」+ UTC−12:00…UTC+14:00。
    /// 首次读取时才建,那时配置里现存的值已经载入(不在表里的也会被插进去,见 <see cref="FtpTimeZoneOption.Build" />)。
    /// </summary>
    public IReadOnlyList<FtpTimeZoneOption> FtpTimeZoneOptions =>
        field ??= FtpTimeZoneOption.Build(_ftpServerUtcOffsetMinutes);

    /// <summary>
    /// 选中的服务器时区:目录比较与同步按它换算 LIST 的时间(见 <see cref="FtpSettings.ServerUtcOffsetMinutes" />)。
    /// 「与本机相同」存为 null,即旧行为。
    /// </summary>
    public FtpTimeZoneOption SelectedFtpTimeZone
    {
        get => FtpTimeZoneOptions.FirstOrDefault(o => o.Minutes == _ftpServerUtcOffsetMinutes) ?? FtpTimeZoneOptions[0];
        set
        {
            int? minutes = value?.Minutes;
            if (minutes == _ftpServerUtcOffsetMinutes)
            {
                return;
            }
            _ftpServerUtcOffsetMinutes = minutes;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>分组下拉候选:“未分组” + 全部已保存分组。</summary>
    public ObservableCollection<GroupOption> Groups { get; }

    /// <summary>当前选中的分组项;null 项表示未分组。</summary>
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

    /// <summary>保存配置命令;成功返回保存后的 <see cref="SessionProfile" />,失败返回 null。</summary>
    public ReactiveCommand<RxVoid, SessionProfile?> SaveCommand { get; }

    /// <summary>连接命令;先保存配置,再请求宿主窗口在弹窗关闭后立即连接。</summary>
    public ReactiveCommand<RxVoid, SessionProfile?> ConnectCommand { get; }

    /// <summary>取消命令;关闭弹窗并返回 null。</summary>
    public ReactiveCommand<RxVoid, SessionProfile?> CancelCommand { get; }

    /// <summary>连接测试命令;不落库,仅探测能否连通并回填结果。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TestConnectionCommand { get; }

    /// <summary>
    /// 复制当前错误信息到剪贴板。连接失败的原因常是一长串服务端原文(认证链、
    /// 主机密钥指纹、栈顶异常),要拿去搜索或贴给同事,靠眼睛照抄不现实。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyErrorCommand { get; }

    /// <summary>写系统剪贴板的回调;由视图注入(视图模型层拿不到 TopLevel)。</summary>
    [Reactive]
    public partial Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>浏览私钥文件命令;由视图层挂接文件选择对话框。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BrowseKeyFileCommand { get; }

    /// <summary>浏览证书文件命令;由视图层挂接文件选择对话框。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BrowseCertificateFileCommand { get; }

    /// <summary>切换密码明文/掩码显示的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    /// <summary>选择 SSH 或 SFTP;Telnet/串口由插件贡献页签,不走这条命令。</summary>
    public ReactiveCommand<ConnectionType, RxVoid> SelectConnectionTypeCommand { get; }

    /// <summary>切到某个插件协议页签(按需触发该插件的惰性激活并渲染它的设置表单)。</summary>
    public ReactiveCommand<string, RxVoid> SelectPluginProtocolCommand { get; }

    /// <summary>
    /// 重新向插件索取「主机」下拉的候选项。
    /// <para>
    /// 为什么非要有这么个按钮:串口适配器是**热插拔**的,而用户很可能是先打开连接对话框、
    /// 才想起去插线。没有刷新,他就得把对话框关掉再开一次。
    /// </para>
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshHostChoicesCommand { get; }

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

    /// <summary>
    /// SSH 配置候选。表单可能在候选加载完之前就渲染(协议激活与仓储读取是两条独立的异步链),
    /// 那时至少要给出"不经跳板机"这一项 —— 一个空下拉会让用户以为功能坏了。
    /// </summary>
    private List<PluginSessionChoice> EnsureSshSessionChoices()
    {
        if (_sshSessionChoices.Count == 0)
        {
            _sshSessionChoices = [new(string.Empty, Strings.Get("Msg_DirectConnection"))];
        }
        return _sshSessionChoices;
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
            // 插件字段里的「已保存的 SSH 配置」选择器与跳板机候选同源,但只收 SSH/SFTP 类型
            // —— 拿一条 S3 配置去建隧道没有意义。第一项固定是"不经跳板机"。
            _sshSessionChoices = [new(string.Empty, Strings.Get("Msg_DirectConnection"))];
            foreach (SessionProfile profile in profiles
                                               .Where(p => p.Id != _profileId)
                                               .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (profile.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP)
                {
                    _sshSessionChoices.Add(new(
                        profile.Id.ToString("N"),
                        $"{profile.Name} ({HostAddress.FormatEndPoint(profile.Host, profile.Port)})"));
                }
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
            BeginBusy();
            ErrorMessage = null;
            await ResolveGroupFromTextAsync();
            // 限定了密钥却一把都没勾:存下去就是一条永远不转发的配置,而用户以为转发开着。
            if (SupportsPostAuthCommand && RequiresSshAuth && _sshAgentForwarding
                && SelectedAgentForwardKeys() is { Count: 0 })
            {
                throw new InvalidOperationException(Strings.Get("Profile_SshAgentForwardKeysEmpty"));
            }
            // 选了系统密钥库却没选钥:存下去就是一条连不上的配置。
            if (RequiresSshAuth && IsKeyStoreAuth && _keyStoreKey is null)
            {
                throw new InvalidOperationException(Strings.Get("Svc_KeyStoreKeyRequired"));
            }
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
            EndBusy();
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
            BeginBusy();
            ErrorMessage = null;
            ConnectionTestResult result = await _connectionWorkflowService.TestConnectionAsync(BuildProfile());
            LastTestSucceeded = result.Success;
            ErrorMessage = result.ErrorMessage;
            // 败在算法协商上时给一键补救(与终端标签同一份计算);FTP 与插件协议不走 SSH,没有这回事。
            AlgorithmRemedy = RequiresSshAuth ? SshAlgorithmRemedy.Find(result.Error) : null;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// 反馈条上的「放开这几个算法并重新测试」:与终端标签同一个确认框、同一套写法,确认后重新测试。
    /// </summary>
    /// <remarks>
    /// 终点那一跳就是正在编辑的这份:改的是表单里的自定义算法清单(展开它、把那一类写成补救后的样子),
    /// 按「保存」或「连接」才存下 —— 不替用户把表单里别的未保存改动一起落盘。
    /// 谈不成的是跳板时改跳板自己的配置并当场存下(表单里改不到别的配置),顺着表单里选的跳板往上数。
    /// </remarks>
    private async Task AllowAlgorithmsAndRetestAsync()
    {
        if (AlgorithmRemedy is not { } remedy || IsBusy || AllowAlgorithmsConfirmer is not { } confirm)
        {
            return;
        }
        SessionProfile profile = BuildProfile();
        SshAlgorithmRemedyFlow remedies = new(_sessionRepository, _connectionWorkflowService);
        if (await remedies.ConfirmAndSaveAsync(
                profile, remedy, SshAlgorithmRemedyFollowUp.Retest, confirm, message => ErrorMessage = message,
                () => AlgorithmRemedy == remedy && !IsBusy, formOnly: true)
            is null)
        {
            return;
        }
        if (remedy.HopDepth == 0)
        {
            // 照表单此刻生效的清单(收起时不算数)算出补救后的四类,写回表单并展开:用户看得见改了什么。
            SshSessionOptions next = remedy.ApplyTo(profile.Ssh);
            SshCustomAlgorithms = true;
            SshKexAlgorithms = next.KexAlgorithms;
            SshHostKeyAlgorithms = next.HostKeyAlgorithms;
            SshCiphers = next.Ciphers;
            SshMacs = next.Macs;
        }
        await TestConnectionAsync();
    }

    /// <summary>
    /// 把错误信息原文送进剪贴板,并在按钮上留一句「已复制」的回执 ——
    /// 没有回执就分不清"复制成功了"和"按钮没反应",用户只会再点两下。
    /// 回执 <see cref="CopyFeedbackDuration" /> 后自动退回;连点时先取消上一个定时器,
    /// 否则第二次点完会立刻被第一次的定时器把提示抹掉。
    /// </summary>
    private async Task CopyErrorAsync()
    {
        if (CopyToClipboard is not { } copy || ErrorMessage is not { Length: > 0 } message)
        {
            return;
        }
        await copy(message).ConfigureAwait(true);
        ErrorCopied = true;
        _copyFeedbackReset?.Dispose();
        _copyFeedbackReset = DispatcherTimer.RunOnce(() => ErrorCopied = false, CopyFeedbackDuration);
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
        // 排在已有分组之后:取现有最大序号 + 1。不能按「当前个数」取号 —— 分组可以删、也可以拖动重排(#571),
        // 序号早就不连续了,按个数取出来的号会和某个已有分组撞上,新分组就不一定落在最后。
        List<ServerGroup> stored = await _sessionRepository.GetAllGroupsAsync();
        var group = new ServerGroup
        {
            Name = text,
            SortOrder = stored.Count == 0 ? 0 : stored.Max(static g => g.SortOrder) + 1
        };
        await _sessionRepository.SaveGroupAsync(group);
        var option = new GroupOption(group.Id, group.Name);
        Groups.Add(option);
        SelectedGroup = option;
    }

    private SessionProfile BuildProfile()
    {
        // 显示名称留空时用 user@host 兜底,保证列表/标签页有可读名称。
        // 收起来的用户名不拿来拼:它不落盘(见 HidesPluginCredentials),也不该换个地方落进名称里。
        string user = HidesPluginCredentials ? string.Empty : Username;
        string name = string.IsNullOrWhiteSpace(Name) ? $"{user}@{EffectiveEndpoint().Host}" : Name.Trim();
        string? postAuthCommand = SupportsPostAuthCommand && _postAuthCommand?.Trim() is { Length: > 0 } trimmed
            ? trimmed
            : null;
        SessionProfile profile = BuildProfileCore(name, postAuthCommand);
        // 凭据来源那一栏不显示(匿名 FTP、无凭据协议)时,引用一并不存:存下去就是一条看不见、
        // 却会在切回来时突然生效的引用(#550)。
        if (CredentialSourceApplies && _credentialSource is { } reference)
        {
            profile.CredentialSource = reference;
            // 表单里收起来的那几栏可能还留着切换之前填的值:一起清掉。留着的话,「测试」会拿这份
            // 看不见的旧值去连(内存里有材料即视为本次手输,见 CredentialMaterial.HasInline),
            // 测出来的根本不是共享凭据。
            CredentialMaterial.ClearInline(profile);
        }
        CredentialMaterial.ClearTailscaleCredentials(profile);
        return profile;
    }

    private SessionProfile BuildProfileCore(string name, string? postAuthCommand)
    {
        (string host, int port) = EffectiveEndpoint();
        // 插件把两栏收起来了:收起之前填的用户名口令一起不存(理由见 HidesPluginCredentials)。
        bool hidesCredentials = HidesPluginCredentials;
        return new()
        {
            Id = _profileId,
            ConnectionType = ConnectionType,
            Name = name,
            Host = host,
            Port = port,
            Username = hidesCredentials ? string.Empty : Username.Trim(),
            AuthMethod = AuthMethod,
            Password = IsTailscaleAuth || IsKeyStoreAuth || hidesCredentials ? null : SecureStringConvert.ToPlaintext(Password),
            RememberPassword = RememberPassword,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            CertificatePath = CertificatePath,
            // 只在选的就是系统密钥库时存引用:切到别的认证方式还留着,就是一条看不见、切回来却突然生效的引用。
            KeyStoreKey = RequiresSshAuth && IsKeyStoreAuth ? _keyStoreKey : null,
            GroupId = GroupId,
            IsPinned = _isPinned,
            LastConnectedAt = _lastConnectedAt,
            AutoStartTunnelIds = _autoStartTunnelIds is null ? null : [.. _autoStartTunnelIds],
            DirectoryBookmarks = [.. _directoryBookmarks.Select(static bookmark => bookmark.Clone())],
            Tags = [.. TagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            // 首尾的空行空格去掉(悬停提示里一条空行也是一截空白);只有空白的归 null,
            // 免得一个回车就让这条连接带上一个空的悬停提示。行内的换行与缩进原样保留。
            Notes = _notes?.Trim() is { Length: > 0 } notes ? notes : null,
            JumpHostProfileId = _jumpHostProfileId,
            // 只有 SSH 有 shell 通道可注入;换到别的协议还把它存回去,存下的就是一条永远不执行的
            // 命令,而且切回 SSH 时会诈尸执行一次。空白也一律归 null,免得存进一个只有空格的命令。
            PostAuthCommand = postAuthCommand,
            PostAuthCommandDelaySeconds = _postAuthCommandDelaySeconds,
            // 只有 FTP 才落这块设置:其余协议保持 null,旧数据与旧版本读取零影响。
            Ftp = ConnectionType == ConnectionType.FTP
                ? new FtpSettings
                {
                    EncryptionMode = FtpEncryption,
                    DataConnectionMode = FtpPassive ? FtpDataConnectionMode.Passive : FtpDataConnectionMode.Active,
                    Anonymous = FtpAnonymous,
                    TrustedCertificateThumbprint = _ftpTrustedThumbprint,
                    MaxConnections = _ftpMaxConnections,
                    // setter 自带归一化(补前导 /、去尾斜杠、空串归 null),这里原样交给它。
                    InitialRemotePath = _ftpInitialRemotePath,
                    ServerUtcOffsetMinutes = _ftpServerUtcOffsetMinutes,
                }
                : null,
            // 插件协议:只存协议 id 与它自己声明的那些字段。宿主对这些键一无所知,
            // 这正是把 S3 之类的协议移出宿主的前提。
            PluginProtocolId = ConnectionType == ConnectionType.Plugin ? PluginProtocolId : null,
            PluginSettings = ConnectionType == ConnectionType.Plugin ? CollectPluginValues(secrets: false) : null,
            // 机密与非机密分成两个字典:仓储层在落盘那一刻并不知道某个协议哪些字段是机密
            // (那在插件里),分开存才能做到「机密永远加密」这条不依赖任何查表的硬保证。
            PluginSecrets = ConnectionType == ConnectionType.Plugin ? CollectPluginValues(secrets: true) : null,
            // 会话级终端覆盖(F-06)。一项都没设时整个对象存 null,而不是一个全空对象:
            // 后者会让"有没有覆盖"这件事有了两种表示,也给每条老配置的 JSON 平白多一段。
            Terminal = BuildTerminalOverrides(),
            Ssh = BuildSshOptions()
        };
    }

    /// <summary>把界面上的 SSH 可选能力收成一个对象;一项都没开(或不是 SSH 系协议)时返回 null。</summary>
    /// <remarks>
    /// 两个转发只在 SSH 上存:SFTP 配置没有交互式 shell,存下来的开关永远不生效,
    /// 切回 SSH 时却会突然起作用 —— 与「认证后执行命令」同一个理由。
    /// X11 关着时显示地址也不存,免得留下一个看不见、却会在下次打开 X11 时悄悄生效的旧值。
    /// </remarks>
    private SshSessionOptions? BuildSshOptions()
    {
        if (!RequiresSshAuth)
        {
            return null;
        }
        bool shell = SupportsPostAuthCommand;
        SshSessionOptions options = new()
        {
            Compression = _sshCompression,
            // 与两个转发同理:SFTP 配置没有交互式 shell,存下来永远不生效,切回 SSH 时却会突然起作用。
            ObscureKeystrokeTiming = shell && _sshObscureKeystrokeTiming,
            AgentForwarding = shell && _sshAgentForwarding,
            AgentForwardKeys = shell && _sshAgentForwarding ? SelectedAgentForwardKeys() : null,
            AgentForwardConfirm = shell && _sshAgentForwarding && _sshAgentForwardConfirm,
            // SSH 与 SFTP 都存:SFTP 也能用 agent 认证。
            AgentEndpoint = SshAgentEndpoints.Normalize(_sshAgentEndpoint),
            X11Forwarding = shell && _sshX11Forwarding,
            X11Display = shell && _sshX11Forwarding && !string.IsNullOrWhiteSpace(_sshX11Display)
                ? _sshX11Display.Trim()
                : null,
            X11Trusted = _sshX11Trusted,
            LegacyAlgorithms = _sshLegacyAlgorithms,
            // 清单收起时不存:看不见的旧写法不该在下次展开前悄悄生效。
            KexAlgorithms = _sshCustomAlgorithms ? TrimToNull(_sshKexAlgorithms) : null,
            HostKeyAlgorithms = _sshCustomAlgorithms ? TrimToNull(_sshHostKeyAlgorithms) : null,
            Ciphers = _sshCustomAlgorithms ? TrimToNull(_sshCiphers) : null,
            Macs = _sshCustomAlgorithms ? TrimToNull(_sshMacs) : null
        };
        return options.IsEmpty ? null : options;
    }

    /// <summary>把界面上的六个会话级终端项收成一个对象;一项都没设时返回 null。</summary>
    private TerminalOverrides? BuildTerminalOverrides()
    {
        TerminalOverrides overrides = new()
        {
            Encoding = _overrideEncoding,
            TerminalType = _overrideTerminalType,
            TabColor = string.IsNullOrWhiteSpace(_overrideTabColor) ? null : _overrideTabColor.Trim(),
            // 图标在「常规」页挑,对每一种协议都生效;认不出的 id 存成 null(跟协议)。
            TabIcon = SessionTabIcons.Normalize(_tabIcon),
            StartupDirectory = string.IsNullOrWhiteSpace(_overrideStartupDirectory)
                ? null
                : _overrideStartupDirectory.Trim(),
            KeepAliveSeconds = _overrideKeepAliveSeconds < 0 ? null : _overrideKeepAliveSeconds,
            // 0 存回 null 而不是 0:两者对运行时是一回事(都不注入),但存 null 才不会给
            // 每条从没碰过这一项的配置留下一段"我设过、设的是关"的假痕迹。
            AntiIdleSeconds = _antiIdleSeconds <= 0 ? null : _antiIdleSeconds
        };
        return overrides.IsEmpty ? null : overrides;
    }

    /// <summary>
    /// 按当前分页**与字段自己声明的显示条件**下发插件字段的行可见性,
    /// 并刷新各自定义页签在不在、「高级」页签上的字段数与圆点。
    /// <para>
    /// 插件字段在「常规」、自定义页、「高级」上各画一份,绑的是同一批行;每一行归哪一页看它的页键
    /// (<see cref="PluginProtocolFieldViewModel.PageKey" />)。同一时刻只显示一页,所以每一行只要一个可见性:
    /// 属于当前这一页,且是表单行(<see cref="IsFormRowOn" />)。
    /// </para>
    /// <para>
    /// 藏的是**行**而不是整个列表:行本身早就渲染好了,只是 IsVisible 变化 ——
    /// 若改成往一个隐藏容器里灌行,那些行会占着高度却画不出来(踩过)。
    /// </para>
    /// <para>
    /// 两个条件是**与**关系:一个当前不适用的字段(哨兵专用的主节点名,而形态选的是独立)
    /// 切到「高级」页也不该出现。
    /// </para>
    /// </summary>
    private void ApplyPluginFieldVisibility()
    {
        string page = CurrentPluginPageKey;
        foreach (PluginProtocolFieldViewModel field in PluginFields)
        {
            // 出卡片时变体字段那一行永不显示:表头「类型:X」替代它(决策 D11)。
            field.IsRowVisible = IsFormRowOn(field, page);
        }
        // 一页的字段全被显示条件藏掉时,它的页签收起;停在那一页的话,下面的 RefreshSectionAvailability 落回「常规」。
        RefreshPluginPageTabs();
        this.RaisePropertyChanged(nameof(AdvancedBadge));
        this.RaisePropertyChanged(nameof(HasAdvancedBadge));
        this.RaisePropertyChanged(nameof(IsAdvancedSectionModified));
        this.RaisePropertyChanged(nameof(ShowPluginGeneralFields));
        // 字段进出会改变「高级」页在不在:最后一个高级字段没了,停在那一页就是一片空白。
        RefreshSectionAvailability();
    }

    /// <summary>字段声明的显示条件此刻是否成立(没声明条件即恒成立)。</summary>
    private bool IsApplicable(PluginProtocolFieldViewModel field) =>
        field.Field.VisibleWhen is not { } condition || condition.IsSatisfiedBy(PluginFieldValue);

    /// <summary>
    /// 这一行会不会画在表单里:显示条件成立,且不是被「选类型」卡片替代掉的变体字段。
    /// 行可见性、「常规」页那一节在不在、「高级」页签上的字段数用的是同一条判定 ——
    /// 否则变体字段是「常规」页唯一的字段时,会留下一个只有标题的空节。
    /// </summary>
    private bool IsFormRow(PluginProtocolFieldViewModel field) => IsApplicable(field) && !IsTypePickerRow(field);

    /// <summary>
    /// 字段进出集合时接线/退订,并重算一次可见性。
    /// <para>
    /// 退订必须与移除同一条路径,漏掉就是每次切协议留一批活着的处理器,
    /// 用户来回点几次页签后一次赋值会触发十几遍可见性重算。
    /// </para>
    /// </summary>
    private void OnPluginFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (PluginProtocolFieldViewModel row in e.OldItems?.OfType<PluginProtocolFieldViewModel>() ?? [])
        {
            row.PropertyChanged -= OnPluginFieldChanged;
        }
        foreach (PluginProtocolFieldViewModel row in e.NewItems?.OfType<PluginProtocolFieldViewModel>() ?? [])
        {
            // 先减后加:Replace 事件里同一个实例可能同时出现在两侧。
            row.PropertyChanged -= OnPluginFieldChanged;
            row.PropertyChanged += OnPluginFieldChanged;
        }
        ApplyPluginFieldVisibility();
    }

    /// <summary>
    /// 清空插件字段。<c>ObservableCollection.Clear</c> 发的是 <c>Reset</c> —— 它**不带**
    /// OldItems,所以退订不能只靠集合事件,这里显式走一遍。
    /// </summary>
    private void ClearPluginFields()
    {
        foreach (PluginProtocolFieldViewModel row in PluginFields)
        {
            row.PropertyChanged -= OnPluginFieldChanged;
        }
        PluginFields.Clear();
        // 主机下拉的候选项同属上一个协议:留着的话,从串口切回 SSH 再切到别的插件协议时,
        // 那个下拉里还挂着一串 COM 口。
        HostChoices.Clear();
    }

    /// <summary>被依赖字段的值一变就重算所有行的可见性(只关心值,不关心可见性自身的变化)。</summary>
    private void OnPluginFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginProtocolFieldViewModel.Text)
                           or nameof(PluginProtocolFieldViewModel.Toggle)
                           or nameof(PluginProtocolFieldViewModel.SelectedChoice))
        {
            ApplyPluginFieldVisibility();
            // 选变体的那个字段变了,连接框本身也要跟着换形态(端口、三格标签、要不要凭据)。
            // 只在**它**变时才做:别的字段变一次就重算一遍标签是白费的通知。
            if (sender is PluginProtocolFieldViewModel changed
                && _pluginBaseForm?.VariantKey is { Length: > 0 } key
                && string.Equals(changed.Key, key, StringComparison.Ordinal))
            {
                ApplyPluginVariant(followPort: true);
                // 表头「类型:X」读的就是这个字段。
                this.RaisePropertyChanged(nameof(CurrentTypeLabel));
            }
        }
    }

    /// <summary>
    /// 按键取表单里某个插件字段的当前值(显示条件求值用);字段不存在时返回
    /// <see langword="null" />,由条件自行按"取不到即不成立"处理。
    /// <para>
    /// 隐藏字段也在这里查得到:证书指纹之类不进表单,但拿它当显示条件是合法的。
    /// </para>
    /// </summary>
    private string? PluginFieldValue(string key)
    {
        foreach (PluginProtocolFieldViewModel candidate in PluginFields)
        {
            if (string.Equals(candidate.Key, key, StringComparison.Ordinal))
            {
                return candidate.Text;
            }
        }
        return (_pluginStored?.TryGetValue(key, out string? stored) == true ? stored : null)
               ?? (_pluginForm?.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))?.DefaultValue);
    }

    /// <summary>把表单字段收集成落盘字典;<paramref name="secrets" /> 决定收机密还是非机密那一半。</summary>
    private Dictionary<string, string>? CollectPluginValues(bool secrets)
    {
        // **以已存的那份为底**,再让表单里可见的字段覆盖上去。
        // 直接从空字典攒是不行的:隐藏字段(如用户确认信任后写回的证书指纹)压根不进表单,
        // 从空字典攒等于每保存一次就把它抹掉一次,下次连接又弹「证书不可信」。
        Dictionary<string, string>? stored = secrets ? _pluginStoredSecrets : _pluginStored;
        Dictionary<string, string> values = SessionProfile.CloneSettings(stored) ?? [with(StringComparer.Ordinal)];
        foreach (PluginProtocolFieldViewModel field in PluginFields.Where(f => f.IsSecret == secrets))
        {
            values[field.Key] = field.Text;
        }
        return values.Count == 0 ? stored : values;
    }

    /// <summary>
    /// 选中一个插件协议页签:切到 Plugin 类型、按需触发插件惰性激活、渲染它声明的表单。
    /// <para>
    /// 这个方法里挤着四件容易互相咬的事,注释逐条说明为什么这么写:
    /// 「换没换协议」的判定基准、旧值的保管、陈旧续体的作废、以及忙态的归属。
    /// </para>
    /// </summary>
    /// <param name="protocolId">协议 id。</param>
    public async Task SelectPluginProtocolAsync(string protocolId)
    {
        // 判定基准是 _loadedProtocolId(最近一次真正渲染过表单的协议),**不是** PluginProtocolId ——
        // 后者会被 SelectConnectionType 在切回内建协议时置 null,于是「点 SSH 再点回 S3」
        // 会被误判成换了协议,把 _pluginStored 一起清掉:隐藏字段(证书指纹)与机密
        // (sessionToken)就此静默永久丢失。
        bool switchedProtocol = _loadedProtocolId is { } loaded
                                && !string.Equals(loaded, protocolId, StringComparison.Ordinal);
        PluginProtocolId = protocolId;
        ConnectionType = ConnectionType.Plugin;
        foreach (PluginProtocolTabViewModel tabViewModel in PluginProtocols)
        {
            tabViewModel.IsSelected = tabViewModel.Id == protocolId;
        }
        // 插件协议同样没有私钥认证:与 SelectConnectionType 走同一套善后,
        // 否则从「私钥认证的 SSH」切过来后,认证方式下拉被隐藏、口令框也不显示,
        // 界面上再没有任何途径把它切回口令。
        NormalizeAuthMethodForProtocol();
        // 端口跟随用与内建协议**同一套**判定(它已把插件协议的默认端口一并算进「用户没手填过」)。
        // **目标端口先算好再写一次**:当前变体声明了端口就用它,否则用页签的默认端口。
        // 若直接写页签默认值,"选了 PostgreSQL(5432)再点一次同一个页签"会先被改写成 3306、
        // 然后从下面的早退处返回 —— 下拉仍是 PostgreSQL,端口框却成了 3306。
        // 换了协议时 _pluginBaseForm 还是上一个协议的,它的变体表与这个页签无关,不能拿来算。
        PluginProtocolTabViewModel? tab = PluginProtocols.FirstOrDefault(p => p.Id == protocolId);
        if (tab is not null && IsProtocolDefaultPort(Port))
        {
            Port = (switchedProtocol ? null : CurrentVariantDefaultPort()) ?? tab.DefaultPort;
        }
        if (switchedProtocol)
        {
            // 真的换了协议:上一个协议的字段与已存值都不能留,否则等激活的这段时间里
            // 表单还是旧协议的样子,此时保存会把 A 的键值写进 B 的配置。
            ClearPluginFields();
            _pluginBaseForm = null;
            _pluginForm = null;
            // 上一个协议的自定义页签同理:等激活的这段时间里不能还挂在页签条上。
            RebuildPluginPages(null);
            _pluginStored = null;
            _pluginStoredSecrets = null;
            // T5:上一个页签的卡片与"类型已确认"不能带到这个页签上。
            ResetTypeStep();
        }
        else if (_pluginForm is not null
                 && string.Equals(_loadedProtocolId, protocolId, StringComparison.Ordinal))
        {
            // 重复点当前页签:表单已经渲染好了,直接返回。
            // 走下去会 PluginFields.Clear() 把用户填了一半的内容复位。
            RaisePluginLabelsChanged();
            // T1:类型还没确认过就回到卡片(表单还在,步骤按同一条规则重定)。
            EnterStepForLoadedForm();
            return;
        }
        if (_protocolRegistry is not { } registry)
        {
            return;
        }

        IsPluginLoading = true;
        // 忙态用引用计数:SaveAsync / TestConnectionAsync / 本方法共用同一个 IsBusy,
        // 裸布尔会让先结束的那个提前解除忙态 —— 于是用户能在 PluginFields 还空着时按保存,
        // 落盘一条 PluginSettings/PluginSecrets 全为 null 的配置。
        BeginBusy();
        bool applied = false;
        try
        {
            // 这一步可能真的去装载插件程序集(onProtocol / onWorkspace 惰性激活),因此是异步的。
            // 形态由**声明**决定,查它不会装载任何程序集;两条解析路径拿到的描述随即被
            // 归一成 PluginConnectionForm,后面的表单渲染只有一条路。
            PluginConnectionForm? form = registry.KindOf(protocolId) == PluginConnectionKind.Workspace
                ? await registry.ResolveWorkspaceAsync(protocolId).ConfigureAwait(true) is { } workspace
                    ? PluginConnectionForm.From(workspace.Descriptor)
                    : null
                : await registry.ResolveAsync(protocolId).ConfigureAwait(true) is { } protocol
                    // 实现体一起带上:动态下拉的候选项由它现给(它兼实现 IProtocolChoiceSource
                    // 时才有;串口是终端实现,S3 是文件系统实现,所以两边都要问)。
                    ? PluginConnectionForm.From(protocol.Descriptor,
                        (object?)protocol.Terminal ?? protocol.FileSystem)
                    : null;
            // **陈旧续体作废**:装载期间用户可能已经点去了 SSH 或另一个协议(协议页签不受
            // canExecute 约束)。不校验就会把 S3 的描述符盖到 SSH 表单上,
            // 主机那格显示「服务端点」、用户名显示「Access Key ID」。
            if (ConnectionType != ConnectionType.Plugin
                || !string.Equals(PluginProtocolId, protocolId, StringComparison.Ordinal))
            {
                return;
            }
            applied = true;
            // 测试接缝(见 PluginFormOverride):分页的边界情况由用例经它喂合成表单。
            if (form is not null && PluginFormOverride is { } overrideForm)
            {
                form = overrideForm(form);
            }
            _pluginBaseForm = form;
            _pluginForm = form;
            _loadedProtocolId = protocolId;
            // 插件被禁用/装载失败:表单是空的,而 AllowsAnonymous 随之为 false,
            // 匿名配置的保存/连接会同时灰死。给一条能看懂的话,别让用户对着灰按钮猜。
            PluginUnavailable = form is null;
            // 自定义页签先于字段建好:字段逐行进集合时的那几次下发要能算出各页签在不在。
            RebuildPluginPages(form);
            ClearPluginFields();
            foreach (ProtocolSettingField field in _pluginForm?.Fields ?? [])
            {
                if (field.IsHidden)
                {
                    // 隐藏字段(证书指纹之类)不进表单,但要原样带回落盘 —— 见 CollectPluginValues。
                    continue;
                }
                string? stored = (field.IsSecret ? _pluginStoredSecrets : _pluginStored) is { } bag
                                 && bag.TryGetValue(field.Key, out string? value)
                    ? value
                    : null;
                PluginFields.Add(new(field, stored,
                    field.Kind == ProtocolSettingKind.SshSession ? EnsureSshSessionChoices() : null,
                    field.Kind == ProtocolSettingKind.DynamicChoice ? ReloadFieldChoicesAsync : null,
                    pageKey: _pluginForm?.PageKeyFor(field)));
            }
            // 编辑既有配置时高级字段里有非默认值,「高级」页签上会点一个圆点
            // (IsAdvancedSectionModified,由下面这次下发一并刷新):用户填过的东西不能藏得看不出来,
            // 否则"我明明设过分片大小"会变成一次静默丢失的错觉。
            // 显式下发一次:集合事件逐行触发时后面的字段还没进来,显示条件对**新建**配置(全默认)
            // 也必须在表单成形后生效一次 —— 少了这句,「主节点名」会在独立形态下先露出来,
            // 直到用户碰一下别的字段才消失。
            ApplyPluginFieldVisibility();
            // 变体同理,而且漏了它的后果更重:变体决定要不要凭据。
            // 打开一条已存的 SQLite 配置时,方言字段一开始就是 sqlite 却从没触发过 setter,
            // 于是表单停在**基础描述符**那一档 —— 用户名口令两栏冒出来,还被当成必填,
            // 表现就是一个本地文件却非要我登录。
            // followPort: false —— 此刻端口是从已存配置里读出来的,不能被变体的默认值盖掉。
            ApplyPluginVariant(followPort: false);
            // T1:表单成形、变体已套上,定步骤 —— 新建且页签出卡片就先选类型。
            // 只在这条已确认不是陈旧续体的路径上做:上面的早退处不碰步骤。
            EnterStepForLoadedForm();
            // 动态候选项现取一次。放在最后:它可能真的去枚举硬件(串口),
            // 而表单的其余部分不该等它 —— 取不到也只是下拉是空的,手输照旧可用。
            await LoadDynamicChoicesAsync().ConfigureAwait(true);
        }
        finally
        {
            IsPluginLoading = false;
            EndBusy();
            // return 也会走 finally,所以必须用 applied 兜住:陈旧续体不能去刷新标签。
            if (applied)
            {
                RaisePluginLabelsChanged();
                if (PluginUnavailable)
                {
                    ErrorMessage = Strings.Get("Plugin_ProtocolUnavailable");
                }
            }
        }
    }

    /// <summary>
    /// 把所有动态下拉的候选项向插件取一遍(主机那一栏 + 各 <c>DynamicChoice</c> 字段)。
    /// 打开表单时调一次,用户点刷新时按单个字段调。
    /// </summary>
    private async Task LoadDynamicChoicesAsync()
    {
        if (_pluginForm is not { } form)
        {
            return;
        }
        if (form.HostIsChoice)
        {
            IsHostRefreshing = true;
            try
            {
                IReadOnlyList<ProtocolSettingChoice> choices = form.HostIsDynamic
                    ? await FetchChoicesAsync(form, ProtocolDescriptor.HostFieldKey).ConfigureAwait(true)
                    : [];
                ReplaceHostChoices(choices.Count > 0 ? choices : form.HostChoices ?? []);
            }
            finally
            {
                IsHostRefreshing = false;
            }
        }
        foreach (PluginProtocolFieldViewModel field in PluginFields.Where(f => f.IsDynamicChoice).ToList())
        {
            await ReloadFieldChoicesAsync(field).ConfigureAwait(true);
        }
    }

    /// <summary>重新取一个字段的候选项(刷新按钮与首次加载共用)。</summary>
    private async Task ReloadFieldChoicesAsync(PluginProtocolFieldViewModel field)
    {
        if (_pluginForm is not { } form)
        {
            return;
        }
        field.ReplaceChoices(await FetchChoicesAsync(form, field.Key).ConfigureAwait(true));
    }

    /// <summary>重新取「主机」那一栏的候选项(刷新按钮)。</summary>
    private async Task RefreshHostChoicesAsync()
    {
        if (_pluginForm is not { HostIsDynamic: true } form)
        {
            return;
        }
        IsHostRefreshing = true;
        try
        {
            IReadOnlyList<ProtocolSettingChoice> choices =
                await FetchChoicesAsync(form, ProtocolDescriptor.HostFieldKey).ConfigureAwait(true);
            ReplaceHostChoices(choices.Count > 0 ? choices : form.HostChoices ?? []);
        }
        finally
        {
            IsHostRefreshing = false;
        }
    }

    /// <summary>
    /// 向插件索取一批候选项。
    /// <para>
    /// <b>永不抛</b>:这跑在画界面的路径上,一次列不出设备不该变成一个连表单都打不开的错误 ——
    /// 何况这些字段本来就允许手输。插件没实现取值源时同样退化成空表(用声明里的兜底列表)。
    /// </para>
    /// <para>
    /// <c>Task.Run</c> 不能省:枚举串口要读注册表(Windows)或扫 sysfs(Linux),
    /// 是同步阻塞的几十毫秒。在界面线程上直接调就是一次可感的卡顿。
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<ProtocolSettingChoice>> FetchChoicesAsync(
        PluginConnectionForm form, string fieldKey)
    {
        if (form.ChoiceSource is not { } source)
        {
            return [];
        }
        try
        {
            return await Task.Run(() => source.GetChoicesAsync(fieldKey)).ConfigureAwait(true) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[PluginProtocols] Fetching choices for '{fieldKey}' failed: {ex.Message}");
            return [];
        }
    }

    private void ReplaceHostChoices(IReadOnlyList<ProtocolSettingChoice> choices)
    {
        HostChoices.Clear();
        foreach (ProtocolSettingChoice choice in choices)
        {
            HostChoices.Add(PluginChoiceItem.From(choice));
        }
        // 当前值不在新列表里也**照旧留着**:适配器这次没插,不代表这条配置该被改写。
        this.RaisePropertyChanged(nameof(SelectedHostChoice));
    }

    /// <summary>
    /// 当前插件协议不可用(插件未安装/被禁用/激活失败)。
    /// 界面据此放行保存(让用户至少能改名保存),并给出说明而不是三个一起灰死的按钮。
    /// </summary>
    public bool PluginUnavailable
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 忙态引用计数。三条流程(保存 / 测试连接 / 切插件协议)共用一个 <see cref="IsBusy" />,
    /// 裸布尔会让先结束的那条提前把忙态解除,另一条还在跑却已经允许用户操作。
    /// 全在 UI 线程,普通 int 足够。
    /// </summary>
    private int _busyDepth;

    private void BeginBusy()
    {
        if (++_busyDepth == 1)
        {
            IsBusy = true;
        }
    }

    private void EndBusy()
    {
        if (--_busyDepth <= 0)
        {
            _busyDepth = 0;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把当前表单里的值回存进 <c>_pluginStored*</c>。切走时调用,使「绕一圈回来」能复现已填内容;
    /// <c>_loadedProtocolId</c> 不动 —— 它标记的是「这份 stored 属于哪个协议」。
    /// </summary>
    private void StashPluginFieldValues()
    {
        if (PluginFields.Count == 0)
        {
            return;
        }
        Dictionary<string, string> plain = SessionProfile.CloneSettings(_pluginStored) ?? [with(StringComparer.Ordinal)];
        Dictionary<string, string> secrets = SessionProfile.CloneSettings(_pluginStoredSecrets) ?? [with(StringComparer.Ordinal)];
        foreach (PluginProtocolFieldViewModel field in PluginFields)
        {
            (field.IsSecret ? secrets : plain)[field.Key] = field.Text;
        }
        _pluginStored = plain;
        _pluginStoredSecrets = secrets;
    }

    /// <summary>
    /// 协议集合变化 → 重建页签。**必须封送回 UI 线程**:注册表的 Changed 可能在
    /// 发现期的后台线程或惰性激活的线程池线程上触发,直接改 ObservableCollection 会崩。
    /// </summary>
    private void OnProtocolsChanged() => Dispatcher.UIThread.Post(() =>
    {
        string? selected = PluginProtocolId;
        LoadPluginProtocols();
        foreach (PluginProtocolTabViewModel tab in PluginProtocols)
        {
            tab.IsSelected = tab.Id == selected;
        }
    });

    /// <summary>
    /// 退订注册表事件。注册表是宿主单例,不退订的话每开一次连接对话框就在它上面
    /// 挂一个永不释放的视图模型。由 <c>ConnectionProfileView</c> 在窗口关闭时调用。
    /// </summary>
    public void Dispose()
    {
        _protocolRegistry?.Changed -= OnProtocolsChanged;
        _copyFeedbackReset?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>把注册表里的协议页签同步到界面(打开对话框时调用)。</summary>
    public void LoadPluginProtocols()
    {
        PluginProtocols.Clear();
        foreach (PluginProtocolTab tab in _protocolRegistry?.Tabs ?? [])
        {
            PluginProtocols.Add(new(tab.Id, tab.DisplayName, tab.DefaultPort));
        }
    }

    /// <summary>
    /// 切换协议标签。顺带把端口切到新协议的默认值(仅当当前端口还是别的协议的默认值时),
    /// 免得用户从 SSH 切到 FTP 后仍在 22 端口上连不上。
    /// </summary>
    private void SelectConnectionType(ConnectionType connectionType)
    {
        ConnectionType previous = ConnectionType;
        // **端口是不是"用户没手填过"必须在这里就定下来。** 下面切回内建协议那一支会把
        // `_pluginBaseForm` 置空(三格标签只看它,不清就会留着上一个插件的措辞),
        // 而 `IsProtocolDefaultPort` 要靠它才认得出**变体端口**。
        // 等到方法末尾再判,5432 已经取不到变体信息、会被当成用户手填的值留下来 ——
        // 那正是 IsProtocolDefaultPort 上那段注释("从 S3 切回 SSH 时端口停在 443")
        // 要消灭的情形,被变体端口重新引了回来。
        bool portWasDefault = IsProtocolDefaultPort(Port);
        ConnectionType = connectionType;
        if (connectionType != ConnectionType.Plugin)
        {
            // 切回内建协议:插件页签的高亮要跟着灭,否则会同时亮两个。
            PluginProtocolId = null;
            foreach (PluginProtocolTabViewModel tabViewModel in PluginProtocols)
            {
                tabViewModel.IsSelected = false;
            }
            // 先把表单里已填的值回存,再清空 —— 否则「点 SSH 再点回 S3」时用户填了一半的内容没了。
            StashPluginFieldValues();
            // 描述符也必须清:三格标签只看它、不看 ConnectionType,不清的话
            // 「S3 → SSH」之后主机那格会一直写着「服务端点」、用户名写着「Access Key ID」。
            _pluginBaseForm = null;
            _pluginForm = null;
            ClearPluginFields();
            // 内建协议没有自定义页:页签条上不能留着上一个插件的「数据库」「SSL」。
            RebuildPluginPages(null);
            PluginUnavailable = false;
            RaisePluginLabelsChanged();
            // T5:内建协议没有卡片;再点回来时由装载完成的 T1 重新定步骤。
            ResetTypeStep();
        }
        if (previous == ConnectionType)
        {
            return;
        }
        NormalizeAuthMethodForProtocol();
        // 端口跟随:仅当当前端口仍是**某个协议的默认值**时才改 —— 用户手填过的端口一律不动。
        // 用方法入口处算好的那个值,理由见上面那段长注。
        if (portWasDefault)
        {
            Port = DefaultPortFor(ConnectionType);
        }
    }

    /// <summary>
    /// FTP 与插件协议只认密码:切过去时把其它 SSH 认证方式落回口令。
    /// 认证分段在这些协议下隐藏,不能留在一个用户无法切回的认证页。
    /// </summary>
    private void NormalizeAuthMethodForProtocol()
    {
        if (!RequiresSshAuth && AuthMethod != AuthMethod.Password)
        {
            AuthMethodIndex = 0;
        }
    }

    /// <summary>
    /// 按"选变体的那个字段"的当前取值,把连接框换成对应形态。
    /// <para>
    /// 三样东西跟着变:三格标签、要不要凭据/匿名/端口栏、以及默认端口。
    /// <b>端口沿用与切页签完全相同的判定</b> —— 只有用户没手填过时才跟随,
    /// 已经改成 13306 的不会被切回 3306(那是把用户的输入吃掉)。
    /// </para>
    /// <para>
    /// <paramref name="followPort" /> 在**装载表单**那一路是 <see langword="false" />:
    /// 那时端口是从已存配置里读出来的,不该被默认值盖掉。
    /// </para>
    /// </summary>
    /// <param name="followPort">是否让端口跟随变体的默认值。</param>
    private void ApplyPluginVariant(bool followPort)
    {
        if (_pluginBaseForm is not { VariantKey.Length: > 0 } baseForm)
        {
            return;
        }
        _pluginForm = baseForm.ForVariant(PluginFieldValue);
        RaisePluginLabelsChanged();

        if (followPort && CurrentVariantDefaultPort() is { } port && IsProtocolDefaultPort(Port))
        {
            Port = port;
        }
    }

    /// <summary>
    /// 当前选中的变体声明的默认端口。
    /// </summary>
    /// <returns>
    /// 变体的 <see cref="WorkspaceVariant.DefaultPort" />;没有变体、一个都对不上、
    /// 或对上的那一档没声明端口时为 <see langword="null" />(沿用页签的默认端口)。
    /// </returns>
    private int? CurrentVariantDefaultPort() => CurrentVariant()?.DefaultPort;

    /// <summary>
    /// 按"选变体的那个字段"的当前取值对上的那一档变体。
    /// </summary>
    /// <returns>没有变体或一个都对不上时为 <see langword="null" />。</returns>
    private WorkspaceVariant? CurrentVariant()
    {
        if (_pluginBaseForm is not { VariantKey: { Length: > 0 } key, Variants: { Count: > 0 } variants })
        {
            return null;
        }
        string current = PluginFieldValue(key) ?? string.Empty;
        foreach (WorkspaceVariant variant in variants)
        {
            if (string.Equals(variant.Value, current, StringComparison.Ordinal))
            {
                return variant;
            }
        }
        return null;
    }

    /// <summary>
    /// 三格标签与匿名能力都只看协议描述符,描述符一换就得整组补发通知。
    /// 抽出来是因为它有两个调用点(选中插件协议、切回内建协议),漏一处就会出现
    /// 「已经切回 SSH,主机那格还写着"服务端点"」。
    /// </summary>
    private void RaisePluginLabelsChanged()
    {
        this.RaisePropertyChanged(nameof(HostLabel));
        this.RaisePropertyChanged(nameof(HostPlaceholder));
        this.RaisePropertyChanged(nameof(UsernameLabel));
        this.RaisePropertyChanged(nameof(PasswordLabel));
        this.RaisePropertyChanged(nameof(ShowCredentialFields));
        this.RaisePropertyChanged(nameof(ShowPasswordField));
        RaiseCredentialSourceDependents();
        // 主机那一栏的形态与端口栏的显隐同样只看描述符 —— 漏发的表现是
        // 「已经切回 SSH,主机那格还是个串口下拉」。
        this.RaisePropertyChanged(nameof(ShowPortField));
        this.RaisePropertyChanged(nameof(ShowJumpHostField));
        // 主机留空的提示跟着变体的主机标签说,标签一换它也得重算。
        this.RaisePropertyChanged(nameof(HostError));
        this.RaisePropertyChanged(nameof(HostIsText));
        this.RaisePropertyChanged(nameof(HostIsChoice));
        this.RaisePropertyChanged(nameof(HostIsEditableChoice));
        this.RaisePropertyChanged(nameof(HostIsDynamicChoice));
        this.RaisePropertyChanged(nameof(SelectedHostChoice));
        // 走属性 setter 而不是只发通知:它要驱动 canExecute 的组合器重算。
        AllowsAnonymous = _pluginForm?.AllowsAnonymous == true;
    }

    /// <summary>
    /// 该端口是否还是某个协议的默认值(即「用户没自己填过」)。
    /// 插件协议的默认端口来自它们各自的清单声明,因此这里要连它们一起看 ——
    /// 写死一张内建协议的表,会让"从 S3 切回 SSH"时端口停在 443 上。
    /// </summary>
    private bool IsProtocolDefaultPort(int port) =>
        port is 22 or FtpSettings.DefaultPort or FtpSettings.DefaultImplicitPort
        || PluginProtocols.Any(protocol => protocol.DefaultPort == port)
        // 变体的默认端口同样算"没手填过"。少算它们,换方言时端口就不跟随了 ——
        // 因为 5432 不在任何一张页签默认端口表里,会被当成用户自己填的。
        || (_pluginBaseForm?.VariantPorts().Contains(port) ?? false);

    private int DefaultPortFor(ConnectionType type) =>
        type switch
        {
            ConnectionType.FTP => FtpEncryption == FtpEncryptionMode.Implicit
                ? FtpSettings.DefaultImplicitPort
                : FtpSettings.DefaultPort,
            ConnectionType.Plugin => PluginProtocols.FirstOrDefault(p => p.Id == PluginProtocolId)?.DefaultPort ?? 443,
            _ => 22
        };
}
