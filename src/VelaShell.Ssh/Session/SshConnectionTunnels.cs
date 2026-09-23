// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §7.2   direct-tcpip
//   OpenSSH PROTOCOL  direct-streamlocal@openssh.com
//   行为规格:       velashell-docs/zh/ssh/spec/07-forwarding.md §一、§2.1

using System.Buffers;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

/// <summary>直接开一条到远端端点的隧道。</summary>
public static class SshConnectionTunnels
{
    /// <summary>开一条到远端 TCP 端点的隧道。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="host">
    /// 目标主机。<b>从服务端视角解析</b> —— <c>localhost</c> 指的是服务端的环回。
    /// </param>
    /// <param name="port">目标端口。</param>
    /// <param name="originatorHost">本机发起方地址。</param>
    /// <param name="originatorPort">本机发起方端口。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// <b>这条路不在本机开监听端口。</b>流直接交给调用方，同机的其它进程连不上去。
    /// </para>
    /// <para>
    /// 接内网 HTTP API 这类端点时，这才是该用的形态 ——
    /// 开一个 <c>-L</c> 监听意味着同机任何进程都能连上去。
    /// </para>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §2.1〕<b>originator 如实填写。</b>
    /// 服务端会把它写进日志；填假的会让管理员无法追溯，
    /// 而且没有任何隐私收益 —— 服务端本来就知道我们的连接来源。
    /// </para>
    /// </remarks>
    public static ValueTask<SshChannel> OpenTcpTunnelAsync(
        this SshConnection connection,
        string host,
        int port,
        string originatorHost = "127.0.0.1",
        int originatorPort = 0,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(host);
        writer.WriteUInt32((uint)port);
        writer.WriteUtf8String(originatorHost);
        writer.WriteUInt32((uint)originatorPort);

        return connection.OpenChannelAsync(
            SshAlgorithmNames.ChannelDirectTcpIp, payload.WrittenMemory, options, cancellationToken);
    }

    /// <summary>开一条到远端 <b>Unix 套接字</b>的隧道。</summary>
    /// <param name="connection">会话。</param>
    /// <param name="socketPath">远端套接字路径。</param>
    /// <param name="options">通道参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 接 <c>/var/run/docker.sock</c> 这类端点的唯一正路：
    /// 它<b>不在本机开监听端口</b>，流直接交给调用方。
    /// 对 root 等价的端点，这个区别不是优化而是前提 ——
    /// 开一个本地端口等于把 docker 的控制权交给同机的每一个进程。
    /// </remarks>
    public static ValueTask<SshChannel> OpenUnixSocketTunnelAsync(
        this SshConnection connection,
        string socketPath,
        SshChannelOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(socketPath);

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUtf8String(socketPath);
        writer.WriteUtf8String("");   // reserved
        writer.WriteUInt32(0);        // reserved

        return connection.OpenChannelAsync(
            SshAlgorithmNames.ChannelDirectStreamLocal, payload.WrittenMemory, options, cancellationToken);
    }
}
