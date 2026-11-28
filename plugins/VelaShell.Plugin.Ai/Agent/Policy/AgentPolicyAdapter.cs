using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Agent.Policy;

/// <summary>
/// Bridges the current Agent callback and run-state conventions to the host-neutral policy gate.
/// The adapter is wired at the runtime boundary; existing execution loops remain the final
/// authority for starting side effects and continue to enforce their own cancellation and ledger rules.
/// </summary>
public sealed class AgentPolicyAdapter : IAgentPolicyGate
{
    private readonly AgentApprovalMode _approvalMode;
    private readonly Func<ApprovalRequest, Task<bool>>? _approvalHandler;
    private readonly Func<bool>? _hasDeniedExecution;
    private readonly Func<bool>? _hasUnknownOutcome;
    private readonly Action? _markExecutionDenied;
    private readonly IAgentPolicyEvaluator _evaluator;

    /// <summary>Creates an adapter over the existing approval callback and run-state probes.</summary>
    public AgentPolicyAdapter(ApprovalMode approvalMode,
        Func<ApprovalRequest, Task<bool>>? approvalHandler = null,
        Func<bool>? hasDeniedExecution = null,
        Func<bool>? hasUnknownOutcome = null,
        Action? markExecutionDenied = null,
        IAgentPolicyEvaluator? evaluator = null)
    {
        _approvalMode = approvalMode switch
        {
            ApprovalMode.Ask => AgentApprovalMode.Ask,
            ApprovalMode.ReadOnlyAuto => AgentApprovalMode.ReadOnlyAuto,
            ApprovalMode.Bypass => AgentApprovalMode.Bypass,
            _ => throw new ArgumentOutOfRangeException(nameof(approvalMode), approvalMode, "Unknown approval mode.")
        };
        _approvalHandler = approvalHandler;
        _hasDeniedExecution = hasDeniedExecution;
        _hasUnknownOutcome = hasUnknownOutcome;
        _markExecutionDenied = markExecutionDenied;
        _evaluator = evaluator ?? new AgentPolicyEvaluator();
    }

    /// <summary>Evaluates state and plugin approval policy without invoking the approval callback.</summary>
    public AgentPolicyDecision Preflight(AgentPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_hasDeniedExecution?.Invoke() == true)
        {
            return AgentPolicyDecision.ExecutionDenied();
        }

        if (_hasUnknownOutcome?.Invoke() == true)
        {
            return AgentPolicyDecision.UnknownOutcome();
        }

        return _evaluator.Evaluate(request, _approvalMode);
    }

    /// <inheritdoc/>
    /// <remarks>即使审批通过，也会重新检查运行状态；等待期间发生的执行拒绝或结果不明优先于批准答复。</remarks>
    public async ValueTask<AgentPolicyDecision> AuthorizeAsync(AgentPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        AgentPolicyDecision decision = Preflight(request);
        if (!decision.RequiresApproval)
        {
            return decision;
        }

        bool approved;
        if (_approvalHandler is null)
        {
            approved = false;
        }
        else
        {
            ApprovalRequest approval = new(request.Operation, request.Detail, request.RepeatKey, request.Origin);
            approved = await _approvalHandler(approval).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!approved)
        {
            _markExecutionDenied?.Invoke();
            return EvaluateDeniedAfterApproval();
        }

        // Approval is not a lease over run state. A denial or unknown result that appeared while
        // the prompt was open wins over the callback's answer.
        return EvaluateDeniedAfterApproval() switch
        {
            { Kind: AgentPolicyDecisionKind.ExecutionDenied } denied => denied,
            { Kind: AgentPolicyDecisionKind.UnknownOutcome } unknown => unknown,
            _ => AgentPolicyDecision.Allowed("approval-granted")
        };
    }

    /// <inheritdoc/>
    public AgentRetryDecision ClassifyRetry(AgentRetryRequest request)
        => _evaluator.ClassifyRetry(request);

    private AgentPolicyDecision EvaluateDeniedAfterApproval()
    {
        if (_hasDeniedExecution?.Invoke() == true)
        {
            return AgentPolicyDecision.ExecutionDenied();
        }

        if (_hasUnknownOutcome?.Invoke() == true)
        {
            return AgentPolicyDecision.UnknownOutcome();
        }

        return AgentPolicyDecision.ApprovalDenied();
    }
}
