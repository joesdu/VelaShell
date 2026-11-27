using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 外部 MCP 服务器配成 HTTP 时,插件真实的 Streamable HTTP 连接代码(<see cref="McpManager" />)。
/// 覆盖带会话的 2025-11-25、无会话的新版 Streamable HTTP，以及无响应服务器。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class McpHttpTransportTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    [TestMethod]
    public async Task Lifecycle_ProtocolErrorIsDegraded_StopCancelsPendingApproval()
    {
        await using var server = new McpTestServer(McpTestServer.Kind.StreamableStateless, 400);
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        var config = new McpServerConfig { Transport = McpTransportType.Http, Url = server.BaseUrl + "/mcp" };
        await using McpManager.ToolLease lease = await manager.AcquireToolsAsync([config],
            new McpApproval(null, ApprovalMode.Bypass), CancellationToken.None);
        await lease.Tools.OfType<AIFunction>().Single().InvokeAsync(new AIFunctionArguments { ["text"] = "__fail__" });
        Assert.AreEqual("Degraded", manager.GetSnapshot(config.Id).State);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using McpManager.ToolLease pending = await manager.AcquireToolsAsync([config], new McpApproval(_ =>
        {
            requested.SetResult();
            return approve.Task;
        }), CancellationToken.None);
        Task<object?> invocation = pending.Tools.OfType<AIFunction>().Single()
            .InvokeAsync(new AIFunctionArguments { ["text"] = "pending" }).AsTask();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync(config.Id);
        await Assert.ThrowsAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("Stopped", manager.GetSnapshot(config.Id).State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KeepAlive_LastConcurrentLeaseControlsRelease(bool keepAlive)
    {
        await using var server = new McpTestServer(McpTestServer.Kind.StreamableStateless, 400);
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        var config = new McpServerConfig
        {
            Transport = McpTransportType.Http,
            Url = server.BaseUrl + "/mcp",
            KeepAlive = keepAlive
        };
        var approval = new McpApproval(null, ApprovalMode.Bypass);
        await using McpManager.ToolLease first = await manager.AcquireToolsAsync([config], approval, CancellationToken.None);
        await using McpManager.ToolLease second = await manager.AcquireToolsAsync([config], approval, CancellationToken.None);
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.AreEqual("Ready", manager.GetSnapshot(config.Id).State);
        object? result = await second.Tools.OfType<AIFunction>().Single().InvokeAsync(
            new AIFunctionArguments { ["text"] = "still-used" }, CancellationToken.None);
        Assert.IsNotNull(result);
        await second.DisposeAsync();
        Assert.AreEqual(keepAlive ? "Ready" : "Stopped", manager.GetSnapshot(config.Id).State);
        await using McpManager.ToolLease next = await manager.AcquireToolsAsync([config], approval, CancellationToken.None);
        Assert.AreEqual("Ready", manager.GetSnapshot(config.Id).State);
        Assert.HasCount(1, next.Tools);
    }

    [TestMethod]
    public async Task Lifecycle_StopInterruptsStartup_WithoutStoppingOtherServer()
    {
        await using var healthy = new McpTestServer(McpTestServer.Kind.StreamableStateless, 400);
        await using var stalled = new McpTestServer(McpTestServer.Kind.Unresponsive, 400);
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        using var timeout = new CancellationTokenSource(Budget);
        var first = new McpServerConfig { Transport = McpTransportType.Http, Url = healthy.BaseUrl + "/mcp" };
        var second = new McpServerConfig { Transport = McpTransportType.Http, Url = stalled.BaseUrl + "/mcp" };
        await manager.StartAsync(first, timeout.Token);
        Task starting = manager.StartAsync(second, timeout.Token);
        while (stalled.Log.Length == 0) await Task.Delay(10, timeout.Token);
        Assert.AreEqual("Starting", manager.GetSnapshot(second.Id).State);
        await manager.StopAsync(second.Id, timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
        Assert.AreEqual("Stopped", manager.GetSnapshot(second.Id).State);
        Assert.AreEqual("Ready", manager.GetSnapshot(first.Id).State);
    }

    [TestMethod]
    public async Task Lifecycle_ManualStartStopRestartAndRefresh_ControlsActualConnection()
    {
        await using var server = new McpTestServer(McpTestServer.Kind.StreamableStateless, 400);
        var config = new McpServerConfig
        {
            Name = "lifecycle",
            Transport = McpTransportType.Http,
            Url = server.BaseUrl + "/mcp",
            AutoStart = false
        };
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        using var timeout = new CancellationTokenSource(Budget);
        var approval = new McpApproval(null, ApprovalMode.Bypass);
        Assert.IsEmpty((await manager.GetToolsAsync([config], approval, timeout.Token)).Tools);
        Assert.IsEmpty(server.Log);
        await manager.StartAsync(config, timeout.Token);
        Assert.AreEqual("Ready", manager.GetSnapshot(config.Id).State);
        List<AITool> tools = (await manager.GetToolsAsync([config], approval, timeout.Token)).Tools;
        AIFunction oldTool = tools.OfType<AIFunction>().Single();
        Assert.HasCount(1, await manager.RefreshServerToolsAsync(config.Id, timeout.Token));
        await manager.StopAsync(config.Id, timeout.Token);
        Assert.AreEqual("Stopped", manager.GetSnapshot(config.Id).State);
        config.AutoStart = true;
        Assert.IsEmpty((await manager.GetToolsAsync([config], approval, timeout.Token)).Tools,
            "显式停止不能在下一次取工具时被自动启动覆盖");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await oldTool.InvokeAsync(new AIFunctionArguments { ["text"] = "stale" }, timeout.Token));
        await manager.RestartAsync(config, timeout.Token);
        Assert.AreEqual("Ready", manager.GetSnapshot(config.Id).State);
        config.Enabled = false;
        await manager.ReconcileAsync([config]);
        Assert.AreEqual("Stopped", manager.GetSnapshot(config.Id).State);
    }

    [TestMethod]
    public async Task Lifecycle_FailedStartHasSafeError_AndExplicitStartCanRetry()
    {
        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        var config = new McpServerConfig
        {
            Transport = McpTransportType.Http,
            Url = "invalid-secret-url",
            Headers = "Authorization: Bearer private-value"
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.StartAsync(config, CancellationToken.None));
        McpServerSnapshot failed = manager.GetSnapshot(config.Id);
        Assert.AreEqual("Failed", failed.State);
        Assert.IsNotNull(failed.LastError);
        Assert.DoesNotContain("private-value", failed.LastError);
        Assert.DoesNotContain("invalid-secret-url", failed.LastError);
        await using var server = new McpTestServer(McpTestServer.Kind.StreamableStateless, 400);
        config.Url = server.BaseUrl + "/mcp";
        await manager.StartAsync(config, CancellationToken.None);
        Assert.AreEqual("Ready", manager.GetSnapshot(config.Id).State);
        Assert.IsNull(manager.GetSnapshot(config.Id).LastError);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StreamableHttp_ListsAndCallsTheTool(bool stateless)
    {
        await using var server = new McpTestServer(stateless ? McpTestServer.Kind.StreamableStateless : McpTestServer.Kind.StreamableSession, 400);
        var config = new McpServerConfig { Name = "modern", Transport = McpTransportType.Http, Url = server.BaseUrl + "/mcp" };

        await AssertListsAndCallsEchoAsync(config);

        string[] log = server.Log;
        Assert.Contains(line => line.StartsWith("POST /mcp", StringComparison.Ordinal) && line.Contains("\"tools/call\"", StringComparison.Ordinal), log);
        Assert.DoesNotContain(line => line.Contains("/messages", StringComparison.Ordinal) || line.StartsWith("GET /sse", StringComparison.Ordinal), log,
            "Streamable HTTP 连接不应访问旧版端点");
        if (stateless)
        {
            Assert.DoesNotContain(line => line.Contains("\"initialize\"", StringComparison.Ordinal), log, "2026-07-28 无会话:探测通过就不再握手");
        }
        else
        {
            Assert.Contains(line => line.Contains("\"initialize\"", StringComparison.Ordinal), log, "拒了 server/discover 的会话型服务器按 initialize 握手");
        }
    }

    /// <summary>设置页「测试」/「更新工具库」那条路列得出工具,Agent 一轮里那条路(经审批包装器)调得动它。</summary>
    private static async Task AssertListsAndCallsEchoAsync(McpServerConfig config)
    {
        using var timeout = new CancellationTokenSource(Budget);
        IReadOnlyList<McpToolInfo> listed = await McpManager.RefreshToolsAsync(config, timeout.Token);
        McpToolInfo echo = listed.Single();
        Assert.AreEqual("echo", echo.Name);
        Assert.AreEqual("Echo the text back.", echo.Description);

        using var context = new TestPluginContext();
        await using var manager = new McpManager(context);
        (List<AITool> tools, List<string> errors) = await manager.GetToolsAsync([config], new McpApproval(null, ApprovalMode.Bypass), timeout.Token);
        Assert.IsEmpty(errors, string.Join("; ", errors));
        AIFunction function = tools.OfType<AIFunction>().Single();
        Assert.AreEqual(config.Name + "_echo", function.Name);
        object? result = await function.InvokeAsync(new AIFunctionArguments { ["text"] = "hi" }, timeout.Token);
        Assert.Contains("echo:hi", JsonSerializer.Serialize(result));
    }

    /// <summary>进程内的 MCP 测试服务器:只有一个 echo 工具,协议由 SDK 的服务端传输实现,HTTP 外壳由 <see cref="HttpListener" /> 搭。</summary>
    private sealed class McpTestServer : IAsyncDisposable
    {
        public enum Kind
        {
            /// <summary>带 <c>Mcp-Session-Id</c> 的 Streamable HTTP:不认 <c>server/discover</c>,按 initialize 握手。</summary>
            StreamableSession,

            /// <summary>2026-07-28 的无会话 Streamable HTTP:每个 POST 自带元数据,不握手。</summary>
            StreamableStateless,
            Unresponsive
        }

        private readonly Kind _kind;
        private readonly int _rejectStatus;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _log = new();
        private readonly ConcurrentDictionary<string, StreamableHttpServerTransport> _sessions = new();
        private readonly ConcurrentBag<IAsyncDisposable> _owned = [];
        private readonly Task _loop;

        public McpTestServer(Kind kind, int rejectStatus)
        {
            _kind = kind;
            _rejectStatus = rejectStatus;
            for (int attempt = 0; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183) { listener.Close(); }
                catch { listener.Close(); throw; }
            }
            _loop = AcceptLoopAsync();
        }

        public string BaseUrl { get; }

        /// <summary>收到的每个请求一行:「方法 路径+查询 正文」。</summary>
        public string[] Log => [.. _log];

        private static McpServerOptions Options() => new()
        {
            ServerInfo = new Implementation { Name = "vela-test", Version = "1.0" },
            ToolCollection = [McpServerTool.Create((string text) => text == "__fail__"
                ? throw new InvalidOperationException("test tool failure") : "echo:" + text,
                new McpServerToolCreateOptions { Name = "echo", Description = "Echo the text back." })]
        };

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; } // 停服务时监听器关掉,不再收
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                string body = "";
                if (request.HasEntityBody)
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    body = await reader.ReadToEndAsync();
                }
                _log.Enqueue($"{request.HttpMethod} {request.Url!.PathAndQuery} {body}");
                switch (_kind)
                {
                    case Kind.Unresponsive:
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        break;
                    default:
                        await HandleStreamableAsync(request, response, body);
                        break;
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // 测试收尾:连接被关掉
            }
            catch (Exception ex)
            {
                _log.Enqueue("server error: " + ex.Message);
                try { response.StatusCode = 500; } catch (InvalidOperationException) { }
            }
            finally
            {
                try { response.Close(); } catch (Exception) { /* 客户端已断开 */ }
            }
        }

        private async Task HandleStreamableAsync(HttpListenerRequest request, HttpListenerResponse response, string body)
        {
            if (request.Url!.AbsolutePath != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // 不开独立的 GET 推送流、也不处理 DELETE:协议允许服务器回 405
                response.StatusCode = 405;
                return;
            }
            JsonRpcMessage message = JsonSerializer.Deserialize<JsonRpcMessage>(body, McpJsonUtilities.DefaultOptions)!;
            StreamableHttpServerTransport transport;
            if (_kind == Kind.StreamableStateless)
            {
                transport = new StreamableHttpServerTransport(null) { Stateless = true };
                _owned.Add(transport);
                var server = McpServer.Create(transport, Options());
                _owned.Add(server);
                _ = server.RunAsync(_stop.Token);
            }
            else if (request.Headers["Mcp-Session-Id"] is { } sessionId)
            {
                if (!_sessions.TryGetValue(sessionId, out transport!))
                {
                    response.StatusCode = 404;
                    return;
                }
                response.Headers["Mcp-Session-Id"] = sessionId;
            }
            else if (message is JsonRpcRequest { Method: "initialize" })
            {
                string id = Guid.NewGuid().ToString("N");
                transport = new StreamableHttpServerTransport(null) { SessionId = id };
                _owned.Add(transport);
                _sessions[id] = transport;
                var server = McpServer.Create(transport, Options());
                _owned.Add(server);
                _ = server.RunAsync(_stop.Token);
                response.Headers["Mcp-Session-Id"] = id;
            }
            else
            {
                // 2025-11-25 的会话型服务器不认 server/discover(也不认没带会话的其它请求)
                response.StatusCode = _rejectStatus;
                return;
            }
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            if (!await transport.HandlePostRequestAsync(message, response.OutputStream, _stop.Token))
            {
                response.StatusCode = 202;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _loop;
            foreach (IAsyncDisposable owned in _owned)
            {
                try { await owned.DisposeAsync(); } catch (Exception) { /* 收尾,连接可能早已断开 */ }
            }
            _stop.Dispose();
        }
    }
}
