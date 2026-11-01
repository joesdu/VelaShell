using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>设置 → 共享凭据页表格里的一行。</summary>
/// <param name="Credential">凭据(明文,只在本进程内)。</param>
/// <param name="UsageCount">引用它的连接数。</param>
public sealed record SharedCredentialRow(SharedCredential Credential, int UsageCount)
{
    /// <summary>名称。</summary>
    public string Name => Credential.Name;

    /// <summary>用户名列:没带用户名的写明「由连接提供」,而不是留一格空白让人以为没存上。</summary>
    public string UsernameText => Credential.Username.Length > 0
        ? Credential.Username
        : Strings.Get("SharedCred_UsernameFromConnection");

    /// <summary>认证方式列。</summary>
    public string AuthText => Credential.AuthMethod switch
    {
        AuthMethod.PrivateKey => Strings.Get("Profile_KeyAuth"),
        AuthMethod.Certificate => Strings.Get("Profile_CertAuth"),
        AuthMethod.Agent => Strings.Get("Profile_AgentAuth"),
        _ => Strings.Get("Profile_PasswordAuth")
    };

    /// <summary>使用中列:「N 条连接」。</summary>
    public string UsageText => Strings.Format("SharedCred_UsageCount", UsageCount);

    /// <summary>
    /// 本机缺认证材料(云同步没开端到端口令时拉来的凭据只有名称与用户名)。
    /// 表格上给个记号,免得用户到连接时才发现。
    /// </summary>
    public bool MissingSecret => !Credential.HasSecret;
}

/// <summary>
/// 设置 → 共享凭据页(#550):列出共享凭据、谁在用,新建 / 编辑 / 删除。
/// </summary>
/// <remarks>
/// 与「密钥管理」页同一种做法:操作当场生效,不等设置窗口的「保存」——
/// 凭据存在自己的集合里,不是 <see cref="AppSettings" /> 的一部分。
/// </remarks>
public sealed class SharedCredentialsViewModel : ReactiveObject
{
    private readonly ISharedCredentialRepository? _repository;
    private readonly SharedCredentialService? _service;
    private readonly ISessionRepository? _sessions;

    /// <summary>创建页面视图模型;三个服务缺一(设计期、无 UI 单测)时页面只显示空表。</summary>
    public SharedCredentialsViewModel(
        ISharedCredentialRepository? repository,
        SharedCredentialService? service,
        ISessionRepository? sessions)
    {
        _repository = repository;
        _service = service;
        _sessions = sessions;
        // 登录框里勾了「同时更新共享凭据」、云同步拉下来新凭据,都会改到这张表。
        _repository?.Changed += (_, _) => RxSchedulers.MainThreadScheduler.Schedule(() => _ = RefreshAsync());
    }

    /// <summary>服务是否齐全(不齐全时新建按钮不可用)。</summary>
    public bool IsAvailable => _repository is not null && _service is not null && _sessions is not null;

    /// <summary>全部凭据行。</summary>
    public ObservableCollection<SharedCredentialRow> Items { get; } = [];

    /// <summary>按搜索框筛过的行(表格绑它)。</summary>
    public ObservableCollection<SharedCredentialRow> FilteredItems { get; } = [];

    /// <summary>搜索框:按名称、用户名、备注筛。</summary>
    public string SearchQuery
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ApplyFilter();
        }
    } = string.Empty;

    /// <summary>一条凭据都没有(显示空状态的那段说明)。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>最近一次操作的结果或错误;没有为 null。</summary>
    public string? StatusMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>重读凭据与引用数。</summary>
    public async Task RefreshAsync()
    {
        if (_repository is null || _service is null)
        {
            return;
        }
        try
        {
            List<SharedCredential> credentials = await _repository.GetAllAsync();
            IReadOnlyDictionary<Guid, int> usages = await _service.CountUsagesAsync();
            Items.Clear();
            foreach (SharedCredential credential in credentials)
            {
                Items.Add(new(credential, usages.GetValueOrDefault(credential.Id)));
            }
            ApplyFilter();
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>为新建(<paramref name="existing" /> 为 null)或编辑准备编辑框视图模型。</summary>
    /// <param name="existing">要编辑的凭据。</param>
    /// <returns>编辑框视图模型;服务不齐全时为 null。</returns>
    public async Task<SharedCredentialEditorViewModel?> CreateEditorAsync(SharedCredential? existing)
    {
        if (!IsAvailable)
        {
            return null;
        }
        List<SessionProfile> profiles = await _sessions!.GetAllSessionsAsync();
        return new(existing?.Clone(), profiles);
    }

    /// <summary>主密码闸口:锁着时填了新密码先弹解锁框;没有时不过闸。</summary>
    public IMasterPasswordGate? MasterPassword { get; init; }

    /// <summary>保存编辑框的结果(凭据 + 谁在用它)。</summary>
    /// <param name="result">编辑框结果。</param>
    public async Task SaveAsync(SharedCredentialEditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_service is null)
        {
            return;
        }
        // 主密码锁着而这次填了新的密码 / 口令:加不了密。先弹解锁框;取消就照存别的字段、密码这次不存(仓储冻在库里那一份上),明说。
        bool typedSecret = IsTypedSecret(result.Credential.Password) || IsTypedSecret(result.Credential.PrivateKeyPassphrase);
        bool secretNotSaved = typedSecret && MasterPassword is { } gate && !await gate.EnsureUnlockedAsync(interactive: true);
        try
        {
            await _service.SaveAsync(result.Credential, result.Users);
            StatusMessage = Strings.Format("SharedCred_Saved", result.Credential.Name, result.Users.Count)
                            + (secretNotSaved ? " " + Strings.Get("MasterPassword_SecretNotSaved") : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            StatusMessage = ex.Message;
        }
        await RefreshAsync();
    }

    private static bool IsTypedSecret(string? value) => !string.IsNullOrEmpty(value) && !StoredSecret.IsUnreadable(value);

    /// <summary>删除确认框的正文:有连接在用时说清楚它们会怎样。</summary>
    /// <param name="row">要删的那一行。</param>
    /// <returns>确认文案。</returns>
    public static string DeleteConfirmMessage(SharedCredentialRow row) =>
        row.UsageCount > 0
            ? Strings.Format("SharedCred_DeleteConfirmInUse", row.Name, row.UsageCount)
            : Strings.Format("SharedCred_DeleteConfirm", row.Name);

    /// <summary>删除一条凭据(引用它的连接各自留一份同样的凭据,删完照样能连)。</summary>
    /// <param name="credential">要删的凭据。</param>
    public async Task DeleteAsync(SharedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (_service is null)
        {
            return;
        }
        try
        {
            await _service.DeleteAsync(credential);
            StatusMessage = Strings.Format("SharedCred_Deleted", credential.Name);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            StatusMessage = ex.Message;
        }
        await RefreshAsync();
    }

    private void ApplyFilter()
    {
        string query = SearchQuery.Trim();
        FilteredItems.Clear();
        foreach (SharedCredentialRow row in Items)
        {
            if (query.Length == 0
                || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Credential.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Credential.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                FilteredItems.Add(row);
            }
        }
    }
}
