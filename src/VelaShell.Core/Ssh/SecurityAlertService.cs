using System.Text;
using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>一条安全告警及其(按设置解析后的)投递通道。</summary>
/// <param name="Category">告警类别标识(如指纹变更、指纹被拒)。</param>
/// <param name="Message">面向用户的告警文案。</param>
/// <param name="InApp">是否需要应用内通知。</param>
/// <param name="Sound">是否需要伴随提示音。</param>
public sealed record SecurityAlertNotice(string Category, string Message, bool InApp, bool Sound);

/// <summary>
/// 安全告警通道(设置 → 安全审计 → 告警通道):Webhook 与审计日志在这里投递;
/// 应用内/系统通知通过 <see cref="Alerted" /> 交给 UI 层渲染。
/// </summary>
public interface ISecurityAlertService
{
    /// <summary>需要 UI 呈现的告警(已按设置过滤;在任意线程触发,订阅方自行调度)。</summary>
    event Action<SecurityAlertNotice>? Alerted;

    /// <summary>投递一条安全事件(指纹变更被阻断、指纹被拒等)。永不抛出。</summary>
    Task RaiseAsync(string category, string message, object? detail = null);
}

/// <summary>
/// <see cref="ISecurityAlertService" /> 的默认实现:投递审计日志与 Webhook,并通过 <see cref="Alerted" /> 事件将应用内/系统通知转交 UI 层。
/// </summary>
public sealed class SecurityAlertService(ISettingsService settingsService, IAuditLogService? auditLog = null) : ISecurityAlertService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly ISettingsService _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    /// <inheritdoc />
    public event Action<SecurityAlertNotice>? Alerted;

    /// <inheritdoc />
    public async Task RaiseAsync(string category, string message, object? detail = null)
    {
        SecurityOptions security;
        try
        {
            security = (await _settingsService.GetSettingsAsync().ConfigureAwait(false)).Security;
        }
        catch
        {
            security = new();
        }

        // 审计日志始终记录(安全事件不可静默丢失)。
        if (auditLog is not null)
        {
            try
            {
                await auditLog.WriteAsync(new()
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Category = "security",
                    Action = category,
                    Detail = message
                }).ConfigureAwait(false);
            }
            catch
            {
                // 审计写入失败不阻塞告警链路。
            }
        }
        if (security.AlertInApp || security.AlertSound)
        {
            try
            {
                Alerted?.Invoke(new(category, message, security.AlertInApp, security.AlertSound));
            }
            catch
            {
                // UI 订阅者异常不影响其余通道。
            }
        }
        if (security.AlertWebhook && Uri.TryCreate(security.WebhookUrl, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    source = "VelaShell",
                    @event = category,
                    message,
                    detail,
                    timestamp = DateTimeOffset.UtcNow
                });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await Http.PostAsync(url, content).ConfigureAwait(false);
            }
            catch
            {
                // Webhook 端点不可达是常态,不影响连接流程。
            }
        }
    }
}
