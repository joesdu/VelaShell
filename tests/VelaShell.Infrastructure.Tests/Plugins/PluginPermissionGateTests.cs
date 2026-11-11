using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class PluginPermissionGateTests
{
    [TestMethod]
    public async Task AutoApproval_DoesNotWaitForPromptAndKeepsRevocableOwnedLeases()
    {
        var asking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pending = new CancellationTokenSource();
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            asking.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(6));
            return PluginPermissionDecision.Deny;
        });
        var gate = new PluginPermissionGate(null, prompt);
        using UploadPermissionLease waiting = gate.CreateUploadLease("manual");
        Task<bool> manual = gate.CheckUploadAsync(waiting, "manual", "s", "/local", "/remote", false, 8, pending.Token);
        try
        {
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using UploadPermissionLease upload = gate.CreateUploadLease("p");
            using DownloadPermissionLease download = gate.CreateDownloadLease("p");
            int callbacks = 0;
            Assert.IsFalse(await gate.CheckUploadAsync(upload, "other", "s", "/local", "/remote", false, 8, default,
                () => callbacks++, autoApprove: true));
            Assert.IsFalse(await gate.CheckDownloadAsync(download, "other", "s", "/remote", "/local", false, 8, default,
                () => callbacks++, autoApprove: true));
            Assert.IsTrue(await gate.CheckUploadAsync(upload, "p", "s", "/local", "/remote", false, 8, default,
                () => callbacks++, autoApprove: true).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(await gate.CheckDownloadAsync(download, "p", "s", "/remote", "/local", false, 8, default,
                () => callbacks++, autoApprove: true).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(2, callbacks);
            Assert.IsTrue(upload.Approved);
            Assert.IsTrue(download.Approved);
            await gate.RevokeAsync("p");
            Assert.IsTrue(upload.IsRevoked);
            Assert.IsTrue(download.IsRevoked);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                gate.CheckUploadAsync(upload, "p", "s", "/local", "/remote", false, 8, default, autoApprove: true));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                gate.CheckDownloadAsync(download, "p", "s", "/remote", "/local", false, 8, default, autoApprove: true));
            await prompt.DidNotReceive().RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await pending.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => manual);
        }
    }

    /// <summary>内存 IAppDataStore(单文档存取即可)。</summary>
    private sealed class MemoryStore : IAppDataStore
    {
        private readonly Dictionary<string, object> _docs = [with(StringComparer.Ordinal)];

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(_docs.TryGetValue($"{collection}|{id}", out object? v) ? (T)v : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(new List<T>());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _docs[$"{collection}|{id}"] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _docs.Remove($"{collection}|{id}");
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedPrompt(PluginPermissionDecision decision) : IPluginPermissionPrompt
    {
        public int Calls { get; private set; }

        public int SessionOpenCalls { get; private set; }

        public Task<PluginPermissionDecision> RequestTerminalWriteAsync(string pluginId, string sessionLabel,
            string inputPreview, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(decision);
        }

        public Task<PluginPermissionDecision> RequestSessionOpenAsync(string pluginId, string target, string reason,
            CancellationToken cancellationToken)
        {
            SessionOpenCalls++;
            return Task.FromResult(decision);
        }
    }

    [TestMethod]
    public async Task UploadNeverInheritsTerminalOrSessionGrant_AndDefaultPromptDenies()
    {
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowAlways);
        var gate = new PluginPermissionGate(new MemoryStore(), prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("p", "target", "input", default));
        Assert.IsTrue(await gate.CheckSessionOpenAsync("p", "target", "reason", default));
        using UploadPermissionLease lease = gate.CreateUploadLease("p");
        Assert.IsFalse(await gate.CheckUploadAsync(lease, "p", "target", "/local", "/remote", false, 8, default));
    }

    [TestMethod]
    public async Task UploadRejectsMissingPromptAndBroadGrantDecisions()
    {
        foreach (PluginPermissionDecision decision in new[] { PluginPermissionDecision.Deny, PluginPermissionDecision.AllowSession, PluginPermissionDecision.AllowAlways })
        {
            IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
            prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(decision);
            var gate = new PluginPermissionGate(null, prompt);
            using UploadPermissionLease lease = gate.CreateUploadLease("p");
            Assert.IsFalse(await gate.CheckUploadAsync(lease, "p", "target", "/local", "/remote", false, 8, default));
        }
        var missing = new PluginPermissionGate(null, null);
        using UploadPermissionLease absentLease = missing.CreateUploadLease("p");
        Assert.IsFalse(await missing.CheckUploadAsync(absentLease, "p", "target", "/local", "/remote", false, 8, default));
    }

    /// <summary>上传确认框拿到源句柄的长度照实显示(不设固定大小上限之后,用户只能从这里看出这次要传多大)。</summary>
    [TestMethod]
    public async Task UploadPromptIsGivenTheSourceSize()
    {
        const long size = 5L * 1024 * 1024 * 1024 + 7;
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync("p", "target", "/local", "/remote", true, size, Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        var gate = new PluginPermissionGate(null, prompt);
        using UploadPermissionLease lease = gate.CreateUploadLease("p");
        Assert.IsTrue(await gate.CheckUploadAsync(lease, "p", "target", "/local", "/remote", true, size, default));
    }

    /// <summary>
    /// 批准生效的回调只在真批准时调、而且在闸返回之前(登记批准的同一把锁里)调:调用方据此把任务阶段与
    /// 「已批准」一起推进。拒绝、范围过宽都不调。
    /// </summary>
    [TestMethod]
    [DataRow(PluginPermissionDecision.AllowOnce, true)]
    [DataRow(PluginPermissionDecision.Deny, false)]
    [DataRow(PluginPermissionDecision.AllowAlways, false)]
    public async Task ApprovalCallbackRunsOnlyWhenApprovedAndBeforeTheGateReturns(PluginPermissionDecision decision, bool expected)
    {
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(decision);
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(decision);
        var gate = new PluginPermissionGate(null, prompt);
        using UploadPermissionLease upload = gate.CreateUploadLease("p");
        using DownloadPermissionLease download = gate.CreateDownloadLease("p");
        int uploadCalls = 0, downloadCalls = 0;

        bool uploadApproved = await gate.CheckUploadAsync(upload, "p", "target", "/local", "/remote", false, 8, default,
            () => uploadCalls++);
        bool downloadApproved = await gate.CheckDownloadAsync(download, "p", "target", "/remote", "/local", false, 3, default,
            () => downloadCalls++);

        Assert.AreEqual(expected, uploadApproved);
        Assert.AreEqual(expected, downloadApproved);
        Assert.AreEqual(expected ? 1 : 0, uploadCalls);
        Assert.AreEqual(expected ? 1 : 0, downloadCalls);
    }

    /// <summary>下载确认框拿到审批之前 stat 到的远端大小照实显示(与上传同一口径)。</summary>
    [TestMethod]
    public async Task DownloadPromptIsGivenTheRemoteSize()
    {
        const long size = 5L * 1024 * 1024 * 1024 + 7;
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync("p", "target", "/remote", "/local", false, size, Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        var gate = new PluginPermissionGate(null, prompt);
        using DownloadPermissionLease lease = gate.CreateDownloadLease("p");
        Assert.IsTrue(await gate.CheckDownloadAsync(lease, "p", "target", "/remote", "/local", false, size, default));
    }

    [TestMethod]
    public async Task PendingUploadCanBeRevokedAndCancellationCallbackMayDisposeLease()
    {
        var gate = new PluginPermissionGate(null, null);
        UploadPermissionLease first = gate.CreateUploadLease("p");
        UploadPermissionLease second = gate.CreateUploadLease("p");
        using CancellationTokenRegistration callback = first.Revocation.Register(first.Dispose);
        Assert.IsTrue(await gate.HasGrantAsync("p"), "尚待审批任务也可在管理页撤销。");
        Task revoked = gate.RevokeAsync("p");
        Assert.IsTrue(first.IsRevoked);
        Assert.IsTrue(second.IsRevoked);
        Assert.IsTrue(second.Revocation.IsCancellationRequested);
        await revoked;
        Assert.IsFalse(await gate.HasGrantAsync("p"));
        second.Dispose();
    }

    [TestMethod]
    public async Task DownloadPermissionIsIndependentRejectsBroadGrantAndRevokesPendingLease()
    {
        foreach (PluginPermissionDecision decision in new[] { PluginPermissionDecision.Deny, PluginPermissionDecision.AllowSession, PluginPermissionDecision.AllowAlways })
        {
            IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
            prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(decision);
            var gate = new PluginPermissionGate(null, prompt);
            using DownloadPermissionLease lease = gate.CreateDownloadLease("p");
            Assert.IsFalse(await gate.CheckDownloadAsync(lease, "p", "real-session", "/source", "destination", false, 3, default));
        }
        var noPrompt = new PluginPermissionGate(null, null);
        using DownloadPermissionLease pending = noPrompt.CreateDownloadLease("p");
        Assert.IsFalse(await noPrompt.CheckDownloadAsync(pending, "p", "session", "/source", "target", false, 3, default));
        Assert.IsTrue(await noPrompt.HasGrantAsync("p"));
        await noPrompt.RevokeAsync("p");
        Assert.IsTrue(pending.IsRevoked);
        Assert.IsTrue(pending.Revocation.IsCancellationRequested);
        Assert.IsFalse(await noPrompt.HasGrantAsync("p"));
    }

    [TestMethod]
    public async Task Deny_DoesNotAllow_AndKeepsAsking()
    {
        var prompt = new ScriptedPrompt(PluginPermissionDecision.Deny);
        var gate = new PluginPermissionGate(new MemoryStore(), prompt);
        Assert.IsFalse(await gate.CheckTerminalWriteAsync("acme.p", "prod", "rm -rf", default));
        Assert.IsFalse(await gate.CheckTerminalWriteAsync("acme.p", "prod", "rm -rf", default));
        Assert.AreEqual(2, prompt.Calls, "拒绝不记忆,每次都问");
    }

    [TestMethod]
    public async Task AllowOnce_AllowsThisTimeOnly()
    {
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowOnce);
        var gate = new PluginPermissionGate(new MemoryStore(), prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(2, prompt.Calls, "仅本次不记忆,每次都问");
    }

    [TestMethod]
    public async Task AllowSession_RemembersInMemory_ButNotPersisted()
    {
        var store = new MemoryStore();
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowSession);
        var gate = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(1, prompt.Calls, "本次运行内只问一次");

        // 新实例(模拟重启)= 会话授权不持久,重新问。
        var afterRestart = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await afterRestart.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(2, prompt.Calls);
    }

    [TestMethod]
    public async Task AllowAlways_Persists_AndSurvivesRestart_UntilRevoked()
    {
        var store = new MemoryStore();
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowAlways);
        var gate = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(1, prompt.Calls);

        // 重启后仍允许,不再问。
        var afterRestart = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await afterRestart.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsTrue(await afterRestart.HasGrantAsync("acme.p"));
        Assert.AreEqual(1, prompt.Calls, "始终允许已持久,不再问");

        // 撤销后重新问。
        await afterRestart.RevokeAsync("acme.p");
        Assert.IsFalse(await afterRestart.HasGrantAsync("acme.p"));
        Assert.IsTrue(await afterRestart.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(2, prompt.Calls);
    }

    [TestMethod]
    public async Task NoPrompt_DeniesInsteadOfSilentlyAllowing()
    {
        var gate = new PluginPermissionGate(new MemoryStore(), prompt: null);
        Assert.IsFalse(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.IsFalse(await gate.CheckSessionOpenAsync("acme.p", "prod (root@10.0.0.1:22)", "机器人要看 nginx 日志", default));
    }

    /// <summary>
    /// 两类能力各记各的账。合成一本的话,“允许它替我敲一行命令”会顺带把
    /// “允许它自己连生产机”也批了 —— 用户在确认框上点的从来不是同一个“是”。
    /// </summary>
    [TestMethod]
    public async Task TerminalWriteGrant_DoesNotCarryOverToOpeningSessions()
    {
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowAlways);
        var gate = new PluginPermissionGate(new MemoryStore(), prompt);

        Assert.IsTrue(await gate.CheckTerminalWriteAsync("acme.p", "prod", "ls", default));
        Assert.AreEqual(0, prompt.SessionOpenCalls);

        Assert.IsTrue(await gate.CheckSessionOpenAsync("acme.p", "prod (root@10.0.0.1:22)", "查磁盘", default));
        Assert.AreEqual(1, prompt.SessionOpenCalls, "开会话是另一件事,必须另问一次");
    }

    [TestMethod]
    public async Task SessionOpen_AllowAlways_PersistsAndIsRevokedTogether()
    {
        var store = new MemoryStore();
        var prompt = new ScriptedPrompt(PluginPermissionDecision.AllowAlways);
        var gate = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await gate.CheckSessionOpenAsync("acme.p", "prod", "查磁盘", default));

        var afterRestart = new PluginPermissionGate(store, prompt);
        Assert.IsTrue(await afterRestart.CheckSessionOpenAsync("acme.p", "prod", "查磁盘", default));
        Assert.AreEqual(1, prompt.SessionOpenCalls, "始终允许已持久,不再问");
        Assert.IsTrue(await afterRestart.HasGrantAsync("acme.p"), "管理页要看得见这条授权");

        // 管理页的“撤销”是一刀切:两类账本一起清,不留一半。
        await afterRestart.RevokeAsync("acme.p");
        Assert.IsFalse(await afterRestart.HasGrantAsync("acme.p"));
        Assert.IsTrue(await afterRestart.CheckSessionOpenAsync("acme.p", "prod", "查磁盘", default));
        Assert.AreEqual(2, prompt.SessionOpenCalls);
    }
}
