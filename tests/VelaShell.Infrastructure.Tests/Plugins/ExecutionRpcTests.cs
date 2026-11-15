using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NSubstitute;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.Infrastructure.Ssh;
using VelaShell.PluginHost;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
public class ExecutionRpcTests
{
    private sealed class Sink<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Pair : IAsyncDisposable
    {
        internal required RpcConnection Host { get; init; }
        internal required RpcConnection Plugin { get; init; }
        internal static async Task<Pair> OpenAsync()
        {
            string name = PluginProcessClient.CreatePipeName();
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Task waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(5000);
            await waiting;
            return new() { Host = new(server), Plugin = new(client) };
        }
        public async ValueTask DisposeAsync()
        {
            await Plugin.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    [TestMethod]
    public void NewProtocol_RequiresPositiveContiguousSequence_LegacyMayOmitIt()
    {
        int cancelled = 0;
        int delivered = 0;
        var receiver = new ExecutionEventReceiver<string>(new Sink<string>(_ => delivered++), () => cancelled++);
        receiver.Report(0, "invalid");
        Assert.ThrowsExactly<IOException>(receiver.ThrowIfFailed);
        Assert.AreEqual(1, cancelled);
        Assert.AreEqual(0, delivered);
        var gap = new ExecutionEventReceiver<string>(new Sink<string>(_ => delivered++), () => cancelled++);
        gap.Report(1, "first");
        gap.Report(3, "missing second");
        Assert.ThrowsExactly<IOException>(gap.ThrowIfFailed);
        Assert.AreEqual(1, delivered);
        var legacy = new ExecutionEventReceiver<string>(new Sink<string>(_ => delivered++), () => cancelled++, false);
        legacy.Report(0, "legacy");
        legacy.ThrowIfFailed();
        Assert.AreEqual(2, delivered);
    }

    [TestMethod]
    public void TerminalOutputCount_DetectsMissingTail_WithoutCountingStateEvents()
    {
        var receiver = new ExecutionEventReceiver<SudoExecEvent>(new Sink<SudoExecEvent>(_ => { }), () => { },
            isOutput: value => value.Kind is SudoExecEventKind.Output);
        receiver.Report(1, new(SudoExecEventKind.State, SudoExecState.Running));
        receiver.Report(2, new(SudoExecEventKind.Output, Text: "first"));
        receiver.ValidateOutputCount(1); // 序号含State,OutputChunks只含Output。
        var missing = new ExecutionEventReceiver<SudoExecEvent>(new Sink<SudoExecEvent>(_ => { }), () => { },
            isOutput: value => value.Kind is SudoExecEventKind.Output);
        missing.Report(1, new(SudoExecEventKind.State, SudoExecState.Running));
        Assert.ThrowsExactly<IOException>(() => missing.ValidateOutputCount(1));
    }

    [TestMethod]
    public async Task LegacyHandshake_RejectsSudoWithoutSendingAnyRpc()
    {
        await using Pair pair = await Pair.OpenAsync();
        int requests = 0;
        pair.Host.SetRequestHandler((_, _, _) => { requests++; return Task.FromResult<object?>(null); });
        pair.Host.Start();
        pair.Plugin.Start();
        var proxy = new RpcRemoteExec(pair.Plugin, supportsExecutionOperations: false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => proxy.StreamSudoAsync("s", "id", null,
            new Sink<SudoExecEvent>(_ => Assert.Fail("No host prompt/event is allowed."))));
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task CallbackFailure_CancelsOnlyItsOperation_AndDoesNotReturnSuccess()
    {
        await using Pair pair = await Pair.OpenAsync();
        var proxy = new RpcRemoteExec(pair.Plugin, true);
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Plugin.SetOrderedNotificationHandler(PluginRpc.ExecSudoEvent,
            payload => proxy.OnSudoEvent(payload!.Value.Deserialize<ExecSudoEventNotification>()!));
        pair.Host.SetNotificationHandler((method, payload) =>
        {
            if (method == PluginRpc.ExecCancel) { cancelled.TrySetResult(payload!.Value.Deserialize<ExecCancelNotification>()!.OperationId); }
        });
        pair.Host.SetRequestHandler(async (method, payload, _) =>
        {
            if (method == "probe") { return "alive"; }
            ExecSudoStreamRequest request = payload!.Value.Deserialize<ExecSudoStreamRequest>()!;
            await pair.Host.NotifyAsync(PluginRpc.ExecSudoEvent,
                new ExecSudoEventNotification(request.OperationId, 1, new(SudoExecEventKind.Output, Text: "one")));
            Assert.AreEqual(request.OperationId, await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            return new SudoExecResult(SudoExecOutcome.Exited, 0, 1);
        });
        pair.Host.Start();
        pair.Plugin.Start();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => proxy.StreamSudoAsync("s", "id", null,
            new Sink<SudoExecEvent>(_ => throw new InvalidDataException("receiver failed"))));
        Assert.AreEqual("alive", await pair.Plugin.RequestAsync<string>("probe", null, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Router_HandlesEarlyCancellation_AndShutdownCancelsSilentSudo()
    {
        await using Pair pair = await Pair.OpenAsync();
        pair.Plugin.SetOrderedNotificationHandler(PluginRpc.ExecSudoEvent, _ => { });
        pair.Host.Start();
        pair.Plugin.Start();
        using var shutdown = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeRemoteExec
        {
            SudoHandler = async (_, _, _, sink, ct) =>
            {
                sink.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingAuthentication));
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { cancelled.TrySetResult(); }
                return new(SudoExecOutcome.Unknown);
            }
        };
        using PluginContext context = Context(fake, shutdown.Token);
        using var router = new PluginCapabilityRouter(context, pair.Host, "token", "1");
        await router.HandleRequestAsync(PluginRpc.Handshake,
            JsonSerializer.SerializeToElement(new HandshakeRequest("token", "test.exec", "1", [VelaPluginApi.Level])), CancellationToken.None);
        router.HandleNotification(PluginRpc.ExecCancel, JsonSerializer.SerializeToElement(new ExecCancelNotification("early")));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => router.HandleRequestAsync(PluginRpc.ExecSudoStream,
            JsonSerializer.SerializeToElement(new ExecSudoStreamRequest("s", "id", "early", 0)), CancellationToken.None));
        Assert.IsEmpty(fake.SudoStreamed);
        Task<object?> running = router.HandleRequestAsync(PluginRpc.ExecSudoStream,
            JsonSerializer.SerializeToElement(new ExecSudoStreamRequest("s", "id", "active", 0)), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, router.ActiveExecutions); // 无输出长命令仍阻止空闲回收。
        shutdown.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(5)));
        await router.DrainExecutionsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, router.ActiveExecutions);
    }

