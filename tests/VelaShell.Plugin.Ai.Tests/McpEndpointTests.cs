using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 对外的 MCP 服务端:外部 agent(Claude Code / Codex)真的连得上、看得到工具、调得动。
/// </summary>
/// <remarks>
/// 这几条用例走的是<b>真的 HTTP</b>,不是对着内部方法打桩 —— 这条路上最容易坏的恰恰是
/// 协议层面的细节(鉴权头、会话头、JSON-RPC 信封),打桩全都测不到。
/// </remarks>
[TestClass]
public sealed class McpEndpointTests
{
    /// <summary>找一个空闲端口。写死端口会让并行跑测试的机器互相打架。</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>本机回环不该走代理 —— 宿主给 DefaultProxy 装过东西,测试里绕开它。</summary>
    private static HttpClient CreateClient(int port, string? token)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
        return client;
    }

    private static async Task<(McpEndpoint Endpoint, int Port, string Token)> StartAsync(
        TestPluginContext context, ChatMode mode = ChatMode.Plan, ApprovalMode approval = ApprovalMode.ReadOnlyAuto)
    {
        var store = new McpServerSettingsStore(context);
        int port = FreePort();
        await store.SaveAsync(new McpServerSettings { Enabled = true, Port = port, Mode = mode, Approval = approval });
        string token = await store.TokenAsync();
        var endpoint = new McpEndpoint(context, store);
        await endpoint.ReloadAsync();
        return (endpoint, port, token);
    }

    private static async Task<JsonDocument> RpcAsync(HttpClient client, object request, string? sessionId = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(request)
        };
        if (sessionId is not null)
        {
            message.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }
        using HttpResponseMessage response = await client.SendAsync(message);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Initialize_ReturnsSessionIdAndServerInfo()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            Assert.IsTrue(endpoint.IsRunning);
            using HttpClient client = CreateClient(port, token);

            using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new { protocolVersion = "2025-06-18", clientInfo = new { name = "test", version = "1" } }
                })
            };
            using HttpResponseMessage response = await client.SendAsync(message);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? ids));
            Assert.IsFalse(string.IsNullOrEmpty(ids!.First()));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement result = document.RootElement.GetProperty("result");
            Assert.AreEqual("velashell", result.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.AreEqual("2025-06-18", result.GetProperty("protocolVersion").GetString());
        }
    }

    /// <summary>没有令牌就进不来。本机端口谁都能敲,这是唯一的门。</summary>
    [TestMethod]
    public async Task Request_WithoutToken_IsRejected()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string _) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token: null);

            using HttpResponseMessage response = await client.PostAsJsonAsync("mcp",
                new { jsonrpc = "2.0", id = 1, method = "initialize" });

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task Request_WithWrongToken_IsRejected()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token + "x");

            using HttpResponseMessage response = await client.PostAsJsonAsync("mcp",
                new { jsonrpc = "2.0", id = 1, method = "initialize" });

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// 计划档只给只读工具 —— 外部 agent 默认不该能在生产机上敲命令。
    /// </summary>
    [TestMethod]
    public async Task ToolsList_InPlanMode_ExposesOnlyReadOnlyTools()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);

            string[] names =
            [
                .. document.RootElement.GetProperty("result").GetProperty("tools")
                           .EnumerateArray().Select(t => t.GetProperty("name").GetString()!)
            ];
            Assert.Contains("list_sessions", names);
            Assert.Contains("use_session", names);
            Assert.DoesNotContain("run_command", names);
            Assert.DoesNotContain("write_remote_file", names);
        }
    }

    /// <summary>
    /// 挡位选「对话」时一个工具都不给(维护者 2026-10-25 决定:与界面「纯对话:完全不给工具」一致)。
    /// 以前 Chat 档走的是 Agent 那一套,外部 agent 连上来拿到的是全部工具、包括 run_command。
    /// </summary>
    [TestMethod]
    public async Task ToolsList_InChatMode_ExposesNoTools()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, ChatMode.Chat);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            using JsonDocument initialize = await RpcAsync(client, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-06-18", clientInfo = new { name = "test", version = "1" } }
            });
            string instructions = initialize.RootElement.GetProperty("result").GetProperty("instructions").GetString()!;
            Assert.DoesNotContain("list_sessions", instructions, "说明里不能叫外部 agent 去调一个不存在的工具");
            Assert.Contains("no tools", instructions);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);
            Assert.AreEqual(0, document.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());

            using JsonDocument call = await RpcAsync(client, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "run_command", arguments = new { command = "id" } }
            }, sessionId);
            Assert.IsFalse(call.RootElement.TryGetProperty("result", out JsonElement result)
                           && result.TryGetProperty("isError", out JsonElement isError) && !isError.GetBoolean(),
                $"对话档下调工具必须失败:{call.RootElement}");
            Assert.IsEmpty(context.FakeRemoteExec.Executed, "命令一条都不能跑");
            Assert.IsEmpty(context.FakeRemoteExec.Streamed, "命令一条都不能跑");
        }
    }

    [TestMethod]
    public async Task ToolsList_InAgentMode_ExposesWriteTools()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, ChatMode.Agent);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);

            string[] names =
            [
                .. document.RootElement.GetProperty("result").GetProperty("tools")
                           .EnumerateArray().Select(t => t.GetProperty("name").GetString()!)
            ];
            Assert.Contains("run_command", names);
        }
    }

    [TestMethod]
    public async Task ToolsCall_ListSessions_ReturnsTheHostsSessions()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "list_sessions", arguments = new { } }
            }, sessionId);

            JsonElement result = document.RootElement.GetProperty("result");
            Assert.IsFalse(result.GetProperty("isError").GetBoolean());
            string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("prod-1", text);
        }
    }

    /// <summary>
    /// 一条 MCP 连接只有一个轮次,而默认的只读放行会拒掉任何写命令(这条路没有审批界面)。
    /// 拒绝曾经封死整个轮次:之后连 list_sessions 都报错,客户端每次调用又刷新活动时间、
    /// 闲置回收永远轮不到;任务也只在轮次释放时才删,跑满 128 条命令这条连接就再也执行不了。
    /// </summary>
    [TestMethod]
    public async Task ToolsCall_DenialIsPerCall_AndALongSessionOutlivesTheTaskBudget()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        context.FakeRemoteExec.Handler = (_, _) => "out";
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, ChatMode.Agent);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);
            int id = 10;
            async Task<(bool IsError, string Text)> CallAsync(string name, object arguments)
            {
                using JsonDocument document = await RpcAsync(client, new
                {
                    jsonrpc = "2.0",
                    id = id++,
                    method = "tools/call",
                    @params = new { name, arguments }
                }, sessionId);
                JsonElement result = document.RootElement.GetProperty("result");
                return (result.GetProperty("isError").GetBoolean(),
                    result.GetProperty("content")[0].GetProperty("text").GetString()!);
            }

            (_, string denied) = await CallAsync("run_command", new { command = "systemctl restart nginx" });
            Assert.Contains("DENIED", denied);
            (_, string deniedAgain) = await CallAsync("write_remote_file", new { path = "/etc/motd", content = "x" });
            Assert.Contains("DENIED", deniedAgain);

            (bool listFailed, string list) = await CallAsync("list_sessions", new { });
            Assert.IsFalse(listFailed, list);
            Assert.Contains("prod-1", list);

            for (int i = 0; i < 130; i++)
            {
                (bool failed, string text) = await CallAsync("run_command", new { command = "df -h" });
                Assert.IsFalse(failed, text);
                Assert.Contains("\"state\":\"Succeeded\"", text, $"第 {i + 1} 条命令:{text}");
            }
        }
    }

    /// <summary>
    /// 要 root 的命令不再被插件这一层替用户拒掉:审批选哪一项都走到宿主的 root 确认框与密码框
    /// (这里由 <c>FakeRemoteExec.SudoHandler</c> 扮演宿主)。以前 Ask 与只读放行下一律 DENIED,
    /// 外部 agent 遇到要 sudo 的命令只能失败,用户连密码框都见不到。
    /// 同一条命令不带 elevated 时照旧按审批挡位走 —— 放开的只有提权这一种。
    /// </summary>
    [TestMethod]
    [DataRow(ApprovalMode.Ask)]
    [DataRow(ApprovalMode.ReadOnlyAuto)]
    [DataRow(ApprovalMode.Bypass)]
    public async Task ToolsCall_ElevatedCommand_ReachesTheHostsRootConfirmation_InEveryApprovalMode(ApprovalMode approval)
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "deploy");
        context.FakeRemoteExec.SudoHandler = (_, _, _, progress, _) =>
        {
            progress.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingApproval));
            progress.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingAuthentication));
            progress.Report(new(SudoExecEventKind.State, SudoExecState.Running));
            progress.Report(new(SudoExecEventKind.Output, Text: "restarted"));
            return Task.FromResult(new SudoExecResult(SudoExecOutcome.Exited, 0, 1));
        };
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, ChatMode.Agent, approval);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            string elevated = await CallTextAsync(client, sessionId, "run_command",
                new { command = "systemctl restart nginx", elevated = true, waitSeconds = 10 });

            Assert.DoesNotContain("DENIED", elevated);
            Assert.Contains("\"state\":\"Succeeded\"", elevated);
            Assert.Contains("restarted", elevated);
            Assert.AreEqual("systemctl restart nginx", context.FakeRemoteExec.SudoStreamed.Single().Command);
            Assert.IsEmpty(context.FakeRemoteExec.Streamed, "提权绝不退化成普通 exec");

            string plain = await CallTextAsync(client, sessionId, "run_command", new { command = "systemctl restart nginx" });
            if (approval == ApprovalMode.Bypass)
            {
                Assert.DoesNotContain("DENIED", plain);
            }
            else
            {
                Assert.Contains("DENIED", plain, "不带 elevated 的写命令照旧没有审批界面可问");
                Assert.IsEmpty(context.FakeRemoteExec.Streamed);
            }
        }
    }

    /// <summary>
    /// 密码框开着时 tools/call 不能跟着干等(请求有时限,用户这会儿多半还在外部 agent 的窗口里):
    /// 先回一个 AwaitingUser 的任务,用户在 VelaShell 里答完,再由 task_wait 取结果。
    /// </summary>
    [TestMethod]
    public async Task ToolsCall_ElevatedCommand_ReturnsAwaitingUserWhileTheHostDialogIsOpen()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "deploy");
        var asking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteExec.SudoHandler = async (_, _, _, progress, cancellationToken) =>
        {
            progress.Report(new(SudoExecEventKind.State, SudoExecState.AwaitingAuthentication));
            asking.TrySetResult();
            await answered.Task.WaitAsync(cancellationToken);
            progress.Report(new(SudoExecEventKind.State, SudoExecState.Running));
            progress.Report(new(SudoExecEventKind.Output, Text: "updated"));
            return new SudoExecResult(SudoExecOutcome.Exited, 0, 1);
        };
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, ChatMode.Agent);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            string pending = await CallTextAsync(client, sessionId, "run_command",
                new { command = "apt-get update", elevated = true, waitSeconds = 0 });
            Assert.DoesNotContain("Succeeded", pending, "密码框还开着,不能先报完成");
            string taskId;
            using (JsonDocument receipt = JsonDocument.Parse(pending))
            {
                taskId = receipt.RootElement.GetProperty("taskId").GetString()!;
            }
            await asking.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("\"state\":\"AwaitingUser\"",
                await CallTextAsync(client, sessionId, "task_status", new { taskId }));

            answered.SetResult();
            string done = await CallTextAsync(client, sessionId, "task_wait", new { taskId, waitSeconds = 30 });
            Assert.Contains("\"state\":\"Succeeded\"", done);
            Assert.Contains("updated", done);
        }
    }

    /// <summary>
    /// 外部 agent 的习惯是把 <c>sudo …</c> 直接写进命令 —— 独立 exec 通道没有终端,sudo 一要密码就失败。
    /// 有 run_command 的挡位一上来就得告诉它走 elevated;计划档没有 run_command,提了只会让它去找一个不存在的参数。
    /// </summary>
    [TestMethod]
    [DataRow(ChatMode.Agent, true)]
    [DataRow(ChatMode.Plan, false)]
    public async Task Initialize_TellsClientsWithRunCommandHowToRunRootCommands(ChatMode mode, bool expected)
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, mode);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            using JsonDocument initialize = await RpcAsync(client, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-06-18", clientInfo = new { name = "test", version = "1" } }
            });
            string instructions = initialize.RootElement.GetProperty("result").GetProperty("instructions").GetString()!;
            Assert.AreEqual(expected, instructions.Contains("elevated=true", StringComparison.Ordinal), instructions);
        }
    }

    /// <summary>没走 initialize 就调工具,要给一个说得清的错,而不是空指针。</summary>
    [TestMethod]
    public async Task ToolsList_WithoutSession_ReportsAnError()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 9, method = "tools/list" });

            Assert.AreEqual(-32001, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    /// <summary>桥接与服务端都关着时,不该占任何端口。</summary>
    [TestMethod]
    public async Task Disabled_DoesNotListen()
    {
        using var context = new TestPluginContext();
        var store = new McpServerSettingsStore(context);
        await store.SaveAsync(new McpServerSettings { Enabled = false, Port = FreePort() });

        await using var endpoint = new McpEndpoint(context, store);
        await endpoint.ReloadAsync();

        Assert.IsFalse(endpoint.IsRunning);
        Assert.IsNull(endpoint.Url);
    }

    // ———————————————————— 资源预算 ————————————————————
    //
    // 这个端点跑在用户的桌面程序进程里。合法的本机客户端也会误用(脚本跑飞、把整个仓库
    // 塞进一个 tools/call、连着 initialize 上千次),而以前这里一条上限都没有 ——
    // 撑爆的是他正开着的终端。

    /// <summary>
    /// 超大正文当场回绝,而不是整份读进内存之后再说;且回绝之后服务端照常服务。
    /// </summary>
    /// <remarks>
    /// <b>客户端看到什么是平台相关的,不能作为断言对象。</b>服务端认出 Content-Length 超限后
    /// 立刻回 413 并关掉连接 —— 而此时客户端往往还在往外灌那 5MB。Windows 的 HTTP.SYS 会把
    /// 剩余请求体吸收掉,客户端读到 413;Linux/macOS 上内核直接给出 <c>Broken pipe</c>,
    /// 客户端连响应都读不到。<b>两者都是"被回绝"</b>,差别只在谁先断。
    /// <para>
    /// 所以这里断言的是真正的不变量:请求没被处理、服务端没被搞挂、后续正常请求照旧能用。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task OversizedRequestBody_IsRejected()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            HttpStatusCode? status = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
                {
                    // 5 MB > 4 MB 上限。
                    Content = new StringContent(new string('x', 5 * 1024 * 1024))
                };
                using HttpResponseMessage response = await client.SendAsync(message);
                status = response.StatusCode;
            }
            catch (HttpRequestException)
            {
                // 服务端先关了连接 —— 同样是被回绝,只是我们没机会读到那个 413。
            }

            Assert.IsTrue(
                status is null or HttpStatusCode.RequestEntityTooLarge,
                $"超大正文既没被回绝也没断开,而是拿到了 {status}。");

            // 真正要守住的:回绝一个超大请求不能把服务端搞挂,后面的正常请求还得能用。
            Assert.IsTrue(endpoint.IsRunning);
            using HttpClient healthy = CreateClient(port, token);
            string sessionId = await InitializeAsync(healthy);
            using JsonDocument document = await RpcAsync(
                healthy, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);
            Assert.IsTrue(document.RootElement.TryGetProperty("result", out _), "服务端在超大请求之后不再正常服务。");
        }
    }

    /// <summary>正常大小的请求不受影响(别为了设上限把好人也挡了)。</summary>
    [TestMethod]
    public async Task ANormalSizedRequest_StillWorks()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(
                client, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);

            Assert.IsTrue(document.RootElement.TryGetProperty("result", out _));
        }
    }

    /// <summary>
    /// 会话数有上限:连着 initialize 不会让字典无限涨。
    /// </summary>
    /// <remarks>
    /// 闲置淘汰以前只在 initialize 里顺带跑一次,而淘汰阈值是两小时 —— 短时间内
    /// 反复 initialize(客户端重连、脚本重试)一个都淘汰不掉,会话只增不减。
    /// </remarks>
    [TestMethod]
    public async Task SessionCount_HasAnUpperBound()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            int refused = 0;
            for (int i = 0; i < 40; i++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
                {
                    Content = JsonContent.Create(new { jsonrpc = "2.0", id = i, method = "initialize" })
                };
                using HttpResponseMessage response = await client.SendAsync(message);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    refused++;
                }
            }

            Assert.IsGreaterThan(0, refused, "开了 40 个会话一个都没被拦下 —— 会话数仍然没有上限。");
        }
    }

    /// <summary>
    /// 闲置回收只为还在启动或运行的传输留着会话。已终结、停止未确认的 Unknown 上传等多久都不会变 ——
    /// 它要是也算"活跃",会话就永远回收不掉,攒满上限之后 initialize 一律 503。回收照样发 CancelAll。
    /// </summary>
    [TestMethod]
    public async Task IdleEviction_KeepsRunningUploads_ButNotTerminalUnknownOnes()
    {
        using var context = new TestPluginContext();
        string connected = context.FakeSessions.AddConnected().SessionId;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(new RemoteTransferSnapshot
        {
            TransferId = target,
            OperationId = options.OperationId,
            SessionId = id,
            LocalPath = source,
            RemotePath = target,
            State = target == "/tmp/unknown" ? RemoteTransferState.Unknown : RemoteTransferState.Running,
            RemoteStopConfirmed = false,
            Generation = 1,
            Sequence = 1,
            TotalBytes = 1
        });
        var never = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => never.Task.WaitAsync(ct);
        context.FakeRemoteFs.CancelTransferHandler = (id, generation, _) => Task.FromResult(new RemoteTransferSnapshot
        {
            TransferId = id,
            OperationId = "cancel",
            SessionId = connected,
            LocalPath = "",
            RemotePath = id,
            State = RemoteTransferState.Cancelled,
            RemoteStopConfirmed = true,
            Generation = generation,
            Sequence = 2
        });
        var store = new McpServerSettingsStore(context);
        int port = FreePort();
        await store.SaveAsync(new McpServerSettings { Enabled = true, Port = port, Mode = ChatMode.Agent, Approval = ApprovalMode.Bypass });
        string token = await store.TokenAsync();
        string local = Path.Combine(Path.GetTempPath(), "vela-mcp-upload-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(local, "x");
        try
        {
            await using var endpoint = new McpEndpoint(context, store);
            await endpoint.ReloadAsync();
            using HttpClient client = CreateClient(port, token);
            async Task<string> UploadAsync(string sessionId, string remotePath)
            {
                using JsonDocument document = await RpcAsync(client, new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new { name = "upload_local_file", arguments = new { localPath = local, remotePath, sessionId = connected } }
                }, sessionId);
                return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            }
            string unknown = await InitializeAsync(client);
            string running = await InitializeAsync(client);
            Assert.Contains("\"state\":\"Unknown\"", await UploadAsync(unknown, "/tmp/unknown"));
            Assert.Contains("\"state\":\"Running\"", await UploadAsync(running, "/tmp/running"));
            var sessions = (ConcurrentDictionary<string, McpToolHost>)typeof(McpEndpoint)
                .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpoint)!;
            foreach (McpToolHost host in sessions.Values) host.LastActivity = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);

            await InitializeAsync(client);

            Assert.IsFalse(sessions.ContainsKey(unknown), "只剩已终结 Unknown 上传的闲置会话应被回收");
            Assert.IsTrue(sessions.ContainsKey(running), "仍在运行的上传继续保护会话");
            Assert.AreEqual("/tmp/unknown", context.FakeRemoteFs.TransfersCancelled.Single().TransferId, "回收会话照样 CancelAll");
        }
        finally { File.Delete(local); }
    }

    /// <summary>
    /// 撤权后被扣下的上传也不再钉住会话:扣下时已经向宿主发过取消,它的快照再也读不到、永远停在扣下前那份
    /// (多半还是 Running)。把它算成"未终结",会话就永远回收不掉。仍在运行的上传照旧保护会话。
    /// </summary>
    [TestMethod]
    public async Task IdleEviction_DoesNotKeepSessionsForWithheldUploads()
    {
        using var context = new TestPluginContext();
        string connected = context.FakeSessions.AddConnected().SessionId;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.StartUploadHandler = (id, source, target, options, _) => Task.FromResult(new RemoteTransferSnapshot
        {
            TransferId = target,
            OperationId = options.OperationId,
            SessionId = id,
            LocalPath = source,
            RemotePath = target,
            State = RemoteTransferState.Running,
            Generation = 1,
            Sequence = 1,
            TotalBytes = 1
        });
        var revoked = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.WaitTransferHandler = (id, _, _, ct) => (id == "/tmp/withheld" ? revoked.Task : never.Task).WaitAsync(ct);
        var withholdCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.CancelTransferHandler = (id, generation, _) =>
        {
            if (id == "/tmp/withheld") withholdCancelled.TrySetResult();
            // 撤权后宿主只交还清理所需的状态
            return Task.FromResult(new RemoteTransferSnapshot
            {
                TransferId = id,
                OperationId = "cancel",
                SessionId = "",
                LocalPath = "",
                RemotePath = "",
                State = RemoteTransferState.Unknown,
                RemoteStopConfirmed = false,
                Generation = generation,
                Sequence = 2,
                ErrorCode = "permission-revoked"
            });
        };
        var store = new McpServerSettingsStore(context);
        int port = FreePort();
        await store.SaveAsync(new McpServerSettings { Enabled = true, Port = port, Mode = ChatMode.Agent, Approval = ApprovalMode.Bypass });
        string token = await store.TokenAsync();
        string local = Path.Combine(Path.GetTempPath(), "vela-mcp-upload-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(local, "x");
        try
        {
            await using var endpoint = new McpEndpoint(context, store);
            await endpoint.ReloadAsync();
            using HttpClient client = CreateClient(port, token);
            async Task<string> UploadAsync(string sessionId, string remotePath)
            {
                using JsonDocument document = await RpcAsync(client, new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new { name = "upload_local_file", arguments = new { localPath = local, remotePath, sessionId = connected } }
                }, sessionId);
                return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            }
            string withheld = await InitializeAsync(client);
            string running = await InitializeAsync(client);
            Assert.Contains("\"state\":\"Running\"", await UploadAsync(withheld, "/tmp/withheld"));
            Assert.Contains("\"state\":\"Running\"", await UploadAsync(running, "/tmp/running"));
            // 宿主撤了权:进度等待报拒绝,插件扣下元数据、向宿主发取消
            revoked.SetException(new PluginSdk.PluginPermissionDeniedException("Upload authorization was revoked."));
            await withholdCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sessions = (ConcurrentDictionary<string, McpToolHost>)typeof(McpEndpoint)
                .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(endpoint)!;
            foreach (McpToolHost host in sessions.Values) host.LastActivity = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);

            await InitializeAsync(client);

            Assert.IsFalse(sessions.ContainsKey(withheld), "只剩被扣下上传的闲置会话应被回收");
            Assert.IsTrue(sessions.ContainsKey(running), "仍在运行的上传继续保护会话");
        }
        finally { File.Delete(local); }
    }

    private static int _nextCallId = 1000;

    /// <summary>调一个工具,取回给模型看的那段文本。</summary>
    private static async Task<string> CallTextAsync(HttpClient client, string sessionId, string name, object arguments)
    {
        using JsonDocument document = await RpcAsync(client, new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _nextCallId),
            method = "tools/call",
            @params = new { name, arguments }
        }, sessionId);
        return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static async Task<string> InitializeAsync(HttpClient client)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "initialize" })
        };
        using HttpResponseMessage response = await client.SendAsync(message);
        return response.Headers.GetValues("Mcp-Session-Id").First();
    }
}
