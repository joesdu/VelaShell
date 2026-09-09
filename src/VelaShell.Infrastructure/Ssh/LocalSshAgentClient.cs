using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// <see cref="ISshAgentClient" /> 的本机实现:Windows 走命名管道,类 Unix 走 unix 域套接字。
/// <para>
/// <b>端点按三级优先取:</b>用户在设置里显式填的 → 环境变量 <c>SSH_AUTH_SOCK</c> → 平台默认。
/// 平台默认只有 Windows 有一个(<see cref="WindowsOpenSshPipe" />,微软 OpenSSH 服务固定用它);
/// 类 Unix 上没有默认可言 —— agent 的套接字名带随机后缀,除了环境变量无处可查。
/// </para>
/// <para>
/// ⚠️ Windows 上 <c>SSH_AUTH_SOCK</c> 常常指向 msys2 / Git Bash / Cygwin 的**伪套接字**:
/// 那是一个带魔法内容的普通文件加一条环回 TCP,不是 AF_UNIX,任何 Win32 程序都连不上。
/// 这正是 <c>AddCredential</c> 那段注释里说的、每次连接都刷一发异常的根源。这里的处置是
/// 连不上就**如实说出原因**并返回不可用,而不是把异常抛给调用方 ——「本机没有 agent」
/// 是常态,不是故障。
/// </para>
/// </summary>
public sealed class LocalSshAgentClient(Func<string?> endpointOverride) : ISshAgentClient
{
    /// <summary>微软 OpenSSH 的 ssh-agent 服务固定监听的命名管道。</summary>
    public const string WindowsOpenSshPipe = @"\\.\pipe\openssh-ssh-agent";

    /// <summary>命名管道路径的前缀(本机)。</summary>
    private const string LocalPipePrefix = @"\\.\pipe\";

    /// <summary>探测与建链的超时:本机 IPC,一秒还不通就是没有。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string?> _endpointOverride = endpointOverride ?? throw new ArgumentNullException(nameof(endpointOverride));

    /// <summary>用平台默认端点构造(不读用户设置);测试与无设置服务的场景用。</summary>
    public LocalSshAgentClient() : this(static () => null)
    {
    }

    /// <inheritdoc />
    public async Task<SshAgentProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        (string endpoint, SshAgentEndpointSource source) = ResolveEndpoint();
        if (endpoint.Length == 0)
        {
            return new(false, string.Empty, SshAgentEndpointSource.None, [], DescribeMissingEndpoint());
        }
        Stream? stream = null;
        try
        {
            stream = await ConnectCoreAsync(endpoint, cancellationToken).ConfigureAwait(false);
            byte[]? answer = await SshAgentRelay
                .ExchangeAsync(stream, SshAgentRelay.BuildRequestIdentities(), cancellationToken)
                .ConfigureAwait(false);
            // 连上了但答不出身份列表:多半是端点被别的东西占着(同名管道不等于 agent)。
            return answer is null
                       ? new(false, endpoint, source, [], "SSH agent did not answer the identity request.")
                       : new(true, endpoint, source, SshAgentProtocol.ParseIdentities(answer), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, endpoint, source, [], DescribeConnectFailure(endpoint, ex));
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken = default)
    {
        (string endpoint, _) = ResolveEndpoint();
        return endpoint.Length == 0
                   ? throw new InvalidOperationException(DescribeMissingEndpoint())
                   : ConnectCoreAsync(endpoint, cancellationToken);
    }

    /// <summary>
    /// 解析要用的端点及其来源。空串表示本机没有可用端点。
    /// </summary>
    /// <returns>端点与来源。</returns>
    /// <remarks>
    /// 每次调用都重新解析(而不是构造时定下来):用户在设置里改了端点、或者中途把
    /// ssh-agent 服务起起来,不该要求重启应用才认。
    /// </remarks>
    internal (string Endpoint, SshAgentEndpointSource Source) ResolveEndpoint()
    {
        if (_endpointOverride()?.Trim() is { Length: > 0 } configured)
        {
            return (configured, SshAgentEndpointSource.UserConfigured);
        }
        if (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK")?.Trim() is { Length: > 0 } fromEnv)
        {
            return (fromEnv, SshAgentEndpointSource.Environment);
        }
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                   ? (WindowsOpenSshPipe, SshAgentEndpointSource.PlatformDefault)
                   : (string.Empty, SshAgentEndpointSource.None);
    }

    /// <summary>按端点形态选管道还是套接字并建链。</summary>
    private static async Task<Stream> ConnectCoreAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        if (TryGetPipeName(endpoint) is { } pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>端点是命名管道时给出管道名(不含 <c>\\.\pipe\</c> 前缀),否则 null。</summary>
    /// <param name="endpoint">端点字符串。</param>
    /// <returns>管道名;不是命名管道时为 <see langword="null" />。</returns>
    /// <remarks>
    /// 只认本机形式。远程管道(<c>\\SERVER\pipe\…</c>)不在支持范围:一个"本机 agent"
    /// 指到别的机器上去,已经不是这个功能要解决的问题了。
    /// </remarks>
    internal static string? TryGetPipeName(string endpoint) =>
        endpoint.StartsWith(LocalPipePrefix, StringComparison.OrdinalIgnoreCase)
            ? endpoint[LocalPipePrefix.Length..]
            : null;

    /// <summary>没有端点时的解释。Windows 与类 Unix 上"该去做什么"完全不同,分开说。</summary>
    private static string DescribeMissingEndpoint() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "No SSH agent endpoint. Start the Windows OpenSSH Authentication Agent service, or set the agent endpoint in Settings → Key management."
            : "No SSH agent endpoint. SSH_AUTH_SOCK is not set — start ssh-agent, or set the agent endpoint in Settings → Key management.";

    /// <summary>
    /// 连不上时的解释。Windows 上把最常见的那一种误配单独点名:<c>SSH_AUTH_SOCK</c>
    /// 指向 msys / Git Bash 的伪套接字,报"文件找不到"其实完全不是那么回事。
    /// </summary>
    private static string DescribeConnectFailure(string endpoint, Exception ex) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TryGetPipeName(endpoint) is null
            ? $"Cannot connect to the SSH agent at '{endpoint}'. On Windows the agent must be a named pipe such as {WindowsOpenSshPipe}; an SSH_AUTH_SOCK left behind by msys2 / Git Bash / Cygwin points at a pseudo-socket that Windows programs cannot open. ({ex.Message})"
            : $"Cannot connect to the SSH agent at '{endpoint}': {ex.Message}";
}
