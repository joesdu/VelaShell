using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 连接诊断中心的执行引擎(设计 RGXg1):按 DNS 解析 → TCP 建链 → SSH 握手 →
/// 用户认证 四步逐项检测。配了跳板的会话,前三步针对链路第一跳(本机实际直连的主机),
/// 认证步骤经 <see cref="IConnectionWorkflowService.TestConnectionAsync" /> 走完整跳板链。
/// </summary>
public sealed class ConnectionDiagnosticsService(
    ISessionRepository sessionRepository,
    IConnectionWorkflowService connectionWorkflowService) : IConnectionDiagnosticsService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromSeconds(5);
    private readonly IConnectionWorkflowService _connectionWorkflowService = connectionWorkflowService ?? throw new ArgumentNullException(nameof(connectionWorkflowService));

    private readonly ISessionRepository _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));

    /// <summary>
    /// 对指定会话执行四步连接诊断(DNS 解析 → TCP 建链 → SSH 握手 → 用户认证),
    /// 并通过 <paramref name="progress" /> 实时上报各步骤状态变化。
    /// </summary>
    /// <param name="profile">要诊断的会话配置。</param>
    /// <param name="progress">各步骤状态变更的进度回调,可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>汇总四步结果及问题标题/描述/修复建议的诊断报告。</returns>
    public async Task<DiagnosticReport> DiagnoseAsync(
        SessionProfile profile,
        IProgress<DiagnosticStepUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // 跳板链场景:本机实际直连的是链路最外层的跳板;DNS/TCP/握手针对它才有意义。
        (SessionProfile entry, bool viaJump, string? chainError) = await ResolveEntryHopAsync(profile).ConfigureAwait(false);
        string hopSuffix = viaJump ? Strings.Format("DiagSvc_JumpSuffix", DisplayName(entry)) : string.Empty;
        var steps = new DiagnosticStepUpdate[]
        {
            new(0, Strings.Get("DiagSvc_StepDns") + hopSuffix, DiagnosticStepStatus.Pending),
            new(1, Strings.Get("DiagSvc_StepTcp") + hopSuffix, DiagnosticStepStatus.Pending),
            new(2, Strings.Get("DiagSvc_StepSsh") + hopSuffix, DiagnosticStepStatus.Pending),
            new(3, viaJump ? Strings.Get("DiagSvc_StepAuthFullChain") : Strings.Get("DiagSvc_StepAuth"), DiagnosticStepStatus.Pending)
        };
        DiagnosticStepUpdate[] results = [.. steps];
        string? issueTitle = null;
        string? issueDescription = null;
        var suggestions = new List<string>();
        if (chainError is not null)
        {
            // 跳板配置本身有问题(被删除/成环):四步都无从谈起,直接给出问题面板。
            foreach (DiagnosticStepUpdate t in results)
            {
                Publish(t with { Status = DiagnosticStepStatus.Skipped, Detail = Strings.Get("DiagSvc_JumpConfigError") });
            }
            return new()
            {
                Steps = results,
                IssueTitle = Strings.Get("DiagSvc_JumpChainConfigError"),
                IssueDescription = chainError,
                Suggestions =
                [
                    Strings.Get("DiagSvc_SuggestReselectJump"),
                    Strings.Get("DiagSvc_SuggestRecreateJump")
                ]
            };
        }

        // ---- 步骤 1:DNS 解析 ----
        IPAddress? address = null;
        Publish(results[0] with { Status = DiagnosticStepStatus.Running });
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (IPAddress.TryParse(entry.Host, out IPAddress? literal))
            {
                address = literal;
                Publish(results[0] with
                {
                    Status = DiagnosticStepStatus.Success,
                    ElapsedMs = 0,
                    Detail = Strings.Get("DiagSvc_IpLiteral")
                });
            }
            else
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(entry.Host, cancellationToken)
                                                 .WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                address = (addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()) ?? throw new SocketException((int)SocketError.HostNotFound);
                Publish(results[0] with
                {
                    Status = DiagnosticStepStatus.Success,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Detail = $"{entry.Host} → {address}"
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(results[0] with
            {
                Status = DiagnosticStepStatus.Failed,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Detail = Trim(ex.Message)
            });
            issueTitle = Strings.Get("DiagSvc_DnsFailed");
            issueDescription = Strings.Format("DiagSvc_DnsFailedDesc", entry.Host, Trim(ex.Message));
            suggestions.Add(Strings.Get("DiagSvc_SuggestCheckHostname"));
            suggestions.Add(Strings.Get("DiagSvc_SuggestCheckDns"));
            suggestions.Add(Strings.Get("DiagSvc_SuggestVpn"));
        }

        // ---- 步骤 2:TCP 建链 + 步骤 3:SSH 握手(同一条 socket) ----
        if (address is not null)
        {
            Publish(results[1] with { Status = DiagnosticStepStatus.Running });
            using var tcp = new TcpClient(address.AddressFamily);
            stopwatch.Restart();
            bool tcpOk = false;
            try
            {
                await tcp.ConnectAsync(address, entry.Port, cancellationToken)
                         .AsTask().WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                tcpOk = true;
                Publish(results[1] with
                {
                    Status = DiagnosticStepStatus.Success,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Detail = $"{address}:{entry.Port}"
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string reason = ex is TimeoutException ? Strings.Get("DiagSvc_ConnectTimeout") : Trim(ex.Message);
                Publish(results[1] with
                {
                    Status = DiagnosticStepStatus.Failed,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Detail = reason
                });
                string tcpFailedTitle = Strings.Get("DiagSvc_TcpFailed");
                issueTitle ??= tcpFailedTitle;
                issueDescription ??= Strings.Format("DiagSvc_TcpFailedDesc", entry.Host, entry.Port, reason);
                if (issueTitle == tcpFailedTitle)
                {
                    suggestions.Add(Strings.Format("DiagSvc_SuggestCheckPort", entry.Port));
                    suggestions.Add(Strings.Get("DiagSvc_SuggestFirewall"));
                    suggestions.Add(Strings.Get("DiagSvc_SuggestHostOnline"));
                }
            }

            // ---- 步骤 3:SSH 握手(读服务端识别串,RFC 4253 服务端先发) ----
            if (tcpOk)
            {
                Publish(results[2] with { Status = DiagnosticStepStatus.Running });
                stopwatch.Restart();
                try
                {
                    string? banner = await ReadBannerAsync(tcp, cancellationToken).ConfigureAwait(false);
                    if (banner is not null && banner.StartsWith("SSH-", StringComparison.Ordinal))
                    {
                        Publish(results[2] with
                        {
                            Status = DiagnosticStepStatus.Success,
                            ElapsedMs = stopwatch.ElapsedMilliseconds,
                            Detail = banner
                        });
                    }
                    else
                    {
                        Publish(results[2] with
                        {
                            Status = DiagnosticStepStatus.Failed,
                            ElapsedMs = stopwatch.ElapsedMilliseconds,
                            Detail = banner is null ? Strings.Get("DiagSvc_NoBanner") : Strings.Format("DiagSvc_NonSshBanner", Trim(banner))
                        });
                        string notSshTitle = Strings.Get("DiagSvc_NotSshPort");
                        issueTitle ??= notSshTitle;
                        issueDescription ??= banner is null
                                                 ? Strings.Format("DiagSvc_NoBannerDesc", entry.Host, entry.Port, BannerTimeout.TotalSeconds.ToString("0"))
                                                 : Strings.Format("DiagSvc_NonSshDesc", entry.Host, entry.Port, Trim(banner));
                        if (issueTitle == notSshTitle)
                        {
                            suggestions.Add(Strings.Format("DiagSvc_SuggestDefaultPort", entry.Port));
                            suggestions.Add(Strings.Get("DiagSvc_SuggestPortOccupied"));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Publish(results[2] with
                    {
                        Status = DiagnosticStepStatus.Failed,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        Detail = Trim(ex.Message)
                    });
                    issueTitle ??= Strings.Get("DiagSvc_SshHandshakeFailed");
                    issueDescription ??= Strings.Format("DiagSvc_SshBannerReadError", Trim(ex.Message));
                }
            }
            else
            {
                Publish(results[2] with { Status = DiagnosticStepStatus.Skipped, Detail = Strings.Get("DiagSvc_WaitFix") });
            }
        }
        else
        {
            Publish(results[1] with { Status = DiagnosticStepStatus.Skipped, Detail = Strings.Get("DiagSvc_WaitFix") });
            Publish(results[2] with { Status = DiagnosticStepStatus.Skipped, Detail = Strings.Get("DiagSvc_WaitFix") });
        }

        // ---- 步骤 4:用户认证(完整连接,含跳板链与主机密钥校验) ----
        if (issueTitle is not null)
        {
            Publish(results[3] with { Status = DiagnosticStepStatus.Skipped, Detail = Strings.Get("DiagSvc_WaitFix") });
        }
        else if (!HasStoredCredentials(profile))
        {
            Publish(results[3] with
            {
                Status = DiagnosticStepStatus.Warning,
                Detail = Strings.Get("DiagSvc_NoCredsSkip")
            });
            suggestions.Add(Strings.Get("DiagSvc_SuggestSaveCreds"));
        }
        else
        {
            Publish(results[3] with { Status = DiagnosticStepStatus.Running });
            stopwatch.Restart();
            ConnectionTestResult test = await _connectionWorkflowService.TestConnectionAsync(profile, cancellationToken).ConfigureAwait(false);
            if (test.Success)
            {
                Publish(results[3] with
                {
                    Status = DiagnosticStepStatus.Success,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Detail = Strings.Format("DiagSvc_LoginSuccess", profile.Username, profile.Host)
                });
            }
            else
            {
                string reason = Trim(test.ErrorMessage ?? Strings.Get("DiagSvc_UnknownError"));
                Publish(results[3] with
                {
                    Status = DiagnosticStepStatus.Failed,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Detail = reason
                });
                (issueTitle, issueDescription) = ClassifyAuthFailure(profile, viaJump, reason);
                suggestions.AddRange(SuggestForAuthFailure(profile, viaJump, reason));
            }
        }
        return new()
        {
            Steps = results,
            IssueTitle = issueTitle,
            IssueDescription = issueDescription,
            Suggestions = suggestions
        };

        void Publish(DiagnosticStepUpdate update)
        {
            results[update.Index] = update;
            progress?.Report(update);
        }
    }

    /// <summary>
    /// 沿 JumpHostProfileId 找到链路最外层跳板(本机实际直连的主机);
    /// 无跳板时返回配置本身。链路损坏(成环/配置丢失)返回错误描述。
    /// </summary>
    private async Task<(SessionProfile Entry, bool ViaJump, string? Error)> ResolveEntryHopAsync(SessionProfile profile)
    {
        if (profile.JumpHostProfileId is null)
        {
            return (profile, false, null);
        }
        var visited = new HashSet<Guid> { profile.Id };
        SessionProfile current = profile;
        for (int depth = 0; current.JumpHostProfileId is { } jumpId; depth++)
        {
            if (depth >= 5)
            {
                return (profile, true, Strings.Get("DiagSvc_JumpChainTooLong"));
            }
            if (!visited.Add(jumpId))
            {
                return (profile, true, Strings.Get("DiagSvc_JumpChainLoop"));
            }
            SessionProfile? jump = await _sessionRepository.GetSessionAsync(jumpId).ConfigureAwait(false);
            if (jump is null)
            {
                return (profile, true, Strings.Get("Svc_JumpHostMissing"));
            }
            current = jump;
        }
        return (current, true, null);
    }

    /// <summary>读服务端 SSH 识别串的第一行(服务端可能先发多行 banner,识别串以 SSH- 开头)。</summary>
    private static async Task<string?> ReadBannerAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        NetworkStream stream = tcp.GetStream();
        byte[] buffer = new byte[512];
        var builder = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BannerTimeout);
        try
        {
            while (builder.Length < 4096)
            {
                int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                foreach (string line in builder.ToString().Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (trimmed.StartsWith("SSH-", StringComparison.Ordinal))
                    {
                        return trimmed;
                    }
                }
                if (builder.ToString().Contains('\n'))
                {
                    // 已经收到完整行但不是 SSH 识别串:返回首行供诊断展示。
                    string first = builder.ToString().Split('\n')[0].TrimEnd('\r');
                    if (first.Length > 0)
                    {
                        return first;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 服务端在超时前没有说话。
        }
        string text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool HasStoredCredentials(SessionProfile profile) =>
        profile.AuthMethod switch
        {
            AuthMethod.Password => !string.IsNullOrEmpty(profile.Password),
            AuthMethod.PrivateKey => !string.IsNullOrEmpty(profile.PrivateKeyPath),
            _ => false
        };

    private static (string Title, string Description) ClassifyAuthFailure(SessionProfile profile, bool viaJump, string reason)
    {
        if (ContainsAny(reason, "Permission denied", "denied", "认证", "password", "publickey", "Authentication"))
        {
            return (Strings.Get("DiagSvc_AuthFailed"), Strings.Format("DiagSvc_AuthFailedDesc", profile.Username, reason));
        }
        if (ContainsAny(reason, "fingerprint", "host key", "指纹", "主机密钥"))
        {
            return (Strings.Get("DiagSvc_HostKeyFailed"), reason);
        }
        if (viaJump && ContainsAny(reason, "跳板", "jump"))
        {
            return (Strings.Get("DiagSvc_JumpChainFailed"), reason);
        }
        return (Strings.Get("DiagSvc_ConnFailed"), reason);
    }

    private static IEnumerable<string> SuggestForAuthFailure(SessionProfile profile, bool viaJump, string reason)
    {
        if (ContainsAny(reason, "Permission denied", "denied", "认证", "password", "publickey", "Authentication"))
        {
            yield return Strings.Get("DiagSvc_SuggestCheckCreds");
            if (profile.AuthMethod == AuthMethod.PrivateKey)
            {
                yield return Strings.Get("DiagSvc_SuggestKeyMatch");
            }
            else
            {
                yield return Strings.Get("DiagSvc_SuggestPasswordDisabled");
            }
            yield break;
        }
        if (ContainsAny(reason, "fingerprint", "host key", "指纹", "主机密钥"))
        {
            yield return Strings.Get("DiagSvc_SuggestTrustNewKey");
            yield return Strings.Get("DiagSvc_SuggestMitm");
            yield break;
        }
        if (viaJump)
        {
            yield return Strings.Get("DiagSvc_SuggestJumpCreds");
            yield return Strings.Get("DiagSvc_SuggestDiagnoseJump");
            yield break;
        }
        yield return Strings.Get("DiagSvc_SuggestCheckSshd");
    }

    private static bool ContainsAny(string text, params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(SessionProfile profile) => string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;

    private static string Trim(string message)
    {
        string single = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > 160 ? single[..160] + "…" : single;
    }
}
