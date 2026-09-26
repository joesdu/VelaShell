using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Terminal;
using VelaShell.Terminal.Emulation;

namespace VelaShell.ShellIntegration.Tests;

/// <summary>
/// 把「注入 → 抑制 → 解析」整条链路接到一台<b>真的 sshd、真的登录 shell</b> 上。
/// </summary>
/// <remarks>
/// <para>
/// 替身测得了字符串长什么样,测不了这条链路真正难的地方:对端 shell 怎么解析这一行、
/// PTY 把回显吐成什么样、rc 里的东西什么时候跑、哨兵和上报序列以什么顺序到达。
/// 这个功能栽过的每一个跟头(<c>;;</c> 语法错误、fish 解析期报错、多条注入互相顶掉、
/// 摘历史前缀把 fish 整行判死)都只在真机上现形。
/// </para>
/// <para>
/// 靶子是 <c>docker-compose.test.yml</c> 里的 <c>ssh-shells</c> 服务
/// (见 <c>tests/fixtures/ssh-shells/Dockerfile</c>):一台容器、七个账号,
/// 登录 shell 分别是 bash / zsh / fish / dash / ash,外加两个"用户已经动过手脚"的 bash 账号。
/// 起不来就整组报 Inconclusive —— 全绿的报告里不能混着一行断言都没跑的用例。
/// </para>
/// </remarks>
internal sealed class ShellIntegrationHarness : IAsyncDisposable
{
    /// <summary>写 IPv4 字面量:localhost 先解析到 ::1,而 Docker Desktop 的端口转发只在 IPv4 上应答。</summary>
    public const string Host = "127.0.0.1";

    /// <summary>多 shell 靶子的端口(2222 是另一台单用户容器,别撞)。</summary>
    public const int Port = 2223;

    /// <summary>容器里所有账号的口令,见 fixture 的 Dockerfile。</summary>
    public const string Password = "velapass";

    private readonly VelaSshClientWrapper _client;
    private readonly IShellStreamWrapper _shell;
    private readonly TerminalEmulator _emulator = new(120, 40, TerminalType.XtermColor256);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Lock _sync = new();
    private readonly StringBuilder _visible = new();
    private readonly StringBuilder _raw = new();
    private readonly List<string> _workingDirectories = [];
    private EchoSuppressor? _suppressor;

    /// <summary>最后一次收到远端输出的时刻(UTC ticks);读循环写、用例线程读。</summary>
    private long _lastOutputTicks;

