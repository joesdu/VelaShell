using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

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

    public async Task<DiagnosticReport> DiagnoseAsync(
        SessionProfile profile,
        IProgress<DiagnosticStepUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // 跳板链场景:本机实际直连的是链路最外层的跳板;DNS/TCP/握手针对它才有意义。
        (SessionProfile entry, bool viaJump, string? chainError) = await ResolveEntryHopAsync(profile).ConfigureAwait(false);
        string hopSuffix = viaJump ? $"(跳板 {DisplayName(entry)})" : string.Empty;
        var steps = new DiagnosticStepUpdate[]
        {
            new(0, $"DNS 解析{hopSuffix}", DiagnosticStepStatus.Pending),
            new(1, $"TCP 建链{hopSuffix}", DiagnosticStepStatus.Pending),
            new(2, $"SSH 握手{hopSuffix}", DiagnosticStepStatus.Pending),
            new(3, viaJump ? "用户认证(完整跳板链)" : "用户认证", DiagnosticStepStatus.Pending)
        };
        DiagnosticStepUpdate[] results = steps.ToArray();
        string? issueTitle = null;
        string? issueDescription = null;
        var suggestions = new List<string>();
        if (chainError is not null)
        {
            // 跳板配置本身有问题(被删除/成环):四步都无从谈起,直接给出问题面板。
            foreach (DiagnosticStepUpdate t in results)
            {
                Publish(t with { Status = DiagnosticStepStatus.Skipped, Detail = "跳板配置异常" });
            }
            return new()
            {
                Steps = results,
                IssueTitle = "跳板链配置异常",
                IssueDescription = chainError,
                Suggestions =
                [
                    "打开该连接的编辑窗口 → 高级选项,重新选择跳板主机。",
                    "若跳板配置已删除,请改为直连或重建跳板配置。"
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
                    Detail = "IP 直连,无需解析"
                });
            }
            else
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(entry.Host, cancellationToken)
                                                 .WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (address is null)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }
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
            issueTitle = "DNS 解析失败";
            issueDescription = $"无法解析主机名 {entry.Host}:{Trim(ex.Message)}";
            suggestions.Add("检查主机名拼写是否正确。");
            suggestions.Add("检查本机 DNS 设置,或直接改用 IP 地址连接。");
            suggestions.Add("若目标在内网,确认已接入公司网络或 VPN。");
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
                string reason = ex is TimeoutException ? "连接超时" : Trim(ex.Message);
                Publish(results[1] with
                {
                    Status = DiagnosticStepStatus.Failed,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Detail = reason
                });
                issueTitle ??= "无法建立 TCP 连接";
                issueDescription ??= $"到 {entry.Host}:{entry.Port} 的 TCP 连接失败:{reason}";
                if (issueTitle == "无法建立 TCP 连接")
                {
                    suggestions.Add($"确认端口 {entry.Port} 正确,且服务器上的 SSH 服务正在运行。");
                    suggestions.Add("检查服务器防火墙/云安全组是否放行了该端口。");
                    suggestions.Add("确认主机在线(可尝试 ping 或从其它网络访问)。");
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
                            Detail = banner is null ? "未收到服务端应答" : $"非 SSH 应答:{Trim(banner)}"
                        });
                        issueTitle ??= "端口未运行 SSH 服务";
                        issueDescription ??= banner is null
                                                 ? $"{entry.Host}:{entry.Port} 可连通,但在 {BannerTimeout.TotalSeconds:0} 秒内未返回 SSH 服务标识。"
                                                 : $"{entry.Host}:{entry.Port} 返回了非 SSH 协议应答:{Trim(banner)}";
                        if (issueTitle == "端口未运行 SSH 服务")
                        {
                            suggestions.Add("确认端口号:SSH 默认 22,当前配置为 " + entry.Port + "。");
                            suggestions.Add("该端口可能被其它服务(HTTP/数据库等)占用,请核对服务器配置。");
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
                    issueTitle ??= "SSH 握手失败";
                    issueDescription ??= $"读取 SSH 服务标识时出错:{Trim(ex.Message)}";
                }
            }
            else
            {
                Publish(results[2] with { Status = DiagnosticStepStatus.Skipped, Detail = "等待修复后重试" });
            }
        }
        else
        {
            Publish(results[1] with { Status = DiagnosticStepStatus.Skipped, Detail = "等待修复后重试" });
            Publish(results[2] with { Status = DiagnosticStepStatus.Skipped, Detail = "等待修复后重试" });
        }

        // ---- 步骤 4:用户认证(完整连接,含跳板链与主机密钥校验) ----
        if (issueTitle is not null)
        {
            Publish(results[3] with { Status = DiagnosticStepStatus.Skipped, Detail = "等待修复后重试" });
        }
        else if (!HasStoredCredentials(profile))
        {
            Publish(results[3] with
            {
                Status = DiagnosticStepStatus.Warning,
                Detail = "未保存凭据,跳过认证测试"
            });
            suggestions.Add("该配置未保存密码/密钥,连接时会弹出登录验证;如需自动认证测试,请在编辑窗口保存凭据。");
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
                    Detail = $"{profile.Username}@{profile.Host} 登录成功"
                });
            }
            else
            {
                string reason = Trim(test.ErrorMessage ?? "未知错误");
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
                return (profile, true, "跳板链超过 5 跳,请精简跳板层级。");
            }
            if (!visited.Add(jumpId))
            {
                return (profile, true, "跳板配置形成了循环引用,请检查各配置的跳板主机设置。");
            }
            SessionProfile? jump = await _sessionRepository.GetSessionAsync(jumpId).ConfigureAwait(false);
            if (jump is null)
            {
                return (profile, true, "跳板主机配置不存在(可能已被删除),请重新设置。");
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
            AuthMethod.Password   => !string.IsNullOrEmpty(profile.Password),
            AuthMethod.PrivateKey => !string.IsNullOrEmpty(profile.PrivateKeyPath),
            _                     => false
        };

    private static (string Title, string Description) ClassifyAuthFailure(SessionProfile profile, bool viaJump, string reason)
    {
        if (ContainsAny(reason, "Permission denied", "denied", "认证", "password", "publickey", "Authentication"))
        {
            return ("身份验证失败", $"服务器拒绝了 {profile.Username} 的登录:{reason}");
        }
        if (ContainsAny(reason, "fingerprint", "host key", "指纹", "主机密钥"))
        {
            return ("主机密钥校验未通过", reason);
        }
        if (viaJump && ContainsAny(reason, "跳板", "jump"))
        {
            return ("跳板链建立失败", reason);
        }
        return ("连接建立失败", reason);
    }

    private static IEnumerable<string> SuggestForAuthFailure(SessionProfile profile, bool viaJump, string reason)
    {
        if (ContainsAny(reason, "Permission denied", "denied", "认证", "password", "publickey", "Authentication"))
        {
            yield return "检查用户名与密码/私钥是否正确,注意大小写。";
            if (profile.AuthMethod == AuthMethod.PrivateKey)
            {
                yield return "确认私钥文件与服务器上的公钥匹配,私钥口令(passphrase)正确。";
            }
            else
            {
                yield return "服务器可能禁用了密码登录(PasswordAuthentication no),可尝试改用密钥认证。";
            }
            yield break;
        }
        if (ContainsAny(reason, "fingerprint", "host key", "指纹", "主机密钥"))
        {
            yield return "服务器主机密钥与本地记录不一致:若服务器确实重装过,请在提示中信任新指纹。";
            yield return "若非预期变更,请警惕中间人攻击,先与服务器管理员核实。";
            yield break;
        }
        if (viaJump)
        {
            yield return "跳板机的配置需已保存凭据才能免交互连上,请检查各跳板配置。";
            yield return "可先对跳板机单独执行一次连接诊断,逐跳定位问题。";
            yield break;
        }
        yield return "网络与端口均正常,失败发生在 SSH 协商阶段;检查服务器 sshd 日志(/var/log/auth.log)获取具体原因。";
    }

    private static bool ContainsAny(string text, params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(SessionProfile profile) => string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;

    private static string Trim(string message)
    {
        string single = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > 160 ? single[..160] + "…" : single;
    }
}
