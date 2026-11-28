namespace VelaShell.Plugin.Ai.Chat.Runtime;

/// <summary>
/// Stable, persistence-safe summary of one Agent run.
/// It intentionally contains no prompt, arguments, tool output, credentials, or other sensitive execution data.
/// The legacy namespace is retained so serialized chat metadata and existing host consumers remain compatible.
/// </summary>
public sealed record AgentRunTelemetry(
    string RunId,
    string Status,
    int CurrentStep,
    int ToolCalls,
    int CompletedToolCalls,
    int SuccessfulToolCalls,
    int FailedToolCalls,
    int CancelledToolCalls,
    int BackgroundWaits,
    int CompletedBackgroundWaits,
    int Pauses,
    long ElapsedMilliseconds,
    long LastSequence);
