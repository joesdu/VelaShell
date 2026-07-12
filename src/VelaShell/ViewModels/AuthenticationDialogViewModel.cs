using System.Reactive;
using System.Reactive.Linq;
using System.Security;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 身份验证弹窗的结果:登录所需的完整凭据。<see cref="Password" /> 以 SecureString 承载,
/// 由消费方负责在使用后 Dispose。
/// </summary>
public sealed record AuthenticationResult(
    string Username,
    AuthMethod AuthMethod,
    SecureString? Password,
    string? PrivateKeyPath,
    string? PrivateKeyPassphrase,
    bool RememberPassword);

/// <summary>
/// 两步身份验证弹窗(设计 oNZIM / twD13):
/// 第 1 步输入用户名(显示目标与主机指纹),第 2 步选择认证方式(密码/证书/密钥)并输入凭据。
/// </summary>
public class AuthenticationDialogViewModel : ReactiveObject
{
    private readonly int _port;
    private int _methodIndex; // 0=密码 1=证书(暂未支持) 2=密钥

    private string _username;

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
        _methodIndex = initialMethod == AuthMethod.PrivateKey ? 2 : 0;
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
            (method, password, keyPath) => method switch
            {
                0 => password is { Length: > 0 },
                2 => !string.IsNullOrWhiteSpace(keyPath),
                _ => false
            });
        LoginCommand = ReactiveCommand.Create(BuildResult, canLogin);
        SelectPasswordCommand = ReactiveCommand.Create(() => { MethodIndex = 0; });
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
            });
    }

    public int Step
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = 1;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

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

    public string FingerprintText { get; }

    /// <summary>第 2 步信息栏第二行。</summary>
    public string UsernameLine => Strings.Format("Auth_UsernameLine", Username);

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

    public int MethodIndex
    {
        get => _methodIndex;
        private set => this.RaiseAndSetIfChanged(ref _methodIndex, value);
    }

    public bool IsPasswordMethod => MethodIndex == 0;

    public bool IsKeyMethod => MethodIndex == 2;

    public SecureString? Password
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool ShowPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool RememberPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public string? PrivateKeyPath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? PrivateKeyPassphrase
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> NextCommand { get; }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }

    public ReactiveCommand<Unit, AuthenticationResult?> CancelCommand { get; }

    public ReactiveCommand<Unit, AuthenticationResult> LoginCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectPasswordCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectKeyCommand { get; }

    public ReactiveCommand<Unit, Unit> TogglePasswordVisibilityCommand { get; }

    private AuthenticationResult BuildResult()
    {
        return new(Username.Trim(),
            IsKeyMethod ? AuthMethod.PrivateKey : AuthMethod.Password,
            // 传一份副本,与弹窗自身的生命周期解耦;消费方负责 Dispose。
            IsPasswordMethod ? Password?.Copy() : null,
            IsKeyMethod ? PrivateKeyPath : null,
            IsKeyMethod ? PrivateKeyPassphrase : null,
            RememberPassword);
    }

    private static string Shorten(string fingerprint) => fingerprint.Length <= 24 ? fingerprint : $"{fingerprint[..12]}...{fingerprint[^4..]}";
}
