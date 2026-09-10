using System.Security;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 身份验证弹窗的结果:登录所需的完整凭据。<see cref="Password" /> 以 SecureString 承载,
/// 由消费方负责在使用后 Dispose。
/// </summary>
/// <param name="Username">登录用户名。</param>
/// <param name="AuthMethod">选用的认证方式(密码 / 证书 / 密钥)。</param>
/// <param name="Password">以 SecureString 承载的登录密码;消费方负责在使用后 Dispose。</param>
/// <param name="PrivateKeyPath">私钥文件路径(密钥认证时使用)。</param>
/// <param name="PrivateKeyPassphrase">私钥口令短语(如私钥已加密)。</param>
/// <param name="CertificatePath">OpenSSH 用户证书文件路径(证书认证时使用)。</param>
/// <param name="RememberPassword">是否记住本次登录密码。</param>
public sealed record AuthenticationResult(
    string Username,
    AuthMethod AuthMethod,
    SecureString? Password,
    string? PrivateKeyPath,
    string? PrivateKeyPassphrase,
    string? CertificatePath,
    bool RememberPassword);

/// <summary>
/// 两步身份验证弹窗(设计 oNZIM / twD13):
/// 第 1 步输入用户名(显示目标与主机指纹),第 2 步选择认证方式(密码/证书/密钥)并输入凭据。
/// </summary>
public class AuthenticationDialogViewModel : ReactiveObject
{
    private readonly int _port;
    private int _methodIndex; // 0=密码 1=证书 2=密钥;顺序即分段选择器上从左到右的顺序

    private string _username;

    /// <summary>
    /// 创建身份验证弹窗视图模型:根据目标主机、端口、可选用户名、已知指纹与初始认证方式初始化两步流程的界面状态与命令。
    /// </summary>
    public AuthenticationDialogViewModel(
        string host,
        int port,
        string? username = null,
        string? knownFingerprint = null,
        AuthMethod initialMethod = AuthMethod.Password)
    {
        TargetText = host;
        _port = port;
        _username = username ?? string.Empty;
        // 分段选择器的顺序(密码/证书/密钥)与 AuthMethod 的枚举顺序不一致,这里显式映射,
        // 不要图省事写成强转 —— 那会把证书认证的配置打开成密钥页。
        _methodIndex = initialMethod switch
        {
            AuthMethod.Certificate => 1,
            AuthMethod.PrivateKey => 2,
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
            x => x.Password,
            x => x.PrivateKeyPath,
            x => x.CertificatePath,
            (method, password, keyPath, certPath) => method switch
            {
                0 => password is { Length: > 0 },
                // 证书那一路要两个文件都齐:证书是 CA 的背书,签名仍旧由私钥出。
                1 => !string.IsNullOrWhiteSpace(certPath) && !string.IsNullOrWhiteSpace(keyPath),
                2 => !string.IsNullOrWhiteSpace(keyPath),
                _ => false
            });
        LoginCommand = ReactiveCommand.Create(BuildResult, canLogin);
        SelectPasswordCommand = ReactiveCommand.Create(() => { MethodIndex = 0; });
        SelectCertificateCommand = ReactiveCommand.Create(() => { MethodIndex = 1; });
        SelectKeyCommand = ReactiveCommand.Create(() => { MethodIndex = 2; });
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
            string target = string.IsNullOrWhiteSpace(Username)
                                ? $"{field}:{_port}"
                                : $"{Username}@{field}:{_port}";
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

    /// <summary>当前选中的认证方式索引:0=密码,1=证书,2=密钥。</summary>
    public int MethodIndex
    {
        get => _methodIndex;
        private set => this.RaiseAndSetIfChanged(ref _methodIndex, value);
    }

    /// <summary>当前是否选择密码认证方式。</summary>
    public bool IsPasswordMethod => MethodIndex == 0;

    /// <summary>当前是否选择密钥认证方式。</summary>
    public bool IsKeyMethod => MethodIndex == 2;

    /// <summary>当前是否选择证书认证方式。</summary>
    public bool IsCertificateMethod => MethodIndex == 1;

    /// <summary>私钥与口令两个字段是否可见:密钥认证与证书认证都要用到它们。</summary>
    /// <remarks>
    /// 证书页就是密钥页再加一个证书文件 —— Tmds 的 CertificateCredential 本就是
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

    /// <summary>切换密码明文/密文显示状态的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    private AuthenticationResult BuildResult()
    {
        // 分段选择器的索引不等于枚举值(选择器是密码/证书/密钥),照抄会把证书存成私钥。
        AuthMethod method = MethodIndex switch
        {
            1 => AuthMethod.Certificate,
            2 => AuthMethod.PrivateKey,
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
            RememberPassword);
    }

    private static string Shorten(string fingerprint) => fingerprint.Length <= 24 ? fingerprint : $"{fingerprint[..12]}...{fingerprint[^4..]}";
}
