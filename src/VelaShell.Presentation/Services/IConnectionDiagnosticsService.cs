using VelaShell.Core.Models;

namespace VelaShell.Presentation.Services;

/// <summary>诊断步骤状态(设计 RGXg1:✅ 成功 / ⚠ 警告 / ✗ 失败 / ⏸ 跳过)。</summary>
public enum DiagnosticStepStatus
{
    /// <summary>等待执行,尚未开始。</summary>
    Pending,

    /// <summary>正在执行中。</summary>
    Running,

    /// <summary>执行成功。</summary>
    Success,

    /// <summary>执行完成但存在警告。</summary>
    Warning,

    /// <summary>执行失败。</summary>
    Failed,

    /// <summary>因前置条件不满足等原因被跳过。</summary>
    Skipped
}

/// <summary>单个诊断步骤的即时快照;Index 对应固定的四步顺序。</summary>
/// <param name="Index">步骤序号,对应固定的四步诊断顺序。</param>
/// <param name="Name">步骤名称。</param>
/// <param name="Status">步骤当前状态。</param>
/// <param name="Detail">步骤的补充说明或错误详情,可为 <see langword="null" />。</param>
/// <param name="ElapsedMs">步骤耗时(毫秒),未完成时可为 <see langword="null" />。</param>
public sealed record DiagnosticStepUpdate(
    int Index,
    string Name,
    DiagnosticStepStatus Status,
    string? Detail = null,
    long? ElapsedMs = null);

/// <summary>一次完整诊断的结果:四个步骤 + 发现的问题与修复建议(设计 RGXg1)。</summary>
public sealed class DiagnosticReport
{
    /// <summary>本次诊断的四个步骤及其最终状态。</summary>
    public required IReadOnlyList<DiagnosticStepUpdate> Steps { get; init; }

    /// <summary>发现的问题标题;无问题时为 <see langword="null" />。</summary>
    public string? IssueTitle { get; init; }

    /// <summary>发现问题的详细描述;无问题时为 <see langword="null" />。</summary>
    public string? IssueDescription { get; init; }

    /// <summary>针对所发现问题给出的修复建议列表。</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>诊断是否整体成功(即未发现任何问题)。</summary>
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
