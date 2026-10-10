// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(传输与协议无关);
//   本地传输的约定:显示号 N 的服务端监听 /tmp/.X11-unix/XN(Linux 另有抽象命名空间里的同名套接字,
//   Xlib / XCB 对 DISPLAY=:N 先试它),与 X.Org 的 Xtrans 行为一致。
//
//   本机客户端(DISPLAY=:N)走这里,比 TCP 快,也不必开端口。连进来的一律算本机连接;是不是「运行服务端的这个用户」:
//   套接字文件在 Listen 之前就改成 0600,连得上的只有属主(与 root);抽象命名空间没有文件权限;取得到对端 uid 时一律以 uid 为准(见 IsLocalUser)。
//   名字被别人占着时整个显示号不用(StartAsync 抛异常),放套接字文件的目录属主不可信时不开套接字文件 —— 见 StartUnixListeners。

using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private readonly List<(Socket Socket, string? File)> _unixListeners = [];

    /// <summary>Unix 套接字的路径:选项给了就用它,否则 Windows 以外默认 /tmp/.X11-unix/X{N};空字符串 = 不监听。</summary>
    private string? UnixSocketPath => _options.UnixSocketPath switch
    {
        "" => null,
        { } path => path,
        null when OperatingSystem.IsWindows() => null,
        null => $"/tmp/.X11-unix/X{_options.DisplayNumber}",
    };

    /// <summary>
    /// 开 Unix 套接字(Linux 上先开抽象名,再开套接字文件)。
    /// </summary>
    /// <remarks>
    /// 名字被别人占着 —— 抽象名已经有人 bind、套接字文件后面有人在听、或者是别的用户留下而删不掉的 —— 时抛
    /// <see cref="SocketException" />(<see cref="SocketError.AddressAlreadyInUse" />):这个显示号不能用。原先只记一行日志、
    /// 照样用别的传输开起来,<see cref="Display" /> 照样是 <c>:N</c>:攻击者先 bind 抽象名不 listen(我们的探测连过去被拒、判为空闲),
    /// 等我们开起来再 listen,Xlib / XCB 对 <c>:N</c> 先试抽象名,建立报文里带着 <c>.Xauthority</c> 里的 cookie 就交到了它手上,
    /// 它再拿 cookie 经 TCP 连进真服务端。放套接字文件的目录不可信(<see cref="SocketDirectoryProblem" />)时不开套接字文件、只记日志。
    /// </remarks>
    private void StartUnixListeners(CancellationToken cancellationToken)
    {
        if (UnixSocketPath is not { } path || !Socket.OSSupportsUnixDomainSockets)
        {
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            // 抽象命名空间:不落文件,进程退出自动消失;Xlib / XCB 对 :N 先试它。
            TryListen("\0" + path, file: null, cancellationToken);
        }
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows() && directory == "/tmp/.X11-unix")
                {
                    // 与 X.Org 一致:目录人人可写、带粘滞位,别的用户的服务端也能在这里建自己的套接字。
                    File.SetUnixFileMode(directory, (UnixFileMode)0x3FF);   // 八进制 1777
                }
            }
            // 建完再核:别人可能抢在 CreateDirectory 之前把目录(或指向别处的符号链接)建好了,CreateDirectory 对已有的目录不报错。
            if (directory is not null && !OperatingSystem.IsWindows() && SocketDirectoryProblem(directory) is { } problem)
            {
                Log($"Unix socket {path}: {problem}; not listening on it");
                return;
            }
            if (File.Exists(path))
            {
                // 有人在听(桌面自己的 Xorg 通常不开 TCP,TCP 那一侧的占用检查看不出它):不碰,这个显示号不能用。
                // 没人应答才是上次没收拾干净的残留,删掉重建;删不掉(别的用户的文件)同样不能用 —— 它的属主随时可以再 listen。
                if (IsUnixSocketLive(path))
                {
                    throw NameTaken(path, "another X server is listening on it");
                }
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw NameTaken(path, $"a stale socket cannot be removed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Unix socket {path} unavailable: {ex.Message}");
            return;
        }
        TryListen(path, path, cancellationToken);
    }

    /// <summary>记一行「名字被占、这个显示号不能用」,返回要抛的异常。</summary>
    private SocketException NameTaken(string endpoint, string why)
    {
        Log($"Unix socket {EndpointText(endpoint)} is taken ({why}): display :{_options.DisplayNumber} is unusable");
        return new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    /// <summary>日志里的写法:抽象名按惯例写成 <c>@/tmp/.X11-unix/XN</c>。</summary>
    private static string EndpointText(string endpoint) => endpoint.StartsWith('\0') ? "@" + endpoint[1..] : endpoint;

    /// <summary>
    /// 放套接字文件的目录可不可信;不可信时返回原因,可信(或者这个平台查不了属主)时返回 null。
    /// </summary>
    /// <remarks>
    /// 目录的属主能把里面的条目改名、删掉,粘滞位也拦不住属主:属主是别的用户的话,它随时能把我们的 0600 套接字挪走、换上自己的,
    /// 用 <c>:N</c> 连的本机客户端就把 cookie 交给了它(macOS 上 <c>/tmp/.X11-unix</c> 通常不存在,谁先建谁就是属主;
    /// Linux 上开机时没建它的发行版同样)。要求:本身是目录、不是符号链接;属主是本用户,或者是 root 且带粘滞位(<c>/tmp/.X11-unix</c> 的标准形态)。
    /// 只在 Linux(statx)与 macOS(lstat)上查;别的平台返回 null,照旧开。
    /// </remarks>
    internal static string? SocketDirectoryProblem(string directory)
    {
        if (LStat(directory) is not { } stat || EffectiveUid is not { } self)
        {
            return null;
        }
        const uint typeMask = 0xF000, directoryType = 0x4000, sticky = 0x200;   // S_IFMT、S_IFDIR、S_ISVTX
        if ((stat.Mode & typeMask) != directoryType)
        {
            return $"{directory} is not a directory (a symbolic link?)";
        }
        if (stat.Uid == self || (stat.Uid == 0 && (stat.Mode & sticky) != 0))
        {
            return null;
        }
        return stat.Uid == 0
            ? $"{directory} is owned by root but lacks the sticky bit"
            : $"{directory} is owned by another user (uid {stat.Uid})";
    }

    /// <summary>本进程的有效 uid(Linux、macOS、FreeBSD);别的平台为 null。</summary>
    private static readonly uint? EffectiveUid =
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? GetEffectiveUid() : null;

    /// <summary>
    /// 不跟随符号链接地取路径的属主与 st_mode。查不了(不是 Linux / macOS、libc 没有这个函数、内核或 seccomp 不给 statx)时返回 null;
    /// 取失败(不存在、没权限)时抛 <see cref="IOException" />。
    /// </summary>
    private static unsafe (uint Uid, uint Mode)? LStat(string path)
    {
        byte* buffer = stackalloc byte[512];   // struct statx 256 字节;macOS 的 struct stat 144 字节
        int result;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // statx 的结构各架构一样(stx_uid 在 20、stx_mode 在 28);struct stat 的布局随架构变,glibc 2.33 之前也不导出 stat。
                const int atFdCwd = -100, atSymlinkNoFollow = 0x100;
                const uint statxType = 0x1, statxMode = 0x2, statxUid = 0x8;
                result = StatX(atFdCwd, path, atSymlinkNoFollow, statxType | statxMode | statxUid, buffer);
                if (result == 0)
                {
                    uint mask = *(uint*)buffer;
                    return (mask & (statxType | statxMode | statxUid)) == (statxType | statxMode | statxUid)
                        ? (*(uint*)(buffer + 20), *(ushort*)(buffer + 28))
                        : null;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                // 64 位 inode 的 struct stat:st_mode(16 位)在 4、st_uid 在 16。x64 上它的符号是 lstat$INODE64,arm64 上就是 lstat。
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatInode64(path, buffer) : LStatDarwin(path, buffer);
                if (result == 0)
                {
                    return (*(uint*)(buffer + 16), *(ushort*)(buffer + 4));
                }
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        int errno = Marshal.GetLastPInvokeError();
        const int enosys = 38;   // Linux:内核太老,或容器的 seccomp 不放行 statx
        return OperatingSystem.IsLinux() && errno == enosys
            ? null
            : throw new IOException($"cannot stat {path} (errno {errno})");
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatX(int directoryFd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int LStatDarwin(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int LStatInode64(string path, byte* buffer);

    /// <summary>
    /// 这个 Unix 套接字文件后面有没有进程在听。限时 300 毫秒:Linux 上对 backlog 已满的 AF_UNIX 流套接字做阻塞 connect 会一直等 ——
    /// 原先同步 Connect、不设时限,本机任何用户布置一个 backlog 满的套接字就能让 StartAsync 永远卡在这里。到时限按「有人占着」算。
    /// </summary>
    internal static bool IsUnixSocketLive(string path)
    {
        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using CancellationTokenSource limit = new(TimeSpan.FromMilliseconds(300));
        try
        {
            probe.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain)
        {
            return true;   // 有人在听、backlog 满了(Linux 上非阻塞 connect 回 EAGAIN):占着
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private void TryListen(string endpoint, string? file, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool ownerOnly = false;
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(endpoint));
            // 在 Listen 之前把套接字文件改成只有属主能读写:连接要对套接字文件有写权限,于是连得上的只有这个用户(与 root),
            // 也就不必再要 cookie。还没 Listen,改权限之前没有人连得进来。改不了(文件系统不支持)就照常要 cookie。
            if (file is not null && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    ownerOnly = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Unix socket {file}: cannot restrict permissions ({ex.Message}); clients need the cookie");
                }
            }
            socket.Listen(64);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // 抽象名被人 bind 了(不必 listen),或者套接字文件在我们删掉之后又被人建了回来。
                throw NameTaken(endpoint, "someone else has bound it");
            }
            Log($"Unix socket {EndpointText(endpoint)} unavailable: {ex.SocketErrorCode}");
            return;
        }
        _unixListeners.Add((socket, file));
        _ = AcceptUnixLoopAsync(socket, ownerOnly, cancellationToken);
    }

    private async Task AcceptUnixLoopAsync(Socket listener, bool ownerOnly, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested || listener.SafeHandle.IsClosed)
                {
                    return;   // 监听已经关了
                }
                await AcceptFailedAsync("Unix socket", ex, cancellationToken).ConfigureAwait(false);   // 暂时的(fd 用完之类):接着接
                continue;
            }
            TrackConnection(ServeUnixAsync(connection, ownerOnly, cancellationToken));
        }
    }

    /// <param name="connection">接进来的连接。</param>
    /// <param name="ownerOnly">它连的是只有属主能连的套接字文件(见 <see cref="TryListen" />)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ServeUnixAsync(Socket connection, bool ownerOnly, CancellationToken cancellationToken)
    {
        (uint? peerUid, int peerPid) = PeerCredentials(connection);
        bool localUser = IsLocalUser(ownerOnly, peerUid, EffectiveUid);
        await using NetworkStream stream = new(connection, ownsSocket: true);
        if (!localUser && peerUid is not null && _cookie is null)
        {
            return;   // 别的用户、又没配 cookie:授权必然失败,accept 时就关掉,不让它占握手的名额
        }
        try
        {
            await ServeCoreAsync(stream, new Peer(IsLocal: true, SameHost: SharesMemoryWith(peerPid), peerUid, localUser, Pid: peerPid,
                Authenticated: false), cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 服务端正在收工。
        }
    }

    /// <summary>
    /// 连进来的是不是运行服务端的这个用户:取到对端 uid 时<b>以 uid 为准</b>,取不到才看连的是不是只有属主能连的套接字文件。
    /// 原先两者取「或」:自定义路径落在 9p / drvfs(WSL 挂进来的 Windows 盘)之类的文件系统上时,chmod 0600 不报错却不生效,
    /// 谁都连得上,别的用户也被当成了本用户、不要 cookie。
    /// </summary>
    internal static bool IsLocalUser(bool ownerOnly, uint? peerUid, uint? self) => peerUid is { } uid ? uid == self : ownerOnly;

    /// <summary>
    /// 连接对端的 uid 与 pid:Linux 上经 SO_PEERCRED(struct ucred:pid、uid、gid 各 4 字节;pid 已换算到本进程所在的 pid 命名空间,
    /// 对端在那里看不见时为 0),macOS / FreeBSD 上经 getpeereid(uid),macOS 另经 LOCAL_PEERPID 取 pid。取不到的为 null / 0。
    /// </summary>
    private static (uint? Uid, int Pid) PeerCredentials(Socket connection)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> credentials = stackalloc byte[12];
                int length = connection.GetRawSocketOption(1, 17, credentials);   // SOL_SOCKET、SO_PEERCRED
                return length >= 8 ? (BitConverter.ToUInt32(credentials[4..]), BitConverter.ToInt32(credentials)) : (null, 0);
            }
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                uint? uid = GetPeerEid((int)connection.Handle, out uint peer, out _) == 0 ? peer : null;
                int pid = 0;
                if (OperatingSystem.IsMacOS())
                {
                    try
                    {
                        Span<byte> value = stackalloc byte[4];
                        pid = connection.GetRawSocketOption(0, 2, value) >= 4 ? BitConverter.ToInt32(value) : 0;   // SOL_LOCAL、LOCAL_PEERPID
                    }
                    catch (SocketException)
                    {
                        // 取不到 pid 不影响 uid(授权只看 uid)
                    }
                }
                return (uid, pid);
            }
        }
        catch (Exception ex) when (ex is SocketException or EntryPointNotFoundException or DllNotFoundException)
        {
        }
        return (null, 0);
    }

    /// <summary>
    /// 这条 Unix 套接字连接的对端能不能与服务端共享 SysV 内存(MIT-SHM 只对这样的客户端可见,见 <c>XClient.SameHost</c>)。
    /// Linux 上要求对端与服务端在同一个 IPC 命名空间:shmid 按服务端自己的命名空间解释 —— 把 <c>/tmp/.X11-unix</c> 挂进容器后,
    /// 容器里同一个 uid 的进程给的 shmid 指的是宿主这边的段,原先它能借服务端之手读写宿主用户的 SysV 段。
    /// 核对不了(取不到 pid、读不了 <c>/proc/&lt;pid&gt;/ns/ipc</c>)也算不同。别的平台不提供 MIT-SHM,照旧为 true。
    /// </summary>
    private static bool SharesMemoryWith(int pid) => !OperatingSystem.IsLinux() || SameIpcNamespace(pid);

    /// <summary>进程 <paramref name="pid" /> 与本进程在同一个 IPC 命名空间里(比较 <c>/proc/…/ns/ipc</c> 的链接目标);核对不了时为 false。</summary>
    internal static bool SameIpcNamespace(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            string? mine = new FileInfo("/proc/self/ns/ipc").LinkTarget;
            string? theirs = new FileInfo($"/proc/{pid}/ns/ipc").LinkTarget;
            return mine is not null && mine == theirs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "getpeereid")]
    private static partial int GetPeerEid(int socket, out uint effectiveUid, out uint effectiveGid);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();

    private void StopUnixListeners()
    {
        foreach ((Socket socket, string? file) in _unixListeners)
        {
            socket.Dispose();
            if (file is not null)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 删不掉就留着;下次启动会先删。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        _unixListeners.Clear();
    }

    // ------------------------------------------------------------------ 显示号锁(/tmp/.X{N}-lock)

    /// <summary>持着的显示号锁文件;没持着为 null。</summary>
    private string? _displayLock;

    /// <summary>
    /// Xserver(1) 的约定:显示号 N 的服务端持有 <c>/tmp/.X{N}-lock</c>,内容是自己的 PID(十位右对齐、换行结尾)。
    /// Xvfb、<c>xvfb-run -a</c> 挑显示号时只看它 —— 原先不建,它们会挑中我们正在用的号;我们也不看它,会挑中 Xvfb 的号。
    /// 只在类 Unix 上、监听着与显示号挂钩的传输(TCP 6000 + N,或默认的 <c>/tmp/.X11-unix/XN</c>)时持有。
    /// 文件在、持有者还活着(或者内容认不出)时抛 <see cref="SocketException" />(<see cref="SocketError.AddressAlreadyInUse" />);
    /// 持有者已经不在了是上次没收拾干净的残留,删掉重建;<c>/tmp</c> 写不了只记日志,照常开。
    /// </summary>
    private void ClaimDisplayLock()
    {
        if (OperatingSystem.IsWindows() || !(ListensOnTcp || _options.UnixSocketPath is null))
        {
            return;
        }
        string path = $"/tmp/.X{_options.DisplayNumber}-lock";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                FileStreamOptions create = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,   // 0444
                };
                using (FileStream stream = new(path, create))
                {
                    stream.Write(System.Text.Encoding.ASCII.GetBytes($"{Environment.ProcessId,10}\n"));
                }
                _displayLock = path;
                return;
            }
            catch (IOException) when (File.Exists(path))
            {
                if (DisplayLockHolderAlive(path))
                {
                    throw DisplayTaken(path, "another X server holds it");
                }
                try
                {
                    File.Delete(path);   // 持有者已经不在了
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw DisplayTaken(path, $"a stale lock cannot be removed: {ex.Message}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"display lock {path} unavailable: {ex.Message}");
                return;
            }
        }
        throw DisplayTaken(path, "it keeps reappearing");
    }

    /// <summary>锁文件的持有者还在不在;内容认不出、读不了也算在(不是我们的东西不碰)。</summary>
    private static bool DisplayLockHolderAlive(string path)
    {
        try
        {
            if (!int.TryParse(File.ReadAllText(path).Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            {
                return true;
            }
            using var holder = System.Diagnostics.Process.GetProcessById(pid);
            return !holder.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // 没有这个进程
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return true;
        }
    }

    private SocketException DisplayTaken(string lockPath, string why)
    {
        Log($"display :{_options.DisplayNumber} is unusable: {lockPath} {why}");
        return new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    /// <summary>放掉显示号锁(收工,或启动半途失败)。</summary>
    private void ReleaseDisplayLock()
    {
        if (Interlocked.Exchange(ref _displayLock, null) is not { } path)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着:内容是我们的 PID,进程退出之后别人会当残留清掉。
        }
    }
}
