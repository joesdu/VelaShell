using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>一台 MCP 服务器在当前管理器中的运行状态，不包含连接凭据。</summary>
/// <param name="State">Stopped、Starting、Ready、Degraded、Failed 或 Stopping。</param>
/// <param name="ToolCount">已发现的工具数量。</param>
/// <param name="LastError">不含服务器原始响应或配置秘密的最近错误。</param>
public sealed record McpServerSnapshot(string State, int ToolCount, string? LastError);

/// <summary>
/// MCP 服务器连接管理:按配置连接启用的服务器,把其工具
/// (<see cref="McpClientTool" /> 本身就是 M.E.AI 的 AIFunction)并入 Agent 工具箱。
/// 连接按配置指纹缓存复用;连接失败短暂退避,避免每轮对话都重复拉起失败进程。
/// 非只读工具(MCP 注解 readOnlyHint != true)执行前走与 run_command 相同的审批。
/// </summary>
public sealed class McpManager(IPluginContext context) : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(30);

    private sealed class Connection
    {
        public string Fingerprint = "";
        public string? WorkspaceRoot;
        public McpClient? Client;
        public IList<McpClientTool> Tools = [];
        public string? Error;
        public DateTimeOffset FailedAt;
        public SemaphoreSlim Calls = new(2, 2);
        public CancellationTokenSource Lifetime = new();
        public int Users;
        public bool KeepAlive = true;
    }

    private readonly Dictionary<string, Connection> _connections = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, McpServerSnapshot> _states = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _lifetimes = new();
    private readonly HashSet<string> _manuallyStopped = [];

    /// <summary>本轮工具及连接租约；所有调用和子任务结束后释放。</summary>
    public sealed class ToolLease : IAsyncDisposable
    {
        private Func<ValueTask>? _release;
        internal ToolLease(List<AITool> tools, List<string> errors, Func<ValueTask> release)
        {
            Tools = tools;
            Errors = errors;
            _release = release;
        }
        /// <summary>本轮可见工具。</summary>
        public List<AITool> Tools { get; }
        /// <summary>连接错误。</summary>
        public List<string> Errors { get; }
        /// <summary>释放本轮持有的连接，最后一轮退出后关闭不保活连接。</summary>
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
    }

    /// <summary>取得本轮工具并持有连接，防止其它轮次结束时提前关闭。</summary>
    public async Task<ToolLease> AcquireToolsAsync(IReadOnlyList<McpServerConfig> servers, McpApproval approval,
        CancellationToken token, IReadOnlyList<LocalWorkspace>? workspaces = null)
    {
        var acquired = new List<(string Id, Connection Connection)>();
        async ValueTask ReleaseAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var failures = new List<Exception>();
                foreach ((string id, Connection connection) in acquired)
                {
                    connection.Users--;
                    if (connection.Users == 0 && !connection.KeepAlive
                        && _connections.TryGetValue(id, out Connection? current) && ReferenceEquals(current, connection))
                    {
                        try
                        {
                            await CloseAsync(id, connection).ConfigureAwait(false);
                            _connections.Remove(id);
                        }
                        catch (Exception ex) { failures.Add(ex); }
                    }
                }
                if (failures.Count > 0) throw new AggregateException(failures);
            }
            finally { _gate.Release(); }
        }
        try
        {
            (List<AITool>? tools, List<string>? errors) = await GetToolsCoreAsync(servers, approval, token, workspaces, (id, connection) =>
            {
                connection.Users++;
                acquired.Add((id, connection));
            }).ConfigureAwait(false);
            return new ToolLease(tools, errors, ReleaseAsync);
        }
        catch
        {
            await ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>取得实时状态；从未连接的服务器为 Stopped。</summary>
    public McpServerSnapshot GetSnapshot(string serverId)
        => _states.GetValueOrDefault(serverId) ?? new("Stopped", 0, null);

    private void SetState(string id, string state, int tools = 0, string? error = null)
        => _states[id] = new(state, tools, error);

    /// <summary>显式启动服务器，绕过自动启动设置与失败退避。</summary>
    public async Task StartAsync(McpServerConfig server, CancellationToken cancellationToken,
        IReadOnlyList<LocalWorkspace>? localWorkspaces = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!server.Enabled)
            {
                throw new InvalidOperationException("Enable the MCP server before starting it.");
            }
            _manuallyStopped.Remove(server.Id);
            Connection connection = await EnsureConnectionAsync(server, cancellationToken, localWorkspaces, retry: true)
                .ConfigureAwait(false);
            if (connection.Client is null)
            {
                // 显式启动途中被手动停止:对发起启动的界面而言这就是一次取消
                if (connection.Lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(connection.Error, connection.Lifetime.Token);
                }
                throw new InvalidOperationException(connection.Error);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>停止服务器并撤销旧工具；后续取工具不会自动重新启动，直到显式启动。</summary>
    public async Task StopAsync(string serverId, CancellationToken cancellationToken = default)
    {
        if (_lifetimes.TryGetValue(serverId, out CancellationTokenSource? lifetime))
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manuallyStopped.Add(serverId);
            if (_connections.TryGetValue(serverId, out Connection? connection))
            {
                await CloseAsync(serverId, connection).ConfigureAwait(false);
                _connections.Remove(serverId);
            }
            else
            {
                SetState(serverId, "Stopped");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>关闭旧连接后用当前配置重新启动。</summary>
    public async Task RestartAsync(McpServerConfig server, CancellationToken cancellationToken,
        IReadOnlyList<LocalWorkspace>? localWorkspaces = null)
    {
        await StopAsync(server.Id, cancellationToken).ConfigureAwait(false);
        await StartAsync(server, cancellationToken, localWorkspaces).ConfigureAwait(false);
    }

    /// <summary>刷新实际运行连接的工具目录，不创建临时测试连接。</summary>
    public async Task<IReadOnlyList<McpToolInfo>> RefreshServerToolsAsync(string serverId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_connections.TryGetValue(serverId, out Connection? connection) || connection.Client is null)
            {
                throw new InvalidOperationException("Start the MCP server before refreshing its tools.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                connection.Tools = await connection.Client.ListToolsAsync(
                    (ModelContextProtocol.RequestOptions?)null, timeout.Token).ConfigureAwait(false);
                SetState(serverId, "Ready", connection.Tools.Count);
                return [.. connection.Tools.Select(t => new McpToolInfo
                {
                    Name = t.Name, Description = Shorten(t.Description),
                    ReadOnly = t.ProtocolTool.Annotations?.ReadOnlyHint == true
                })];
            }
            catch (Exception ex) when (!token.IsCancellationRequested && !connection.Lifetime.IsCancellationRequested)
            {
                string error = $"MCP tool discovery failed ({ex.GetType().Name}).";
                SetState(serverId, "Degraded", connection.Tools.Count, error);
                context.Log.Warn(error);
                throw new InvalidOperationException(error);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>关闭已删除、停用或修改连接参数的服务器，不启动新连接。</summary>
    public async Task ReconcileAsync(IReadOnlyList<McpServerConfig> servers)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (string id in _connections.Keys.ToArray())
            {
                McpServerConfig? server = servers.FirstOrDefault(s => s.Id == id && s.Enabled);
                if (server is null || _connections[id].Fingerprint != Fingerprint(server))
                {
                    Connection connection = _connections[id];
                    await CloseAsync(id, connection).ConfigureAwait(false);
                    _connections.Remove(id);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private static string Fingerprint(McpServerConfig server)
        => JsonSerializer.Serialize(new
        {
            server.Transport,
            server.Command,
            server.Arguments,
            server.WorkingDirectory,
            server.EnvironmentVariables,
            server.EnvironmentInheritMode,
            server.Url,
            server.Headers,
            server.WorkspaceId,
            server.MaxConcurrentCalls,
            server.StartupTimeoutSeconds
        });

    private async Task<Connection> EnsureConnectionAsync(McpServerConfig server, CancellationToken token,
        IReadOnlyList<LocalWorkspace>? workspaces, bool retry = false)
    {
        string fingerprint = Fingerprint(server);
        string? workspaceRoot = null;
        string? scopeError = null;
        if (server.Transport == McpTransportType.Stdio && !string.IsNullOrWhiteSpace(server.WorkspaceId))
        {
            var scope = new LocalPathScope(workspaces ?? []);
            if (scope.TryResolve(server.WorkspaceId, "", write: false, out LocalPath root, out string error))
                workspaceRoot = root.FullPath;
            else
                scopeError = error;
        }
        if (_connections.TryGetValue(server.Id, out Connection? existing))
        {
            if (existing.Fingerprint == fingerprint && !existing.Lifetime.IsCancellationRequested
                && scopeError is null && existing.WorkspaceRoot == workspaceRoot
                && (existing.Client is not null || (!retry && DateTimeOffset.UtcNow - existing.FailedAt < FailureRetryDelay)))
            {
                existing.KeepAlive = server.KeepAlive;
                return existing;
            }
            if (existing.Users > 0 && existing.Client is not null
                && existing.Fingerprint == fingerprint && !existing.Lifetime.IsCancellationRequested
                && existing.WorkspaceRoot != workspaceRoot)
            {
                // 另一个对话正拿着这条连接(绑定的是另一个工作区)。关掉它会打断那边进行中的调用,
                // 两个对话还会轮流把对方的进程重启;这一轮只报这一台不可用。
                return new Connection
                {
                    Error = "MCP server is in use by another chat bound to a different workspace; try again after it finishes.",
                    FailedAt = DateTimeOffset.UtcNow
                };
            }
            await CloseAsync(server.Id, existing).ConfigureAwait(false);
        }
        var connection = new Connection
        {
            Fingerprint = fingerprint,
            WorkspaceRoot = workspaceRoot,
            KeepAlive = server.KeepAlive,
            Calls = new SemaphoreSlim(Math.Clamp(server.MaxConcurrentCalls, 1, 32), Math.Clamp(server.MaxConcurrentCalls, 1, 32))
        };
        _connections[server.Id] = connection;
        _lifetimes[server.Id] = connection.Lifetime;
        SetState(server.Id, "Starting");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Lifetime.Token);
        try
        {
            if (scopeError is not null) throw new InvalidOperationException("MCP workspace is not authorized.");
            (connection.Client, connection.Tools) = await Task.Run(
                () => ConnectAsync(server, linked.Token, workspaces), linked.Token).ConfigureAwait(false);
            SetState(server.Id, "Ready", connection.Tools.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(server.Id, "Stopped");
            throw;
        }
        catch (OperationCanceledException) when (connection.Lifetime.IsCancellationRequested)
        {
            // 用户在启动途中手动停止了这台服务器:只影响这一台,不能把整轮的工具获取当成用户取消。
            connection.Error = "MCP server was stopped.";
            connection.FailedAt = DateTimeOffset.UtcNow;
            SetState(server.Id, "Stopped");
        }
        catch (Exception ex)
        {
            connection.Error = ex is OperationCanceledException
                ? "MCP startup timed out."
                : $"MCP connection or tool discovery failed ({ex.GetType().Name}).";
            connection.FailedAt = DateTimeOffset.UtcNow;
            SetState(server.Id, "Failed", error: connection.Error);
            context.Log.Warn(connection.Error);
        }
        return connection;
    }

    /// <summary>把一条工具执行告警写进插件日志(供嵌套的工具包装器用,它拿不到 <c>context</c>)。</summary>
    internal void LogToolWarning(string message) => context.Log.Warn(message);

    /// <summary>
    /// 是不是那种"再试一次多半就好"的瞬时故障:网络层的断连 / 超时 / DNS 抖动。
    /// 用来决定 MCP 工具调用要不要自动重试 —— 语义类错误(参数不对、服务端 4xx)重试没意义。
    /// </summary>
    internal static bool IsTransientToolFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // HttpClient 超时抛的是 TaskCanceledException(OperationCanceledException 的子类);
            // 真正的"用户按停止"在调用点已按 cancellationToken 单独拦下,到这儿的都是内部超时。
            if (e is HttpRequestException or IOException or SocketException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 汇集全部启用服务器的工具。逐服务器容错:单个失败不影响其余,
    /// 错误以 "<c>名称: 原因</c>" 文本返回给调用方展示。
    /// </summary>
    /// <param name="servers">配置的服务器。</param>
    /// <param name="approval">
    /// 这一轮的审批上下文。工具包装器各自捕获传进来的这一份,<b>不读管理器上的共享状态</b>:
    /// 连接跨轮次、跨聊天复用,IM 桥接里几个聊天的轮次会同时拿着同一个管理器(见 <see cref="McpApproval" />)。
    /// </param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="localWorkspaces">本地工作区配置;用于校验并绑定 Stdio MCP 的工作目录。</param>
    public Task<(List<AITool> Tools, List<string> Errors)> GetToolsAsync(
        IReadOnlyList<McpServerConfig> servers,
        McpApproval approval,
        CancellationToken cancellationToken,
        IReadOnlyList<LocalWorkspace>? localWorkspaces = null)
        => GetToolsCoreAsync(servers, approval, cancellationToken, localWorkspaces, null);

    private async Task<(List<AITool> Tools, List<string> Errors)> GetToolsCoreAsync(
        IReadOnlyList<McpServerConfig> servers, McpApproval approval, CancellationToken cancellationToken,
        IReadOnlyList<LocalWorkspace>? localWorkspaces, Action<string, Connection>? acquire)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var tools = new List<AITool>();
        var errors = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = servers.Where(s => s.Enabled).ToList();

            // 被删除/停用的服务器:断开并移除
            foreach (string staleId in _connections.Keys.Except(wanted.Select(s => s.Id)).ToList())
            {
                await CloseAsync(staleId, _connections[staleId]).ConfigureAwait(false);
                _connections.Remove(staleId);
            }

            foreach (McpServerConfig server in wanted)
            {
                if (_manuallyStopped.Contains(server.Id)
                    || (!server.AutoStart && (!_connections.TryGetValue(server.Id, out Connection? active) || active.Client is null)))
                {
                    continue;
                }
                Connection connection = await EnsureConnectionAsync(server, cancellationToken, localWorkspaces)
                    .ConfigureAwait(false);
                if (connection.Client is not null)
                {
                    acquire?.Invoke(server.Id, connection);
                    CollectTools(tools, usedNames, server, connection.Tools, approval);
                }
                else
                {
                    errors.Add($"{DisplayName(server)}: {connection.Error}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return (tools, errors);
    }

    /// <summary>
    /// 只返回服务器声明为只读的 MCP 工具，供子 Agent 继承。
    /// 该筛选只决定可见性，工具仍保留本轮的本地审批包装。
    /// </summary>
    public static IReadOnlyList<AITool> FilterReadOnlyTools(IEnumerable<AITool> tools)
        => [.. tools.Where(static tool => tool is ApprovalGatedFunction gated && gated.ReadOnlyHint)];

    /// <summary>设置页"测试":临时连接,返回工具名列表后即断开。</summary>
    public static async Task<IReadOnlyList<string>> ProbeAsync(McpServerConfig server, CancellationToken cancellationToken)
        => [.. (await RefreshToolsAsync(server, cancellationToken).ConfigureAwait(false)).Select(t => t.Name)];

    /// <summary>
    /// "更新工具库":连上去把这台服务器现在提供的工具列回来(名称 + 说明 + 只读注解)。
    /// 调用方负责写回 <see cref="McpServerConfig.KnownTools" /> —— 缓存下来,
    /// "配置工具"窗口才能在不连网的情况下列出勾选项。
    /// </summary>
    public static async Task<IReadOnlyList<McpToolInfo>> RefreshToolsAsync(McpServerConfig server,
        CancellationToken cancellationToken)
    {
        (McpClient client, IList<McpClientTool> tools) = await Task.Run(
            () => ConnectAsync(server, cancellationToken), cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return
            [
                .. tools.Select(t => new McpToolInfo
                {
                    Name = t.Name,
                    Description = Shorten(t.Description),
                    ReadOnly = t.ProtocolTool.Annotations?.ReadOnlyHint == true
                })
            ];
        }
    }

    /// <summary>工具说明只用于界面上那一行,过长没有意义。</summary>
    private static string Shorten(string? description)
    {
        string text = (description ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= 120 ? text : text[..120] + "…";
    }

    /// <summary>断开全部连接(面板关闭时调用)。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (CancellationTokenSource lifetime in _lifetimes.Values)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach ((string id, Connection connection) in _connections)
            {
                await CloseAsync(id, connection).ConfigureAwait(false);
            }
            _connections.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 连接与工具包装 ----------

    private static async Task<(McpClient Client, IList<McpClientTool> Tools)> ConnectAsync(
        McpServerConfig server,
        CancellationToken outerToken,
        IReadOnlyList<LocalWorkspace>? localWorkspaces = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(server.StartupTimeoutSeconds, 1, 300)));
        McpClient client = await McpClient.CreateAsync(
            CreateTransport(server, localWorkspaces), cancellationToken: timeout.Token).ConfigureAwait(false);
        try
        {
            IList<McpClientTool> tools = await client
                .ListToolsAsync((ModelContextProtocol.RequestOptions?)null, timeout.Token)
                .ConfigureAwait(false);
            return (client, tools);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static IClientTransport CreateTransport(McpServerConfig server,
        IReadOnlyList<LocalWorkspace>? localWorkspaces = null)
    {
        if (server.Transport == McpTransportType.Http)
        {
            Uri endpoint = ValidateHttpEndpoint(server.Url);
            Dictionary<string, string> headers = McpConfigParser.ParseHeaderLines(server.Headers);
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = DisplayName(server),
                AdditionalHeaders = headers.Count > 0 ? headers : null
                // TransportMode 默认 AutoDetect:先试 Streamable HTTP,再回落 SSE
            });
        }
        return new StdioClientTransport(CreateStdioOptions(server, localWorkspaces));
    }

    internal static StdioClientTransportOptions CreateStdioOptions(McpServerConfig server,
        IReadOnlyList<LocalWorkspace>? localWorkspaces = null)
    {
        if (!Enum.IsDefined(server.EnvironmentInheritMode))
            throw new InvalidOperationException("Unknown MCP environment inheritance mode.");
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException("MCP command is empty.");
        }
        Dictionary<string, string?> env = server.EnvironmentInheritMode == McpEnvironmentInheritMode.Minimal
            ? StdioClientTransportOptions.GetDefaultEnvironmentVariables()
            : [with(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
        foreach (KeyValuePair<string, string?> pair in McpConfigParser.ParseEnvironmentLines(server.EnvironmentVariables))
            env[pair.Key] = pair.Value;
        string workingDirectory = McpWorkspace.ResolveAndEnsure(server.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(server.WorkspaceId))
        {
            LocalPathScope scope = new(localWorkspaces ?? []);
            if (!scope.TryResolve(server.WorkspaceId, "", write: false, out LocalPath workspaceRoot, out string error))
            {
                throw new InvalidOperationException($"MCP workspace '{server.WorkspaceId}' is unavailable: {error}");
            }
            workingDirectory = workspaceRoot.FullPath;
        }
        string command = server.Command.Trim();
        List<string> arguments = McpConfigParser.SplitArguments(server.Arguments);
        if (OperatingSystem.IsWindows() && command.Contains(' '))
        {
            if (command.StartsWith('"') && command.EndsWith('"'))
                command = command[1..^1];
            // SDK 使用 ArgumentList 包装 cmd /c；首项是带空格的路径时，cmd 会剥掉路径引号。
            // @ 保留首命令的引号，不引入 call 的二次变量展开；/d 禁止 AutoRun。
            arguments = ["/d", "/c", "@", command, .. arguments];
            command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }
        return new StdioClientTransportOptions
        {
            // npx/uvx 等脚本命令的 Windows cmd 包装由 SDK 处理
            Command = command,
            Arguments = arguments,
            // 空 = ~/.velashell;~ 要在这儿展开,Process.Start 不认它(见 McpWorkspace)
            WorkingDirectory = workingDirectory,
            InheritEnvironmentVariables = server.EnvironmentInheritMode == McpEnvironmentInheritMode.All,
            EnvironmentVariables = env,
            Name = DisplayName(server)
        };
    }

    /// <summary>
    /// 工具改名(前缀 = 服务器名,防多服务器同名冲突)并按只读注解决定是否包审批;
    /// 截断/清洗后仍撞名时追加序号(模型侧要求工具名唯一)。
    /// </summary>
    private void CollectTools(List<AITool> sink, HashSet<string> usedNames, McpServerConfig server, IList<McpClientTool> tools,
        McpApproval approval)
    {
        string prefix = McpConfigParser.SanitizeToolPrefix(server.Name);
        HashSet<string> disabled = new(
            server.DisabledTools.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowed = new(
            server.AllowedTools.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        Connection connection = _connections[server.Id];
        foreach (McpClientTool tool in tools)
        {
            // 用户屏蔽掉的工具直接不给模型看见:工具太多既占上下文又容易被误调
            if ((allowed.Count > 0 && !allowed.Contains(tool.Name))
                || (allowed.Count == 0 && disabled.Contains(tool.Name)))
            {
                continue;
            }
            string name = $"{prefix}_{tool.Name}";
            if (name.Length > 64)
            {
                name = name[..64]; // OpenAI 函数名长度上限
            }
            for (int i = 2; !usedNames.Add(name); i++)
            {
                string suffix = $"_{i}";
                name = name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)] + suffix : $"{name}{suffix}";
            }
            McpClientTool named = tool.WithName(name);
            // readOnlyHint 是远端服务器自己给出的建议性元数据,不能作为安全边界。恶意服务器
            // 完全可以把删除/上传工具伪装成只读;所有 MCP 工具统一经过本地审批策略。
            sink.Add(new ApprovalGatedFunction(named, DisplayName(server), this, approval,
                connection.Calls, TimeSpan.FromSeconds(Math.Clamp(server.CallTimeoutSeconds, 1, 3600)),
                server.Id, connection));
        }
    }

    /// <summary>
    /// HTTP MCP 端点只允许 HTTPS。为本机开发保留 loopback HTTP;鉴权请求头绝不能经普通
    /// 局域网 HTTP 明文发送。其它 URI scheme 也拒绝,避免 SDK 对非网络 URI 的意外解释。
    /// </summary>
    internal static Uri ValidateHttpEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Invalid MCP server URL. Use an absolute HTTPS URL.");
        }
        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                "Insecure MCP server URL. HTTP is allowed only for localhost; use HTTPS for remote servers.");
        }
        return endpoint;
    }

    private static string DisplayName(McpServerConfig server)
        => string.IsNullOrWhiteSpace(server.Name) ? "(unnamed MCP)" : server.Name.Trim();

    private async Task CloseAsync(string id, Connection connection)
    {
        SetState(id, "Stopping");
        await connection.Lifetime.CancelAsync().ConfigureAwait(false);
        // 换代的连接不再需要这份登记;只摘仍指向自己的那一条,免得删掉新连接的
        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_lifetimes)
            .Remove(new KeyValuePair<string, CancellationTokenSource>(id, connection.Lifetime));
        if (connection.Client is { } client)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
                connection.Client = null;
            }
            catch (Exception ex)
            {
                string error = $"MCP connection cleanup failed ({ex.GetType().Name}).";
                context.Log.Warn(error);
                SetState(id, "Failed", error: error);
                throw new InvalidOperationException(error);
            }
        }
        SetState(id, "Stopped");
    }

    /// <summary>非只读 MCP 工具的审批闸:批准后转调原工具,拒绝以文本告知模型。</summary>
    private sealed class ApprovalGatedFunction(
        McpClientTool inner,
        string serverName,
        McpManager owner,
        McpApproval approval,
        SemaphoreSlim calls,
        TimeSpan callTimeout,
        string serverId,
        Connection connection)
        : DelegatingAIFunction(inner)
    {
        private readonly SemaphoreSlim _calls = calls;
        private readonly TimeSpan _callTimeout = callTimeout;
        internal bool ReadOnlyHint => inner.ProtocolTool.Annotations?.ReadOnlyHint == true;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            connection.Lifetime.Token.ThrowIfCancellationRequested();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Lifetime.Token);
            // MCP 的 readOnlyHint 来自远端,只能用于展示,不能据此跳过本地审批。
            // 挡位与回调都取自造这个包装器的那一轮(approval),不读管理器:管理器是各轮、各聊天共用的
            if (approval.Mode != ApprovalMode.Bypass)
            {
                if (approval.Handler is not { } handler)
                {
                    return "No approval channel available; the call was not executed.";
                }
                // 记忆键到"服务器 + 工具名"为止:同一个工具会被反复调用,但换工具仍要点头
                var request = new ApprovalRequest($"mcp:{serverName} · {Name}", SerializeArguments(arguments),
                    $"mcp:{serverName}:{Name}");
                // 用户点停止时不再傻等审批
                if (!await handler(request).WaitAsync(lifetime.Token).ConfigureAwait(false))
                {
                    return "The user DENIED this MCP tool call. Do not retry it; ask the user how to proceed.";
                }
            }

            // MCP 工具常常连着一台远端服务器,链路一抖(尤其在墙内)整条调用就抛。
            // 以前这里直接把异常放出去:这一轮里挂着的 tool_use 收不到结果,下一次请求就被服务端
            // 以 "'<tool>' cannot be absent" 挡回来 —— 整轮作废,还没法重试。
            // 现在:①瞬时网络故障自动重试几次(仅对服务器自称只读的工具,免得把有副作用的调用重放两遍);
            //       ②最终仍失败就把原因转成给模型看的文本(而不是抛)—— 工具调用始终有个结果,
            //         tool_use 不再悬空,模型可以自己决定再试一次还是如实告诉用户。
            // 只有用户按了停止(cancellationToken)才照旧抛出,交给外层统一收尾。
            await _calls.WaitAsync(lifetime.Token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(_callTimeout);
            try
            {
                bool retrySafe = inner.ProtocolTool.Annotations?.ReadOnlyHint == true;
                int maxAttempts = retrySafe ? 3 : 1;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        object? result = await base.InvokeCoreAsync(arguments, timeout.Token).ConfigureAwait(false);
                        if (!connection.Lifetime.IsCancellationRequested)
                        {
                            bool failed = result is ModelContextProtocol.Protocol.CallToolResult { IsError: true }
                                || (result is JsonElement { ValueKind: JsonValueKind.Object } json
                                    && json.TryGetProperty("isError", out JsonElement isError)
                                    && isError.ValueKind == JsonValueKind.True);
                            owner.SetState(serverId, failed ? "Degraded" : "Ready", connection.Tools.Count,
                                failed ? "MCP server reported a tool error." : null);
                        }
                        return result;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (!timeout.IsCancellationRequested && attempt < maxAttempts && IsTransientToolFailure(ex))
                    {
                        owner.LogToolWarning(
                            $"MCP tool '{Name}' failed (attempt {attempt}/{maxAttempts}), retrying: {ex.GetType().Name}");
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        owner.SetState(serverId, "Degraded", connection.Tools.Count, "MCP tool call timed out.");
                        return $"The MCP tool '{Name}' timed out after {_callTimeout.TotalSeconds:0} seconds.";
                    }
                    catch (Exception ex)
                    {
                        string error = $"MCP tool call failed ({ex.GetType().Name}).";
                        owner.SetState(serverId, "Degraded", connection.Tools.Count, error);
                        owner.LogToolWarning(error);
                        string hint = IsTransientToolFailure(ex)
                            ? " This looks like a transient network issue (the MCP server may be unreachable or blocked);"
                              + " you may retry the call."
                            : "";
                        return $"The MCP tool '{Name}' failed: {ex.GetType().Name}.{hint}"
                               + " Do not treat this as your own mistake — report what happened to the user if it keeps failing.";
                    }
                }
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                owner.SetState(serverId, "Degraded", connection.Tools.Count, "MCP tool call timed out.");
                return $"The MCP tool '{Name}' timed out after {_callTimeout.TotalSeconds:0} seconds.";
            }
            finally
            {
                _calls.Release();
            }
        }

        private static string SerializeArguments(AIFunctionArguments arguments)
        {
            try
            {
                return arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments.ToDictionary(p => p.Key, p => p.Value));
            }
            catch
            {
                return string.Join(", ", arguments.Keys);
            }
        }
    }
}

