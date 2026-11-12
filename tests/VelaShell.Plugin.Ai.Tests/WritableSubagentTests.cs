using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class WritableSubagentTests
{
    private static AIFunction Tool(IList<AITool> tools, string name)
        => tools.OfType<AIFunction>().Single(tool => tool.Name == name);

    [TestMethod]
    public async Task Tools_RequireOptInAndPreserveDisabledTools()
    {
        using var context = new TestPluginContext();
        await using var parent = new AgentToolbox(context)
        {
            LocalWorkspaces = [new LocalWorkspace { Id = "local", RootPath = Environment.CurrentDirectory, AllowWrite = true }],
            DisabledTools = new HashSet<string> { "rename_remote_path" }
        };
        await using CommandJobRunScope run = parent.BeginRun("test", "turn");
        await using AgentToolbox child = parent.CreateSubagentToolbox();
        string[] readOnly = [.. child.CreateSubagentTools(false, run).Select(t => t.Name)];
        string[] writable = [.. child.CreateSubagentTools(true, run).Select(t => t.Name)];
        Assert.DoesNotContain("write_local_file", readOnly);
        Assert.DoesNotContain("write_remote_file", readOnly);
        Assert.Contains("read_local_file", readOnly);
        Assert.Contains("write_local_file", writable);
        Assert.Contains("patch_remote_file", writable);
        foreach (string name in new[] { "rename_remote_path", "run_command", "run_on_sessions", "write_terminal",
                     "open_session", "close_session", "delegate_subagent", "upload_local_file", "download_remote_file",
                     "transfer_resume", "task_status" })
        {
            Assert.DoesNotContain(name, writable);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteWrite_UsesCurrentParentApprovalAndDenialStopsParent(bool missingHandler)
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        await using var parent = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            Approval = ApprovalMode.Bypass
        };
        await using CommandJobRunScope run = parent.BeginRun("test", "turn");
        await using AgentToolbox child = parent.CreateSubagentToolbox();
        AIFunction write = Tool(child.CreateSubagentTools(true, run), "write_remote_file");
        parent.Approval = ApprovalMode.Ask;
        int approvals = 0;
        if (!missingHandler)
            parent.ApprovalHandler = _ => { approvals++; return Task.FromResult(false); };
        object? result = await write.InvokeAsync(new AIFunctionArguments { ["path"] = "/new", ["content"] = "denied" });
        Assert.Contains("DENIED", result?.ToString() ?? "");
        Assert.AreEqual(missingHandler ? 0 : 1, approvals);
        Assert.IsNull(context.FakeRemoteFs.GetFile(session.SessionId, "/new"));
        Assert.IsTrue(run.HasDeniedExecution);
        AIFunction parentWrite = Tool(parent.CreateTools(ChatMode.Agent, runScope: run), "write_remote_file");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await parentWrite.InvokeAsync(new AIFunctionArguments { ["path"] = "/new", ["content"] = "retry" }));
    }

    [TestMethod]
    public async Task CancelDuringApproval_ReleasesGateWithoutWriting()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var parent = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ => { entered.TrySetResult(); return approval.Task; }
        };
        await using CommandJobRunScope run = parent.BeginRun("test", "turn");
        await using AgentToolbox child = parent.CreateSubagentToolbox();
        AIFunction write = Tool(child.CreateSubagentTools(true, run), "write_remote_file");
        using var cancellation = new CancellationTokenSource();
        Task<object?> pending = write.InvokeAsync(new AIFunctionArguments
        { ["path"] = "/new", ["content"] = "cancelled" }, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        approval.TrySetResult(true);
        Assert.IsNull(context.FakeRemoteFs.GetFile(session.SessionId, "/new"));
        Assert.IsFalse(run.HasDeniedExecution);
        parent.Approval = ApprovalMode.Bypass;
        await write.InvokeAsync(new AIFunctionArguments { ["path"] = "/new", ["content"] = "next" })
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("next", System.Text.Encoding.UTF8.GetString(
            context.FakeRemoteFs.GetFile(session.SessionId, "/new")!));
    }

    [TestMethod]
    public async Task LocalWrites_PreserveScopeAndSerializeParentAndChildren()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "subagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(file, "one two");
            using var context = new TestPluginContext();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int approvals = 0;
            await using var parent = new AgentToolbox(context)
            {
                LocalWorkspaces = [new LocalWorkspace { Id = "local", RootPath = root, AllowWrite = true }],
                LocalDefaultWorkspaceId = "local",
                ApprovalHandler = _ =>
                {
                    if (Interlocked.Increment(ref approvals) == 1)
                    {
                        entered.TrySetResult();
                        return release.Task;
                    }
                    return Task.FromResult(true);
                }
            };
            await using CommandJobRunScope run = parent.BeginRun("test", "turn");
            await using AgentToolbox first = parent.CreateSubagentToolbox();
            await using AgentToolbox second = parent.CreateSubagentToolbox();
            AIFunction patch = Tool(first.CreateSubagentTools(true, run), "patch_local_file");
            Task<object?> active = patch.InvokeAsync(new AIFunctionArguments
            { ["path"] = "file.txt", ["oldText"] = "one", ["newText"] = "ONE" }).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            Task<object?> queued = Tool(second.CreateSubagentTools(true, run), "write_local_file")
                .InvokeAsync(new AIFunctionArguments { ["path"] = "file.txt", ["content"] = "must not write" },
                    cancelled.Token).AsTask();
            Task<object?> parentPatch = Tool(parent.CreateTools(ChatMode.Agent, runScope: run), "patch_local_file")
                .InvokeAsync(new AIFunctionArguments
                { ["path"] = "file.txt", ["oldText"] = "two", ["newText"] = "TWO" }).AsTask();
            Assert.IsFalse(parentPatch.IsCompleted);
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
            Assert.AreEqual(1, approvals);
            release.TrySetResult(true);
            await Task.WhenAll(active, parentPatch).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("ONE TWO", await File.ReadAllTextAsync(file));
            Assert.AreEqual(2, approvals);
            AIFunction write = Tool(second.CreateSubagentTools(true, run), "write_local_file");
            object? outside = await write.InvokeAsync(new AIFunctionArguments
            { ["path"] = Path.Combine("..", "outside.txt"), ["content"] = "no" });
            Assert.Contains("outside", outside?.ToString() ?? "");
            Assert.AreEqual(2, approvals);
            parent.LocalWorkspaces[0].RootPath = Path.GetDirectoryName(root)!;
            object? changedParent = await write.InvokeAsync(new AIFunctionArguments
            { ["path"] = Path.Combine("..", "outside.txt"), ["content"] = "no" });
            Assert.Contains("outside", changedParent?.ToString() ?? "");
            Assert.AreEqual(2, approvals);
            object? unavailable = await write.InvokeAsync(new AIFunctionArguments
            { ["path"] = "file.txt", ["workspaceId"] = "not-authorized", ["content"] = "no" });
            Assert.AreEqual("ONE TWO", await File.ReadAllTextAsync(file));
            Assert.AreEqual(2, approvals);
            Assert.Contains("not available", unavailable?.ToString() ?? "");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteWrites_InheritSessionScopeAndApprovedWritesSucceed()
    {
        using var context = new TestPluginContext();
        SessionInfo allowed = context.FakeSessions.AddConnected(host: "allowed");
        SessionInfo blocked = context.FakeSessions.AddConnected(host: "blocked");
        int approvals = 0;
        await using var parent = new AgentToolbox(context)
        {
            SessionIdProvider = () => allowed.SessionId,
            Scope = new TargetListScope([$"{allowed.Username}@{allowed.Host}:{allowed.Port}"]),
            ApprovalHandler = _ => { approvals++; return Task.FromResult(true); }
        };
        await using CommandJobRunScope run = parent.BeginRun("test", "turn");
        await using AgentToolbox child = parent.CreateSubagentToolbox();
        AIFunction write = Tool(child.CreateSubagentTools(true, run), "write_remote_file");
        await write.InvokeAsync(new AIFunctionArguments
        { ["path"] = "/new", ["content"] = "blocked", ["sessionId"] = blocked.SessionId });
        Assert.AreEqual(0, approvals);
        Assert.IsNull(context.FakeRemoteFs.GetFile(blocked.SessionId, "/new"));
        await write.InvokeAsync(new AIFunctionArguments { ["path"] = "/new", ["content"] = "allowed" });
        Assert.AreEqual(1, approvals);
        Assert.AreEqual("allowed", System.Text.Encoding.UTF8.GetString(
            context.FakeRemoteFs.GetFile(allowed.SessionId, "/new")!));
    }
}
