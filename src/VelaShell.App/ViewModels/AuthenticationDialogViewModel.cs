using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Security;
using VelaShell.Core.Models;
using ReactiveUI;

namespace VelaShell.App.ViewModels;

/// <summary>
/// 身份验证弹窗的结果:登录所需的完整凭据。<see cref="Password"/> 以 SecureString 承载,
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
    private readonly string _host;
    private readonly int _port;

    private int _step = 1;
    private string _username;
    private int _methodIndex; // 0=密码 1=证书(暂未支持) 2=密钥
    private SecureString? _password;
    private bool _showPassword;
    private bool _rememberPassword = true;
    private string? _privateKeyPath;
    private string? _privateKeyPassphrase;

    public AuthenticationDialogViewModel(
        string host,
        int port,
        string? username = null,
        string? knownFingerprint = null,
        AuthMethod initialMethod = AuthMethod.Password)
    {
        _host = host;
        _port = port;
        _username = username ?? string.Empty;
        _methodIndex = initialMethod == AuthMethod.PrivateKey ? 2 : 0;

        FingerprintText = string.IsNullOrEmpty(knownFingerprint)
            ? "指纹: 首次连接,将在握手时记录"
            : $"指纹: {Shorten(knownFingerprint)}(已信任)";

        var canNext = this.WhenAnyValue(x => x.Username)
            .Select(name => !string.IsNullOrWhiteSpace(name));
        NextCommand = ReactiveCommand.Create(() => { Step = 2; }, canNext);
        BackCommand = ReactiveCommand.Create(() => { Step = 1; });
        CancelCommand = ReactiveCommand.Create(() => (AuthenticationResult?)null);

        var canLogin = this.WhenAnyValue(
            x => x.MethodIndex,
            x => x.Password,
            x => x.PrivateKeyPath,
            (method, password, keyPath) => method switch
            {
                0 => password is { Length: > 0 },
                2 => !string.IsNullOrWhiteSpace(keyPath),
                _ => false,
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
        get => _step;
        private set => this.RaiseAndSetIfChanged(ref _step, value);
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;

    public string HeaderTitle => $"身份验证 - 第 {Step} 步";

    /// <summary>信息栏第一行:第 1 步“正在连接 …”,第 2 步“user@host:port”。</summary>
    public string TargetText
    {
        get
        {
            var target = string.IsNullOrWhiteSpace(Username)
                ? $"{_host}:{_port}"
                : $"{Username}@{_host}:{_port}";
            return Step == 1 ? $"正在连接 {target}" : target;
        }
    }

    public string FingerprintText { get; }

    /// <summary>第 2 步信息栏第二行。</summary>
    public string UsernameLine => $"用户名: {Username}";

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
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    public bool ShowPassword
    {
        get => _showPassword;
        set => this.RaiseAndSetIfChanged(ref _showPassword, value);
    }

    public bool RememberPassword
    {
        get => _rememberPassword;
        set => this.RaiseAndSetIfChanged(ref _rememberPassword, value);
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

    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, AuthenticationResult?> CancelCommand { get; }
    public ReactiveCommand<Unit, AuthenticationResult?> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectPasswordCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePasswordVisibilityCommand { get; }

    private AuthenticationResult? BuildResult()
    {
        return new AuthenticationResult(
            Username.Trim(),
            IsKeyMethod ? AuthMethod.PrivateKey : AuthMethod.Password,
            // 传一份副本,与弹窗自身的生命周期解耦;消费方负责 Dispose。
            IsPasswordMethod ? Password?.Copy() : null,
            IsKeyMethod ? PrivateKeyPath : null,
            IsKeyMethod ? PrivateKeyPassphrase : null,
            RememberPassword);
    }

    private static string Shorten(string fingerprint)
        => fingerprint.Length <= 24 ? fingerprint : $"{fingerprint[..12]}...{fingerprint[^4..]}";
}