    private ShellIntegrationHarness(VelaSshClientWrapper client, IShellStreamWrapper shell)
    {
        _client = client;
        _shell = shell;
        _emulator.WorkingDirectoryChanged += path =>
        {
            lock (_sync)
            {
                _workingDirectories.Add(path);
            }
        };
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>喂给终端仿真器的那一份字节 —— 也就是<b>用户真正看得见的</b>那些。</summary>
    public string Visible
    {
        get
        {
            lock (_sync)
            {
                return _visible.ToString();
            }
        }
    }

    /// <summary>
    /// 仿真器<b>渲染之后</b>的屏幕(每行一条,已去掉行尾空白)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Visible" /> 的差别正是"喂进去的字节"与"画出来的样子"之差:
    /// 光标移动、清行、覆写这些在字节流里看得见、在屏幕上却不留痕。
    /// "屏幕上到底有几个提示符"这类断言只能问它 —— 问字节流永远是"有两份提示符文本",
    /// 哪怕第二份把第一份原地覆盖掉了。
    /// </remarks>
    public string Screen
    {
        get
        {
            lock (_sync)
            {
                var sb = new StringBuilder();
                for (int row = 0; row < _emulator.Rows; row++)
                {
                    sb.Append(_emulator.Screen.ActiveLine(row).GetText().TrimEnd()).Append('\n');
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>抑制之前的原始字节,仅用于断言失败时把现场打出来。</summary>
    public string Raw
    {
        get
        {
            lock (_sync)
            {
                return _raw.ToString();
            }
        }
    }

    /// <summary>仿真器一路解析出来的工作目录上报,按到达顺序。</summary>
    public IReadOnlyList<string> WorkingDirectories
    {
        get
        {
            lock (_sync)
            {
                return [.. _workingDirectories];
            }
        }
    }

    /// <summary>连上容器里的某个账号并开一条真 PTY。</summary>
    /// <param name="user">账号名,例如 <c>vela-bash</c>。</param>
    public static async Task<ShellIntegrationHarness> ConnectAsync(string user)
    {
        VelaSshClientWrapper client = new(
            ct => SshConnection.ConnectAsync(new SshConnectionOptions(user, Host, Port)
            {
                Credentials = [new PasswordCredential(Password)],
                // 测试容器的主机键每次重建都变：无条件信任，不写 known_hosts。
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, ct),
            TimeSpan.FromSeconds(10));

        await client.ConnectAsync(CancellationToken.None);
        IShellStreamWrapper shell = await client.CreateShellStreamAsync(
            "xterm-256color", 120, 40, 0, 0, 4096, cancellationToken: CancellationToken.None);
        return new(client, shell);
    }

    /// <summary>
    /// 按宿主真正的走法注入一条静默命令:摘历史前缀(该接才接)+ 前导空格 + 换行,
    /// 并按需给它装上注入窗口的哨兵。
    /// </summary>
    /// <remarks>
    /// 这里刻意重走 <c>TerminalTabViewModel.SendSilentCommand</c> 的每一步而不是调用它 ——
    /// 那个方法在 UI 程序集里,拖进来要连 Avalonia 一起。步骤一旦分叉就验不到真东西,
    /// 所以两边都写着对方的名字:改了一边,记得看另一边。
    /// </remarks>
    public async Task InjectAsync(RemoteShellKind kind, string? hidden, string? visible)
    {
        ShellIntegrationInjection injection = SilentCommand.Build(kind, hidden, visible);
        if (injection.IsEmpty)
        {
            return;
        }
        lock (_sync)
        {
            if (injection.Sentinel.Length > 0)
            {
                _suppressor = EchoSuppressor.OpenWindow(
                    Encoding.UTF8.GetBytes(injection.Sentinel), TimeSpan.FromSeconds(10));
            }
            else
            {
                byte[] needle = Encoding.UTF8.GetBytes(injection.CommandLine + "\r\n");
                if (_suppressor is { Expired: false } active)
                {
                    active.AddNeedle(needle);
                }
                else
                {
                    _suppressor = new(needle, 2, TimeSpan.FromSeconds(10));
                }
            }
        }
        await WriteAsync(" " + injection.CommandLine + "\n");
    }

    /// <summary>注入目录上报脚本(装载藏掉、末尾那次上报留着)—— 与宿主的 SendShellIntegration 同义。</summary>
    public Task InjectShellIntegrationAsync(RemoteShellKind kind) =>
        InjectAsync(kind, ShellIntegrationScript.For(kind), ShellIntegrationScript.FunctionName);

    /// <summary>注入一条用户命令(只藏回显,输出照常显示)—— 与宿主的 SendSilentCommand 同义。</summary>
    public Task InjectUserCommandAsync(RemoteShellKind kind, string command) =>
        InjectAsync(kind, null, command);

    /// <summary>
    /// 等对端安静下来 —— 与宿主的 <c>SshTerminalBridge.RunWhenOutputIdle</c> 同义。
    /// </summary>
    /// <remarks>
    /// 注入窗口是"从现在起整段扣住",armed 得太早会把横幅、MOTD、第一个提示符一起吞掉。
    /// 所以先等它们放完。这里同样是「至少收到过一块输出,且此后静默满 idle」。
    /// </remarks>
    public void WaitForOutputIdle(int idleMs = 400, int maxMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            long ticks = Interlocked.Read(ref _lastOutputTicks);
            if (ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >= TimeSpan.FromMilliseconds(idleMs))
            {
                return;
            }
            Thread.Sleep(30);
        }
    }

    /// <summary>像用户敲键一样发一行(不抑制,不摘历史)。</summary>
    public Task TypeAsync(string line) => WriteAsync(line + "\n");

    /// <summary>等条件成立;超时就把现场(可见/原始输出)一起报出来。</summary>
    public void WaitFor(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(50);
        }
        Assert.IsTrue(
            condition(),
            $"等不到「{what}」({timeoutMs}ms)。\n--- 可见输出 ---\n{Escape(Visible)}\n--- 原始输出 ---\n{Escape(Raw)}");
    }

    /// <summary>等一条上报把 cwd 变成期望值。</summary>
    public void WaitForWorkingDirectory(string expected) =>
        WaitFor(() => WorkingDirectories.Contains(expected), $"工作目录上报 {expected}");

    /// <summary>把控制字符写成可读形式,断言失败时才看得懂现场。</summary>
    public static string Escape(string value) =>
        value.Replace("\e", "<ESC>", StringComparison.Ordinal)
            .Replace("\a", "<BEL>", StringComparison.Ordinal)
            .Replace("\r", "<CR>", StringComparison.Ordinal);

    /// <summary>跑一条命令并取回它的标准输出(独立 exec 通道,不碰交互式 shell)。</summary>
    public Task<string> RunAsync(string command) => _client.RunCommandAsync(command);

    /// <summary>
    /// 走真探针问一句对端是哪种 shell —— 与宿主握手时走的是同一条路(独立 exec 通道)。
    /// </summary>
    /// <remarks>
    /// 缓存键留空 = 本次不缓存:每个用例都该从零问起,免得前一个用例的结论串味。
    /// </remarks>
    public Task<RemoteShellKind> DetectShellKindAsync() =>
        RemoteShellProbe.DetectAsync(_client, string.Empty, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _shell.DisposeAsync();
        }
        catch
        {
            // 尽力而为:通道可能已被远端关掉。
        }
        try
        {
            await _pump;
        }
        catch (Exception)
        {
            // 拆除期间读循环抛出的异常与断言无关。
        }
        await _client.DisposeAsync();
        _cts.Dispose();
    }

    private async Task WriteAsync(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _shell.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
        _shell.Flush();
    }

    /// <summary>
    /// 读循环:原始字节 → 抑制器 → 仿真器,与 <c>SshTerminalBridge</c> 的显示路径同构。
    /// </summary>
    private async Task PumpAsync(CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (!token.IsCancellationRequested && _shell.CanRead)
            {
                int read = await _shell.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                byte[] chunk = buffer[..read];
                Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
                lock (_sync)
                {
                    _raw.Append(Encoding.UTF8.GetString(chunk));
                    if (_suppressor is { } suppressor)
                    {
                        chunk = suppressor.Process(chunk);
                        if (suppressor.Expired)
                        {
                            chunk = Concat(chunk, suppressor.TakeHeld());
                            _suppressor = null;
                        }
                    }
                    if (chunk.Length == 0)
                    {
                        continue;
                    }
                    _visible.Append(Encoding.UTF8.GetString(chunk));

                    // 喂仿真器也在锁里:用例线程会读 Screen(它要遍历整屏的行),
                    // 而仿真器不是线程安全的 —— 放到锁外就是边写边读。
                    _emulator.Feed(chunk);
                }
            }
        }
        catch (Exception)
        {
            // 拆除期间的取消/释放都会走到这里,与断言无关。
        }
    }

    private static byte[] Concat(byte[] head, byte[] tail)
    {
        if (tail.Length == 0)
        {
            return head;
        }
        byte[] merged = new byte[head.Length + tail.Length];
        head.CopyTo(merged, 0);
        tail.CopyTo(merged, head.Length);
        return merged;
    }

    /// <summary>靶子起没起来。起不来就整组报 Inconclusive,而不是假装通过。</summary>
    public static void RequireContainer()
    {
        if (!IsDockerAvailable())
        {
            Assert.Inconclusive(
                "Docker 不可用。运行 'docker compose -f docker-compose.test.yml up -d ssh-shells' 以启用。");
        }
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"多 shell 测试服务器 {Host}:{Port} 不可达。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"多 shell 测试服务器 {Host}:{Port} 不可达:{ex.Message}");
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
