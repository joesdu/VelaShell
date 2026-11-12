using System.Security;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 身份验证弹窗的结果:登录所需的完整凭据。<see cref="Password" /> 以 SecureString 承载,
/// 由消费方负责在使用后 Dispose。
/// </summary>
/// <param name="Username">登录用户名。</param>
/// <param name="AuthMethod">选用的认证方式(密码 / 证书 / 密钥 / Tailscale SSH)。</param>
/// <param name="Password">以 SecureString 承载的登录密码;消费方负责在使用后 Dispose。</param>
/// <param name="PrivateKeyPath">私钥文件路径(密钥认证时使用)。</param>
/// <param name="PrivateKeyPassphrase">私钥口令短语(如私钥已加密)。</param>
/// <param name="CertificatePath">OpenSSH 用户证书文件路径(证书认证时使用)。</param>
/// <param name="RememberPassword">是否记住本次登录密码。临时连接上只有「另存为连接」时才为 true。</param>
/// <param name="UpdateSharedCredential">
/// 是否把本次输入的凭据存回连接所引用的共享凭据(#550);连接没引用共享凭据时恒为 false。
/// </param>
/// <param name="SaveAsConnection">
/// 是否把这条临时连接(外部拉起、「最近连接」重建的,不在库里)存成一条正式的连接配置;不是临时连接时恒为 false。
/// </param>
public sealed record AuthenticationResult(
    string Username,
    AuthMethod AuthMethod,
    SecureString? Password,
    string? PrivateKeyPath,
    string? PrivateKeyPassphrase,
    string? CertificatePath,
    bool RememberPassword,
    bool UpdateSharedCredential = false,
    bool SaveAsConnection = false);

/// <summary>为什么一条引用共享凭据的连接又来问凭据(#550)。</summary>
public enum SharedCredentialPromptReason
{
    /// <summary>不必说明(例如本次手输的凭据又被拒了)。</summary>
    None,

    /// <summary>用共享凭据登录被服务器拒绝 —— 多半是密码刚改过。</summary>
    Rejected,

    /// <summary>共享凭据在本机不完整(缺用户名、密码或密钥)。</summary>
    Incomplete,

    /// <summary>引用的共享凭据已不存在。</summary>
    Missing,

    /// <summary>共享凭据使用不支持的 Tailscale SSH 认证。</summary>
    Unsupported,

    /// <summary>
    /// 共享凭据里存着的密码 / 私钥口令在这台设备上解不开(本机加密密钥换过、系统密钥链这次没打开)。
    /// 原因由登录框上「已存的密码解不开」那一条说(<see cref="AuthenticationDialogViewModel.UnreadableSecretNotice" />)。
    /// </summary>
    Unreadable
}

/// <summary>
/// 登录框里关于共享凭据的那一块:用的是哪条、为什么又来问、有几条连接在用它。
/// </summary>
/// <param name="CredentialName">共享凭据名称;<see cref="SharedCredentialPromptReason.Missing" /> 时为空串。</param>
/// <param name="UsageCount">引用它的连接数。</param>
/// <param name="Reason">又来问的原因。</param>
public sealed record SharedCredentialPromptContext(string CredentialName, int UsageCount, SharedCredentialPromptReason Reason);

/// <summary>
/// 两步身份验证弹窗(设计 oNZIM / twD13):
/// 第 1 步输入用户名(显示目标与主机指纹),第 2 步选择认证方式并输入所需凭据。
/// </summary>
public class AuthenticationDialogViewModel : ReactiveObject
{
    private readonly int _port;
    private int _methodIndex; // 0=密码 1=证书 2=密钥 3=Tailscale;顺序即分段选择器从左到右

    private string _username;

