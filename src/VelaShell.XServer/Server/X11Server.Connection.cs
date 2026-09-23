// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」与附录 B「Connection Setup」
//   (客户端开场 12 字节 + 授权名 / 数据;成功回复的定长部分、FORMAT、SCREEN、DEPTH、VISUALTYPE 的布局;失败回复)
//   BIG-REQUESTS Extension(请求长度字段为 0 时后跟 4 字节的扩展长度)

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    /// <summary>核心协议允许的最大请求长度(以 4 字节计)。</summary>
    internal const ushort MaxRequestLength = 65535;

    /// <summary>BIG-REQUESTS 打开后的最大请求长度(以 4 字节计,16 MB)。</summary>
    internal const uint MaxBigRequestLength = 4 * 1024 * 1024;

    /// <summary>读端缓冲。一批典型的绘图请求(几十到几百条)一次读进来。</summary>
    private const int InputBufferSize = 64 * 1024;

    /// <summary>写出端拼包缓冲。</summary>
    private const int OutputBufferSize = 64 * 1024;

    private int _nextClientIndex = 1;

    /// <summary>
    /// 在一条已经建立的双工流上服务一个 X 客户端,直到它断开。
    /// </summary>
    /// <param name="stream">双工流(TCP、Unix 套接字、SSH 的 x11 通道……)。</param>
    /// <param name="isLocal">对端是不是本机;没配置 cookie 时只接受本机连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ServeAsync(Stream stream, bool isLocal = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken ct = linked.Token;
        CancellationTokenSource? connection = null;

        XClient? client = null;
        Task? writer = null;
        try
        {
            byte[] head = new byte[12];
            await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
            bool bigEndian = head[0] switch
            {
                (byte)'B' => true,
                (byte)'l' => false,
                _ => throw new InvalidDataException("连接建立报文的字节序标记非法。"),
            };
            ushort major = Read16(head.AsSpan(2), bigEndian);
            int nameLength = Read16(head.AsSpan(6), bigEndian);
            int dataLength = Read16(head.AsSpan(8), bigEndian);
            byte[] rest = new byte[XWire.Pad(nameLength) + XWire.Pad(dataLength)];
            await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
            string authName = XWire.Latin1.GetString(rest, 0, nameLength);
            byte[] authData = rest.AsSpan(XWire.Pad(nameLength), dataLength).ToArray();

            if (major != 11)
            {
                await SendSetupFailureAsync(stream, bigEndian, "Protocol version mismatch", ct).ConfigureAwait(false);
                return;
            }
            if (Authorize(authName, authData, isLocal) is { } reason)
            {
                await SendSetupFailureAsync(stream, bigEndian, reason, ct).ConfigureAwait(false);
                return;
            }

            client = await InvokeAsync(() => RegisterClient(bigEndian)).WaitAsync(ct).ConfigureAwait(false);
            // 连接的读写还要跟着「服务端主动断开这个客户端」一起停。
            connection = CancellationTokenSource.CreateLinkedTokenSource(ct, client.Aborted);
            ct = connection.Token;
            writer = PumpOutputAsync(client, stream, ct);
            // 读端单独套一层缓冲:X 请求又小又密(常见 8–40 字节),不缓冲就是每条请求两次系统调用。
            // ⚠️ 只经它读、从不经它写 —— BufferedStream 读写共用一块缓冲,在不可寻址的流上混用会抛异常;
            // 写出端直接写底层流。
            BufferedStream input = new(stream, InputBufferSize);
            await ReadRequestsAsync(client, input, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            // 对端走了、乱发、或者服务端在收工。
        }
        finally
        {
            if (client is not null)
            {
                XClient gone = client;
                Post(null, () => DisconnectClient(gone));
                gone.Output.Writer.TryComplete();
            }
            if (writer is not null)
            {
                // 对端已经不读了:别再等积压的输出写完(对端半关闭时那会永远等下去)。
                connection?.Cancel();
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // 写出端跟着收工。
                }
            }
            connection?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>授权检查;通过返回 null,否则返回给客户端看的原因。</summary>
    private string? Authorize(string name, byte[] data, bool isLocal)
    {
        if (_options.AuthorizationCookie is { } cookie)
        {
            // ⚠️ 常数时间比较:逐字节短路会泄漏「前几个字节对了几个」。
            return name == "MIT-MAGIC-COOKIE-1" && CryptographicOperations.FixedTimeEquals(data, cookie)
                ? null
                : "Authorization required, but no authorization protocol specified";
        }
        // 没配置 cookie:与 X.Org 的主机访问控制一致 —— 本机放行,客户端带来的 cookie 不看。
        return isLocal ? null : "No protocol specified: only local connections are accepted";
    }

    private static async Task SendSetupFailureAsync(Stream stream, bool bigEndian, string reason, CancellationToken ct)
    {
        byte[] text = XWire.Latin1.GetBytes(reason);
        XWriter w = new(bigEndian);
        w.U8(0).U8((byte)Math.Min(255, text.Length)).U16(11).U16(0).U16((ushort)(XWire.Pad(text.Length) / 4));
        w.Bytes(text).Pad4();
        await stream.WriteAsync(w.ToArray(), ct).ConfigureAwait(false);
    }

    private XClient RegisterClient(bool bigEndian)
    {
        int index = _nextClientIndex;
        while (_clients.ContainsKey(index) || index == 0)
        {
            index = index >= 1000 ? 1 : index + 1;
        }
        _nextClientIndex = index >= 1000 ? 1 : index + 1;
        XClient client = new(index, bigEndian);
        _clients[index] = client;
        client.Send(BuildSetupReply(client));
        _options.Log?.Invoke($"{client} connected ({(bigEndian ? "MSB" : "LSB")} first)");
        return client;
    }

    /// <summary>连接建立成功回复:一块屏幕、深度 24 的 TrueColor 视觉(外加深度 32 与深度 1)。</summary>
    private byte[] BuildSetupReply(XClient client)
    {
        byte[] vendor = XWire.Latin1.GetBytes(_options.Vendor);
        XWriter w = client.Writer(256);
        w.U8(1).U8(0).U16(11).U16(0).U16(0);        // success、主版本 11、次版本 0、长度(回填)
        w.U32(12101000);                             // release-number
        w.U32(client.ResourceBase).U32(XClient.ResourceMask);
        w.U32(0);                                    // motion-buffer-size:不保存移动历史(GetMotionEvents 回空)
        w.U16((ushort)vendor.Length).U16(MaxRequestLength);
        w.U8(1);                                     // 屏幕数
        w.U8(7);                                     // FORMAT 数
        w.U8(0);                                     // image-byte-order:LSBFirst
        w.U8(0);                                     // bitmap-format-bit-order:LeastSignificant
        w.U8(32).U8(32);                             // bitmap scanline unit / pad
        w.U8(Input.Keymap.MinKeycode).U8(Input.Keymap.MaxKeycode);
        w.Zero(4);
        w.Bytes(vendor).Pad4();

        // FORMAT:depth、bits-per-pixel、scanline-pad、5 字节空
        foreach ((byte depth, byte bpp) in ((byte, byte)[])[(1, 1), (4, 8), (8, 8), (15, 16), (16, 16), (24, 32), (32, 32)])
        {
            w.U8(depth).U8(bpp).U8(32).Zero(5);
        }

        // SCREEN
        (int mmW, int mmH) = ScreenMillimeters();
        w.U32(Root.Id).U32(DefaultColormapId).U32(0xFFFFFF).U32(0x000000);
        w.U32(Root.AllEventMasks);
        w.U16((ushort)Root.Width).U16((ushort)Root.Height).U16((ushort)mmW).U16((ushort)mmH);
        w.U16(1).U16(1);                             // min / max installed maps
        w.U32(RootVisualId);
        w.U8(0);                                     // backing-stores:Never(我们另有顶层缓冲,不对客户端承诺)
        w.Bool(false);                               // save-unders
        w.U8(24);                                    // root-depth
        w.U8(7);                                     // DEPTH 数

        // DEPTH 24:一个 TrueColor 视觉
        w.U8(24).U8(0).U16(1).Zero(4);
        WriteVisual(w, RootVisualId);
        // DEPTH 1 / 4 / 8 / 15 / 16:没有视觉(只能做像素图 —— 协议只允许在列出的深度上建像素图)
        foreach (byte depth in (byte[])[1, 4, 8, 15, 16])
        {
            w.U8(depth).U8(0).U16(0).Zero(4);
        }
        // DEPTH 32:一个 TrueColor 视觉(ARGB,RENDER 用)
        w.U8(32).U8(0).U16(1).Zero(4);
        WriteVisual(w, ArgbVisualId);

        byte[] bytes = w.ToArray();
        ushort extra = (ushort)((bytes.Length - 8) / 4);
        if (client.BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), extra);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), extra);
        }
        return bytes;

        static void WriteVisual(XWriter w, uint id)
        {
            // visual-id、class(4 = TrueColor)、bits-per-rgb、colormap-entries、红绿蓝掩码、4 字节空
            w.U32(id).U8(4).U8(8).U16(256).U32(0xFF0000).U32(0x00FF00).U32(0x0000FF).Zero(4);
        }
    }

    private async Task ReadRequestsAsync(XClient client, Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        byte[] extended = new byte[4];
        while (!ct.IsCancellationRequested && !client.Closed)
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            uint units = Read16(header.AsSpan(2), client.BigEndian);
            int headerSize = 4;
            if (units == 0)
            {
                // BIG-REQUESTS:长度字段为 0,后面 4 字节才是真长度(含这 8 字节头)。
                // 没打开扩展就收到 0 长度 —— 按协议是 BadLength,这里直接断开,免得后面整条流错位。
                if (!client.BigRequestsEnabled)
                {
                    throw new InvalidDataException("收到长度为 0 的请求,但客户端没有打开 BIG-REQUESTS。");
                }
                await stream.ReadExactlyAsync(extended, ct).ConfigureAwait(false);
                units = client.BigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(extended)
                    : BinaryPrimitives.ReadUInt32LittleEndian(extended);
                headerSize = 8;
                if (units < 2 || units > MaxBigRequestLength)
                {
                    throw new InvalidDataException("BIG-REQUESTS 长度越界。");
                }
            }
            // 交给执行线程的请求统一是「4 字节头 + 正文」:扩展长度字段剥掉,正文紧接在头后。
            byte[] request = new byte[4 + (int)(units * 4) - headerSize];
            header.CopyTo(request, 0);
            await stream.ReadExactlyAsync(request.AsMemory(4), ct).ConfigureAwait(false);
            // 背压:已读进来、还没执行的请求到了上限就等执行线程消化(同步完成的快路径不分配)。
            await client.PendingRequests.WaitAsync(ct).ConfigureAwait(false);
            PostRequest(client, request);
        }
    }

    /// <summary>
    /// 写出端:把已经排队的回复 / 事件 / 错误拼进一块缓冲再一次写出 —— 事件动辄几十条一批,
    /// 每条单独写就是每条一次系统调用(TCP 上还可能每条一个包)。单条超过缓冲的(GetImage 的大回复)直接写。
    /// </summary>
    private static async Task PumpOutputAsync(XClient client, Stream stream, CancellationToken ct)
    {
        ChannelReader<byte[]> reader = client.Output.Reader;
        byte[] batch = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                int used = 0;
                long written = 0;
                while (reader.TryRead(out byte[]? message))
                {
                    written += message.Length;
                    if (used + message.Length > batch.Length)
                    {
                        if (used > 0)
                        {
                            await stream.WriteAsync(batch.AsMemory(0, used), ct).ConfigureAwait(false);
                            used = 0;
                        }
                        if (message.Length > batch.Length)
                        {
                            await stream.WriteAsync(message, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    message.CopyTo(batch, used);
                    used += message.Length;
                }
                if (used > 0)
                {
                    await stream.WriteAsync(batch.AsMemory(0, used), ct).ConfigureAwait(false);
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
                client.NoteWritten(written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(batch);
        }
    }

    private static ushort Read16(ReadOnlySpan<byte> span, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);

    /// <summary>客户端断开:按 CloseDownMode = Destroy 释放它的一切(协议第 10 节「Connection Close」)。</summary>
    private void DisconnectClient(XClient client)
    {
        if (!_clients.Remove(client.Index))
        {
            return;
        }
        client.Closed = true;
        _options.Log?.Invoke($"{client} disconnected");
        try
        {
            CleanupClient(client);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[X11Server] cleanup of {client} failed: {ex}");
        }
        if (ReferenceEquals(_serverGrabber, client))
        {
            ReleaseServerGrab();
        }
    }
}
