using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Audit;

/// <summary>
/// 给设置服务加上审计:每次保存比一下改之前的那份,记下哪几类变了(<see cref="SettingsCategories" />),不记值。
/// 其余操作原样转给内层。
/// </summary>
/// <remarks>
/// <para>
/// 保存点很多:设置窗口的「保存」、回放中心的自动录制开关、外部拉起勾「不再询问」、终端里缩放字号、
/// 文件面板切列、云同步拉取……挂在服务上一处管住。窗口尺寸、上次打开的会话这类运行状态与一次性迁移标记不算改动
/// (每次退出都会写窗口尺寸),所以只动了它们的保存不留记录。
/// </para>
/// <para>
/// <b>先比、再存</b>:有的调用方存完还接着改手里那份对象,存完再比会把之后的改动也算进这一次。
/// 改之前的那份取内层的只读快照,没有快照时读一次;读不出来就不记。
/// </para>
/// </remarks>
/// <param name="inner">真正的设置服务。</param>
/// <param name="auditLog">审计日志;null 时只转发。</param>
public sealed class AuditedSettingsService(ISettingsService inner, IAuditLogService? auditLog) : ISettingsService
{
    private readonly ISettingsService _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public event Action<AppSettings>? SettingsSaved
    {
        add => _inner.SettingsSaved += value;
        remove => _inner.SettingsSaved -= value;
    }

    /// <inheritdoc />
    public AppSettings? CurrentSnapshot => _inner.CurrentSnapshot;

    /// <inheritdoc />
    public AppSettings GetStartupSnapshot() => _inner.GetStartupSnapshot();

    /// <inheritdoc />
    public Task SecretsLoaded => _inner.SecretsLoaded;

    /// <inheritdoc />
    public Task<AppSettings> GetSettingsAsync() => _inner.GetSettingsAsync();

    /// <inheritdoc />
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IReadOnlyList<SettingsCategory> changed = [];
        if (auditLog is not null)
        {
            try
            {
                AppSettings before = _inner.CurrentSnapshot ?? await _inner.GetSettingsAsync().ConfigureAwait(false);
                changed = SettingsCategories.Changed(before, settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 读不出改之前的那份:不记,保存照常。
            }
        }
        await _inner.SaveSettingsAsync(settings).ConfigureAwait(false);
        if (changed.Count > 0)
        {
            await ConfigAudit.WriteAsync(auditLog!, AuditCategories.Settings, AuditActions.SettingsChanged, null,
                SettingsCategories.JoinLabels(changed)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<AppState> GetStateAsync() => _inner.GetStateAsync();

    /// <inheritdoc />
    public Task SaveStateAsync(AppState state) => _inner.SaveStateAsync(state);
}
