// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL  ping@openssh.com(SSH2_MSG_PING / SSH2_MSG_PONG)
//   RFC 8308 §2.4     认证之后的第二次 EXT_INFO
//   行为规格:         velashell-docs/zh/ssh/spec/05-connection.md §6.5

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

public sealed partial class SshConnection
{
    /// <summary>在等 PONG 的 PING（按数据里的序号）。</summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _pings = new();

    private ulong _nextPing;
    private int _peerSupportsPing;

    /// <summary>
    /// 对端认传输层的 PING / PONG（在 <c>EXT_INFO</c> 里宣告了 <c>ping@openssh.com</c>，OpenSSH 9.5 起）。
    /// </summary>
    /// <remarks>认了它，<see cref="MeasureRoundTripAsync"/> 用 PING 量；按键时序混淆（<c>SshShellOptions.ObscureKeystrokeTiming</c>）也要它。</remarks>
    public bool PeerSupportsPing
    {
        get => Volatile.Read(ref _peerSupportsPing) != 0;
        internal init => _peerSupportsPing = value ? 1 : 0;
    }

    /// <summary>认证之后的 <c>EXT_INFO</c>：只看 <c>ping@openssh.com</c>（别的扩展这一层用不上）。</summary>
    internal void OnExtensionInfo(ReadOnlyMemory<byte> payload)
    {
        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.ExtInfo);
            uint count = reader.ReadUInt32();
            for (uint i = 0; i < count && i < 256; i++)
            {
                string name = reader.ReadUtf8String(1024);
                _ = reader.ReadString(MaxFieldBytes);
                if (name == SshProtocolNames.ExtPing)
                {
                    Volatile.Write(ref _peerSupportsPing, 1);
                }
            }
        }
        catch (SshWireFormatException)
        {
            // 格式不对的 EXT_INFO 不理（RFC 8308：未知或坏掉的扩展信息不影响连接）。
        }
    }

    /// <summary>对端的 PING：原样回 PONG（<c>string</c> 数据）。</summary>
    internal void OnPing(ReadOnlyMemory<byte> payload)
    {
        byte[] pong = payload.ToArray();
        pong[0] = (byte)SshMessageNumber.Pong;
        Post(pong);
    }

    /// <summary>PONG：数据是我们发的那 8 字节序号就交给等它的测量；别的（对端自己的、过期的）不理。</summary>
    internal void OnPong(ReadOnlyMemory<byte> payload)
    {
        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.Pong);
            ReadOnlySequence<byte> data = reader.ReadString(64);
            if (data.Length == 8 && _pings.TryRemove(BinaryPrimitives.ReadUInt64BigEndian(data.ToArray()), out TaskCompletionSource? waiter))
            {
                waiter.TrySetResult();
            }
        }
        catch (SshWireFormatException)
        {
            // 格式不对的 PONG 不理。
        }
    }

    /// <summary>发一个 PING、等它的 PONG：往返时间（同时记进 <see cref="LastRoundTrip"/>）。</summary>
    private async ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        ulong id = Interlocked.Increment(ref _nextPing);
        TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pings[id] = waiter;
        try
        {
            long startedAt = Time.GetTimestamp();
            await SendAsync(BuildPing(id), cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Disconnected);
            await waiter.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            TimeSpan elapsed = Time.GetElapsedTime(startedAt);
            RecordRoundTrip(elapsed);
            return elapsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Disconnected.IsCancellationRequested)
        {
            throw CloseReason ?? new SshConnectionClosedException(SshFailureReason.Disconnected, SshPhase.Open, "连接断了，PING 没有等到 PONG。");
        }
        finally
        {
            _pings.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 按键时序混淆的掩护：一个 PING，数据 5 个随机字节 —— 整个载荷 10 字节，与一次按键的 <c>CHANNEL_DATA</c>（1 字节数据）一样长。
    /// 对端不认 PING 时不发、返回 <see langword="false"/>。它的 PONG 不对任何序号，收到就丢。
    /// </summary>
    internal async ValueTask<bool> SendChaffAsync(CancellationToken cancellationToken)
    {
        if (!PeerSupportsPing)
        {
            return false;
        }
        byte[] packet = new byte[1 + 4 + 5];
        packet[0] = (byte)SshMessageNumber.Ping;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), 5);
        System.Security.Cryptography.RandomNumberGenerator.Fill(packet.AsSpan(5));
        await SendAsync(packet, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>一个 PING 报文：<c>byte 192</c> ‖ <c>string</c> 数据（8 字节序号）。</summary>
    internal static byte[] BuildPing(ulong id)
    {
        byte[] packet = new byte[1 + 4 + 8];
        packet[0] = (byte)SshMessageNumber.Ping;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), 8);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(5), id);
        return packet;
    }
}
