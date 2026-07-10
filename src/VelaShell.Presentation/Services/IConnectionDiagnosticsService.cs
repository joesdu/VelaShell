using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

/// <summary>诊断步骤状态(设计 RGXg1:✅ 成功 / ⚠ 警告 / ✗ 失败 / ⏸ 跳过)。</summary>
public enum DiagnosticStepStatus
{
    Pending,
    Running,
    Success,
    Warning,
    Failed,
    Skipped
}

/// <summary>单个诊断步骤的即时快照;Index 对应固定的四步顺序。</summary>
public sealed record DiagnosticStepUpdate(
    int Index,
    string Name,
    DiagnosticStepStatus Status,
    string? Detail = null,
    long? ElapsedMs = null);

/// <summary>一次完整诊断的结果:四个步骤 + 发现的问题与修复建议(设计 RGXg1)。</summary>
public sealed class DiagnosticReport
{
    public required IReadOnlyList<DiagnosticStepUpdate> Steps { get; init; }

    public string? IssueTitle { get; init; }

    public string? IssueDescription { get; init; }

    public IReadOnlyList<string> Suggestions { get; init; } = [];

    public bool Success => IssueTitle is null;
}

/// <summary>连接诊断中心(设计 RGXg1):逐步分析 DNS、TCP、SSH 握手与用户认证。</summary>
public interface IConnectionDiagnosticsService
{
    /// <summary>对一条连接配置执行逐步诊断;<paramref name="progress" /> 实时上报每步状态。</summary>
    Task<DiagnosticReport> DiagnoseAsync(
        SessionProfile profile,
        IProgress<DiagnosticStepUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
