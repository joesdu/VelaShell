using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Infrastructure.Tests.Ftp;

/// <summary>
/// 仅供测试使用的环回 FTP 服务器:把一个临时目录当作 FTP 根,实现 RFC 959 里
/// 客户端跑通「登录 → 列目录 → 上传/下载/改名/删除」所需的最小命令集。
/// <para>
/// 为什么自己写:CI 与开发机不保证能拉到 vsftpd 镜像,而只用 Mock 验证不了
/// 「FluentFTP 到底能不能跟一个真服务器把 PASV 数据连接跑通、LIST 输出能不能被解析成
/// <c>RemoteFileInfo</c>」—— 那正是这套后端最容易出错、也最值得守住的部分。
/// </para>
/// <para>
/// 刻意**不**在 FEAT 里通告 MLSD:强制客户端走 Unix 风格 LIST 解析,那是真实老服务器上的
/// 常见路径,也是权限/属主字符串真正被解析出来的路径。
/// </para>
/// </summary>
internal sealed class LoopbackFtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly Lock _sync = new();

    public LoopbackFtpServer(string rootDirectory)
    {
        Root = rootDirectory;
        Directory.CreateDirectory(Root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>FTP 根目录在本地磁盘上的位置。</summary>
    public string Root { get; }

    /// <summary>监听端口(随机分配,避免与本机既有服务冲突)。</summary>
    public int Port { get; }

    /// <summary>最近一次登录使用的用户名(用于断言匿名登录)。</summary>
    public string? LastUser { get; private set; }

    /// <summary>最近一次登录使用的口令。</summary>
    public string? LastPassword { get; private set; }

    /// <summary>已接受的控制连接数(用于断言连接池确实开了多条连接)。</summary>
    public int AcceptedConnections;

    /// <summary>已创建的被动监听器数:每条控制连接最多一个,不能随文件数增长。</summary>
    public int PassiveListenersCreated;

    /// <summary>
    /// 同一时刻允许的控制连接数上限;超出的连接一律以 <c>421</c> 顶回去(连欢迎语都不给)。
    /// <para>
    /// 复刻用户报的那类服务器:只支持单线程上传,一次只能传一个文件,批量上传时
    /// 除第一个以外全部失败。0 = 不限(默认)。
    /// </para>
    /// </summary>
    public int MaxConcurrentSessions { get; set; }

    /// <summary>被 <see cref="MaxConcurrentSessions" /> 顶回去的连接数(用于断言确实撞过限制)。</summary>
    public int RejectedConnections;

    /// <summary>
    /// 新来的控制连接一律当场复位(欢迎语都不给):复刻「主机还在、端口那头的服务已经没了」,
    /// 客户端拨号看到的是 ConnectionReset。已经连着的会话不受影响。
    /// </summary>
    public bool ResetNewSessions { get; set; }

    /// <summary>
    /// 同一时刻允许的上传数上限;超出的 <c>STOR</c> 以 <c>450 Transfer busy</c> 顶回去。
    /// 复刻另一类服务器:控制连接随便开,但一次只让传一个文件。0 = 不限(默认)。
    /// </summary>
    public int MaxConcurrentTransfers { get; set; }

    /// <summary>被 <see cref="MaxConcurrentTransfers" /> 顶回去的传输数。</summary>
    public int RejectedTransfers;

    /// <summary>
    /// 同一时刻允许的 RETR 数上限;超出的那次**不回 425/450,直接复位数据连接**。
    /// 复刻不少服务器的真实做法 —— 客户端只看到 ConnectionReset,一个字节都读不到。
    /// 0 = 不限(默认)。
    /// </summary>
    public int MaxConcurrentRetrieves { get; set; }

    /// <summary>被 <see cref="MaxConcurrentRetrieves" /> 静默掐断的 RETR 次数。</summary>
    public int RejectedRetrieves;

    /// <summary>
    /// 是否实现 <c>MFMT</c>(设置修改时间)。默认 true;置 false 复刻只会 RFC 959 那一套的老服务器
    /// —— 命令落进 default 分支回 <c>500</c>,客户端应当据此报「不支持」。
    /// </summary>
    public bool SupportsMfmt { get; set; } = true;

    /// <summary>
    /// 是否实现 <c>XSHA256</c>(服务器端算 SHA-256,FileZilla Server / Serv-U 一类的扩展)。默认 true;
    /// 置 false 时 FEAT 不通告、命令回 <c>500</c>,客户端应当回退到大小与修改时间。
    /// </summary>
    public bool SupportsSha256 { get; set; } = true;

    /// <summary>
    /// 是否实现 <c>MLSD</c> / <c>MLST</c>(RFC 3659,时间一律 UTC)。默认 false —— 类型注释里说的那条主路径是 LIST;
    /// 置 true 时 FEAT 通告它们,客户端就改走机读列表,用来验「MLSD 的时间不该再按时区偏移挪」。
    /// </summary>
    public bool SupportsMlsd { get; set; }

    /// <summary>
    /// LIST 按哪个 UTC 偏移的时钟写时间;null(默认)= 本机时区,即服务器与客户端在同一个时区。
    /// 复刻「服务器在 UTC+0、客户端在 UTC+8」这类整体错位的场景。MLSD 不受它影响(那边恒为 UTC)。
    /// </summary>
    public TimeSpan? ListClockOffset { get; set; }

    /// <summary>按远端路径控制 RETR 的数据和最终回复;默认不注入故障。</summary>
    public Func<string, ReadFault?>? ReadFaultForPath { get; set; }

    /// <summary>
    /// 按远端路径控制 STOR / APPE:返回 N 时收满 N 个字节就复位数据连接并回 <c>426</c>(上传传到一半被掐断);
    /// null = 照常收完。默认不注入故障。
    /// </summary>
    public Func<string, int?>? ResetStoreAfterBytes { get; set; }

    /// <summary>按路径与当前 TYPE I 状态控制 SIZE 回复;null 使用磁盘长度。</summary>
    public Func<string, bool, string?>? SizeReplyForPath { get; set; }

    /// <summary>按路径覆盖 LIST/MLST/MLSD 中的长度,独立于 SIZE 与 RETR。</summary>
    public Func<string, long, long>? ListingSizeForPath { get; set; }

    public ConcurrentDictionary<string, int> RetrCounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, int> StoreCounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, int> SizeCounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<(string Path, bool Binary)> SizeRequests { get; } = new();
    public int TypeCommands;
    public ConcurrentQueue<long> RestartOffsets { get; } = new();

    internal sealed class ReadFault
    {
        public TaskCompletionSource DataOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DataSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowData { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldData { get; init; }
        public bool HoldReply { get; init; }
        public bool CloseControl { get; init; }
        public int? DataLimit { get; init; }
        public bool ResetData { get; init; }
        /// <summary>
        /// 发完数据之后先等这么久再复位:客户端已经把收到的字节读走、正阻塞在下一次读上,
        /// 复位落在一次进行中的读上,客户端看到的就是 <c>ConnectionReset</c>(而不是一个干净的 EOF)。
        /// </summary>
        public int ResetDelayMs { get; init; }
        /// <summary>
        /// 发完 <see cref="DataLimit" /> 个字节后**不复位、也不给 226**,直接让数据流正常结束
        /// (纯短读);置 true 与 <see cref="ResetData" /> 互斥。
        /// </summary>
        public bool TruncateSilently { get; init; }
        public string? RejectRetr { get; init; }
        public string? Reply { get; init; } = "226 Transfer complete";
    }

    private int _liveSessions;
    private int _liveTransfers;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref AcceptedConnections);
            var session = Task.Run(() => HandleClientAsync(client));
            lock (_sync)
            {
                _clients.Add(session);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            if (ResetNewSessions)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();
                return;
            }
            // Preliminary and completion replies are separate small writes. Do not let
            // Nagle wait for Linux's delayed ACK between them on the control connection.
            client.NoDelay = true;
            using var state = new SessionState(_cts.Token);
            NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\r\n" };

            // 连接数超限:按真实服务器的做法,开口就是 421 然后关掉,不给欢迎语。
            int live = Interlocked.Increment(ref _liveSessions);
            if (MaxConcurrentSessions > 0 && live > MaxConcurrentSessions)
            {
                Interlocked.Decrement(ref _liveSessions);
                Interlocked.Increment(ref RejectedConnections);
                await writer.WriteLineAsync(
                    "421 Too many users connected, please try again later.").ConfigureAwait(false);
                return;
            }
            try
            {
                await ServeAsync(reader, writer, state).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _liveSessions);
            }
        }
    }

    private async Task ServeAsync(StreamReader reader, StreamWriter writer, SessionState state)
    {
        await writer.WriteLineAsync("220 VelaShell loopback FTP").ConfigureAwait(false);
        while (!_cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                return;
            }
            if (line is null)
            {
                return;
            }
            int space = line.IndexOf(' ');
            string command = (space < 0 ? line : line[..space]).ToUpperInvariant();
            string argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            if (!await ExecuteAsync(command, argument, state, writer).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>执行一条命令;返回 false 表示会话结束(QUIT)。</summary>
    private async Task<bool> ExecuteAsync(string command, string argument, SessionState state, StreamWriter writer)
    {
        switch (command)
        {
            case "USER":
                LastUser = argument;
                await writer.WriteLineAsync("331 Need password").ConfigureAwait(false);
                return true;
            case "PASS":
                LastPassword = argument;
                await writer.WriteLineAsync("230 Logged in").ConfigureAwait(false);
                return true;
            case "SYST":
                await writer.WriteLineAsync("215 UNIX Type: L8").ConfigureAwait(false);
                return true;
            case "FEAT":
                // 只通告 SIZE / REST / UTF8:不给 MLSD,逼客户端走 Unix LIST 解析。
                await writer.WriteLineAsync("211-Features:").ConfigureAwait(false);
                await writer.WriteLineAsync(" SIZE").ConfigureAwait(false);
                await writer.WriteLineAsync(" REST STREAM").ConfigureAwait(false);
                await writer.WriteLineAsync(" UTF8").ConfigureAwait(false);
                if (SupportsMfmt)
                {
                    await writer.WriteLineAsync(" MFMT").ConfigureAwait(false);
                }
                if (SupportsSha256)
                {
                    await writer.WriteLineAsync(" XSHA256").ConfigureAwait(false);
                }
                if (SupportsMlsd)
                {
                    await writer.WriteLineAsync(" MLST type*;size*;modify*;").ConfigureAwait(false);
                    await writer.WriteLineAsync(" MLSD").ConfigureAwait(false);
                }
                await writer.WriteLineAsync("211 End").ConfigureAwait(false);
                return true;
            case "MLSD" when SupportsMlsd:
                await SendMachineListingAsync(state, argument, writer).ConfigureAwait(false);
                return true;
            case "MLST" when SupportsMlsd:
                {
                    string remote = Normalize(state, argument);
                    string local = Local(remote);
                    if (!File.Exists(local) && !Directory.Exists(local))
                    {
                        await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                        return true;
                    }
                    await writer.WriteLineAsync($"250-Listing {remote}").ConfigureAwait(false);
                    await writer.WriteLineAsync($" {MachineFacts(local)} {remote}").ConfigureAwait(false);
                    await writer.WriteLineAsync("250 End").ConfigureAwait(false);
                    return true;
                }
            case "XSHA256" when SupportsSha256:
                {
                    string path = Local(Normalize(state, argument));
                    if (!File.Exists(path))
                    {
                        await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                        return true;
                    }
                    byte[] digest;
                    await using (FileStream source = File.OpenRead(path))
                    {
                        digest = await System.Security.Cryptography.SHA256.HashDataAsync(source).ConfigureAwait(false);
                    }
                    await writer.WriteLineAsync($"213 {Convert.ToHexString(digest)}").ConfigureAwait(false);
                    return true;
                }
            case "MFMT" when SupportsMfmt:
                {
                    // MFMT <yyyyMMddHHmmss(UTC)> <path>:时间与路径之间只隔一个空格,路径本身可以含空格。
                    int gap = argument.IndexOf(' ');
                    string path = gap < 0 ? string.Empty : Local(Normalize(state, argument[(gap + 1)..]));
                    if (gap < 0
                        || !DateTime.TryParseExact(argument[..gap], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
                    {
                        await writer.WriteLineAsync("501 Syntax error").ConfigureAwait(false);
                        return true;
                    }
                    if (!File.Exists(path))
                    {
                        await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                        return true;
                    }
                    File.SetLastWriteTimeUtc(path, utc);
                    await writer.WriteLineAsync($"213 Modify={argument[..gap]}; {argument[(gap + 1)..]}").ConfigureAwait(false);
                    return true;
                }
            case "TYPE":
                Interlocked.Increment(ref TypeCommands);
                state.Binary = argument.Equals("I", StringComparison.OrdinalIgnoreCase);
                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                return true;
            case "OPTS":
            case "NOOP":
                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                return true;
            case "PWD":
            case "XPWD":
                await writer.WriteLineAsync($"257 \"{state.WorkingDirectory}\" is current directory").ConfigureAwait(false);
                return true;
            case "CWD":
                state.WorkingDirectory = Normalize(state, argument);
                await writer.WriteLineAsync(Directory.Exists(Local(state.WorkingDirectory))
                    ? "250 Directory changed"
                    : "550 No such directory").ConfigureAwait(false);
                return true;
            case "REST":
                state.RestartOffset = long.TryParse(argument, out long offset) ? offset : 0;
                RestartOffsets.Enqueue(state.RestartOffset);
                await writer.WriteLineAsync("350 Restarting").ConfigureAwait(false);
                return true;
            case "PASV":
                await OpenPassiveAsync(state, writer, extended: false).ConfigureAwait(false);
                return true;
            case "EPSV":
                await OpenPassiveAsync(state, writer, extended: true).ConfigureAwait(false);
                return true;
            case "LIST":
            case "NLST":
                await SendListingAsync(state, argument, writer, command == "NLST").ConfigureAwait(false);
                return true;
            case "SIZE":
                {
                    string remote = Normalize(state, argument);
                    string path = Local(remote);
                    SizeCounts.AddOrUpdate(remote, 1, static (_, count) => count + 1);
                    SizeRequests.Enqueue((remote, state.Binary));
                    string? reply = SizeReplyForPath?.Invoke(remote, state.Binary);
                    await writer.WriteLineAsync(reply ?? (File.Exists(path)
                        ? $"213 {new FileInfo(path).Length}"
                        : "550 Not found")).ConfigureAwait(false);
                    return true;
                }
            case "MDTM":
                {
                    string path = Local(Normalize(state, argument));
                    await writer.WriteLineAsync(File.Exists(path)
                        ? $"213 {File.GetLastWriteTimeUtc(path):yyyyMMddHHmmss}"
                        : "550 Not found").ConfigureAwait(false);
                    return true;
                }
            case "RETR":
                return await SendFileAsync(state, argument, writer).ConfigureAwait(false);
            case "STOR":
            case "APPE":
                await ReceiveFileAsync(state, argument, writer, append: command == "APPE").ConfigureAwait(false);
                return true;
            case "DELE":
                {
                    string path = Local(Normalize(state, argument));
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        await writer.WriteLineAsync("250 Deleted").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                    }
                    return true;
                }
            case "MKD":
            case "XMKD":
                {
                    string remote = Normalize(state, argument);
                    Directory.CreateDirectory(Local(remote));
                    await writer.WriteLineAsync($"257 \"{remote}\" created").ConfigureAwait(false);
                    return true;
                }
            case "RMD":
            case "XRMD":
                {
                    string path = Local(Normalize(state, argument));
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                        await writer.WriteLineAsync("250 Removed").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                    }
                    return true;
                }
            case "RNFR":
                state.RenameFrom = Local(Normalize(state, argument));
                await writer.WriteLineAsync("350 Ready for destination").ConfigureAwait(false);
                return true;
            case "RNTO":
                {
                    string target = Local(Normalize(state, argument));
                    if (state.RenameFrom is { } source && (File.Exists(source) || Directory.Exists(source)))
                    {
                        if (Directory.Exists(source))
                        {
                            Directory.Move(source, target);
                        }
                        else
                        {
                            File.Move(source, target, true);
                        }
                        await writer.WriteLineAsync("250 Renamed").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("550 Rename failed").ConfigureAwait(false);
                    }
                    state.RenameFrom = null;
                    return true;
                }
            case "SITE":
                // SITE CHMOD:真实服务器上是可选命令,这里接受并忽略,足以验证调用链路。
                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                return true;
            case "QUIT":
                await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                return false;
            default:
                await writer.WriteLineAsync("500 Unknown command").ConfigureAwait(false);
                return true;
        }
    }

    private async Task OpenPassiveAsync(SessionState state, StreamWriter writer, bool extended)
    {
        state.CloseData();
        // Keep one passive port per control session. Allocating a fresh listener for every
        // transfer exhausts Windows' dynamic ports while previous sockets are in TIME_WAIT.
        if (state.DataListener is null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            state.DataListener = listener;
            Interlocked.Increment(ref PassiveListenersCreated);
        }
        int port = ((IPEndPoint)state.DataListener.LocalEndpoint).Port;
        if (extended)
        {
            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{port}|)").ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync(
                $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})").ConfigureAwait(false);
        }
    }

    private async Task SendListingAsync(SessionState state, string argument, StreamWriter writer, bool namesOnly)
    {
        // 客户端有时会给 LIST 带上 -a / -l 这类参数,得剥掉再当路径用。
        string cleaned = string.Join(' ', argument.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(static token => !token.StartsWith('-')));
        string remote = Normalize(state, cleaned);
        string local = Local(remote);
        if (!Directory.Exists(local))
        {
            await writer.WriteLineAsync("550 No such directory").ConfigureAwait(false);
            return;
        }
        var payload = new StringBuilder();
        foreach (string directory in Directory.GetDirectories(local))
        {
            var info = new DirectoryInfo(directory);
            payload.Append(namesOnly ? info.Name : FormatEntry(info.Name, 4096, ListClock(info.LastWriteTimeUtc), isDirectory: true)).Append("\r\n");
        }
        foreach (string file in Directory.GetFiles(local))
        {
            var info = new FileInfo(file);
            payload.Append(namesOnly ? info.Name : FormatEntry(info.Name, ListingSize(file, info.Length), ListClock(info.LastWriteTimeUtc), isDirectory: false)).Append("\r\n");
        }
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString());
            await data.WriteAsync(bytes).ConfigureAwait(false);
        }
        state.CloseData();
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    private async Task<bool> SendFileAsync(SessionState state, string argument, StreamWriter writer)
    {
        string remote = Normalize(state, argument);
        string path = Local(remote);
        if (!File.Exists(path))
        {
            await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
            return true;
        }
        RetrCounts.AddOrUpdate(remote, 1, static (_, count) => count + 1);
        ReadFault? fault = ReadFaultForPath?.Invoke(remote);
        if (fault?.RejectRetr is { } rejection)
        {
            // REST 只管紧跟着的那一条传输命令:被拒的 RETR 同样把它用掉,不能留给下一条不带 REST 的 RETR。
            state.RestartOffset = 0;
            state.CloseData();
            await writer.WriteLineAsync(rejection).ConfigureAwait(false);
            return true;
        }
        // 「一次只让下一个」的服务器在并发时的另一种形态:不回 425/450,直接把数据连接复位。
        // 客户端看到的只有 ConnectionReset,一个字节都读不到 —— 批量中转里 actual=0 成片就是它。
        int live = Interlocked.Increment(ref _liveTransfers);
        try
        {
            if (MaxConcurrentRetrieves > 0 && live > MaxConcurrentRetrieves)
            {
                Interlocked.Increment(ref RejectedRetrieves);
                await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
                await using (Stream rejected = await state.AcceptDataAsync().ConfigureAwait(false))
                {
                    ((DataStream)rejected).Reset();
                }
                state.CloseData();
                state.RestartOffset = 0;
                await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
                return true;
            }
            return await SendFileCoreAsync(state, path, fault, writer).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _liveTransfers);
        }
    }

    private async Task<bool> SendFileCoreAsync(SessionState state, string path, ReadFault? fault, StreamWriter writer)
    {
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        await using (FileStream source = File.OpenRead(path))
        {
            fault?.DataOpened.TrySetResult();
            if (fault is { HoldData: true })
            {
                await fault.AllowData.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            if (state.RestartOffset > 0 && state.RestartOffset <= source.Length)
            {
                source.Seek(state.RestartOffset, SeekOrigin.Begin);
            }
            if (fault?.DataLimit is int limit)
            {
                byte[] bytes = new byte[limit];
                int read = await source.ReadAsync(bytes, _cts.Token).ConfigureAwait(false);
                await data.WriteAsync(bytes.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
            }
            else
            {
                await source.CopyToAsync(data, _cts.Token).ConfigureAwait(false);
            }
            if (fault is { ResetData: true })
            {
                if (fault.ResetDelayMs > 0)
                {
                    await data.FlushAsync(_cts.Token).ConfigureAwait(false);
                    await Task.Delay(fault.ResetDelayMs, _cts.Token).ConfigureAwait(false);
                }
                ((DataStream)data).Reset();
            }
        }
        state.RestartOffset = 0;
        state.CloseData();
        fault?.DataSent.TrySetResult();
        if (fault is { HoldReply: true })
        {
            await fault.AllowReply.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        if (fault is { CloseControl: true })
        {
            return false;
        }
        string? reply = fault is null ? "226 Transfer complete" : fault.Reply;
        if (reply is not null)
        {
            await writer.WriteLineAsync(reply).ConfigureAwait(false);
        }
        return true;
    }

    private async Task ReceiveFileAsync(SessionState state, string argument, StreamWriter writer, bool append)
    {
        string path = Local(Normalize(state, argument));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        StoreCounts.AddOrUpdate(Normalize(state, argument), 1, static (_, count) => count + 1);

        // "一次只让传一个文件"的服务器:控制连接开几条都行,但第二个传输直接 450 顶回去。
        int live = Interlocked.Increment(ref _liveTransfers);
        if (MaxConcurrentTransfers > 0 && live > MaxConcurrentTransfers)
        {
            Interlocked.Decrement(ref _liveTransfers);
            Interlocked.Increment(ref RejectedTransfers);
            await writer.WriteLineAsync("450 Transfer busy, only one transfer at a time").ConfigureAwait(false);
            return;
        }
        try
        {
            int? resetAfter = ResetStoreAfterBytes?.Invoke(Normalize(state, argument));
            await ReceiveFileCoreAsync(state, path, writer, append, resetAfter).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _liveTransfers);
        }
    }

    private static async Task ReceiveFileCoreAsync(SessionState state, string path, StreamWriter writer, bool append, int? resetAfter)
    {
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        await using (FileStream target = append || state.RestartOffset > 0
                         ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None)
                         : new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (resetAfter is int limit)
            {
                // 收满 limit 个字节就掐断:客户端还在往数据连接里写,看到的是一次进行中的写被复位。
                byte[] buffer = new byte[Math.Max(1, limit)];
                int received = 0;
                while (received < limit)
                {
                    int read = await data.ReadAsync(buffer.AsMemory(received, limit - received)).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                }
                await target.WriteAsync(buffer.AsMemory(0, received)).ConfigureAwait(false);
                ((DataStream)data).Reset();
                state.RestartOffset = 0;
                state.CloseData();
                await writer.WriteLineAsync("426 Connection closed; transfer aborted").ConfigureAwait(false);
                return;
            }
            await data.CopyToAsync(target).ConfigureAwait(false);
        }
        state.RestartOffset = 0;
        state.CloseData();
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    /// <summary>LIST 用的「服务器墙上时间」:默认本机时区,配了 <see cref="ListClockOffset" /> 就按那个偏移。</summary>
    private DateTime ListClock(DateTime utc) =>
        ListClockOffset is { } offset ? DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified) : utc.ToLocalTime();

    private async Task SendMachineListingAsync(SessionState state, string argument, StreamWriter writer)
    {
        string local = Local(Normalize(state, argument));
        if (!Directory.Exists(local))
        {
            await writer.WriteLineAsync("550 No such directory").ConfigureAwait(false);
            return;
        }
        var payload = new StringBuilder();
        foreach (string entry in Directory.GetDirectories(local).Concat(Directory.GetFiles(local)))
        {
            payload.Append(MachineFacts(entry)).Append(' ').Append(Path.GetFileName(entry)).Append("\r\n");
        }
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        {
            await data.WriteAsync(Encoding.UTF8.GetBytes(payload.ToString())).ConfigureAwait(false);
        }
        state.CloseData();
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    private long ListingSize(string localPath, long length) =>
        ListingSizeForPath?.Invoke("/" + Path.GetRelativePath(Root, localPath).Replace('\\', '/'), length) ?? length;

    /// <summary>MLSD / MLST 的事实串;<c>modify</c> 按 RFC 3659 恒为 UTC。</summary>
    private string MachineFacts(string localPath)
    {
        string modify = File.GetLastWriteTimeUtc(localPath).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return Directory.Exists(localPath)
            ? $"type=dir;modify={modify};"
            : $"type=file;size={ListingSize(localPath, new FileInfo(localPath).Length)};modify={modify};";
    }

    /// <summary>Unix 风格的 LIST 行:权限、链接数、属主、属组、大小、时间、名称。</summary>
    private static string FormatEntry(string name, long length, DateTime modified, bool isDirectory)
    {
        string permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--";
        string timestamp = modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{permissions}   1 deploy   staff {length,12} {timestamp} {name}";
    }

    /// <summary>把客户端给的相对/绝对路径规整成以 / 开头的远端绝对路径。</summary>
    private static string Normalize(SessionState state, string path)
    {
        string candidate = string.IsNullOrWhiteSpace(path)
            ? state.WorkingDirectory
            : path.StartsWith('/') ? path : $"{state.WorkingDirectory.TrimEnd('/')}/{path}";
        var segments = new List<string>();
        foreach (string segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>远端绝对路径 → 本地磁盘路径(始终限制在根目录内)。</summary>
    private string Local(string remote) =>
        Path.GetFullPath(Path.Combine(Root, remote.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

    private sealed class SessionState(CancellationToken cancellationToken) : IDisposable
    {
        public string WorkingDirectory { get; set; } = "/";
        public long RestartOffset { get; set; }
        // RFC 959 新控制连接默认 TYPE A;不假定客户端的 LIST 一定使用 ASCII。
        public bool Binary { get; set; }
        public string? RenameFrom { get; set; }
        public TcpListener? DataListener { get; set; }

        public async Task<Stream> AcceptDataAsync()
        {
            TcpListener listener = DataListener ?? throw new InvalidOperationException("No PASV/EPSV issued.");
            TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            return new DataStream(client);
        }

        public void CloseData()
        {
            // A rejected RETR/STOR can leave a connected client in the accept queue.
            // Discard it without releasing the session's passive port.
            while (DataListener is { } listener && listener.Pending())
            {
                listener.AcceptTcpClient().Dispose();
            }
        }

        public void Dispose()
        {
            CloseData();
            DataListener?.Stop();
            DataListener = null;
        }
    }

    /// <summary>数据连接:释放流即关闭连接(FTP 用连接关闭表示传输结束)。</summary>
    private sealed class DataStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public void Reset()
        {
            client.Client.LingerState = new LingerOption(true, 0);
            client.Close();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