/// <summary>
/// 一轮的外部 MCP 审批上下文:这一轮用哪个审批回调、按什么挡位。
/// </summary>
/// <remarks>
/// <para>
/// 以前这两样是 <see cref="McpManager" /> 上的两个可写属性,每轮开头写一次、工具调用时再去读。
/// 管理器与它的连接是跨轮次复用的 —— IM 桥接里一个管理器服务所有聊天,而不同聊天的轮次会并发:
/// 第二个聊天一开轮次就把属性改写成自己的,第一个聊天里还挂着的 MCP 调用随后拿着第二个聊天的回调
/// 去要审批(审批卡发到了别的群),第二个聊天是「全部自动」时干脆不问就执行了(#652 复审)。
/// 现在每轮造一份,由那一轮的工具包装器各自捕获,轮与轮之间没有共享的可写状态。
/// </para>
/// <para>
/// <see cref="Mode" /> 仍可改写:聊天面板里一轮跑到一半改了审批挡位要当场生效(与内置工具一致),
/// 面板自己持有一份、只在自己的轮次里用。
/// </para>
/// </remarks>
/// <param name="handler">审批回调;null 视为没有审批通道(非全部自动时一律拒绝)。</param>
/// <param name="mode">审批挡位。</param>
public sealed class McpApproval(Func<ApprovalRequest, Task<bool>>? handler, ApprovalMode mode = ApprovalMode.Ask)
{
    /// <summary>审批回调(与 AgentToolbox 共用同一交互)。null 视为拒绝。</summary>
    public Func<ApprovalRequest, Task<bool>>? Handler { get; } = handler;

    /// <summary>审批挡位(与内置工具共用同一设置)。</summary>
    public ApprovalMode Mode { get; set; } = mode;
}