    /// <summary>
    /// 创建身份验证弹窗视图模型:根据目标主机、端口、可选用户名、已知指纹与初始认证方式初始化两步流程的界面状态与命令。
    /// </summary>
    /// <param name="host">目标主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="username">预填的用户名。</param>
    /// <param name="knownFingerprint">已信任的主机指纹;首次连接为 null。</param>
    /// <param name="initialMethod">初始选中的认证方式。</param>
    /// <param name="sharedCredential">连接引用了共享凭据时传入(#550);否则为 null。</param>
    /// <param name="connectionType">连接类型:只有 SSH / SFTP 有密码之外的认证方式可选。</param>
    /// <param name="temporaryProfile">
    /// 要连的是不在库里的临时配置(外部拉起、「最近连接」重建的):不给「记住密码」,改给「另存为连接」。
    /// </param>
    /// <param name="unreadableSecretNotice">
    /// 这次要用的那份已存密码 / 私钥口令在这台设备上解不开时的说明(见 <c>UnreadableSecretPrompt</c>);没有这回事时为 null。
    /// </param>
    public AuthenticationDialogViewModel(
        string host,
        int port,
        string? username = null,
        string? knownFingerprint = null,
        AuthMethod initialMethod = AuthMethod.Password,
        SharedCredentialPromptContext? sharedCredential = null,
        ConnectionType connectionType = ConnectionType.SSH,
        bool temporaryProfile = false,
        string? unreadableSecretNotice = null)
    {
        TargetText = host;
        _port = port;
        _username = username ?? string.Empty;
        SharedCredential = sharedCredential;
        IsTemporaryProfile = temporaryProfile;
        UnreadableSecretNotice = unreadableSecretNotice;
        // 凭据不完整、或存着的那份在这台设备上解不开时,用户就是来补它的,默认存回去;
        // 被拒时可能只是这一台密码不同,默认不动 —— 勾错的代价是另外几十台一起连不上。
        UpdateSharedCredential = sharedCredential?.Reason is SharedCredentialPromptReason.Incomplete
            or SharedCredentialPromptReason.Unreadable;
        // FTP 与插件协议只认用户名 + 密码:证书 / 密钥 / Tailscale 那几段对它们没有意义,
        // 选了反而把这次连接的认证方式改掉、密码清空,登录时发出去的是空密码。
        ShowsMethodSelector = connectionType is ConnectionType.SSH or ConnectionType.SFTP;
        // 分段选择器的顺序与 AuthMethod 的枚举顺序不一致,这里显式映射,
        // 不要图省事写成强转 —— 那会把证书认证的配置打开成密钥页。
        _methodIndex = !ShowsMethodSelector ? 0 : initialMethod switch
        {
            AuthMethod.Certificate => 1,
            AuthMethod.PrivateKey => 2,
            AuthMethod.Tailscale => 3,
            _ => 0
        };
        FingerprintText = string.IsNullOrEmpty(knownFingerprint)
                              ? Strings.Get("Auth_FingerprintFirstConnect")
                              : Strings.Format("Auth_FingerprintTrusted", Shorten(knownFingerprint));
        IObservable<bool> canNext = this.WhenAnyValue(x => x.Username)
                                        .Select(name => !string.IsNullOrWhiteSpace(name));
        NextCommand = ReactiveCommand.Create(() => { Step = 2; }, canNext);
        BackCommand = ReactiveCommand.Create(() => { Step = 1; });
        CancelCommand = ReactiveCommand.Create<AuthenticationResult?>(() => null);
        IObservable<bool> canLogin = this.WhenAnyValue(x => x.MethodIndex,
            x => x.PrivateKeyPath,
            x => x.CertificatePath,
            x => x.Username,
            (method, keyPath, certPath, name) => method switch
            {
                0 => !string.IsNullOrWhiteSpace(name),
                // 证书那一路要两个文件都齐:证书是 CA 的背书,签名仍旧由私钥出。
                1 => !string.IsNullOrWhiteSpace(certPath) && !string.IsNullOrWhiteSpace(keyPath),
                2 => !string.IsNullOrWhiteSpace(keyPath),
                3 => !string.IsNullOrWhiteSpace(name),
                _ => false
            });
        LoginCommand = ReactiveCommand.Create(BuildResult, canLogin);
        SelectPasswordCommand = ReactiveCommand.Create(() => { MethodIndex = 0; });
        SelectCertificateCommand = ReactiveCommand.Create(() => { MethodIndex = 1; });
        SelectKeyCommand = ReactiveCommand.Create(() => { MethodIndex = 2; });
        SelectTailscaleCommand = ReactiveCommand.Create(() => { MethodIndex = 3; });
        TogglePasswordVisibilityCommand = ReactiveCommand.Create(() => { ShowPassword = !ShowPassword; });
        this.WhenAnyValue(x => x.Step)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HeaderTitle));
                this.RaisePropertyChanged(nameof(IsStep1));
                this.RaisePropertyChanged(nameof(IsStep2));
                this.RaisePropertyChanged(nameof(TargetText));
                this.RaisePropertyChanged(nameof(UsernameLine));
            });
        this.WhenAnyValue(x => x.MethodIndex)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsPasswordMethod));
                this.RaisePropertyChanged(nameof(IsKeyMethod));
                this.RaisePropertyChanged(nameof(IsCertificateMethod));
                this.RaisePropertyChanged(nameof(IsTailscaleMethod));
                this.RaisePropertyChanged(nameof(ShowRememberPassword));
                this.RaisePropertyChanged(nameof(CanUpdateSharedCredential));
                this.RaisePropertyChanged(nameof(SharedCredentialNotice));
                this.RaisePropertyChanged(nameof(HasSharedCredentialNotice));
                this.RaisePropertyChanged(nameof(HasUnreadableSecretNotice));
                this.RaisePropertyChanged(nameof(ShowsKeyFields));
            });
    }

    /// <summary>当前所处的步骤:1 为输入用户名,2 为选择认证方式并输入凭据。</summary>
    public int Step
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1;

    /// <summary>是否处于第 1 步(输入用户名)。</summary>
    public bool IsStep1 => Step == 1;

    /// <summary>是否处于第 2 步(选择认证方式并输入凭据)。</summary>
    public bool IsStep2 => Step == 2;

    /// <summary>弹窗标题栏文本,随当前步骤变化。</summary>
    public string HeaderTitle => Strings.Format("Auth_HeaderTitle", Step);

    /// <summary>信息栏第一行:第 1 步“正在连接 …”,第 2 步“user@host:port”。</summary>
    public string TargetText
    {
        get
        {
            // IPv6 加方括号,否则 ::1:22 读不出哪段是端口。
            string target = HostAddress.FormatTarget(string.IsNullOrWhiteSpace(Username) ? null : Username, field, _port);
            return Step == 1 ? Strings.Format("Auth_ConnectingTo", target) : target;
        }
    }

    /// <summary>主机指纹提示文本:首次连接或已信任主机的指纹说明。</summary>
    public string FingerprintText { get; }

    /// <summary>第 2 步信息栏第二行。</summary>
    public string UsernameLine => Strings.Format("Auth_UsernameLine", Username);

    /// <summary>登录用户名;变更时同步刷新目标与第 2 步信息栏文本。</summary>
    public string Username
    {
        get => _username;
        set
        {
            this.RaiseAndSetIfChanged(ref _username, value);
            this.RaisePropertyChanged(nameof(TargetText));
            this.RaisePropertyChanged(nameof(UsernameLine));
        }
    }

    /// <summary>当前选中的认证方式索引:0=密码,1=证书,2=密钥,3=Tailscale。</summary>
    public int MethodIndex
    {
        get => _methodIndex;
        private set => this.RaiseAndSetIfChanged(ref _methodIndex, value);
    }

    /// <summary>是否显示认证方式的分段选择器:只有 SSH / SFTP 有密码之外的方式可选。</summary>
    public bool ShowsMethodSelector { get; }

    /// <summary>当前是否选择密码认证方式。</summary>
    public bool IsPasswordMethod => MethodIndex == 0;

    /// <summary>当前是否选择密钥认证方式。</summary>
    public bool IsKeyMethod => MethodIndex == 2;

    /// <summary>当前是否选择证书认证方式。</summary>
    public bool IsCertificateMethod => MethodIndex == 1;

    /// <summary>当前是否选择 Tailscale SSH 认证。</summary>
    public bool IsTailscaleMethod => MethodIndex == 3;

    /// <summary>私钥与口令两个字段是否可见:密钥认证与证书认证都要用到它们。</summary>
    /// <remarks>
    /// 证书页就是密钥页再加一个证书文件 —— 证书凭据本就是
    /// 「证书 + 匹配私钥」两件套,共用一块面板,校验与浏览按钮才不会两边各改各的。
    /// </remarks>
    public bool ShowsKeyFields => IsKeyMethod || IsCertificateMethod;

    /// <summary>以 SecureString 承载的登录密码。</summary>
    public SecureString? Password
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否以明文显示密码输入框。</summary>
    public bool ShowPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否记住本次登录密码。</summary>
    public bool RememberPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // ---- 共享凭据(#550) ----

    /// <summary>连接引用的共享凭据及又来问的原因;没引用时为 null。</summary>
    public SharedCredentialPromptContext? SharedCredential { get; }

    /// <summary>
    /// 是否显示「记住密码」。引用共享凭据的连接不显示:手输的这一份只用于本次,
    /// 要长期保存就存回共享凭据(<see cref="UpdateSharedCredential" />)—— 连接自己不再存密码。
    /// 临时连接也不显示:它不落库(plan.md §252),勾了也存不下,改给「另存为连接」(<see cref="SaveAsConnection" />)。
    /// </summary>
    public bool ShowRememberPassword => !IsTailscaleMethod && SharedCredential is null && !IsTemporaryProfile;

    // ---- 临时连接:另存为连接 ----

    /// <summary>要连的是不在库里的临时配置(外部拉起、「最近连接」重建的)。</summary>
    public bool IsTemporaryProfile { get; }

    /// <summary>
    /// 是否把这条临时连接存成正式的连接配置(进资源管理器,未分组)。存成之后才记住密码 ——
    /// 仍受「常规 → 隐私与安全 → 记住密码」总开关约束。默认不勾:外部拉起带来的本来就是一次性的东西。
    /// </summary>
    /// <remarks>与认证方式无关(用密钥、Tailscale 登录的也可以存),所以放在各认证方式的面板外面。</remarks>
    public bool SaveAsConnection
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>又来问凭据的原因说明;不必说明时为 null。</summary>
    public string? SharedCredentialNotice => SharedCredential is { Reason: SharedCredentialPromptReason.Unsupported }
        ? Strings.Get("SharedCred_ErrTailscaleUnsupported")
        : IsTailscaleMethod ? null : SharedCredential switch
        {
            { Reason: SharedCredentialPromptReason.Rejected } shared =>
                Strings.Format("Auth_SharedRejected", shared.CredentialName),
            { Reason: SharedCredentialPromptReason.Incomplete } shared =>
                Strings.Format("Auth_SharedIncomplete", shared.CredentialName),
            { Reason: SharedCredentialPromptReason.Missing } => Strings.Get("Auth_SharedMissing"),
            _ => null
        };

    /// <summary>是否显示原因说明条。</summary>
    public bool HasSharedCredentialNotice => SharedCredentialNotice is not null;

    /// <summary>
    /// 「已存的密码解不开」的说明:是哪一份、为什么(本机加密密钥的解析结果,§253)、怎么办;没有这回事时为 null。
    /// </summary>
    /// <remarks>
    /// 以前这种情形当成「有密码」拿密文去试一次,认证失败才弹一个什么都不说的登录框 ——
    /// 用户只会以为是自己记错了密码(第二轮第 20 条)。
    /// </remarks>
    public string? UnreadableSecretNotice { get; }

    /// <summary>是否显示「已存的密码解不开」说明条(选了 Tailscale 就用不着那份密码了,不显示)。</summary>
    public bool HasUnreadableSecretNotice => UnreadableSecretNotice is not null && !IsTailscaleMethod;

    /// <summary>能否把本次输入存回共享凭据:凭据还在才能存。</summary>
    public bool CanUpdateSharedCredential =>
        !IsTailscaleMethod && SharedCredential is
        { Reason: not (SharedCredentialPromptReason.Missing or SharedCredentialPromptReason.Unsupported) };

    /// <summary>「同时更新共享凭据」勾选框的文案(带凭据名与引用它的连接数)。</summary>
    public string UpdateSharedCredentialText => SharedCredential is { } shared
        ? Strings.Format("Auth_UpdateShared", shared.CredentialName, shared.UsageCount)
        : string.Empty;

    /// <summary>是否把本次输入存回共享凭据 —— 服务器改了密码之后,一次改完所有引用它的连接。</summary>
    public bool UpdateSharedCredential
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>私钥文件路径(密钥认证方式使用)。</summary>
    public string? PrivateKeyPath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>私钥的口令短语(如私钥已加密)。</summary>
    public string? PrivateKeyPassphrase
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>OpenSSH 用户证书文件路径(证书认证方式使用)。</summary>
    public string? CertificatePath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>从第 1 步进入第 2 步的命令(用户名非空时可用)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NextCommand { get; }

    /// <summary>从第 2 步返回第 1 步的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BackCommand { get; }

    /// <summary>取消弹窗的命令,返回空结果。</summary>
    public ReactiveCommand<RxVoid, AuthenticationResult?> CancelCommand { get; }

    /// <summary>确认登录的命令,构建并返回完整凭据结果(凭据齐备时可用)。</summary>
    public ReactiveCommand<RxVoid, AuthenticationResult> LoginCommand { get; }

    /// <summary>切换到密码认证方式的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectPasswordCommand { get; }

    /// <summary>切换到密钥认证方式的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectKeyCommand { get; }

    /// <summary>切换到证书认证方式的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectCertificateCommand { get; }

    /// <summary>切换到 Tailscale SSH 认证方式的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectTailscaleCommand { get; }

    /// <summary>切换密码明文/密文显示状态的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    private AuthenticationResult BuildResult()
    {
        // 选择器索引不等于枚举值,必须显式映射。
        AuthMethod method = MethodIndex switch
        {
            1 => AuthMethod.Certificate,
            2 => AuthMethod.PrivateKey,
            3 => AuthMethod.Tailscale,
            _ => AuthMethod.Password
        };
        return new(Username.Trim(),
            method,
            // 传一份副本,与弹窗自身的生命周期解耦;消费方负责 Dispose。
            IsPasswordMethod ? Password?.Copy() : null,
            // 私钥两项走 ShowsKeyFields 而不是 IsKeyMethod:证书认证同样靠私钥签名,
            // 只按 IsKeyMethod 取会把用户刚填的私钥原地丢掉。
            ShowsKeyFields ? PrivateKeyPath : null,
            ShowsKeyFields ? PrivateKeyPassphrase : null,
            IsCertificateMethod ? CertificatePath : null,
            // 临时连接:另存为连接才记住(存不进库的配置,记住了也落不了盘);其余照「记住密码」那一勾。
            !IsTailscaleMethod && (IsTemporaryProfile ? SaveAsConnection : RememberPassword),
            // 允许空密码登录(有的服务器就是空密码),但空密码不回写共享凭据:那份可能只是这台设备
            // 暂时解不开,连接还没试就把它写空,所有引用它的连接都会一起丢掉密码。
            CanUpdateSharedCredential && UpdateSharedCredential && !(IsPasswordMethod && Password is not { Length: > 0 }),
            IsTemporaryProfile && SaveAsConnection);
    }

    private static string Shorten(string fingerprint) => fingerprint.Length <= 24 ? fingerprint : $"{fingerprint[..12]}...{fingerprint[^4..]}";
}
