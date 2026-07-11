using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Sync;

namespace VelaShell.ViewModels;

/// <summary>
/// 设置 → 云同步页:GitHub Gist 多端同步的配置、手动同步与版本历史。
/// 配置保存即时生效(独立于设置窗口的“保存设置”按钮):同步配置属于本机绑定数据,
/// 不参与设置的预览/回滚,也永远不进入同步载荷。
/// </summary>
public class SyncViewModel : ReactiveObject
{
    private readonly IGistSyncService _syncService;
    private SyncSettings _config = new();

    public SyncViewModel(IGistSyncService syncService)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        IObservable<bool> notBusy = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync, notBusy);
        SyncNowCommand = ReactiveCommand.CreateFromTask(() => RunAsync(_syncService.SyncNowAsync), notBusy);
        PushCommand = ReactiveCommand.CreateFromTask(() => RunAsync(_syncService.PushAsync), notBusy);
        PullCommand = ReactiveCommand.CreateFromTask(() => RunAsync(_syncService.PullAsync), notBusy);
        LoadRevisionsCommand = ReactiveCommand.CreateFromTask(LoadRevisionsAsync, notBusy);
        RestoreRevisionCommand = ReactiveCommand.CreateFromTask<GistRevision>(RestoreRevisionAsync, notBusy);
    }

    // ———— 配置字段(绑定) ————

    public bool Enabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool AutoSync
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public string GistId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string DeviceName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Environment.MachineName;

    /// <summary>令牌输入框内容:只写不回显;空 = 保留已保存令牌。</summary>
    public string TokenInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public bool HasSavedToken
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>口令输入框内容:只写不回显;空且未点“清除”时保留已保存口令。</summary>
    public string PassphraseInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public bool HasSavedPassphrase
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool SyncAppSettings
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public bool SyncProfiles
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    public bool SyncSnippets
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // ———— 运行状态 ————

    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string LastSyncText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "从未同步";

    public ObservableCollection<GistRevision> Revisions { get; } = [];

    public bool HasRevisions
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> SaveConfigCommand { get; }

    public ReactiveCommand<Unit, Unit> SyncNowCommand { get; }

    public ReactiveCommand<Unit, Unit> PushCommand { get; }

    public ReactiveCommand<Unit, Unit> PullCommand { get; }

    public ReactiveCommand<Unit, Unit> LoadRevisionsCommand { get; }

    public ReactiveCommand<GistRevision, Unit> RestoreRevisionCommand { get; }

    /// <summary>设置窗口打开时载入当前同步配置。</summary>
    public async Task LoadAsync()
    {
        try
        {
            _config = await _syncService.GetSyncSettingsAsync();
        }
        catch
        {
            _config = new();
        }
        Enabled = _config.Enabled;
        AutoSync = _config.AutoSync;
        GistId = _config.GistId;
        DeviceName = _config.DeviceName;
        SyncAppSettings = _config.SyncAppSettings;
        SyncProfiles = _config.SyncProfiles;
        SyncSnippets = _config.SyncSnippets;
        HasSavedToken = _syncService.HasToken(_config);
        HasSavedPassphrase = _syncService.HasPassphrase(_config);
        TokenInput = "";
        PassphraseInput = "";
        Status = "";
        UpdateLastSyncText();
    }

    private async Task SaveConfigAsync()
    {
        IsBusy = true;
        try
        {
            await SaveConfigCoreAsync();
            Status = HasSavedPassphrase
                         ? "同步配置已保存(端到端加密:已启用)。"
                         : "同步配置已保存(端到端加密:未启用,凭据不会上传)。";
        }
        catch (Exception ex)
        {
            Status = $"保存失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把界面当前配置(含未保存的令牌/口令输入)落盘并刷新状态标志。</summary>
    private async Task SaveConfigCoreAsync()
    {
        // 先取最新持久化配置作为底,避免覆盖同步操作刚写入的状态字段(LastSyncAtUtc 等)。
        _config = await _syncService.GetSyncSettingsAsync();
        _config.Enabled = Enabled;
        _config.AutoSync = AutoSync;
        _config.GistId = GistId.Trim();
        _config.DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
        _config.SyncAppSettings = SyncAppSettings;
        _config.SyncProfiles = SyncProfiles;
        _config.SyncSnippets = SyncSnippets;
        _syncService.ApplyToken(_config, TokenInput);

        // 口令:输入了新值才更新;留空保持已保存值不变。
        if (!string.IsNullOrEmpty(PassphraseInput))
        {
            _syncService.ApplyPassphrase(_config, PassphraseInput);
        }
        await _syncService.SaveSyncSettingsAsync(_config);
        HasSavedToken = _syncService.HasToken(_config);
        HasSavedPassphrase = _syncService.HasPassphrase(_config);
        TokenInput = "";
        PassphraseInput = "";
    }

    private async Task RunAsync(Func<CancellationToken, Task<SyncResult>> operation)
    {
        IsBusy = true;
        Status = "同步中…";
        try
        {
            // 同步前先把界面当前配置落盘:防止“在输入框填了令牌/口令,
            // 没点保存就直接点立即同步”导致输入被静默丢弃(用户实际踩过的坑)。
            await SaveConfigCoreAsync();
            SyncResult result = await Task.Run(() => operation(CancellationToken.None));
            Status = result.Message;
            _config = await _syncService.GetSyncSettingsAsync();
            GistId = _config.GistId;
            HasSavedToken = _syncService.HasToken(_config);
            HasSavedPassphrase = _syncService.HasPassphrase(_config);
            UpdateLastSyncText();
        }
        catch (Exception ex)
        {
            Status = $"同步失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRevisionsAsync()
    {
        IsBusy = true;
        try
        {
            List<GistRevision> revisions = await Task.Run(() => _syncService.GetRevisionsAsync());
            Revisions.Clear();
            foreach (GistRevision revision in revisions)
            {
                Revisions.Add(revision);
            }
            Status = revisions.Count > 0 ? $"云端共 {revisions.Count} 个版本(最多显示 30 个)。" : "云端暂无版本记录。";
        }
        catch (Exception ex)
        {
            Status = $"读取版本历史失败:{ex.Message}";
        }
        finally
        {
            HasRevisions = Revisions.Count > 0;
            IsBusy = false;
        }
    }

    private async Task RestoreRevisionAsync(GistRevision revision)
    {
        IsBusy = true;
        Status = "恢复中…";
        try
        {
            await SaveConfigCoreAsync(); // 同步操作前落盘界面配置(含未保存的口令输入)
            SyncResult result = await Task.Run(() => _syncService.RestoreRevisionAsync(revision.Version));
            Status = result.Message;
            _config = await _syncService.GetSyncSettingsAsync();
            UpdateLastSyncText();
        }
        catch (Exception ex)
        {
            Status = $"恢复失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateLastSyncText() =>
        LastSyncText = _config.LastSyncAtUtc is { } at
                           ? $"上次同步:{at.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                           : "从未同步";
}