    [TestMethod]
    public async Task OrdinaryStreamingCancellation_ActuallyReachesHostOperation()
    {
        await using Pair pair = await Pair.OpenAsync();
        var proxy = new RpcRemoteExec(pair.Plugin, true);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var operations = new ExecutionOperations(CancellationToken.None);
        pair.Host.SetNotificationHandler((method, payload) =>
        {
            if (method == PluginRpc.ExecCancel) { operations.Cancel(payload!.Value.Deserialize<ExecCancelNotification>()!.OperationId); }
        });
        pair.Host.SetRequestHandler(async (_, payload, ct) =>
        {
            ExecStreamRequest request = payload!.Value.Deserialize<ExecStreamRequest>()!;
            await using ExecutionOperations.Operation operation = operations.Register(request.OperationId!, ct);
            registered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, operation.Token); }
            finally { cancelled.TrySetResult(); }
            return new ExecStreamResult(0, 0);
        });
        pair.Host.Start();
        pair.Plugin.Start();
        using var stop = new CancellationTokenSource();
        Task<ExecStreamResult> running = proxy.StreamAsync("s", "long", null, new Sink<ExecOutput>(_ => { }), stop.Token);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await running);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// <c>ls -1 /usr/bin</c> 这类短行输出:一段 4096 字符就是上千行。读泵逐行等发送队列,
    /// 整条链(SSH 读泵 → 宿主发送队列 → RPC → 插件接收方)不溢出、不丢行、行序与序号连续。
    /// </summary>
    [TestMethod]
    public async Task Router_ShortLineBurst_IsBackpressuredEndToEnd()
    {
        const int count = 5000;
        await using Pair pair = await Pair.OpenAsync();
        var proxy = new RpcRemoteExec(pair.Plugin, true);
        pair.Plugin.SetOrderedNotificationHandler(PluginRpc.ExecOutput,
            payload => proxy.OnOutput(payload!.Value.Deserialize<ExecOutputNotification>()!));

        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Func<bool, string, ValueTask>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                System.IO.Pipelines.Pipe pipe = new();
                byte[] output = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, count).Select(i => $"{i}\n")));
                await pipe.Writer.WriteAsync(output);
                await pipe.Writer.CompleteAsync();
                long lines = await VelaSshClientWrapper.PumpLinesAsync(pipe.Reader, false,
                    call.Arg<Func<bool, string, ValueTask>>(), call.Arg<CancellationToken>());
                return new RemoteCommandStreamResult(0, lines);
            });
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        using PluginContext context = Context(new RemoteExecCapability(connections), CancellationToken.None);
        using var router = new PluginCapabilityRouter(context, pair.Host, "token", "1");
        pair.Host.SetRequestHandler(router.HandleRequestAsync);
        pair.Host.SetNotificationHandler(router.HandleNotification);
        pair.Host.Start();
        pair.Plugin.Start();
        await router.HandleRequestAsync(PluginRpc.Handshake,
            JsonSerializer.SerializeToElement(new HandshakeRequest("token", "test.exec", "1", [VelaPluginApi.Level])), CancellationToken.None);

        List<string> received = [];
        ExecStreamResult result = await proxy.StreamAsync(sessionId.ToString(), "ls -1 /usr/bin", null,
            new Sink<ExecOutput>(line => received.Add(line.Line))).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(count, result.Lines);
        Assert.HasCount(count, received);
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(i.ToString(CultureInfo.InvariantCulture), received[i]);
        }
    }

    /// <summary>
    /// 插件在请求还排在写闸前时就取消:请求帧照样发出,宿主登记它、再被随后的取消通知取消掉。
    /// 从前请求被丢在插件侧、取消通知却发了出去,宿主的早到取消集合里就多一条永远等不到请求的 id,
    /// 攒满上限后该插件的全部执行被停用。
    /// </summary>
    [TestMethod]
    public async Task CancelWhileRequestQueuedForWrite_StillDeliversTheRequestSoTheCancelIsMatched()
    {
        await using Pair pair = await Pair.OpenAsync();
        var proxy = new RpcRemoteExec(pair.Plugin, true);
        await using var operations = new ExecutionOperations(CancellationToken.None);
        var registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Host.SetNotificationHandler((method, payload) =>
        {
            if (method == PluginRpc.ExecCancel) { operations.Cancel(payload!.Value.Deserialize<ExecCancelNotification>()!.OperationId); }
        });
        pair.Host.SetRequestHandler(async (method, payload, ct) =>
        {
            if (method != PluginRpc.ExecStream) { return null; }
            ExecStreamRequest request = payload!.Value.Deserialize<ExecStreamRequest>()!;
            await using ExecutionOperations.Operation operation = operations.Register(request.OperationId!, ct);
            // 取消通知与请求在宿主上各自走线程池,两种先后都可能;最终必须是已取消。
            try { await Task.Delay(TimeSpan.FromSeconds(5), operation.Token); }
            catch (OperationCanceledException) { registered.TrySetResult(true); throw; }
            registered.TrySetResult(false);
            return new ExecStreamResult(0, 0);
        });
        pair.Plugin.Start();

        // 宿主还没开始读:一条大通知把插件侧的写闸占住,随后的请求只能排队。
        Task blocking = pair.Plugin.NotifyAsync("test.noise", new string('x', 4 * 1024 * 1024));
        Assert.IsFalse(blocking.IsCompleted, "写闸应当被占住");
        using var stop = new CancellationTokenSource();
        Task<ExecStreamResult> running = proxy.StreamAsync("s", "long", null, new Sink<ExecOutput>(_ => { }), stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));

        pair.Host.Start();
        Assert.IsTrue(await registered.Task.WaitAsync(TimeSpan.FromSeconds(10)), "请求送达后应被早到的取消就地取消");
    }

    private static PluginContext Context(IRemoteExecApi exec, CancellationToken shutdown) => new()
    {
        PluginId = "test.exec",
        PluginVersion = "1",
        DataDirectory = Path.GetTempPath(),
        Host = new TestHostInfo(),
        Log = new CollectingLogger(),
        Storage = new InMemoryStorage(),
        TimeSeries = new InMemoryTimeSeries(),
        Sessions = new FakeSessions(),
        RemoteFs = new FakeRemoteFs(),
        RemoteExec = exec,
        RemoteTunnel = new FakeRemoteTunnel(),
        Network = new FakeNetwork(),
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
        Shutdown = shutdown
    };
}
