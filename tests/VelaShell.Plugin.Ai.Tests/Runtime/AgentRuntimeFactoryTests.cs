using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Chat.Runtime;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Core.Runtime;
using VelaShell.PluginSdk.RemoteExec;
using ChatRunStatus = VelaShell.Plugin.Ai.Chat.Runtime.AgentProjectionStatus;

namespace VelaShell.Plugin.Ai.Tests.Runtime;

[TestClass]
public sealed class AgentRuntimeFactoryTests
{
    [TestMethod]
    public async Task Factory_UsesOneSnapshotProjection_AndCompletesImplicitPlan()
    {
        await using var registry = new CommandJobRegistry(new NoopExec());
        await using CommandJobRunScope scope = registry.BeginRun("test", "run-1");
        using AgentRuntimeBundle runtime = AgentRuntimeFactory.Create("run-1", scope,
            ApprovalMode.Ask, null);

        Assert.AreSame(runtime.SnapshotProjector, runtime.Execution.SnapshotProjector);
        runtime.BeginImplicitPlan("inspect the workspace");
        runtime.CompleteWithVerification(["test evidence"]);

        Assert.AreEqual(AgentOrchestrationPhase.Completed, runtime.Orchestration.State.Phase);
        Assert.AreEqual(ChatRunStatus.Completed, runtime.Snapshot.Status);
        Assert.Contains(item => item.Kind == AgentRunEventKind.RunCompleted, runtime.Events.Snapshot);
    }

    [TestMethod]
    public async Task Factory_TracksCancellationAndFailureThroughSharedEvents()
    {
        await using var registry = new CommandJobRegistry(new NoopExec());
        await using CommandJobRunScope scope = registry.BeginRun("test", "run-2");
        using AgentRuntimeBundle runtime = AgentRuntimeFactory.Create("run-2", scope,
            ApprovalMode.Ask, null);

        runtime.BeginImplicitPlan("cancel this");
        runtime.MarkCancelled();
        Assert.AreEqual(AgentOrchestrationPhase.Cancelled, runtime.Orchestration.State.Phase);
        Assert.AreEqual(ChatRunStatus.Cancelled, runtime.Snapshot.Status);

        await using CommandJobRunScope failedScope = registry.BeginRun("test", "run-3");
        using AgentRuntimeBundle failed = AgentRuntimeFactory.Create("run-3", failedScope,
            ApprovalMode.Ask, null);
        failed.BeginImplicitPlan("pause this");
        failed.MarkFailure("policy", pause: true);
        Assert.AreEqual(AgentOrchestrationPhase.Paused, failed.Orchestration.State.Phase);
        Assert.AreEqual(ChatRunStatus.Paused, failed.Snapshot.Status);
    }

    private sealed class NoopExec : IRemoteExecApi
    {
        public Task<SudoExecResult> StreamSudoAsync(string sessionId, string command,
            SudoExecOptions? options, IProgress<SudoExecEvent> progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SudoExecResult(SudoExecOutcome.Exited, 0));

        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecResult(string.Empty) { ExitCode = 0 });

        public Task<ExecStreamResult> StreamAsync(string sessionId, string command,
            ExecStreamOptions? options, IProgress<ExecOutput> output,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecStreamResult(0, 0));
    }
}
