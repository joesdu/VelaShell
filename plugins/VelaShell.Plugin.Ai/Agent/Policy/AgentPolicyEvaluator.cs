namespace VelaShell.Plugin.Ai.Agent.Policy;

/// <summary>
/// Pure, host-neutral policy evaluation. It does not prompt, execute work, or infer that an
/// operation with an unknown outcome is safe to repeat.
/// </summary>
public sealed class AgentPolicyEvaluator : IAgentPolicyEvaluator
{
    /// <inheritdoc/>
    /// <remarks>绕过模式直接放行；只读自动模式仅放行已确认安全且无副作用的操作。询问模式始终要求审批。</remarks>
    public AgentPolicyDecision Evaluate(AgentPolicyRequest request, AgentApprovalMode approvalMode)
    {
        ArgumentNullException.ThrowIfNull(request);
        return approvalMode switch
        {
            AgentApprovalMode.Bypass => AgentPolicyDecision.Allowed(),
            AgentApprovalMode.ReadOnlyAuto when request.SideEffect == AgentSideEffect.None
                && request.IsKnownSafe => AgentPolicyDecision.Allowed("known-safe"),
            AgentApprovalMode.Ask or AgentApprovalMode.ReadOnlyAuto
                => AgentPolicyDecision.ApprovalRequired(),
            _ => throw new ArgumentOutOfRangeException(nameof(approvalMode), approvalMode, "Unknown approval mode.")
        };
    }

    /// <inheritdoc/>
    /// <remarks>仅当操作已获准、已确认安全且无副作用，并在执行前遇到结果明确的暂时性故障时，才判定可安全重试。</remarks>
    public AgentRetryDecision ClassifyRetry(AgentRetryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.OutcomeKnown || request.Decision.Kind == AgentPolicyDecisionKind.UnknownOutcome)
        {
            return AgentRetryDecision.Unsafe("unknown-outcome");
        }

        if (request.Decision.Kind is AgentPolicyDecisionKind.ExecutionDenied
            or AgentPolicyDecisionKind.ApprovalDenied
            or AgentPolicyDecisionKind.ApprovalRequired)
        {
            return AgentRetryDecision.Unsafe("policy-denied");
        }

        if (request.Decision.Kind != AgentPolicyDecisionKind.Allowed)
        {
            return AgentRetryDecision.Unsafe("not-allowed");
        }

        if (request.Failure != AgentPolicyFailureKind.Transient)
        {
            return AgentRetryDecision.Unsafe("failure-not-transient");
        }

        if (request.ExecutionStarted)
        {
            return AgentRetryDecision.Unsafe("execution-started");
        }

        if (request.Operation.SideEffect != AgentSideEffect.None || !request.Operation.IsKnownSafe)
        {
            return AgentRetryDecision.Unsafe("side-effect-not-proven-safe");
        }

        return AgentRetryDecision.Safe();
    }
}

/// <summary>Approval orchestration around the pure evaluator.</summary>
public sealed class AgentPolicyGate : IAgentPolicyGate
{
    private readonly IAgentPolicyEvaluator _evaluator;
    private readonly AgentApprovalMode _approvalMode;
    private readonly IAgentPolicyApprovalPort? _approvalPort;

    /// <summary>Creates a gate using the supplied mode, evaluator, and optional approval port.</summary>
    public AgentPolicyGate(AgentApprovalMode approvalMode, IAgentPolicyEvaluator? evaluator = null,
        IAgentPolicyApprovalPort? approvalPort = null)
    {
        _approvalMode = approvalMode;
        _evaluator = evaluator ?? new AgentPolicyEvaluator();
        _approvalPort = approvalPort;
    }

    /// <inheritdoc/>
    public AgentPolicyDecision Preflight(AgentPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _evaluator.Evaluate(request, _approvalMode);
    }

    /// <inheritdoc/>
    /// <remarks>需要审批但没有配置审批端口时，返回审批未通过的决定。</remarks>
    public async ValueTask<AgentPolicyDecision> AuthorizeAsync(AgentPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        AgentPolicyDecision decision = Preflight(request);
        if (!decision.RequiresApproval)
        {
            return decision;
        }

        if (_approvalPort is null)
        {
            return AgentPolicyDecision.ApprovalDenied("approval-unavailable");
        }

        bool approved = await _approvalPort.RequestApprovalAsync(request, cancellationToken).ConfigureAwait(false);
        return approved
            ? AgentPolicyDecision.Allowed("approval-granted")
            : AgentPolicyDecision.ApprovalDenied();
    }

    /// <inheritdoc/>
    public AgentRetryDecision ClassifyRetry(AgentRetryRequest request)
        => _evaluator.ClassifyRetry(request);
}
