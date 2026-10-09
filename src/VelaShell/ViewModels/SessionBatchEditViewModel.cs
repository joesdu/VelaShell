using System.Diagnostics.CodeAnalysis;
using System.Security;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Security;

namespace VelaShell.ViewModels;

/// <summary>批量修改「跳板机」下拉里的一项。</summary>
/// <param name="Id">跳板连接;null = 直连。</param>
/// <param name="Name">显示名。</param>
public sealed record SessionBatchJumpOption(Guid? Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>批量修改「共享凭据」下拉里的一项。</summary>
/// <param name="Credential">凭据。</param>
public sealed record SessionBatchCredentialOption(SharedCredential Credential)
{
    /// <inheritdoc />
    public override string ToString() =>
        Credential.Username.Length > 0 ? $"{Credential.Name} ({Credential.Username})" : Credential.Name;
}

/// <summary>批量修改完成后交回宿主的结果。</summary>
/// <param name="Changed">实际改了几条。</param>
/// <param name="AuthSkipped">协议不支持所选认证而跳过的条数。</param>
/// <param name="JumpHostSkipped">跳板机没改成的条数。</param>
public sealed record SessionBatchEditOutcome(int Changed, int AuthSkipped, int JumpHostSkipped);

/// <summary>
/// 批量修改对话框(#571):对多选的一批连接统一改用户名、端口、认证、跳板机。每一项前面一个勾,不勾的不动。
/// </summary>
/// <remarks>
/// 「哪些会被跳过」在点应用之前就算好显示出来(<see cref="PreviewText" />)—— 一批里混着 FTP 时把认证改成私钥,
/// 用户应该在按下去之前就知道那几条 FTP 不会跟着变,而不是改完了再去一条条核对。
/// </remarks>
public sealed class SessionBatchEditViewModel : ReactiveObject
{
    private readonly IReadOnlyList<SessionProfile> _targets;
    private readonly IReadOnlyList<SessionProfile> _allSessions;
    private readonly ISessionRepository _repository;

    /// <summary>构造。</summary>
    /// <param name="targets">要改的连接。</param>
    /// <param name="allSessions">本机全部连接(跳板机候选、成环判断)。</param>
    /// <param name="credentials">本机全部共享凭据。</param>
    /// <param name="repository">保存改动的仓储。</param>
    public SessionBatchEditViewModel(
        IReadOnlyList<SessionProfile> targets,
        IReadOnlyList<SessionProfile> allSessions,
        IReadOnlyList<SharedCredential> credentials,
        ISessionRepository repository)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _allSessions = allSessions ?? throw new ArgumentNullException(nameof(allSessions));
        ArgumentNullException.ThrowIfNull(credentials);
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        // 端口与用户名给个起点:大家都一样就用那个值,不一样就留常见的默认值。
        Username = targets.Select(static t => t.Username).Distinct(StringComparer.Ordinal).Count() == 1 ? targets[0].Username : string.Empty;
        Port = targets.Select(static t => t.Port).Distinct().Count() == 1 ? targets[0].Port : 22;

        CredentialOptions = [.. credentials.OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).Select(static c => new SessionBatchCredentialOption(c))];
        var targetIds = targets.Select(static t => t.Id).ToHashSet();
        JumpHostOptions =
        [
            new SessionBatchJumpOption(null, Strings.Get("Msg_DirectConnection")),
            .. allSessions
                .Where(s => !targetIds.Contains(s.Id) && s.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP)
                .OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static s => new SessionBatchJumpOption(s.Id, $"{s.Name} ({s.Host})"))
        ];
        SelectedJumpHost = JumpHostOptions[0];

        IObservable<bool> canApply = this.WhenAnyValue(x => x.ValidationError, x => x.HasAnyChange, x => x.IsBusy)
            .Select(static t => t.Property1 is null && t.Property2 && !t.Property3);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync, canApply);
        CancelCommand = ReactiveCommand.Create(() => { });
        SelectAuthKindCommand = ReactiveCommand.Create<SessionBatchAuthKind>(kind => AuthKind = kind);
    }

    /// <summary>对话框标题。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string Title => Strings.Get("BatchEdit_Title");

    /// <summary>「修改 N 个连接」。</summary>
    public string Summary => Strings.Format("BatchEdit_SummaryFmt", _targets.Count);

    /// <summary>是否改用户名。</summary>
    public bool ChangeUsername { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>新用户名。</summary>
    public string Username { get => field; set { this.RaiseAndSetIfChanged(ref field, value ?? string.Empty); RaiseChanged(); } } = string.Empty;

    /// <summary>是否改端口。</summary>
    public bool ChangePort { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>新端口。</summary>
    public decimal? Port { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>是否改认证。</summary>
    public bool ChangeAuth { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>改成哪种认证。</summary>
    public SessionBatchAuthKind AuthKind
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsSharedCredentialAuth));
            this.RaisePropertyChanged(nameof(IsPasswordAuth));
            this.RaisePropertyChanged(nameof(IsKeyAuth));
            this.RaisePropertyChanged(nameof(IsAgentAuth));
            RaiseChanged();
        }
    } = SessionBatchAuthKind.SharedCredential;

    /// <summary>是否选了共享凭据。</summary>
    public bool IsSharedCredentialAuth => AuthKind == SessionBatchAuthKind.SharedCredential;

    /// <summary>是否选了密码。</summary>
    public bool IsPasswordAuth => AuthKind == SessionBatchAuthKind.Password;

    /// <summary>是否选了私钥。</summary>
    public bool IsKeyAuth => AuthKind == SessionBatchAuthKind.PrivateKey;

    /// <summary>是否选了 SSH Agent。</summary>
    public bool IsAgentAuth => AuthKind == SessionBatchAuthKind.Agent;

    /// <summary>本机的共享凭据。</summary>
    public IReadOnlyList<SessionBatchCredentialOption> CredentialOptions { get; }

    /// <summary>本机有没有共享凭据(没有时那一栏提示去设置里建)。</summary>
    public bool HasCredentials => CredentialOptions.Count > 0;

    /// <summary>选中的共享凭据。</summary>
    public SessionBatchCredentialOption? SelectedCredential { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>密码(可以留空,连接时再问)。</summary>
    public SecureString? Password { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>是否明文显示密码。</summary>
    public bool ShowPassword { get => field; set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>私钥文件路径。</summary>
    public string? PrivateKeyPath { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>私钥口令。</summary>
    public string? PrivateKeyPassphrase { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>是否改跳板机。</summary>
    public bool ChangeJumpHost { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>跳板机候选:直连 + 本机的 SSH 连接(不含正在改的这批)。</summary>
    public IReadOnlyList<SessionBatchJumpOption> JumpHostOptions { get; }

    /// <summary>选中的跳板机。</summary>
    public SessionBatchJumpOption? SelectedJumpHost { get => field; set { this.RaiseAndSetIfChanged(ref field, value); RaiseChanged(); } }

    /// <summary>有没有勾任何一项。</summary>
    public bool HasAnyChange => ChangeUsername || ChangePort || ChangeAuth || ChangeJumpHost;

    /// <summary>输入有什么问题;没问题为 null。</summary>
    public string? ValidationError =>
        ChangePort && Port is not (>= 1 and <= 65535) ? Strings.Get("BatchEdit_BadPort")
        : ChangeAuth && IsSharedCredentialAuth && SelectedCredential is null ? Strings.Get("BatchEdit_PickCredential")
        : ChangeAuth && IsKeyAuth && string.IsNullOrWhiteSpace(PrivateKeyPath) ? Strings.Get("BatchEdit_PickKey")
        : null;

    /// <summary>点「应用」会发生什么:改几条、跳过几条及原因。</summary>
    public string PreviewText
    {
        get
        {
            if (!HasAnyChange || ValidationError is not null)
            {
                return string.Empty;
            }
            SessionBatchEditResult result = BuildEdit().Apply(_targets, _allSessions);
            List<string> parts = [Strings.Format("BatchEdit_PreviewChangedFmt", result.Changed.Count)];
            if (result.AuthSkipped > 0)
            {
                parts.Add(Strings.Format("BatchEdit_PreviewAuthSkippedFmt", result.AuthSkipped));
            }
            if (result.JumpHostSkipped > 0)
            {
                parts.Add(Strings.Format("BatchEdit_PreviewJumpSkippedFmt", result.JumpHostSkipped));
            }
            return string.Join(Strings.Get("BatchEdit_PreviewSeparator"), parts);
        }
    }

    /// <summary>是否正在保存。</summary>
    public bool IsBusy { get => field; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>应用;没有任何改动时结果为 null。</summary>
    public ReactiveCommand<RxVoid, SessionBatchEditOutcome?> ApplyCommand { get; }

    /// <summary>取消。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>切换认证方式(分段按钮)。</summary>
    public ReactiveCommand<SessionBatchAuthKind, RxVoid> SelectAuthKindCommand { get; }

    /// <summary>按勾选的项拼出修改。</summary>
    internal SessionBatchEdit BuildEdit() =>
        new()
        {
            Username = ChangeUsername ? Username : null,
            Port = ChangePort && Port is { } port ? (int)port : null,
            Auth = ChangeAuth
                ? new SessionBatchAuth
                {
                    Kind = AuthKind,
                    Credential = SelectedCredential?.Credential,
                    Password = SecureStringConvert.ToPlaintext(Password),
                    PrivateKeyPath = PrivateKeyPath,
                    PrivateKeyPassphrase = PrivateKeyPassphrase
                }
                : null,
            ChangeJumpHost = ChangeJumpHost,
            JumpHostProfileId = SelectedJumpHost?.Id
        };

    /// <summary>是否已经开始保存(保存到一半失败也算):对话框关掉后宿主据此重读资源管理器,理由同导入对话框。</summary>
    public bool HasWritten { get; private set; }

    /// <summary>保存失败时的说明;对话框留着,用户可以再点一次。</summary>
    public string? ErrorMessage { get => field; private set => this.RaiseAndSetIfChanged(ref field, value); }

    private async Task<SessionBatchEditOutcome?> ApplyAsync()
    {
        SessionBatchEditResult result = BuildEdit().Apply(_targets, _allSessions);
        IsBusy = true;
        ErrorMessage = null;
        HasWritten |= result.Changed.Count > 0;
        try
        {
            foreach (SessionProfile profile in result.Changed)
            {
                await _repository.SaveSessionAsync(profile).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // 同导入对话框:写库失败留在框里说,不让异常逃出命令把应用带走。
            // 已经保存的几条不回滚 —— 每条都是独立的完整修改,再点一次「应用」结果一样。
            ErrorMessage = Strings.Format("BatchEdit_Failed", ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
        return new SessionBatchEditOutcome(result.Changed.Count, result.AuthSkipped, result.JumpHostSkipped);
    }

    private void RaiseChanged()
    {
        this.RaisePropertyChanged(nameof(HasAnyChange));
        this.RaisePropertyChanged(nameof(ValidationError));
        this.RaisePropertyChanged(nameof(PreviewText));
    }
}
