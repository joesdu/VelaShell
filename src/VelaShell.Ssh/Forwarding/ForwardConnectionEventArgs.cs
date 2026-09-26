// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

using System.Net;

namespace VelaShell.Ssh.Forwarding;

/// <summary>一条转发连接的事件。</summary>
/// <remarks>
/// 它继承 <see cref="EventArgs"/> 而不是写成 record —— C# 不允许 record
/// 继承普通类，而 <c>*EventArgs</c> 这个名字按约定就该是一个 <see cref="EventArgs"/>。
/// 字节数的方向与 <see cref="PortForwarder.BytesSent"/> / <see cref="PortForwarder.BytesReceived"/> 相同：从本机的视角说。
/// </remarks>
public sealed class ForwardConnectionEventArgs : EventArgs
{
    /// <summary>创建一条连接事件。</summary>
    /// <param name="connectionId">这条连接在本转发器内的序号。</param>
    /// <param name="source">来源端点。</param>
    /// <param name="target">目标描述。</param>
    /// <param name="bytesSent">本机送进隧道的应用字节数。</param>
    /// <param name="bytesReceived">从隧道收回本机的应用字节数。</param>
    /// <param name="duration">持续时间。</param>
    public ForwardConnectionEventArgs(
        long connectionId,
        EndPoint? source,
        string target,
        long bytesSent = 0,
        long bytesReceived = 0,
        TimeSpan duration = default)
    {
        ConnectionId = connectionId;
        Source = source;
        Target = target;
        BytesSent = bytesSent;
        BytesReceived = bytesReceived;
        Duration = duration;
    }

    /// <summary>这条连接在本转发器内的序号。</summary>
    public long ConnectionId { get; }

    /// <summary>来源端点（远程转发的回连没有）。</summary>
    public EndPoint? Source { get; }

    /// <summary>目标描述（<c>host:port</c> 或套接字路径）。</summary>
    public string Target { get; }

    /// <summary>本机送进隧道的应用字节数。</summary>
    public long BytesSent { get; }

    /// <summary>从隧道收回本机的应用字节数。</summary>
    public long BytesReceived { get; }

    /// <summary>持续时间。</summary>
    public TimeSpan Duration { get; }
}
