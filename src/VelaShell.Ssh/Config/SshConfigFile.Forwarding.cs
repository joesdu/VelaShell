// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  LocalForward / RemoteForward / DynamicForward / GatewayPorts / ExitOnForwardFailure(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.3

using System.Net;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Config;

public static partial class SshConfigFile
{
    /// <summary>
    /// 起配置里的全部转发（<see cref="SshHostConfig.GetForwards"/>），返回起来了的那些。
    /// </summary>
    /// <param name="connection">已经连好的连接。</param>
    /// <param name="config">这台主机的配置。</param>
    /// <param name="onFailure">某一条没起来（<c>ExitOnForwardFailure</c> 没开时）：哪一条、为什么。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>起来了的转发器；由调用方释放。</returns>
    /// <exception cref="SshConnectException">配置写法不对（<see cref="SshFailureReason.InvalidConfiguration"/>）—— 一条都不起。</exception>
    /// <exception cref="SshForwardException">
    /// <c>ExitOnForwardFailure yes</c> 而有一条没起来：已经起来的全部释放之后抛出，消息里有那一条的原文。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/09 §7.3〕没写监听地址的本地 / 动态转发绑环回，<c>GatewayPorts yes</c> 时绑全部网卡；
    /// 写了 <c>*</c> 或空（<c>:8080</c>）是全部网卡，<c>localhost</c> 是环回。远程转发的监听地址原样交给服务端（没写是 <c>localhost</c>）。
    /// 远程转发不给目标是远程动态转发，放行名单取 <see cref="SshHostConfig.GetPermitRemoteOpen"/>。
    /// </para>
    /// <para>
    /// 远程转发两头各自可以是端口或套接字（spec/07 §4.4）。只有不给目标、却在套接字上监听的 <c>RemoteForward /路径</c>
    /// （远程动态转发只有端口一种写法）照「这一条没起来」处理。
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<PortForwarder>> StartForwardsAsync(
        SshConnection connection,
        SshHostConfig config,
        Action<SshConfigForward, Exception>? onFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(config);

        // 写法先全部查一遍：写错是配置错误，一条都不起。
        IReadOnlyList<SshConfigForward> forwards = config.GetForwards();
        RemoteOpenPolicy? permit = forwards.Any(f => f.IsRemoteDynamic) ? config.GetPermitRemoteOpen() : null;

        List<PortForwarder> started = [];
        foreach (SshConfigForward forward in forwards)
        {
            try
            {
                started.Add(await StartForwardAsync(connection, config, forward, permit, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SshException or ArgumentException or IOException)
            {
                if (config.ExitOnForwardFailure)
                {
                    foreach (PortForwarder forwarder in started)
                    {
                        await forwarder.DisposeAsync().ConfigureAwait(false);
                    }
                    throw new SshForwardException(SshFailureReason.ForwardSetupFailed,
                        $"「{PeerText.Sanitize(forward.Line, 200)}」没起来（ExitOnForwardFailure yes，已起的转发全部撤掉）：{ex.Message}", ex)
                    {
                        FailureKind = SshForwardFailureKind.ConfigForwardFailed,
                        ConfigLine = forward.Line,
                    };
                }
                onFailure?.Invoke(forward, ex);
            }
        }
        return started;
    }

    private static async Task<PortForwarder> StartForwardAsync(
        SshConnection connection, SshHostConfig config, SshConfigForward forward, RemoteOpenPolicy? permit, CancellationToken cancellationToken)
    {
        switch (forward.Kind)
        {
            case SshConfigForwardKind.Local or SshConfigForwardKind.Dynamic:
                LocalPortForwardOptions local = forward.ListenSocketPath is { } socket
                    ? new LocalPortForwardOptions { ListenSocketPath = socket }
                    : new LocalPortForwardOptions { BindAddress = LocalBindAddress(forward.ListenAddress, config.GatewayPorts), BindPort = forward.ListenPort };
                return forward.Kind == SshConfigForwardKind.Dynamic
                    ? LocalPortForwarder.StartDynamic(connection, local)
                    : forward.TargetSocketPath is { } remoteSocket
                        ? LocalPortForwarder.StartToUnixSocket(connection, remoteSocket, local)
                        : LocalPortForwarder.Start(connection, forward.TargetHost!, forward.TargetPort, local);

            default:
                if (forward.IsRemoteDynamic && forward.ListenSocketPath is not null)
                {
                    // 远程动态转发只有端口一种写法（spec/07 §4.4）。
                    throw Unsupported(forward, "服务端在套接字上监听的远程动态转发");
                }

                // 〔spec/07 §4.4〕两头各自是端口或套接字：服务端那头写进参数（含 / 的进 ListenSocketPath），本机那头决定起法。
                RemotePortForwardOptions remote = new()
                {
                    BindAddress = forward.ListenAddress ?? "localhost",
                    BindPort = forward.ListenPort,
                    ListenSocketPath = forward.ListenSocketPath,
                };
                return forward.IsRemoteDynamic
                    ? await RemotePortForwarder.StartDynamicAsync(connection, permit!, remote, cancellationToken).ConfigureAwait(false)
                    : forward.TargetSocketPath is { } localSocket
                        ? await RemotePortForwarder.StartToUnixSocketAsync(connection, localSocket, remote, cancellationToken).ConfigureAwait(false)
                        : await RemotePortForwarder.StartAsync(connection, forward.TargetHost!, forward.TargetPort, remote, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 本地转发的监听地址：没写 → 两个环回（<c>GatewayPorts yes</c> 时全部网卡）；<c>*</c> → 全部网卡；<c>localhost</c> → 两个环回（<see langword="null"/>，见 <see cref="LocalPortForwardOptions.BindAddress"/>）。
    /// </summary>
    private static IPAddress? LocalBindAddress(string? address, bool gatewayPorts) => address switch
    {
        null => gatewayPorts ? IPAddress.Any : null,
        "*" => IPAddress.Any,
        _ when address.Equals("localhost", StringComparison.OrdinalIgnoreCase) => null,
        _ => IPAddress.TryParse(address, out IPAddress? parsed)
            ? parsed
            : throw new SshConnectException(SshFailureReason.InvalidConfiguration, SshPhase.Dialing,
                $"本地转发的监听地址「{PeerText.Sanitize(address, 64)}」不是 IP 地址（也不是 * / localhost）。"),
    };

    private static SshForwardException Unsupported(SshConfigForward forward, string what) =>
        new(SshFailureReason.Unsupported, $"「{PeerText.Sanitize(forward.Line, 200)}」：本库还不支持{what}的转发。");
}
