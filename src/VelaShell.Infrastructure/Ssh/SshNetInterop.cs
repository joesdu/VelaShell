using Renci.SshNet;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// SSH.NET 与 Core 中立类型之间的映射(异常/终端模式/转发端口),供各包装器共用。
/// 库类型只在 Infrastructure 内流动;更换底层库时替换本文件的对应映射。
/// </summary>
internal static class SshNetInterop
{
    /// <summary>SSH.NET 异常 → Core 中立异常;不认识的类型返回 null(调用方原样上抛)。</summary>
    public static Exception? Translate(Exception ex)
    {
        return ex switch
        {
            Renci.SshNet.Common.SftpPermissionDeniedException => new SftpPermissionDeniedException(ex.Message, ex),
            Renci.SshNet.Common.SftpPathNotFoundException => new SftpPathNotFoundException(ex.Message, ex),
            Renci.SshNet.Common.SftpException => new SftpOperationException(ex.Message, ex),
            Renci.SshNet.Common.SshAuthenticationException => new SshAuthenticationException(ex.Message, ex),
            Renci.SshNet.Common.SshOperationTimeoutException => new SshOperationTimeoutException(ex.Message, ex),
            Renci.SshNet.Common.SshConnectionException => new SshConnectionException(ex.Message, ex),
            Renci.SshNet.Common.SshException => new SshClientException(ex.Message, ex),
            _ => null
        };
    }

    /// <summary>中立终端模式表 → SSH.NET 字典(操作码数值一一对应,直接转换)。</summary>
    public static IDictionary<Renci.SshNet.Common.TerminalModes, uint>? MapTerminalModes(IReadOnlyDictionary<TerminalMode, uint>? modes)
    {
        if (modes is null)
        {
            return null;
        }
        return modes.ToDictionary(pair => (Renci.SshNet.Common.TerminalModes)(byte)pair.Key, pair => pair.Value);
    }

    /// <summary>中立转发请求 → SSH.NET 的 ForwardedPort 实例。</summary>
    public static ForwardedPort CreateForwardedPort(PortForwardRequest request)
    {
        return request.Kind switch
        {
            PortForwardKind.Local => new ForwardedPortLocal(request.BoundHost,
                request.BoundPort,
                request.TargetHost ?? throw new ArgumentException(@"Local forward requires a target host.", nameof(request)),
                request.TargetPort ?? throw new ArgumentException(@"Local forward requires a target port.", nameof(request))),
            PortForwardKind.Remote => new ForwardedPortRemote(request.BoundHost,
                request.BoundPort,
                request.TargetHost ?? throw new ArgumentException(@"Remote forward requires a target host.", nameof(request)),
                request.TargetPort ?? throw new ArgumentException(@"Remote forward requires a target port.", nameof(request))),
            PortForwardKind.Dynamic => new ForwardedPortDynamic(request.BoundHost, request.BoundPort),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, @"Unknown port forward kind.")
        };
    }
}
