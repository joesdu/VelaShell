using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.Infrastructure.Sftp;
using VelaShell.PluginHost;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;
using RemoteUploadOptions = VelaShell.PluginSdk.RemoteFs.RemoteUploadOptions;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
public sealed partial class RemoteTransferTests
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly ISftpService _sftp = Substitute.For<ISftpService>();
    private readonly ISshConnectionService _connections = Substitute.For<ISshConnectionService>();

    public RemoteTransferTests() => _connections.GetSession(_session).Returns(new SshSession
    {
        SessionId = _session,
        Status = SessionStatus.Connected,
        ConnectionInfo = new() { Host = "test", Username = "user", AuthMethod = AuthMethod.Password },
    });

    [TestMethod]
    public async Task ActualManagerContextRequiresUploadGateAndPassesBoundPluginIdentity()
    {
        string file = CreateFile(8);
        string root = Path.Combine(Path.GetTempPath(), "vela-manager-upload-" + Guid.NewGuid().ToString("N"));
        string digest = Convert.ToHexString(SHA256.HashData(new byte[8]));
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = digest }));
        try
        {
            foreach (bool approved in new[] { false, true })
            {
                TaskCompletionSource asking = NewSignal();
                var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
                prompt.RequestFileUploadAsync("test.manager", Arg.Any<string>(), RemoteFsCapability.ResolveUploadSource(file), "/target", false, Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .Returns(async call =>
                    {
                        asking.TrySetResult();
                        return await decision.Task.WaitAsync(call.ArgAt<CancellationToken>(6));
                    });
                await using var manager = new PluginManager(new()
                {
                    PluginRoots = [],
                    DataRootDirectory = root,
                    Connections = _connections,
                    // 与宿主 DI 一致:插件拿到的是路由服务,不是 SFTP 后端本身(#656 F1:路由漏转提交,上传全部卡在提交)。
                    Sftp = Routed(_sftp),
                    UploadPermissions = approved ? new(null, prompt) : null,
                });
                Type runtimeType = typeof(PluginManager).GetNestedType("PluginRuntime", BindingFlags.NonPublic)!;
                object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
                runtimeType.GetProperty("Descriptor")!.SetValue(runtime, new PluginDescriptor { Directory = root });
                PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>("""{"id":"test.manager","version":"1","displayName":"Test","entry":"Test.dll"}""")!;
                using var context = (PluginContext)typeof(PluginManager).GetMethod("CreateContext", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, [manifest, runtime])!;
                var api = (RemoteFsCapability)context.RemoteFs;
                RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "manager" });
                Assert.AreEqual(RemoteTransferPhase.Approval, start.Phase, "回执就在等宿主确认");
                if (approved)
                {
                    // 先观察待审批状态再放行,不让即时同意与启动回执争抢后台调度。
                    await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.AreEqual(RemoteTransferPhase.Approval, (await api.GetTransferAsync(start.TransferId)).Phase);
                    await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
                    decision.SetResult(PluginPermissionDecision.AllowOnce);
                }
                RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
                Assert.AreEqual(approved ? RemoteTransferState.Succeeded : RemoteTransferState.Failed, done.State, done.ErrorMessage);
                Assert.AreEqual(approved ? RemoteTransferPhase.Commit : RemoteTransferPhase.Approval, done.Phase,
                    "批准之后走完传输、校验与提交;被拒的停在确认阶段");
                Assert.IsTrue(done.RemoteStopConfirmed);
                if (!approved) Assert.AreEqual("permission-denied", done.ErrorCode);
                else await prompt.Received(1).RequestFileUploadAsync("test.manager", Arg.Any<string>(), RemoteFsCapability.ResolveUploadSource(file), "/target", false, Arg.Any<long>(), Arg.Any<CancellationToken>());
                await WaitIdle(api);
            }
            await _sftp.Received(1).CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>
    /// 覆盖上传的临时文件一建出来就按被替换文件的权限位(经路由服务问 SFTP 后端),写入期间不比它宽;
    /// 不覆盖的上传不问、按服务端默认建(与新建上传同一口径)。
    /// </summary>
    [TestMethod]
    public async Task OverwriteUpload_CreatesTheTemporaryWithTheReplacedFilesMode_FromTheStart()
    {
        string file = CreateFile(8);
        string digest = Convert.ToHexString(SHA256.HashData(new byte[8]));
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = digest }));
        _sftp.GetUploadCreateModeAsync(_session, "/target", Arg.Any<CancellationToken>()).Returns(0x180u);
        using var api = new RemoteFsCapability(Routed(_sftp), _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            foreach (bool overwrite in new[] { true, false })
            {
                RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "mode-" + overwrite, Overwrite = overwrite });
                RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
                Assert.IsTrue(done.IsSuccess, done.ErrorMessage);
                await WaitIdle(api);
            }

            await _sftp.Received(1).GetUploadCreateModeAsync(_session, "/target", Arg.Any<CancellationToken>());
            await _sftp.Received(1).UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Is<string>(p => p.StartsWith("/.vela-up-")), 8,
                Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, 0x180u, Arg.Any<CancellationToken>());
            await _sftp.Received(1).UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Is<string>(p => p.StartsWith("/.vela-up-")), 8,
                Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, null, Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    /// <summary>读不出被替换文件的模式只是没法提前收窄,不该让上传失败(提交时照旧核对)。</summary>
    [TestMethod]
    public async Task OverwriteUpload_WhenTheModeCannotBeRead_StillUploadsWithTheServerDefault()
    {
        string file = CreateFile(8);
        string digest = Convert.ToHexString(SHA256.HashData(new byte[8]));
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = digest }));
        _sftp.GetUploadCreateModeAsync(_session, "/target", Arg.Any<CancellationToken>())
            .ThrowsAsync(new VelaSftpPermissionDeniedException("Permission denied"));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "mode-unknown", Overwrite = true });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess, done.ErrorMessage);
            await _sftp.Received(1).UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), 8,
                Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, null, Arg.Any<CancellationToken>());
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task MissingTrustedUploadGateFailsClosedWithoutReadingOrUploading()
    {
        string file = CreateFile(8);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer");
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "no-gate" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State);
            Assert.AreEqual("permission-denied", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task TrustedPromptIsBackgroundAndBindsStableSourceAndExactParameters()
    {
        string file = CreateFile(8);
        TaskCompletionSource asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync("test.transfer", Arg.Any<string>(), RemoteFsCapability.ResolveUploadSource(file), "/target", true, 8L, Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "approval", Overwrite = true });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(start.IsTerminal, "Start短回执不等待可信审批。");
            Assert.AreEqual(RemoteFsCapability.ResolveUploadSource(file), start.LocalPath);
            if (OperatingSystem.IsWindows())
                Assert.ThrowsExactly<IOException>(() => { using var write = new FileStream(file, FileMode.Open, FileAccess.Write); });
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
            decision.TrySetResult(PluginPermissionDecision.Deny);
            Assert.AreEqual("permission-denied", (await Terminal(api, start.TransferId)).ErrorCode);
            await WaitIdle(api);
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { decision.TrySetResult(PluginPermissionDecision.Deny); File.Delete(file); }
    }

    /// <summary>
    /// 等宿主确认的上传报 <see cref="RemoteTransferPhase.Approval" />(与下载同一口径),插件据此只撤"还没批准"的那些:
    /// 排在确认闸后的取消之后不再弹框,已经弹出来的那个随取消关掉,两者都以 Cancelled 收尾、什么都没写。
    /// </summary>
    [TestMethod]
    public async Task UploadsAwaitingApproval_ReportTheApprovalPhase_AndCancellingWithdrawsTheirPrompts()
    {
        string file = CreateFile(8);
        // 两个任务的后台流程谁先走到确认闸不由启动顺序决定(各自 Task.Run,满载时后启动的常先到):
        // 记下真正弹出确认框的是哪一个,先撤排在闸后的另一个。原先写死「先弹的是第一个」,
        // 第二个先到时撤掉它就放第一个进闸、又弹一次,Received(1) 偶发失败(ssh-r2 在满载下见过一次)。
        var shown = new TaskCompletionSource<(string Remote, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                CancellationToken token = call.ArgAt<CancellationToken>(6);
                shown.TrySetResult((call.ArgAt<string>(3), token));
                await Task.Delay(Timeout.Infinite, token);
                return PluginPermissionDecision.Deny;
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot first = await api.StartUploadAsync(_session.ToString(), file, "/a", new() { OperationId = "first" });
            RemoteTransferSnapshot second = await api.StartUploadAsync(_session.ToString(), file, "/b", new() { OperationId = "second" });
            Assert.AreEqual(RemoteTransferPhase.Approval, first.Phase);
            Assert.AreEqual(RemoteTransferPhase.Approval, second.Phase);
            (string shownRemote, CancellationToken shownToken) = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (RemoteTransferSnapshot asked, RemoteTransferSnapshot waiting) = shownRemote == first.RemotePath ? (first, second) : (second, first);
            Assert.AreEqual(RemoteTransferPhase.Approval, (await api.GetTransferAsync(waiting.TransferId)).Phase, "排在确认闸后的同样算等确认");

            await api.CancelTransferAsync(waiting.TransferId, waiting.Generation);
            RemoteTransferSnapshot waitingDone = await Terminal(api, waiting.TransferId);
            Assert.AreEqual(RemoteTransferState.Cancelled, waitingDone.State, waitingDone.ErrorMessage);
            await api.CancelTransferAsync(asked.TransferId, asked.Generation);
            Assert.IsTrue(shownToken.IsCancellationRequested, "已经弹出来的确认框随取消关掉");
            RemoteTransferSnapshot askedDone = await Terminal(api, asked.TransferId);
            Assert.AreEqual(RemoteTransferState.Cancelled, askedDone.State, askedDone.ErrorMessage);
            Assert.IsTrue(askedDone.DestinationCommitConfirmedAbsent && waitingDone.DestinationCommitConfirmedAbsent);
            await WaitIdle(api);
            await prompt.Received(1).RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task UnixSourceModifiedWhileApprovalPendingFailsBeforeRemoteWrite()
    {
        string file = CreateFile(8);
        TaskCompletionSource asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "changed-before-approval" });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var write = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) write.SetLength(16);
            decision.SetResult(PluginPermissionDecision.AllowOnce);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State);
            Assert.AreEqual("source-changed", done.ErrorCode);
            await WaitIdle(api);
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { decision.TrySetResult(PluginPermissionDecision.Deny); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task ParentAliasesBindCanonicalSourceAndReplayDoesNotReopenRetargetedAlias()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-transfer-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        string first = Path.Combine(root, "first", "source");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(root, "second", "source"), [9, 9, 9]);
        string alias = Path.Combine(root, "alias");
        string outer = Path.Combine(root, "outer");
        Directory.CreateSymbolicLink(alias, "first");
        Directory.CreateSymbolicLink(outer, alias);
        string requested = Path.Combine(outer, "source");
        string canonical = RemoteFsCapability.ResolveUploadSource(first);
        TaskCompletionSource asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync("test.transfer", Arg.Any<string>(), canonical, "/target", false, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        string digest = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var bytes = new MemoryStream();
                await call.ArgAt<Stream>(1).CopyToAsync(bytes);
                Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, bytes.ToArray());
            });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = digest }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        var options = new RemoteUploadOptions { OperationId = "alias" };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), requested, "/target", options);
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(canonical, start.LocalPath);
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, "second");
            Assert.AreEqual(start.TransferId, (await api.StartUploadAsync(_session.ToString(), requested, "/target", options)).TransferId);
            decision.SetResult(PluginPermissionDecision.AllowOnce);
            Assert.IsTrue((await Terminal(api, start.TransferId)).IsSuccess);
            await WaitIdle(api);
            File.Delete(Path.Combine(root, "second", "source"));
            Assert.AreEqual(start.TransferId, (await api.StartUploadAsync(_session.ToString(), requested, "/target", options)).TransferId);
            await prompt.Received(1).RequestFileUploadAsync("test.transfer", Arg.Any<string>(), canonical, "/target", false, Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            decision.TrySetResult(PluginPermissionDecision.Deny);
            await WaitIdle(api);
            Directory.Delete(outer);
            Directory.Delete(alias);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void ParentLinkDotDotIsAppliedAfterIntermediateLinkExpansion()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-transfer-dotdot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "real", "sub"));
        Directory.CreateDirectory(Path.Combine(root, "real", "destination"));
        Directory.CreateDirectory(Path.Combine(root, "destination"));
        string jump = Path.Combine(root, "jump");
        string alias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(jump, "real/sub");
        Directory.CreateSymbolicLink(alias, "jump/../destination");
        try
        {
            Assert.AreEqual(RemoteFsCapability.ResolveUploadSource(Path.Combine(root, "real", "destination", "source")),
                RemoteFsCapability.ResolveUploadSource(Path.Combine(alias, "source")));
        }
        finally { Directory.Delete(alias); Directory.Delete(jump); Directory.Delete(root, true); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task LeafLinksAndUnresolvableParentsRemainRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-transfer-reject-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source");
        await File.WriteAllBytesAsync(source, [1]);
        string leaf = Path.Combine(root, "leaf");
        string missing = Path.Combine(root, "missing");
        string loop = Path.Combine(root, "loop");
        File.CreateSymbolicLink(leaf, source);
        Directory.CreateSymbolicLink(missing, "absent");
        Directory.CreateSymbolicLink(loop, "loop");
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => api.StartUploadAsync(_session.ToString(), leaf, "/target", new() { OperationId = "leaf" }));
            PluginTransferRejectedException missingSource = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(
                () => api.StartUploadAsync(_session.ToString(), Path.Combine(missing, "source"), "/target", new() { OperationId = "missing" }));
            Assert.AreEqual(RemoteTransferRejectReason.SourceNotFound, missingSource.Reason);
            Assert.IsInstanceOfType<DirectoryNotFoundException>(missingSource.InnerException);
            await Assert.ThrowsAsync<IOException>(() => api.StartUploadAsync(_session.ToString(), Path.Combine(loop, "source"), "/target", new() { OperationId = "loop" }));
            Assert.AreEqual(0, api.ActiveTransfers);
        }
        finally { File.Delete(leaf); File.Delete(missing); File.Delete(loop); Directory.Delete(root, true); }
    }

    /// <summary>
    /// Windows 上真正改路径解析的重解析点(名字代理:符号链接、目录联接、挂载点)照旧当链接拒绝:
    /// 上传的源本身是联接拒绝;下载目录链上有联接拒绝,联接指向的目录里不落任何东西。
    /// 符号链接要开发者模式或管理员才建得出来,建不出来就只测联接。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsJunctionsAndSymlinksStayRejectedAsLinks()
    {
        string root = NewDirectory("vela-transfer-junction-");
        string real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        string source = Path.Combine(real, "source");
        await File.WriteAllBytesAsync(source, [1]);
        string junction = Path.Combine(root, "junction");
        using (Process mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, real }, UseShellExecute = false, RedirectStandardOutput = true })!)
            await mklink.WaitForExitAsync();
        Assert.IsNotNull(new DirectoryInfo(junction).LinkTarget, "联接没建出来");
        string symlink = Path.Combine(root, "symlink");
        bool canSymlink = true;
        try { File.CreateSymbolicLink(symlink, source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { canSymlink = false; }
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(1));
        // 上传的源在启动时就被拒(审批之前),只有下载需要放行。
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, AllowDownloads()));
        try
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => api.StartUploadAsync(_session.ToString(), junction, "/target", new() { OperationId = "junction-leaf" }));
            if (canSymlink)
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() => api.StartUploadAsync(_session.ToString(), symlink, "/target", new() { OperationId = "symlink-leaf" }));
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(junction, "target"), new() { OperationId = "junction-parent" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.AreEqual("download-failed", done.ErrorCode);
            await WaitIdle(api);
            Assert.AreSequenceEqual([source], Directory.GetFiles(real));
            await _sftp.DidNotReceive().DownloadStreamAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await WaitIdle(api);
            if (canSymlink) File.Delete(symlink);
            Directory.Delete(junction);
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// OneDrive 的云文件占位符是重解析点(IO_REPARSE_TAG_CLOUD_*),但不是名字代理 —— 不改路径解析,不是链接。
    /// 看得到占位符的进程里,同步盘里的目录照样能作上传源的父目录、能作下载目录。
    /// 只读元数据:不打开、不改动同步盘里的任何东西(下载在审批处被拒,什么都不落盘)。本机没有 OneDrive 时不确定。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task OneDrivePlaceholderDirectoriesAreNotTreatedAsLinks()
    {
        string? root = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) Assert.Inconclusive("本机没有 OneDrive。");
        using var exposed = new ExposedPlaceholders();
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0) Assert.Inconclusive("OneDrive 目录不是云文件占位符。");
        string leaf = Path.Combine(root, "vela-onedrive-probe-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(leaf, RemoteFsCapability.ResolveUploadSource(leaf), ignoreCase: true);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", leaf, false, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.Deny);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", leaf, new() { OperationId = "onedrive" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual("permission-denied", done.ErrorCode, done.ErrorMessage);
            await prompt.Received(1).RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", leaf, false, Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); }
        Assert.IsFalse(File.Exists(leaf));
    }

    [TestMethod]
    public async Task RevokingPendingPromptNeverStartsAnyRemoteOperation()
    {
        string file = CreateFile(8);
        TaskCompletionSource asking = NewSignal();
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async call => { asking.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(6)); return PluginPermissionDecision.AllowOnce; });
        var gate = new PluginPermissionGate(null, prompt);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "pending-revoke" });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gate.RevokeAsync("test.transfer");
            await WaitIdle(api);
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.GetTransferAsync(start.TransferId));
            Assert.AreEqual("", (await api.CancelTransferAsync(start.TransferId, 1)).LocalPath);
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task RevokeStopsActiveUploadHidesQueriesButAllowsRedactedCancelCleanup()
    {
        string file = CreateFile(8);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource release = NewSignal();
        PluginPermissionGate gate = AllowUploads();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: gate);
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "revoke" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await gate.HasGrantAsync("test.transfer"));
            Task revocation = gate.RevokeAsync("test.transfer");
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.GetTransferAsync(start.TransferId));
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.WaitTransferAsync(start.TransferId, 0, TimeSpan.Zero));
            await Assert.ThrowsExactlyAsync<PluginPermissionDeniedException>(() => api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "revoke" }));
            RemoteTransferSnapshot cancel = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.AreEqual("", cancel.LocalPath);
            Assert.AreEqual("", cancel.RemotePath);
            Assert.IsFalse(cancel.RemoteStopConfirmed);
            await revocation;
            Assert.IsFalse(await gate.HasGrantAsync("test.transfer"));
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task LargeUpload_RequestCancellationAndWaitTimeoutDoNotStopTask_CommitIsAuthoritative()
    {
        string file = CreateFile(33L * 1024 * 1024 + 17);
        TaskCompletionSource writing = NewSignal();
        TaskCompletionSource close = NewSignal();
        TaskCompletionSource committing = NewSignal();
        TaskCompletionSource commit = NewSignal();
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(),
            Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            Stream source = call.ArgAt<Stream>(1);
            Assert.IsNotInstanceOfType<MemoryStream>(source);
            hash = Convert.ToHexString(await SHA256.HashDataAsync(source, call.ArgAt<CancellationToken>(8)));
            call.ArgAt<IProgress<TransferProgress>>(5).Report(new() { BytesTransferred = source.Length, TotalBytes = source.Length, FileName = "file", SpeedBytesPerSecond = 0, Percentage = 100, EstimatedTimeRemaining = TimeSpan.Zero });
            writing.TrySetResult();
            await close.Task;
        });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async _ => { committing.TrySetResult(); await commit.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            using var request = new CancellationTokenSource();
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "large" }, request.Token);
            request.Cancel();
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RemoteTransferSnapshot running = await api.GetTransferAsync(start.TransferId);
            Assert.IsFalse(running.IsTerminal);
            RemoteTransferSnapshot waited = await api.WaitTransferAsync(start.TransferId, running.Sequence, TimeSpan.FromMilliseconds(10));
            Assert.IsFalse(waited.IsTerminal);
            Assert.IsFalse(waited.CancellationRequested);
            close.TrySetResult();
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RemoteTransferSnapshot tail = await api.GetTransferAsync(start.TransferId);
            Assert.AreEqual(tail.TotalBytes, tail.TransferredBytes);
            Assert.IsFalse(tail.IsSuccess);
            Assert.IsNull(tail.EstimatedTimeRemaining);
            commit.TrySetResult();
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess);
            Assert.AreEqual(33L * 1024 * 1024 + 17, done.TotalBytes);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsFalse(done.CanResume);
            await WaitIdle(api);
        }
        finally
        {
            close.TrySetResult();
            commit.TrySetResult();
            api.Dispose();
            try { await WaitIdle(api); }
            finally { File.Delete(file); }
        }
    }

    [TestMethod]
    public void ProgressUses64BitBytesAndDoesNotTurnFullByteCountIntoSuccess()
    {
        long total = (long)int.MaxValue + 1024 * 1024;
        RemoteTransferSnapshot snapshot = new() { TransferId = "t", OperationId = "o", SessionId = "s", LocalPath = "l", RemotePath = "/r", TotalBytes = total, State = RemoteTransferState.Running };
        RemoteTransferSnapshot updated = RemoteFsCapability.ApplyTransferProgress(snapshot, new()
        {
            FileName = "large",
            BytesTransferred = total,
            TotalBytes = total,
            Percentage = 100,
            SpeedBytesPerSecond = 1024,
            EstimatedTimeRemaining = TimeSpan.Zero,
        });
        Assert.AreEqual(total, updated.TransferredBytes);
        Assert.IsFalse(updated.IsSuccess);
    }

    [TestMethod]
    public async Task EmptyUploadSucceedsWithZeroBytesOnlyAfterCommit()
    {
        string file = CreateFile(0);
        string emptyHash = Convert.ToHexString(SHA256.HashData([]));
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = emptyHash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/empty", new() { OperationId = "empty" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess);
            Assert.AreEqual(0L, done.TotalBytes);
            Assert.AreEqual(0L, done.TransferredBytes);
            await _sftp.Received(1).CommitUploadAsync(_session, Arg.Any<string>(), "/empty", false, Arg.Any<CancellationToken>());
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task HashMismatchFailsWithoutCommittingDestination()
    {
        string file = CreateFile(64);
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = new string('0', 64) }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "hash" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("upload-failed", done.ErrorCode);
            Assert.AreEqual("Uploaded content verification failed.", done.ErrorMessage);
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, Arg.Is<string>(path => path.StartsWith("/.vela-up-", StringComparison.Ordinal)),
                Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    /// <summary>
    /// 临时文件是目标同目录下的一个短名字,不接在目标名后面:接在后面要多出 61 字节,
    /// 目标名一过约 194 字节,临时文件名就超过 NAME_MAX(255 字节),上传还没开始就是 ENAMETOOLONG。
    /// 每次上传的名字都不一样(随机 64 位)。
    /// </summary>
    [TestMethod]
    public async Task UploadTemporaryIsAShortRandomSiblingThatFitsBesideLongNames()
    {
        string file = CreateFile(8);
        string leaf = new('长', 83);   // UTF-8 下 249 字节,接近 NAME_MAX
        var temporaries = new List<string>();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                lock (temporaries) temporaries.Add(call.ArgAt<string>(2));
                return Task.FromException(new IOException("stop after naming"));
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            foreach (string destination in new[] { "/data/sub/" + leaf, "/data/sub/" + leaf + "-2", "/top" })
            {
                RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, destination, new() { OperationId = "name-" + destination });
                Assert.AreEqual("upload-failed", (await Terminal(api, start.TransferId)).ErrorCode);
                await WaitIdle(api);
            }
            Assert.HasCount(3, temporaries);
            foreach ((string temporary, string directory) in temporaries.Zip(new[] { "/data/sub/", "/data/sub/", "/" }))
            {
                Assert.StartsWith(directory + ".vela-up-", temporary);
                string name = temporary[directory.Length..];
                Assert.AreEqual(".vela-up-".Length + 16, name.Length, temporary);
                Assert.IsTrue(name[".vela-up-".Length..].All(Uri.IsHexDigit), temporary);
                Assert.IsLessThanOrEqualTo(255, Encoding.UTF8.GetByteCount(name));
            }
            Assert.AreNotEqual(temporaries[0], temporaries[1]);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task DiskSnapshotBranchUsesSeparateStreamAndCommitsVerifiedBytes()
    {
        // 源放在单独的目录里:快照落在源文件旁边(同一个卷),而不是系统临时目录,这里才分得出来。
        string directory = NewDirectory("vela-snapshot-sibling-");
        string file = Path.Combine(directory, "source");
        using (var create = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) create.SetLength(1024 * 1024 + 7);
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var source = (FileStream)call.ArgAt<Stream>(1);
            Assert.AreNotEqual(file, source.Name);
            // 快照是源文件旁边(同一个卷)的隐藏文件,不是系统临时目录里的一份拷贝。
            Assert.AreEqual(Path.GetDirectoryName(RemoteFsCapability.ResolveUploadSource(file)), Path.GetDirectoryName(source.Name));
            Assert.StartsWith(".vela-snapshot-", Path.GetFileName(source.Name));
            hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
        });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { UseDiskSnapshot = true };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "snapshot" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess, $"{done.State}: {done.ErrorMessage}");
            await WaitIdle(api);
            Assert.AreSequenceEqual([file], Directory.GetFileSystemEntries(directory), "快照用完不留在源目录里");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Unix 的磁盘快照是源文件的一份明文副本。关闭即删只在正常释放时生效,宿主崩在上传中途就会留下它:
    /// 建在源文件旁边之后立刻删掉名字,数据靠打开着的描述符活着,进程一退就由内核回收;上传期间目录里看不到它。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task UnixDiskSnapshotIsUnlinkedWhileTheUploadStillReadsIt()
    {
        string file = CreateFile(256 * 1024 + 3);
        string? hash = null, snapshot = null;
        bool fileVisible = true, directoryVisible = true;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var source = (FileStream)call.ArgAt<Stream>(1);
            snapshot = source.Name;
            fileVisible = File.Exists(source.Name);
            directoryVisible = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(source.Name)!, ".vela-snapshot-*")
                .Any(path => path == source.Name);
            hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
        });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { UseDiskSnapshot = true };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "unlinked-snapshot" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.IsTrue(done.IsSuccess, $"{done.State}: {done.ErrorMessage}");
            Assert.IsNotNull(snapshot);
            Assert.AreNotEqual(file, snapshot);
            Assert.IsFalse(fileVisible, "快照在上传期间仍有路径可达");
            Assert.IsFalse(directoryVisible, "快照在上传期间仍列在目录里");
            Assert.AreEqual(Path.GetDirectoryName(RemoteFsCapability.ResolveUploadSource(file)), Path.GetDirectoryName(snapshot), "快照建在源文件所在的目录(同一个卷)");
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))), hash, "删掉路径之后仍要读到完整内容");
            await WaitIdle(api);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task IdempotencyConflictsAndPluginOwnershipAreEnforced()
    {
        string file = CreateFile(8);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; throw new IOException(); });
        try
        {
            using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
            using var other = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
            var options = new RemoteUploadOptions { OperationId = "same" };
            RemoteTransferSnapshot first = await api.StartUploadAsync(_session.ToString(), file, "/one", options);
            RemoteTransferSnapshot second = await api.StartUploadAsync(_session.ToString(), file, "/one", options);
            Assert.AreEqual(first.TransferId, second.TransferId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), file, "/two", options));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => other.GetTransferAsync(first.TransferId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.CancelTransferAsync(first.TransferId, 2));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _connections.GetSession(_session).Returns((SshSession?)null);
            Assert.AreEqual(first.TransferId, (await api.StartUploadAsync(_session.ToString(), file, "/one", options)).TransferId,
                "断线后仍能用相同幂等标识重取原回执。");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), file, "/changed", options));
            release.TrySetResult();
            RemoteTransferSnapshot failed = await Terminal(api, first.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, failed.State, failed.ErrorMessage);
            Assert.IsTrue(failed.RemoteStopConfirmed);
            Assert.IsTrue(failed.DestinationCommitConfirmedAbsent);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => api.ResumeTransferAsync(first.TransferId,
                new() { OperationId = "resume", ExpectedGeneration = 1, TargetSessionId = _session.ToString() }));
            await WaitIdle(api);
        }
        finally { release.TrySetResult(); File.Delete(file); }
    }

    [TestMethod]
    public async Task QueueQuotaRejectsNewWorkButKeepsExistingReceipt()
    {
        string file = CreateFile(8);
        TaskCompletionSource release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => await release.Task);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        var starts = new List<RemoteTransferSnapshot>();
        try
        {
            for (int i = 0; i < 32; i++) starts.Add(await api.StartUploadAsync(_session.ToString(), file, "/target-" + i, new() { OperationId = "quota-" + i }));
            PluginTransferRejectedException quota = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(() => api.StartUploadAsync(_session.ToString(), file, "/extra", new() { OperationId = "extra" }));
            Assert.AreEqual(RemoteTransferRejectReason.QuotaExceeded, quota.Reason);
            Assert.AreEqual(starts[0].TransferId, (await api.StartUploadAsync(_session.ToString(), file, "/target-0", new() { OperationId = "quota-0" })).TransferId);
        }
        finally
        {
            api.Dispose(); release.TrySetResult(); await WaitIdle(api); File.Delete(file);
        }
    }

    /// <summary>
    /// 登记之前就确定做不了的启动(源文件不在、所在目录不在)抛 SDK 2.1.3 的 <see cref="PluginTransferRejectedException" />:
    /// 它不是 IOException,插件不会按「回执丢了」去重取;什么都没登记 —— 不弹确认框、不碰远端,
    /// 文件补上之后用同一个 OperationId 再启动是一次新的登记,不撞「参数不符」。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingSourceIsRejectedBeforeRegistering_AndTheOperationIdStaysFree(bool missingDirectory)
    {
        string directory = NewDirectory("vela-missing-");
        string file = missingDirectory ? Path.Combine(directory, "gone", "source") : Path.Combine(directory, "source");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.Deny);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            PluginTransferRejectedException rejected = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(
                () => api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "retry-later" }));
            Assert.AreEqual(RemoteTransferRejectReason.SourceNotFound, rejected.Reason);
            Assert.DoesNotContain(directory, rejected.Message, "说明会转给模型,不带路径");
            Assert.AreEqual(0, api.ActiveTransfers);
            await prompt.DidNotReceive().RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, new byte[8]);
            RemoteTransferSnapshot started = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "retry-later" });
            RemoteTransferSnapshot done = await Terminal(api, started.TransferId);
            Assert.AreEqual("permission-denied", done.ErrorCode, "这次登记了、走到了确认框(被拒),证明前一次没占住回执");
            await prompt.Received(1).RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>别的程序以拒读共享打开着源文件(Windows 的共享冲突):登记之前拒,原因是 SourceInUse,同样什么都没开始。</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task SourceLockedByAnotherProgramIsRejectedAsInUse()
    {
        string file = CreateFile(8);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                PluginTransferRejectedException rejected = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(
                    () => api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "locked" }));
                Assert.AreEqual(RemoteTransferRejectReason.SourceInUse, rejected.Reason);
            }
            Assert.AreEqual(0, api.ActiveTransfers);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>隔离插件经 RPC 收到的是同一个类型与原因(错误码 <c>transfer-rejected:&lt;原因&gt;</c>),不是一个泛泛的远端错误。</summary>
    [TestMethod]
    public async Task RejectionBeforeRegisteringCrossesThePipeWithItsReason()
    {
        await using Pair pair = await Pair.Open();
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsTransferOperations.Returns(true);
        fake.StartUploadAsync("s", "local", "/remote", Arg.Any<RemoteUploadOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RemoteTransferSnapshot>(new PluginTransferRejectedException(RemoteTransferRejectReason.SourceInUse, "locked")));
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        var proxy = new RpcRemoteFs(pair._plugin, hello!.SupportsTransferOperations);

        PluginTransferRejectedException rejected = await Assert.ThrowsExactlyAsync<PluginTransferRejectedException>(
            () => proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "operation" }));
        Assert.AreEqual(RemoteTransferRejectReason.SourceInUse, rejected.Reason);
    }

    /// <summary>把四个传输槽都占住(四个上传卡在远端写入里);返回放行它们的开关与已开始写入的计数。</summary>
    private async Task<(TaskCompletionSource Release, Func<int> Writes, string File)> OccupyAllSlots(RemoteFsCapability api)
    {
        string file = CreateFile(8);
        TaskCompletionSource release = NewSignal();
        int writes = 0;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                Interlocked.Increment(ref writes);
                await release.Task;
            });
        for (int i = 0; i < 4; i++)
            await api.StartUploadAsync(_session.ToString(), file, "/busy-" + i, new() { OperationId = "busy-" + i });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref writes) < 4) await Task.Delay(10, timeout.Token);
        return (release, () => Volatile.Read(ref writes), file);
    }

    /// <summary>等到任务批准之后在排队等传输槽(State 走到 Queued 且已不在 Starting)。</summary>
    private static async Task<RemoteTransferSnapshot> WaitQueuedAfterApproval(RemoteFsCapability api, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RemoteTransferSnapshot snapshot = await api.GetTransferAsync(id, timeout.Token);
        while (snapshot.State != RemoteTransferState.Queued || snapshot.Sequence < 3)
        {
            Assert.IsFalse(snapshot.IsTerminal, snapshot.ErrorMessage);
            snapshot = await api.WaitTransferAsync(id, snapshot.Sequence, TimeSpan.FromSeconds(1), timeout.Token);
        }
        return snapshot;
    }

    private static IPluginPermissionPrompt AllowEverything()
    {
        IPluginPermissionPrompt prompt = AllowDownloads();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.AllowOnce);
        return prompt;
    }

    /// <summary>
    /// 已批准、在等传输槽的任务报 Transfer 阶段,不再是 Approval。AI 插件停止时只撤 Approval 阶段的(那些还在等确认),
    /// 原先下载要等拿到传输槽才改阶段,批准之后排队的下载被当成还在等确认,一并撤掉了(sdk-r2 核查)。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ApprovedTransferWaitingForASlotReportsTheTransferPhase(bool download)
    {
        string directory = NewDirectory("vela-queued-");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, AllowEverything()));
        (TaskCompletionSource release, Func<int> writes, string file) = await OccupyAllSlots(api);
        try
        {
            RemoteTransferSnapshot start = download
                ? await api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(directory, "target"), new() { OperationId = "queued" })
                : await api.StartUploadAsync(_session.ToString(), file, "/queued", new() { OperationId = "queued" });
            RemoteTransferSnapshot queued = await WaitQueuedAfterApproval(api, start.TransferId);

            Assert.AreEqual(RemoteTransferPhase.Transfer, queued.Phase, "已批准、排队等槽的任务不能再报等确认");
            Assert.IsFalse(queued.CancellationRequested);
            Assert.AreEqual(4, writes(), "第五个还没拿到槽");
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 批准之后、拿到传输槽之前取消:以 Cancelled 收尾,一个字节都不传(不会「撤了还在传」);
    /// 取消意图留在快照上,不被随后的阶段推进改回 Queued。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellingAnApprovedQueuedTransferNeverStartsIt(bool download)
    {
        string directory = NewDirectory("vela-queued-");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, AllowEverything()));
        (TaskCompletionSource release, Func<int> writes, string file) = await OccupyAllSlots(api);
        try
        {
            RemoteTransferSnapshot start = download
                ? await api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(directory, "target"), new() { OperationId = "queued" })
                : await api.StartUploadAsync(_session.ToString(), file, "/queued", new() { OperationId = "queued" });
            RemoteTransferSnapshot queued = await WaitQueuedAfterApproval(api, start.TransferId);
            RemoteTransferSnapshot cancelling = await api.CancelTransferAsync(start.TransferId, queued.Generation);
            Assert.IsTrue(cancelling.CancellationRequested);

            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            release.TrySetResult();
            await WaitIdle(api);
            Assert.AreEqual(4, writes(), "取消了的那一个从没开始写");
            await _sftp.DidNotReceive().DownloadStreamAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 批准与取消几乎同时到(用户点「允许」的同一刻插件发来取消):反复撞二十次,每次的结局都只有一种 ——
    /// 取消先登记就 Cancelled、什么都没传;没有哪一次是快照上「已批准却还报等确认」,或者撤了之后照样拿到槽去传。
    /// </summary>
    [TestMethod]
    public async Task ApprovalRacingCancellationNeverTransfersACancelledTask()
    {
        // 传输槽按能力实例计:占槽的四个与参赛的必须是同一个实例。占槽的直接放行,参赛的确认由用例掐着点放。
        var decisions = new System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource<PluginPermissionDecision>>();
        IPluginPermissionPrompt racing = Substitute.For<IPluginPermissionPrompt>();
        racing.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<string>(3).StartsWith("/busy-", StringComparison.Ordinal))
                    return Task.FromResult(PluginPermissionDecision.AllowOnce);
                var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                decisions.Enqueue(decision);
                return decision.Task;
            });
        using var raced = new RemoteFsCapability(_sftp, _connections, pluginId: "test.raced", uploadPermissions: new(null, racing));
        (TaskCompletionSource release, Func<int> writes, string file) = await OccupyAllSlots(raced);
        try
        {
            for (int i = 0; i < 20; i++)
            {
                RemoteTransferSnapshot start = await raced.StartUploadAsync(_session.ToString(), file, "/race-" + i, new() { OperationId = "race-" + i });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                TaskCompletionSource<PluginPermissionDecision>? decision;
                while (!decisions.TryDequeue(out decision)) await Task.Delay(1, timeout.Token);
                Task approve = Task.Run(() => decision!.TrySetResult(PluginPermissionDecision.AllowOnce));
                Task<RemoteTransferSnapshot> cancel = Task.Run(() => raced.CancelTransferAsync(start.TransferId, start.Generation));
                await Task.WhenAll(approve, cancel);
                RemoteTransferSnapshot done = await Terminal(raced, start.TransferId);
                Assert.AreEqual(RemoteTransferState.Cancelled, done.State, $"第 {i} 次:{done.ErrorMessage}");
                Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            }
            Assert.AreEqual(4, writes(), "被取消的那些一个都没开始写");
        }
        finally { release.TrySetResult(); await WaitIdle(raced); File.Delete(file); }
    }

    /// <summary>
    /// 同一 OperationId 的两次启动并发:一次查过回执、还在锁外打开源时,另一次先登记了。后到的那次沿用
    /// 先登记的回执、作废自己打开的句柄 —— 不因重复的 OperationId 抛错,也不登记第二个任务。
    /// </summary>
    [TestMethod]
    public async Task ConcurrentStartWithSameOperationIdReusesTheReceiptRegisteredMeanwhile()
    {
        string file = CreateFile(8);
        SshSession connected = _connections.GetSession(_session)!;
        TaskCompletionSource asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        var options = new RemoteUploadOptions { OperationId = "concurrent" };
        RemoteTransferSnapshot? meanwhile = null;
        int lookups = 0;
        // 查会话是启动查过回执之后、去锁外打开源之前的最后一步。账本锁可重入:在这里同步跑完另一次同参数的启动,
        // 等价于它恰好在本次查过回执、还没登记的空当里先登记了。
        _connections.GetSession(_session).Returns(_ =>
        {
            if (Interlocked.Increment(ref lookups) == 1)
                meanwhile = api.StartUploadAsync(_session.ToString(), file, "/target", options).Result;
            return connected;
        });
        try
        {
            RemoteTransferSnapshot later = await api.StartUploadAsync(_session.ToString(), file, "/target", options);
            Assert.IsNotNull(meanwhile);
            Assert.AreEqual(meanwhile.TransferId, later.TransferId);
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, api.ActiveTransfers);
            await prompt.Received(1).RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            decision.SetResult(PluginPermissionDecision.Deny);
            Assert.AreEqual("permission-denied", (await Terminal(api, later.TransferId)).ErrorCode);
            await WaitIdle(api);
            // Windows 上源按拒写共享打开:作废的那个句柄要是没关,任务结束之后照样写不进去
            if (OperatingSystem.IsWindows()) { using var write = new FileStream(file, FileMode.Open, FileAccess.Write); }
        }
        finally { decision.TrySetResult(PluginPermissionDecision.Deny); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 会话在启动查过它之后、登记任务之前断开(源在锁外打开的那段时间):断线回调只停已登记的任务,
    /// 这一次不能照样登记、拿着已断开的会话去要审批。登记前再查一次会话,启动直接报会话不在,什么都不留下。
    /// </summary>
    [TestMethod]
    public async Task SessionLostWhileOpeningSourceRejectsTheStartBeforeRegistering()
    {
        string file = CreateFile(8);
        SshSession connected = _connections.GetSession(_session)!;
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.Deny);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        var options = new RemoteUploadOptions { OperationId = "lost-session" };
        try
        {
            // 启动时那一次查询会话还连着;之后的查询都已断开
            _connections.GetSession(_session).Returns(connected, (SshSession?)null);
            await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(() => api.StartUploadAsync(_session.ToString(), file, "/target", options));
            Assert.AreEqual(0, api.ActiveTransfers);
            await prompt.DidNotReceive().RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            // 没留下回执:会话恢复后同一 OperationId 照常启动一个新任务,而不是重取一张注定失败的旧回执
            _connections.GetSession(_session).Returns(connected);
            RemoteTransferSnapshot retried = await api.StartUploadAsync(_session.ToString(), file, "/target", options);
            Assert.AreEqual("permission-denied", (await Terminal(api, retried.TransferId)).ErrorCode);
            await prompt.Received(1).RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task SessionDisconnectKeepsTaskQueryableAndDoesNotPretendRemoteStopped()
    {
        string file = CreateFile(8);
        TaskCompletionSource release = NewSignal();
        TaskCompletionSource started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "disconnect" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SshSession disconnected = _connections.GetSession(_session)!;
            _connections.GetSession(_session).Returns((SshSession?)null);
            _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(disconnected);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 会话 id 的写法不止一种(大写、不带连字符的 N 格式都解析得了):断线回调按 Guid 认任务。
    /// 按字符串比对的话,换个写法启动的上传与下载永远收不到断线通知,只能等到期。
    /// </summary>
    [TestMethod]
    [DataRow("N", false)]
    [DataRow("upper", false)]
    [DataRow("N", true)]
    public async Task DisconnectStopsTransfersStartedWithAnyGuidSpelling(string spelling, bool download)
    {
        string file = CreateFile(8);
        string directory = NewDirectory("vela-transfer-spelling-");
        TaskCompletionSource started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call => StopCooperatively(started, call.ArgAt<CancellationToken>(8)));
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call => StopCooperatively(started, call.ArgAt<CancellationToken>(4)));
        using RemoteFsCapability api = download ? DownloadApi() : new(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        string session = spelling == "N" ? _session.ToString("N") : _session.ToString().ToUpperInvariant();
        try
        {
            RemoteTransferSnapshot start = download
                ? await api.StartDownloadAsync(session, "/source", Path.Combine(directory, "target"), new() { OperationId = "spelling" })
                : await api.StartUploadAsync(session, file, "/target", new() { OperationId = "spelling" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(_connections.GetSession(_session)!);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.AreEqual("session-disconnected", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
        }
        finally { api.Dispose(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CancellationIsNotRemoteStopConfirmation_AndOldGenerationCannotCancel()
    {
        string file = CreateFile(64);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "cancel" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RemoteTransferSnapshot cancelling = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.IsTrue(cancelling.CancellationRequested);
            Assert.IsFalse(cancelling.RemoteStopConfirmed);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsFalse(done.CanResume);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.CancelTransferAsync(start.TransferId, 2));
            Assert.IsTrue((await api.CancelTransferAsync(start.TransferId, 1)).CancellationRequested);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task CloseFailureCannotSucceedOrCommit()
    {
        string file = CreateFile(64);
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                return Task.FromException(new IOException("close"));
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "close" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            // 写入任务已经以失败结束 —— 后端已经关了句柄,这就是确认停写:Failed,不是 Unknown。
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("upload-failed", done.ErrorCode);
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task RemoteWriteFailureKeepsSanitizedCauseLogsItAndCleansTemporary()
    {
        string file = CreateFile(64);
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                // 服务端原话不可信:里面的终端转义序列不能原样到达插件与模型。
                return Task.FromException(new VelaSftpPermissionDeniedException("Permission denied: quota-7f3a \u001b]52;c;cGF5bG9hZA==\u0007"));
            });
        using var trace = new TraceCapture();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "denied-write" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("upload-failed", done.ErrorCode);
            Assert.Contains("quota-7f3a", done.ErrorMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", done.ErrorMessage!, StringComparison.Ordinal);
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            Assert.Contains(start.TransferId, trace.Text, StringComparison.Ordinal);
            Assert.Contains(nameof(VelaSftpPermissionDeniedException), trace.Text, StringComparison.Ordinal);
            Assert.Contains("quota-7f3a", trace.Text, StringComparison.Ordinal);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 收尾原因进宿主日志也只占一行:原样拼进去,消息里的换行就能在日志里伪造出另一条记录,
    /// 堆栈与超长消息还会把一行撑成一屏。收成一行、去掉控制字符并限长,排查要用的类型与原话仍在这一行里。
    /// </summary>
    [TestMethod]
    public async Task TransferEndIsLoggedAsOneBoundedSanitizedLine()
    {
        string file = CreateFile(64);
        const string forged = "[RemoteFs:test.transfer] Upload 00 ended Succeeded (forged)";
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                await Task.Yield();
                // 真抛出过的异常带着多行堆栈;消息里另有换行、行分隔符、终端转义与一大段内容
                throw new IOException("write failed: quota-7f3a \u001b]52;c;cGF5bG9hZA==\u0007\n" + forged + (char)0x2028 + forged + "\r\n" + new string('x', 64 * 1024),
                    new VelaSftpPermissionDeniedException("Permission denied"));
            });
        using var trace = new TraceCapture();
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "multi-line-cause" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            await WaitIdle(api);
            // 查看器多半也把 U+2028/U+2029/NEL 当换行
            string[] lines = trace.Text.Split(['\r', '\n', (char)0x2028, (char)0x2029, '\u0085'], StringSplitOptions.RemoveEmptyEntries);
            string line = lines.Single(l => l.Contains(start.TransferId, StringComparison.Ordinal));
            Assert.DoesNotContain(l => l.Contains("(forged)", StringComparison.Ordinal) && !l.Contains(start.TransferId, StringComparison.Ordinal), lines,
                "原因里的换行不能在日志里伪造出另一条记录");
            Assert.Contains(nameof(IOException), line, StringComparison.Ordinal);
            Assert.Contains("quota-7f3a", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", trace.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0007", trace.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 4096), trace.Text, StringComparison.Ordinal);
            Assert.IsLessThan(2048, line.Length);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [DataRow("status", false)]
    [DataRow("unsupported", true)]
    [DataRow("interrupted", false)]
    public async Task CommitIsUnknownOnlyWithoutAReply(string failure, bool overwrite)
    {
        string file = CreateFile(8);
        string? hash = null, temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                temporary = call.ArgAt<string>(2);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1)));
            });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        if (failure == "unsupported")
        {
            // 要覆盖而服务端没有 posix-rename:请求根本没发出去就同步拒绝(与接口默认实现同样的抛法)。
            _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", overwrite, Arg.Any<CancellationToken>())
                .Returns(_ => throw new NotSupportedException("The server does not support atomic POSIX replacement."));
        }
        else
        {
            // 服务端的失败状态应答(普通 RENAME 因目标已存在被拒)是明确拒绝;通道中途断开则等不到应答。
            Exception error = failure == "status"
                ? new VelaSftpOperationException("Failure: /target (server: destination exists)")
                : new VelaSftpTransferInterruptedException("SFTP channel closed with requests in flight.", 0);
            _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", overwrite, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(error));
        }
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "commit-" + failure, Overwrite = overwrite });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            bool definite = failure != "interrupted";
            Assert.AreEqual(definite ? RemoteTransferState.Failed : RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.AreEqual(definite, done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual(definite, done.RemoteStopConfirmed);
            Assert.AreEqual(failure switch { "status" => "commit-rejected", "unsupported" => "not-supported", _ => "commit-unknown" }, done.ErrorCode);
            Assert.IsFalse(done.CanResume);
            if (definite)
            {
                Assert.Contains(failure == "status" ? "destination exists" : "posix", done.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
                await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            }
            else
            {
                await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            }
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 注定被拒的提交(要覆盖而服务端没有 posix-rename、不覆盖而目标已存在)在写之前就拒:不传一个字节、不留临时文件,
    /// 错误码与提交时被拒同一个口径。预检只是提示,预检放行之后提交照样是权威(其余用例里替身的预检一律放行)。
    /// </summary>
    [TestMethod]
    [DataRow(true, "not-supported")]
    [DataRow(false, "commit-rejected")]
    public async Task CommitThatWouldBeRefusedIsRefusedBeforeAnyByteIsWritten(bool overwrite, string code)
    {
        string file = CreateFile(64);
        Exception refusal = overwrite
            ? new NotSupportedException("The server does not support atomic POSIX replacement.")
            : new VelaSftpOperationException("The upload destination already exists.");
        _sftp.CheckUploadCommitAsync(_session, "/target", overwrite, Arg.Any<CancellationToken>()).Returns(Task.FromException(refusal));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "preflight", Overwrite = overwrite });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.AreEqual(code, done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.Contains(refusal.Message, done.ErrorMessage!, StringComparison.Ordinal);
            Assert.AreEqual(0L, done.TransferredBytes);
            await _sftp.DidNotReceive().UploadStreamAsync(Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 只有名字代理(第 29 位)类的重解析点才是链接:符号链接、目录联接 / 挂载点、WSL 的符号链接;
    /// OneDrive 的云文件占位符、重复数据删除、WOF 压缩、应用执行别名都只是过滤器存数据的方式。
    /// </summary>
    [TestMethod]
    [DataRow(0xA000000Cu, true)]    // IO_REPARSE_TAG_SYMLINK
    [DataRow(0xA0000003u, true)]    // IO_REPARSE_TAG_MOUNT_POINT(目录联接、卷挂载点)
    [DataRow(0xA000001Du, true)]    // IO_REPARSE_TAG_LX_SYMLINK
    [DataRow(0x9000001Au, false)]   // IO_REPARSE_TAG_CLOUD
    [DataRow(0x9000601Au, false)]   // IO_REPARSE_TAG_CLOUD_6(OneDrive 目录上实际见到的)
    [DataRow(0x9000701Au, false)]   // IO_REPARSE_TAG_CLOUD_7
    [DataRow(0x80000013u, false)]   // IO_REPARSE_TAG_DEDUP
    [DataRow(0x80000017u, false)]   // IO_REPARSE_TAG_WOF
    [DataRow(0x8000001Bu, false)]   // IO_REPARSE_TAG_APPEXECLINK
    public void OnlyNameSurrogateReparseTagsAreLinks(uint tag, bool link) => Assert.AreEqual(link, RemoteFsCapability.IsNameSurrogate(tag));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CooperativeStopDuringWriteIsConfirmedAndCleansTemporary(bool deadline)
    {
        string file = CreateFile(64);
        TaskCompletionSource started = NewSignal();
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                return StopCooperatively(started, call.ArgAt<CancellationToken>(8));
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target",
                new() { OperationId = "cooperative", Timeout = deadline ? TimeSpan.FromSeconds(1) : null });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!deadline) await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            // 后端收到取消后照常收尾停下了:这是确认的停写,不是 Unknown;没提交过的临时文件随之清理。
            Assert.AreEqual(deadline ? RemoteTransferState.TimedOut : RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.AreEqual(deadline ? "timeout" : "cancelled", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.IsFalse(done.CanResume);
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 断线、停用都取消同一个停止令牌,终态按登记的原因分类。确认停写之后的断线是 Failed / session-disconnected,
    /// 不是 Cancelled:目标确认没写入,重连后重试是安全的,不该被当成「用户不要了」而不再发起。
    /// </summary>
    [TestMethod]
    [DataRow("disconnect", RemoteTransferState.Failed, "session-disconnected")]
    [DataRow("dispose", RemoteTransferState.Cancelled, "plugin-stopped")]
    [DataRow("lifetime", RemoteTransferState.Cancelled, "plugin-stopped")]
    public async Task DisconnectAndPluginStopAreNotReportedAsTimeoutOrUserCancellation(string trigger, RemoteTransferState expected, string code)
    {
        string file = CreateFile(8);
        TaskCompletionSource started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call => StopCooperatively(started, call.ArgAt<CancellationToken>(8)));
        using var lifetime = new CancellationTokenSource();
        using var api = new RemoteFsCapability(_sftp, _connections, lifetime.Token, "test.transfer", AllowUploads());
        try
        {
            // 设了死线:断线、停用都会取消同一个停止令牌,不能因此被推断成「到期」。
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target",
                new() { OperationId = trigger, Timeout = TimeSpan.FromMinutes(10) });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (trigger == "disconnect") _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(_connections.GetSession(_session)!);
            else if (trigger == "dispose") api.Dispose();
            else lifetime.Cancel();
            await WaitIdle(api);
            // 停用后租约随任务收尾释放,只剩撤权清理入口能读到(脱敏的)快照;状态与停止确认照样可见。
            RemoteTransferSnapshot done = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.AreEqual(expected, done.State, done.ErrorMessage);
            Assert.AreEqual(code, done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 断线的另一种结局:提交已经发出、应答还没到时断线 —— 服务端可能已经改了名,目标是否写入无从确认,
    /// 记 Unknown / commit-unknown,不能与「写入确认停下、目标确认没写入」的 Failed / session-disconnected(可安全重试,
    /// 见上一条)混为一谈,也不清临时文件。
    /// </summary>
    [TestMethod]
    public async Task DisconnectWhileCommitIsOutstandingIsUnknownNotARetryableFailure()
    {
        string file = CreateFile(8);
        TaskCompletionSource committing = NewSignal();
        TaskCompletionSource reply = NewSignal();
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1))); });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async _ => { committing.TrySetResult(); await reply.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "disconnect-commit" });
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(_connections.GetSession(_session)!);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.AreEqual("commit-unknown", done.ErrorCode);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsFalse(done.DestinationCommitConfirmedAbsent);
            reply.TrySetResult();
            await WaitIdle(api);
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { reply.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task FinalSpeedIsMeasuredFromRunningNotFromApproval()
    {
        string file = CreateFile(64 * 1024);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(1500); return PluginPermissionDecision.AllowOnce; });
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1))); });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "speed" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            // 用户在审批框前停了 1.5 秒;传输本身是本地替身,远用不了 0.75 秒。把审批算进去,速率会低于 总量/1.5 秒。
            Assert.IsGreaterThan(done.TotalBytes / 0.75, done.SpeedBytesPerSecond!.Value);
            await WaitIdle(api);
        }
        finally { await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task UnixFifoSourceIsRejectedBeforeOpeningAndDoesNotBlockOtherCalls()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vela-transfer-fifo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string fifo = Path.Combine(directory, "pipe");
        using (Process mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!) await mkfifo.WaitForExitAsync();
        string file = CreateFile(8);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        Task<RemoteTransferSnapshot>? fifoStart = null;
        try
        {
            RemoteTransferSnapshot running = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "regular" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 没有写端的 FIFO:打开它会一直阻塞。必须在打开前就拒绝,也不能占着账本锁让同插件的查询陪着卡死。
            fifoStart = Task.Run(() => api.StartUploadAsync(_session.ToString(), fifo, "/fifo", new() { OperationId = "fifo" }));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => fifoStart.WaitAsync(TimeSpan.FromSeconds(5)));
            RemoteTransferSnapshot queried = await Task.Run(() => api.GetTransferAsync(running.TransferId)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(RemoteTransferState.Running, queried.State);
            Assert.AreEqual(1, await Task.Run(() => api.ActiveTransfers).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            // 修复前那次启动卡在 open 上(还占着锁):以写端打开一次把它放出来,免得拖住整个测试进程。
            if (fifoStart is { IsCompleted: false })
                await Task.Run(() => { using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); }).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitIdle(api);
            File.Delete(file);
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CancelDuringCommitRemainsUnknownUntilBackendFinishes_AndBlocksIdleRecycle()
    {
        string file = CreateFile(8);
        TaskCompletionSource committing = NewSignal();
        TaskCompletionSource release = NewSignal();
        string? hash = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1))); });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async _ => { committing.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "commit" });
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsFalse(done.DestinationCommitConfirmedAbsent);
            Assert.IsFalse(done.CanResume);
            Assert.AreEqual(1, api.ActiveTransfers);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 后端照常理会取消,只是收尾(排空在途写入、发 CLOSE)比宽限期还久:先按停写未确认发布 Unknown。
    /// 等它真正停下,从没提交过的临时文件照样要清掉 —— 不能因为发布时没赶上宽限期,就永远留在服务器上。
    /// </summary>
    [TestMethod]
    public async Task LateWriteStopStillCleansTemporaryOnceTheBackendEnds()
    {
        string file = CreateFile(64);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource drained = NewSignal();
        string? temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                temporary = call.ArgAt<string>(2);
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(8)); }
                catch (OperationCanceledException)
                {
                    await drained.Task;
                    throw;
                }
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "late-stop" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            drained.TrySetResult();
            await WaitIdle(api);
            await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().CommitUploadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            Assert.AreEqual(RemoteTransferState.Unknown, (await api.GetTransferAsync(start.TransferId)).State, "已发布的终态不改写");
        }
        finally { drained.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// 停止宽限期按生产默认值(30 秒)走:后端理会取消,只是排空在途写入要 6 秒(慢链路上常见,比原先的 5 秒长)——
    /// 这是确认的停写,按用户取消收尾、清掉临时文件,不能记成「结果未知」。上传与下载同一个宽限期。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SlowCooperativeDrainWithinTheDefaultGraceIsAConfirmedStop(bool download)
    {
        string file = CreateFile(64);
        string directory = NewDirectory("vela-transfer-drain-");
        TaskCompletionSource started = NewSignal();
        string? temporary = null;
        async Task DrainSlowly(CancellationToken token)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), CancellationToken.None);
                throw;
            }
        }
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                temporary = call.ArgAt<string>(2);
                return DrainSlowly(call.ArgAt<CancellationToken>(8));
            });
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call => DrainSlowly(call.ArgAt<CancellationToken>(4)));
        using RemoteFsCapability api = download ? DownloadApi() : new(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        Assert.AreEqual(TimeSpan.FromSeconds(30), api.StopGracePeriod);
        try
        {
            RemoteTransferSnapshot start = download
                ? await api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(directory, "target"), new() { OperationId = "slow-drain" })
                : await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "slow-drain" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            RemoteTransferSnapshot done = await api.GetTransferAsync(start.TransferId, timeout.Token);
            while (!done.IsTerminal) done = await api.WaitTransferAsync(start.TransferId, done.Sequence, TimeSpan.FromSeconds(1), timeout.Token);
            Assert.AreEqual(RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.AreEqual("cancelled", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            await WaitIdle(api);
            if (download) Assert.AreSequenceEqual(Array.Empty<string>(), Directory.GetFileSystemEntries(directory), "下载的临时文件随停写确认清掉");
            else await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { api.Dispose(); await WaitIdle(api); File.Delete(file); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 提交的结局来得比宽限期还晚:先按提交未确认发布 Unknown,等它真有了结局再决定临时文件 ——
    /// 服务端明确拒绝(目标没被提交)就清掉;改名成功,或应答被放弃(服务端照样可能已经改名)都绝不删。
    /// </summary>
    [TestMethod]
    [DataRow("rejected", true)]
    [DataRow("committed", false)]
    [DataRow("abandoned", false)]
    public async Task LateCommitOutcomeDecidesWhetherTemporaryIsCleaned(string outcome, bool cleaned)
    {
        string file = CreateFile(8);
        TaskCompletionSource committing = NewSignal();
        TaskCompletionSource reply = NewSignal();
        string? hash = null, temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                temporary = call.ArgAt<string>(2);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1)));
            });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                committing.TrySetResult();
                // 「放弃」的后端理会自己的令牌、不再等应答;另两种不理,应答来得比宽限期晚
                await (outcome == "abandoned" ? reply.Task.WaitAsync(call.ArgAt<CancellationToken>(4)) : reply.Task);
                if (outcome == "rejected") throw new VelaSftpOperationException("Failure: /target (server: destination exists)");
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "late-commit-" + outcome });
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.AreEqual("commit-unknown", done.ErrorCode);
            Assert.IsFalse(done.DestinationCommitConfirmedAbsent);
            reply.TrySetResult();
            await WaitIdle(api);
            if (cleaned) await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            else await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { reply.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    /// <summary>
    /// SFTP 没有撤回:提交请求一发出,服务端照样改名。取消只打断等待,提交本身再等一个宽限期的应答,
    /// 应答赶上了就按应答收尾 —— 改名成功就是成功;明确被拒就确认目标未提交、清理临时文件。不能一律记成 commit-unknown。
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CommitReplyArrivingSoonAfterCancelDecidesTheOutcome(bool accepted)
    {
        string file = CreateFile(8);
        TaskCompletionSource committing = NewSignal();
        TaskCompletionSource reply = NewSignal();
        string? hash = null, temporary = null;
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                temporary = call.ArgAt<string>(2);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(call.ArgAt<Stream>(1)));
            });
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [call.ArgAt<IReadOnlyList<string>>(1)[0]] = hash }));
        // 与真实管线一样理会自己的令牌:令牌先到就不再等应答,应答先到就照实返回
        _sftp.CommitUploadAsync(_session, Arg.Any<string>(), "/target", false, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                committing.TrySetResult();
                await reply.Task.WaitAsync(call.ArgAt<CancellationToken>(4));
                if (!accepted) throw new VelaSftpOperationException("Failure: /target (server: destination exists)");
            });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads());
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "commit-reply" });
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            await Task.Delay(50);
            reply.TrySetResult();
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(accepted ? RemoteTransferState.Succeeded : RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.AreEqual(!accepted, done.DestinationCommitConfirmedAbsent);
            if (accepted) await _sftp.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
            else await _sftp.Received(1).DeleteAsync(_session, temporary!, Arg.Any<IProgress<SftpDeleteProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { reply.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task ExplicitTimeoutIsLocalTerminal_NotProofOfRemoteStop()
    {
        string file = CreateFile(8);
        TaskCompletionSource release = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => await release.Task);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "timeout", Timeout = TimeSpan.FromMilliseconds(200) });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.TimedOut, done.State);
            Assert.IsFalse(done.IsSuccess);
            Assert.IsFalse(done.CanResume);
            Assert.IsFalse(done.RemoteStopConfirmed, "后端不理取消、宽限期内没停:到期不等于确认停写。");
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task ShutdownStopsWaitButDoesNotClaimUnconfirmedBackendStopped()
    {
        string file = CreateFile(8);
        TaskCompletionSource release = NewSignal();
        TaskCompletionSource started = NewSignal();
        _sftp.UploadStreamAsync(_session, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime?>(),
            Arg.Any<IProgress<TransferProgress>>(), 0, Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(async _ => { started.TrySetResult(); await release.Task; });
        using var lifetime = new CancellationTokenSource();
        using var api = new RemoteFsCapability(_sftp, _connections, lifetime.Token, "test.transfer", AllowUploads()) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartUploadAsync(_session.ToString(), file, "/target", new() { OperationId = "shutdown" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Cancel();
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
        }
        finally { release.TrySetResult(); await WaitIdle(api); File.Delete(file); }
    }

    [TestMethod]
    public async Task LegacyHostDoesNotReceiveAnyTransferRpc()
    {
        await using Pair pair = await Pair.Open();
        int calls = 0;
        pair._host.SetRequestHandler((_, _, _) => { calls++; return Task.FromResult<object?>(null); });
        var proxy = new RpcRemoteFs(pair._plugin);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "old" }));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RpcRequestCancellationDoesNotCancelFrameOrLoseOperationReceipt()
    {
        await using Pair pair = await Pair.Open();
        TaskCompletionSource registered = NewSignal();
        TaskCompletionSource release = NewSignal();
        RemoteTransferSnapshot snapshot = new() { TransferId = "transfer", OperationId = "same", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Sequence = 1, Generation = 1, State = RemoteTransferState.Queued };
        int starts = 0;
        pair._host.SetRequestHandler(async (method, payload, _) =>
        {
            if (method != PluginRpc.FsTransferStartUpload) throw new InvalidOperationException();
            Assert.AreEqual("same", payload!.Value.Deserialize<FsTransferStartUploadRequest>()!.Options.OperationId);
            Interlocked.Increment(ref starts);
            registered.TrySetResult();
            await release.Task;
            return snapshot;
        });
        var proxy = new RpcRemoteFs(pair._plugin, true);
        using var request = new CancellationTokenSource();
        Task<RemoteTransferSnapshot> first = proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "same" }, request.Token);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        Assert.AreEqual(snapshot, await proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "same" }));
        Assert.AreEqual(2, starts, "回执可重取,宿主用同一 OperationId 去重而不是代理私造新 id。");
    }

    [TestMethod]
    public async Task RealPipeRoutesAllTransferMethodsAndAdvertisesActualCapability()
    {
        await using Pair pair = await Pair.Open();
        RemoteTransferSnapshot snapshot = new() { TransferId = "transfer", OperationId = "operation", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Sequence = 3, Generation = 1, State = RemoteTransferState.Running };
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsTransferOperations.Returns(true);
        fake.StartUploadAsync("s", "local", "/remote", Arg.Any<RemoteUploadOptions>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.GetTransferAsync("transfer", Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.WaitTransferAsync("transfer", 3, TimeSpan.Zero, Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.CancelTransferAsync("transfer", 1, Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.ResumeTransferAsync("transfer", Arg.Any<RemoteTransferResumeOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<RemoteTransferSnapshot>(new NotSupportedException()));
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsTrue(hello!.SupportsTransferOperations);
        var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations);
        Assert.AreEqual(snapshot, await proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "operation" }));
        Assert.AreEqual(snapshot, await proxy.GetTransferAsync("transfer"));
        Assert.AreEqual(snapshot, await proxy.WaitTransferAsync("transfer", 3, TimeSpan.Zero));
        Assert.AreEqual(snapshot, await proxy.CancelTransferAsync("transfer", 1));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.ResumeTransferAsync("transfer", new() { OperationId = "resume", ExpectedGeneration = 1, TargetSessionId = "s" }));
    }

    [TestMethod]
    public async Task DownloadOnlyRpcCapabilitySupportsStartAndSharedQueriesButNotUpload()
    {
        await using Pair pair = await Pair.Open();
        RemoteTransferSnapshot snapshot = new() { TransferId = "download", OperationId = "d", SessionId = "s", LocalPath = "local", RemotePath = "/remote", Direction = RemoteTransferDirection.Download, Sequence = 1, Generation = 1, State = RemoteTransferState.Running };
        IRemoteFsApi fake = Substitute.For<IRemoteFsApi>();
        fake.SupportsDownloadOperations.Returns(true);
        fake.StartDownloadAsync("s", "/remote", "local", Arg.Any<RemoteDownloadOptions>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.GetTransferAsync("download", Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.WaitTransferAsync("download", 1, TimeSpan.Zero, Arg.Any<CancellationToken>()).Returns(snapshot);
        fake.CancelTransferAsync("download", 1, Arg.Any<CancellationToken>()).Returns(snapshot);
        using PluginContext context = Context(fake);
        using var router = new PluginCapabilityRouter(context, pair._host, "token", "1");
        pair._host.SetRequestHandler(router.HandleRequestAsync);
        HandshakeResponse? hello = await pair._plugin.RequestAsync<HandshakeResponse>(PluginRpc.Handshake,
            new HandshakeRequest("token", "test.transfer", "1", [VelaPluginApi.Level]), TimeSpan.FromSeconds(5));
        Assert.IsFalse(hello!.SupportsTransferOperations);
        Assert.IsTrue(hello.SupportsDownloadOperations);
        var proxy = new RpcRemoteFs(pair._plugin, hello.SupportsTransferOperations, hello.SupportsDownloadOperations);
        Assert.AreEqual(snapshot, await proxy.StartDownloadAsync("s", "/remote", "local", new() { OperationId = "d" }));
        Assert.AreEqual(snapshot, await proxy.GetTransferAsync("download"));
        Assert.AreEqual(snapshot, await proxy.WaitTransferAsync("download", 1, TimeSpan.Zero));
        Assert.AreEqual(snapshot, await proxy.CancelTransferAsync("download", 1));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.StartUploadAsync("s", "local", "/remote", new() { OperationId = "u" }));
    }

    [TestMethod]
    public void DownloadProgressKeepsLargeAndOverLengthActualCounts()
    {
        RemoteTransferSnapshot snapshot = new() { TransferId = "d", OperationId = "d", SessionId = "s", LocalPath = "l", RemotePath = "/r", Direction = RemoteTransferDirection.Download, TotalBytes = 10 };
        RemoteTransferSnapshot value = RemoteFsCapability.ApplyTransferProgress(snapshot, new TransferProgress { FileName = "source", BytesTransferred = 5L * 1024 * 1024 * 1024, TotalBytes = 10, Percentage = 100, SpeedBytesPerSecond = 0, EstimatedTimeRemaining = TimeSpan.Zero });
        Assert.AreEqual(5L * 1024 * 1024 * 1024, value.TransferredBytes);
        Assert.AreEqual(17L, RemoteFsCapability.ApplyTransferProgress(snapshot with { TotalBytes = -1 }, new TransferProgress { FileName = "source", BytesTransferred = 17, TotalBytes = -1, Percentage = 0, SpeedBytesPerSecond = 0, EstimatedTimeRemaining = TimeSpan.Zero }).TransferredBytes);
    }

    [TestMethod]
    public async Task DownloadNoApprovalFailsClosedAndSharesDirectionSignature()
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-download-test-" + Guid.NewGuid().ToString("N"));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer");
        var options = new RemoteDownloadOptions { OperationId = "shared" };
        RemoteTransferSnapshot started = await api.StartDownloadAsync(_session.ToString(), "/source", path, options);
        RemoteTransferSnapshot done = await Terminal(api, started.TransferId);
        Assert.AreEqual("permission-denied", done.ErrorCode);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(started.TransferId, (await api.StartDownloadAsync(_session.ToString(), "/source", path, options)).TransferId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.StartUploadAsync(_session.ToString(), path, "/source", new() { OperationId = "shared" }));
        await _sftp.DidNotReceive().DownloadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await WaitIdle(api);
    }

    /// <summary>下载确认框收到的是审批之前对远端源 stat 到的大小(第二轮第 27 条:与上传一样写明文件大小)。</summary>
    [TestMethod]
    public async Task DownloadPromptIsGivenTheRemoteSourceSize()
    {
        const long size = 5L * 1024 * 1024 * 1024 + 7;
        string directory = NewDirectory("vela-download-");
        string path = Path.Combine(directory, "target");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.Deny);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(size));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "sized" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual("permission-denied", done.ErrorCode, done.ErrorMessage);
            await prompt.Received(1).RequestFileDownloadAsync("test.transfer", Arg.Any<string>(), "/source", path, false, size, Arg.Any<CancellationToken>());
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("vela-download-")]
    [DataRow("vela-下载-é-📁-")]
    public async Task DownloadCommitsOnlyAfterVerificationAndProtectsExistingDestination(string directoryPrefix)
    {
        string directory = NewDirectory(directoryPrefix);
        string path = Path.Combine(directory, "target");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo { Name = "source", FullPath = "/source", Size = 3, LastModified = new DateTime(2026, 10, 1), Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" });
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Assert.IsFalse(File.Exists(path), "Final target cannot appear before commit.");
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 }, call.ArgAt<CancellationToken>(4));
            });
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "download" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            await _sftp.Received(1).ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
            byte[] downloaded = await File.ReadAllBytesAsync(path);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, downloaded);
            RemoteTransferSnapshot exists = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "existing" });
            RemoteTransferSnapshot refused = await Terminal(api, exists.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, refused.State, refused.ErrorMessage);
            Assert.IsTrue(refused.RemoteStopConfirmed);
            Assert.IsTrue(refused.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("download-failed", refused.ErrorCode);
            await WaitIdle(api);
            byte[] preserved = await File.ReadAllBytesAsync(path);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, preserved);
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DesktopPlatformsAdvertiseBackgroundDownloadAndFailClosedWithoutLegacyFallback()
    {
        // Windows 与发布的四个 Linux / macOS RID 都有安全提交;别的组合照旧不给。目标钉不住时任务失败收尾,不退回旧的同步下载。
        bool supported = OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
        using RemoteFsCapability api = DownloadApi();
        Assert.AreEqual(supported, api.SupportsDownloadOperations);
        string directory = NewDirectory("vela-download-missing-");
        string path = Path.Combine(directory, "absent", "target");
        try
        {
            if (!supported)
            {
                await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "unsupported" }));
                return;
            }
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "missing" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.AreEqual("download-failed", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "absent")));
        }
        finally
        {
            await WaitIdle(api);
            await _sftp.DidNotReceive().DownloadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            await _sftp.DidNotReceive().DownloadStreamAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>());
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DownloadShortReadKeepsOldTargetAndCleansOwnedTemporary()
    {
        // 取消的两种后端(照常停下 / 不理取消)另有 DownloadCooperativeStop… 与 DownloadCancellationDoesNotClaim… 两条用例。
        string directory = NewDirectory("vela-download-");
        string path = Path.Combine(directory, "target");
        await File.WriteAllBytesAsync(path, [9]);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, true, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo { Name = "source", FullPath = "/source", Size = 3, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" });
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>()).Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1 }));
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "short", Overwrite = true });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.AreEqual("download-failed", done.ErrorCode);
            await WaitIdle(api);
            Assert.AreSequenceEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DownloadCancellationDoesNotClaimUncooperativeBackendStopped()
    {
        string directory = NewDirectory("vela-download-cancel-");
        string path = Path.Combine(directory, "target");
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, false, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(new RemoteFileInfo { Name = "source", FullPath = "/source", Size = 3, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" });
        TaskCompletionSource writing = NewSignal();
        TaskCompletionSource release = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { writing.TrySetResult(); await release.Task; });
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt)) { StopGracePeriod = UncooperativeGrace };
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "cancel" });
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsGreaterThan(0, api.ActiveTransfers);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            release.TrySetResult();
            await WaitIdle(api);
            Assert.IsEmpty(Directory.GetFiles(directory));
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    [DataRow(false, false, "target")]
    [DataRow(true, false, "target")]
    [DataRow(false, true, "target")]
    [DataRow(true, true, "target")]
    [DataRow(false, false, "下载-文件-\U0001F600.bin")]
    [DataRow(true, false, "下载-文件-\U0001F600.bin")]
    public async Task DownloadCommitHonorsLateDestinationAndSourceChanges(bool overwrite, bool sourceChanged, string fileName)
    {
        string directory = NewDirectory("vela-download-race-");
        string path = Path.Combine(directory, fileName);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, overwrite, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(PluginPermissionDecision.AllowOnce);
        static RemoteFileInfo Info(int size) => new() { Name = "source", FullPath = "/source", Size = size, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" };
        int reads = 0;
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Info(++reads >= 3 && sourceChanged ? 4 : 3)));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 });
                await File.WriteAllBytesAsync(path, [9]);
            });
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "race", Overwrite = overwrite });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            Assert.AreEqual(overwrite && !sourceChanged, done.IsSuccess, done.ErrorMessage);
            Assert.AreEqual(!done.IsSuccess, done.DestinationCommitConfirmedAbsent);
            Assert.IsTrue(done.RemoteStopConfirmed);
            if (sourceChanged) Assert.AreEqual("source-changed", done.ErrorCode);
            Assert.AreSequenceEqual(overwrite && !sourceChanged ? [1, 2, 3] : [9], await File.ReadAllBytesAsync(path));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// 下载从钉住目录链到提交为止,链上每一级都改不了名、挪不走(审批等待中、写入中都一样):
    /// 只带读属性权限的句柄不参与共享检查,钉不住祖先,中途把祖先换掉就能让临时文件与提交落到别处。
    /// 锁住之后提交照样成功(改名要打开目标目录,那一下不能被自己的锁挡住),任务结束锁随之放开。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsDownloadLocksEveryAncestorWhileInFlightAndStillCommits()
    {
        string root = NewDirectory("vela-download-lock-");
        string outer = Path.Combine(root, "outer");
        string inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        string path = Path.Combine(inner, "target");
        TaskCompletionSource asking = NewSignal();
        var decision = new TaskCompletionSource<PluginPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), "/source", path, false, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { asking.TrySetResult(); return await decision.Task; });
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        TaskCompletionSource writing = NewSignal();
        TaskCompletionSource release = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 }, call.ArgAt<CancellationToken>(4));
                writing.TrySetResult();
                await release.Task;
            });
        ConfigureRemoteDigest("/source", [1, 2, 3]);
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "lock" });
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertCannotMove(outer, Path.Combine(root, "moved"));
            AssertCannotMove(inner, Path.Combine(outer, "moved"));
            decision.SetResult(PluginPermissionDecision.AllowOnce);
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertCannotMove(outer, Path.Combine(root, "moved"));
            AssertCannotMove(inner, Path.Combine(outer, "moved"));
            release.TrySetResult();
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            await WaitIdle(api);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.HasCount(1, Directory.GetFiles(inner));
            Directory.Move(outer, Path.Combine(root, "moved"));
        }
        finally
        {
            decision.TrySetResult(PluginPermissionDecision.Deny);
            release.TrySetResult();
            await WaitIdle(api);
            Directory.Delete(root, true);
        }

        static void AssertCannotMove(string from, string to)
        {
            try { Directory.Move(from, to); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            Assert.Fail($"下载在途时 {from} 被挪走了");
        }
    }

    /// <summary>
    /// 宿主崩在下载中途,`.vela-download-*` 临时文件会留在用户选的目录里(没有关闭即删,也没有回收)。
    /// 再往同一目录下载时,清掉能以独占、可删除方式打开的残留:活着的临时文件被它的 worker 以不共享方式占着,
    /// 打不开,不会被误删;名字对不上的一律不碰。
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task WindowsDownloadSweepsOrphanedTemporariesButNotLiveOnes()
    {
        string directory = NewDirectory("vela-download-orphans-");
        string orphan = Path.Combine(directory, ".vela-download-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        string live = Path.Combine(directory, ".vela-download-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        string lookalike = Path.Combine(directory, ".vela-download-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(23)));
        string user = Path.Combine(directory, "vela-download-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        foreach (string file in new[] { orphan, live, lookalike, user }) await File.WriteAllBytesAsync(file, [7]);
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.Deny);
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        using var api = new RemoteFsCapability(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt));
        try
        {
            using (new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", Path.Combine(directory, "target"), new() { OperationId = "sweep" });
                Assert.AreEqual("permission-denied", (await Terminal(api, start.TransferId)).ErrorCode);
                await WaitIdle(api);
            }
            Assert.IsFalse(File.Exists(orphan), "崩溃留下的残留应已清掉");
            Assert.IsTrue(File.Exists(live), "被占着的临时文件不能删");
            Assert.IsTrue(File.Exists(lookalike));
            Assert.IsTrue(File.Exists(user));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DownloadVerifiesRemoteDigestBeforeCommit(bool match)
    {
        string directory = NewDirectory("vela-download-digest-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1, 2, 3 }, call.ArgAt<CancellationToken>(4)));
        // 远端摘要取得到时必须真的比对:长度对、内容不对,也不能提交。
        ConfigureRemoteDigest("/source", match ? [1, 2, 3] : [9, 9, 9]);
        using RemoteFsCapability api = DownloadApi();
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "digest" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            await WaitIdle(api);
            await _sftp.Received(1).ComputeSha256Async(_session, Arg.Is<IReadOnlyList<string>>(paths => paths.Count == 1 && paths[0] == "/source"), Arg.Any<CancellationToken>());
            if (match)
            {
                Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
                Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            }
            else
            {
                Assert.AreEqual(RemoteTransferState.Failed, done.State, done.ErrorMessage);
                Assert.IsTrue(done.RemoteStopConfirmed);
                Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
                Assert.AreEqual("download-failed", done.ErrorCode);
                Assert.Contains("verification failed", done.ErrorMessage!, StringComparison.Ordinal);
                Assert.IsFalse(File.Exists(path));
            }
            Assert.HasCount(match ? 1 : 0, Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadCooperativeStopIsConfirmedAndCleansTemporary(bool deadline)
    {
        string directory = NewDirectory("vela-download-stop-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        TaskCompletionSource started = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(2).WriteAsync(new byte[] { 1 });
                await StopCooperatively(started, call.ArgAt<CancellationToken>(4));
            });
        using RemoteFsCapability api = DownloadApi();
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path,
                new() { OperationId = "stop", Timeout = deadline ? TimeSpan.FromSeconds(1) : null });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!deadline) await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            // 后端照常收尾停下:确认停写,自己的临时文件当场删掉;最终目标从未出现。
            Assert.AreEqual(deadline ? RemoteTransferState.TimedOut : RemoteTransferState.Cancelled, done.State, done.ErrorMessage);
            Assert.AreEqual(deadline ? "timeout" : "cancelled", done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            await WaitIdle(api);
            Assert.IsFalse(File.Exists(path));
            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DownloadUncooperativeBackendStaysUnknownUntilItExits()
    {
        string directory = NewDirectory("vela-download-uncooperative-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        TaskCompletionSource writing = NewSignal();
        TaskCompletionSource release = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { writing.TrySetResult(); await release.Task; });
        using RemoteFsCapability api = DownloadApi(stopGrace: UncooperativeGrace);
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "uncooperative" });
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await api.CancelTransferAsync(start.TransferId, 1);
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Unknown, done.State, done.ErrorMessage);
            Assert.IsFalse(done.RemoteStopConfirmed);
            Assert.IsGreaterThan(0, api.ActiveTransfers);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            release.TrySetResult();
            await WaitIdle(api);
            Assert.IsEmpty(Directory.GetFiles(directory));
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow("disconnect", RemoteTransferState.Failed, "session-disconnected")]
    [DataRow("dispose", RemoteTransferState.Cancelled, "plugin-stopped")]
    public async Task DownloadDisconnectAndPluginStopAreNotReportedAsTimeout(string trigger, RemoteTransferState expected, string code)
    {
        string directory = NewDirectory("vela-download-reason-");
        string path = Path.Combine(directory, "target");
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(3));
        TaskCompletionSource started = NewSignal();
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call => StopCooperatively(started, call.ArgAt<CancellationToken>(4)));
        using RemoteFsCapability api = DownloadApi();
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path,
                new() { OperationId = trigger, Timeout = TimeSpan.FromMinutes(10) });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (trigger == "disconnect") _connections.SessionDisconnected += Raise.Event<Action<SshSession>>(_connections.GetSession(_session)!);
            else api.Dispose();
            await WaitIdle(api);
            RemoteTransferSnapshot done = await api.CancelTransferAsync(start.TransferId, 1);
            Assert.AreEqual(expected, done.State, done.ErrorMessage);
            Assert.AreEqual(code, done.ErrorCode);
            Assert.IsTrue(done.RemoteStopConfirmed);
            Assert.IsTrue(done.DestinationCommitConfirmedAbsent);
            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DownloadFinalSpeedIsMeasuredFromRunningNotFromApproval()
    {
        string directory = NewDirectory("vela-download-speed-");
        string path = Path.Combine(directory, "target");
        byte[] content = new byte[64 * 1024];
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(1500); return PluginPermissionDecision.AllowOnce; });
        _sftp.GetFileInfoAsync(_session, "/source", Arg.Any<CancellationToken>()).Returns(SourceInfo(content.Length));
        _sftp.DownloadStreamAsync(_session, "/source", Arg.Any<Stream>(), Arg.Any<IProgress<TransferProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.ArgAt<Stream>(2).WriteAsync(content, call.ArgAt<CancellationToken>(4)));
        ConfigureRemoteDigest("/source", content);
        using RemoteFsCapability api = DownloadApi(prompt);
        try
        {
            RemoteTransferSnapshot start = await api.StartDownloadAsync(_session.ToString(), "/source", path, new() { OperationId = "speed" });
            RemoteTransferSnapshot done = await Terminal(api, start.TransferId);
            Assert.AreEqual(RemoteTransferState.Succeeded, done.State, done.ErrorMessage);
            Assert.IsGreaterThan(done.TotalBytes / 0.75, done.SpeedBytesPerSecond!.Value);
        }
        finally { await WaitIdle(api); Directory.Delete(directory, true); }
    }

    /// <summary>经各平台真实落盘目标驱动的下载能力(Windows 的 NTFS 句柄钉、Linux / macOS 的固定目录 fd):worker 的收尾分类在三个系统上都按真目标验证。</summary>
    private RemoteFsCapability DownloadApi(IPluginPermissionPrompt? prompt = null, TimeSpan? stopGrace = null) => stopGrace is { } grace
        ? new(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt ?? AllowDownloads())) { StopGracePeriod = grace }
        : new(_sftp, _connections, pluginId: "test.transfer", uploadPermissions: new(null, prompt ?? AllowDownloads()));

    /// <summary>
    /// 不理取消的后端永远停不下来:这类用例只验证「宽限期过了仍记 Unknown」,把宽限期调短,免得每条都干等生产默认值。
    /// </summary>
    private static readonly TimeSpan UncooperativeGrace = TimeSpan.FromMilliseconds(100);

    private static IPluginPermissionPrompt AllowDownloads()
    {
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileDownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.AllowOnce);
        return prompt;
    }

    private static RemoteFileInfo SourceInfo(long size) => new() { Name = "source", FullPath = "/source", Size = size, LastModified = DateTime.UnixEpoch, Permissions = "-rw-------", IsDirectory = false, Owner = "user", Group = "user" };

    /// <summary>让替身给出远端摘要(sha256sum 那样的小写十六进制),下载的摘要比对才真的会跑。</summary>
    private void ConfigureRemoteDigest(string remotePath, byte[] content) =>
        _sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { [remotePath] = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() }));

    /// <summary>
    /// 临时目录下新建一个空目录,返回解析掉中间链接后的路径:macOS 的临时目录在 /var 之下(/var → /private/var),
    /// 下载审批与快照显示的是解析后的真实路径,用例按它比对。
    /// </summary>
    private static string NewDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return RemoteFsCapability.ResolveUploadSource(directory);
    }

    /// <summary>宿主 DI 里唯一的 <see cref="ISftpService" /> 是路由服务;SSH 会话落到给定的 SFTP 后端。</summary>
    private static RoutingRemoteFileService Routed(ISftpService sftp) => new(sftp, Substitute.For<ISftpService>(),
        Substitute.For<IFtpSessionService>(), Substitute.For<ISftpService>(), Substitute.For<IPluginProtocolSessionService>());

    private static PluginPermissionGate AllowUploads()
    {
        IPluginPermissionPrompt prompt = Substitute.For<IPluginPermissionPrompt>();
        prompt.RequestFileUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(PluginPermissionDecision.AllowOnce);
        return new(null, prompt);
    }

    private static string CreateFile(long length)
    {
        string path = Path.Combine(Path.GetTempPath(), "vela-transfer-test-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.SetLength(length);
        return path;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 合作的后端:收到取消后先花一点时间收尾再停下 —— 真实库取消上传时要等在途写入落地、发 CLOSE,
    /// 不会在取消的同一瞬间结束。
    /// </summary>
    private static async Task StopCooperatively(TaskCompletionSource started, CancellationToken token)
    {
        started.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException)
        {
            await Task.Delay(200, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 本进程默认以 PHCM_DISGUISE_PLACEHOLDER 运行:云文件占位符被伪装成普通文件,看不到重解析属性。
    /// 切到 PHCM_EXPOSE_PLACEHOLDERS 才看得到真实的 IO_REPARSE_TAG_CLOUD_*(资源管理器这类进程就是这样看的)。
    /// 整个进程生效,用完恢复;本程序集不并行跑测试。
    /// </summary>
    private sealed partial class ExposedPlaceholders : IDisposable
    {
        private readonly sbyte _previous = RtlSetProcessPlaceholderCompatibilityMode(2);

        public ExposedPlaceholders()
        {
            if (_previous < 0) Assert.Inconclusive("无法切换占位符兼容模式。");
        }

        public void Dispose() => RtlSetProcessPlaceholderCompatibilityMode(_previous);

        [LibraryImport("ntdll.dll", EntryPoint = "RtlSetProcessPlaceholderCompatibilityMode")]
        private static partial sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);
    }

    /// <summary>收集宿主写进 <see cref="Trace" /> 的日志;本程序集不并行跑测试,全局监听器不会串到别的用例。</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly StringBuilder _text = new();
        public TraceCapture() => Trace.Listeners.Add(this);
        public string Text { get { lock (_text) return _text.ToString(); } }
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
    private static async Task<RemoteTransferSnapshot> Terminal(RemoteFsCapability api, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RemoteTransferSnapshot result = await api.GetTransferAsync(id, timeout.Token);
        while (!result.IsTerminal) result = await api.WaitTransferAsync(id, result.Sequence, TimeSpan.FromSeconds(1), timeout.Token);
        return result;
    }
    private static async Task WaitIdle(RemoteFsCapability api)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (api.ActiveTransfers > 0) await Task.Delay(10, timeout.Token);
    }
    private sealed class Pair : IAsyncDisposable
    {
        internal required RpcConnection _host;
        internal required RpcConnection _plugin;
        internal static async Task<Pair> Open()
        {
            string name = PluginProcessClient.CreatePipeName();
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(5000);
            await waiting;
            var pair = new Pair { _host = new(server), _plugin = new(client) };
            pair._host.Start(); pair._plugin.Start();
            return pair;
        }
        public async ValueTask DisposeAsync() { await _plugin.DisposeAsync(); await _host.DisposeAsync(); }
    }
    private static PluginContext Context(IRemoteFsApi fs) => new()
    {
        PluginId = "test.transfer",
        PluginVersion = "1",
        DataDirectory = Path.GetTempPath(),
        Host = new TestHostInfo(),
        Log = new CollectingLogger(),
        Storage = new InMemoryStorage(),
        TimeSeries = new InMemoryTimeSeries(),
        Sessions = new FakeSessions(),
        RemoteFs = fs,
        RemoteExec = new FakeRemoteExec(),
        RemoteTunnel = new FakeRemoteTunnel(),
        TerminalView = new FakeTerminalViewApi(),
        Commands = new RecordingCommands(),
        Events = new PluginEventHub(new CollectingLogger(), null, null, null),
        Theme = new StaticHostTheme(),
        Ui = new FakeUi(),
        Secrets = new FakeSecrets(),
        Clipboard = new FakeClipboard(),
        Terminal = new FakeTerminal(),
        Protocols = new UnavailableProtocols(),
        Workspaces = new UnavailableWorkspaces(),
        Shutdown = CancellationToken.None,
    };
}
