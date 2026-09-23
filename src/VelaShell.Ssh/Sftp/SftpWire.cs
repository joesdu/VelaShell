// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §3、§4、§6、§7
//   OpenSSH PROTOCOL              扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md §二、§三、§四
//
// 这一层是**纯函数**:bytes ↔ 报文,无状态、无 I/O。
// 所以它可以用报文样本逐字节断言,不需要任何对端。

using System.Buffers;
using System.Buffers.Binary;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Sftp;

/// <summary>一个解出来的 SFTP 报文。</summary>
/// <remarks>
/// <see cref="Payload"/> 借的是调用方的缓冲，**只在这一轮处理期间有效**。
/// 要留着就自己复制。
/// </remarks>
public readonly ref struct SftpFrame
{
    internal SftpFrame(SftpMessageType type, ReadOnlySequence<byte> payload)
    {
        Type = type;
        Payload = payload;
    }

    /// <summary>报文类型。</summary>
    public SftpMessageType Type { get; }

    /// <summary>类型之后的全部内容（<b>含 request-id</b>，若这个类型有的话）。</summary>
    public ReadOnlySequence<byte> Payload { get; }
}

/// <summary>SFTP 的编解码。</summary>
/// <remarks>
/// <b>这一层与 SSH 的二进制报文无关</b> —— SFTP 有自己的分帧，
/// 跑在 SSH 通道的字节流上：
/// <code>
/// uint32   length      —— 不含自身这 4 字节
/// byte     type
/// uint32   request-id  —— 除 INIT / VERSION 外都有
/// byte[]   type 相关
/// </code>
/// </remarks>
public static class SftpWire
{
    // ------------------------------------------------------------ 分帧

    /// <summary>从缓冲里切出一个完整报文。</summary>
    /// <param name="buffer">输入缓冲；成功时会被推进到这个报文之后。</param>
    /// <param name="frame">切出来的报文。</param>
    /// <returns>数据够不够一个完整报文。</returns>
    /// <exception cref="SshProtocolException">长度字段超过上限。</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out SftpFrame frame)
    {
        frame = default;

        if (buffer.Length < 4)
        {
            return false;
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthBytes);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

        if (length > SftpProtocol.MaxMessageLength)
        {
            // 这不是「数据还没到齐」，是对端在让我们分配一块巨大的缓冲。
            throw new SshProtocolException(
                SshPhase.Open,
                $"SFTP 报文声称长度 {length} 字节，超过上限 {SftpProtocol.MaxMessageLength}。");
        }

        if (length < 1)
        {
            throw new SshProtocolException(SshPhase.Open, "SFTP 报文长度为 0，连类型字节都放不下。");
        }

        if (buffer.Length < 4 + length)
        {
            return false;
        }

        ReadOnlySequence<byte> body = buffer.Slice(4, length);
        byte type = body.FirstSpan.Length > 0 ? body.FirstSpan[0] : ReadFirstByte(body);

        frame = new SftpFrame((SftpMessageType)type, body.Slice(1));
        buffer = buffer.Slice(4 + length);
        return true;
    }

    private static byte ReadFirstByte(ReadOnlySequence<byte> sequence)
    {
        Span<byte> one = stackalloc byte[1];
        sequence.Slice(0, 1).CopyTo(one);
        return one[0];
    }

    /// <summary>
    /// 把一段已经写好的载荷包上外层的 <c>length</c> 与 <c>type</c>。
    /// </summary>
    /// <remarks>
    /// 长度要等载荷写完才知道，所以这里先写占位、写完回填 ——
    /// 而不是先把载荷拼进一个临时数组再复制一次。
    /// </remarks>
    internal static void WriteFrame(
        IBufferWriter<byte> output, SftpMessageType type, ReadOnlySpan<byte> payload)
    {
        int length = 1 + payload.Length;
        Span<byte> header = output.GetSpan(5);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)length);
        header[4] = (byte)type;
        output.Advance(5);
        output.Write(payload);
    }

    // ------------------------------------------------------------ 请求

    /// <summary>握手：<c>SSH_FXP_INIT</c>。<b>没有 request-id。</b></summary>
    public static void WriteInit(IBufferWriter<byte> output, uint version = SftpProtocol.Version)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(version);
        WriteFrame(output, SftpMessageType.Init, payload.WrittenSpan);
    }

    /// <summary>打开文件。</summary>
    public static void WriteOpen(
        IBufferWriter<byte> output, uint requestId, string path,
        SftpOpenMode flags, SftpFileAttributes attributes)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(path);
        writer.WriteUInt32((uint)flags);
        attributes.Write(ref writer);
        WriteFrame(output, SftpMessageType.Open, payload.WrittenSpan);
    }

    /// <summary>只带一个句柄的请求（<c>CLOSE</c> / <c>READDIR</c> / <c>FSTAT</c>）。</summary>
    public static void WriteHandleRequest(
        IBufferWriter<byte> output, SftpMessageType type, uint requestId, ReadOnlySpan<byte> handle)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteString(handle);
        WriteFrame(output, type, payload.WrittenSpan);
    }

    /// <summary>只带一个路径的请求（<c>STAT</c> / <c>LSTAT</c> / <c>OPENDIR</c> / …）。</summary>
    public static void WritePathRequest(
        IBufferWriter<byte> output, SftpMessageType type, uint requestId, string path)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(path);
        WriteFrame(output, type, payload.WrittenSpan);
    }

    /// <summary>按偏移读。</summary>
    /// <remarks>
    /// <b>返回的数据可能少于请求的长度</b>，那不是错误 ——
    /// 必须循环读直到拿够或遇到 <c>EOF</c>。
    /// </remarks>
    public static void WriteRead(
        IBufferWriter<byte> output, uint requestId, ReadOnlySpan<byte> handle, ulong offset, uint length)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteString(handle);
        writer.WriteUInt64(offset);
        writer.WriteUInt32(length);
        WriteFrame(output, SftpMessageType.Read, payload.WrittenSpan);
    }

    /// <summary>按偏移写。</summary>
    /// <remarks><b>绝对偏移，服务端不维护文件位置</b> —— 所以写可以乱序并发。</remarks>
    public static void WriteWrite(
        IBufferWriter<byte> output, uint requestId, ReadOnlySpan<byte> handle,
        ulong offset, ReadOnlySpan<byte> data)
    {
        // ⚠️ 这是上传路径上最热的一处：**直接写进 output，不经中转缓冲**。
        //
        // 别的 Write* 方法先拼一个 ArrayBufferWriter 再 WriteFrame，
        // 那等于把整个数据块多拷一遍。对 32 KiB 的块来说，
        // 一次上传每块要多搬 32 KiB —— 基准上是 98 KB 分配对 32 KB 载荷。
        // 这里所有字段的长度都是已知的，长度前缀可以直接算出来。
        int bodyLength =
            1                        // 类型
            + 4                      // request-id
            + 4 + handle.Length      // string handle
            + 8                      // uint64 offset
            + 4 + data.Length;       // string data

        Span<byte> header = output.GetSpan(4 + 1 + 4 + 4 + handle.Length + 8 + 4);
        int written = 0;

        BinaryPrimitives.WriteUInt32BigEndian(header[written..], (uint)bodyLength);
        written += 4;

        header[written++] = (byte)SftpMessageType.Write;

        BinaryPrimitives.WriteUInt32BigEndian(header[written..], requestId);
        written += 4;

        BinaryPrimitives.WriteUInt32BigEndian(header[written..], (uint)handle.Length);
        written += 4;
        handle.CopyTo(header[written..]);
        written += handle.Length;

        BinaryPrimitives.WriteUInt64BigEndian(header[written..], offset);
        written += 8;

        BinaryPrimitives.WriteUInt32BigEndian(header[written..], (uint)data.Length);
        written += 4;

        output.Advance(written);
        output.Write(data);
    }

    /// <summary>设属性（按路径）。</summary>
    public static void WriteSetStat(
        IBufferWriter<byte> output, uint requestId, string path, SftpFileAttributes attributes)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(path);
        attributes.Write(ref writer);
        WriteFrame(output, SftpMessageType.SetStat, payload.WrittenSpan);
    }

    /// <summary>设属性（按句柄）。</summary>
    public static void WriteFSetStat(
        IBufferWriter<byte> output, uint requestId, ReadOnlySpan<byte> handle, SftpFileAttributes attributes)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteString(handle);
        attributes.Write(ref writer);
        WriteFrame(output, SftpMessageType.FSetStat, payload.WrittenSpan);
    }

    /// <summary>建目录。</summary>
    public static void WriteMkDir(
        IBufferWriter<byte> output, uint requestId, string path, SftpFileAttributes attributes)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(path);
        attributes.Write(ref writer);
        WriteFrame(output, SftpMessageType.MkDir, payload.WrittenSpan);
    }

    /// <summary>重命名。</summary>
    public static void WriteRename(
        IBufferWriter<byte> output, uint requestId, string oldPath, string newPath)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(oldPath);
        writer.WriteUtf8String(newPath);
        WriteFrame(output, SftpMessageType.Rename, payload.WrittenSpan);
    }

    /// <summary>建符号链接。</summary>
    /// <param name="output">输出。</param>
    /// <param name="requestId">请求编号。</param>
    /// <param name="targetPath">链接<b>指向</b>哪里。</param>
    /// <param name="linkPath">在哪里<b>创建</b>链接。</param>
    /// <remarks>
    /// <para>
    /// ⚠️⚠️ <b>不要「顺手修正」这里的参数顺序。</b>
    /// </para>
    /// <para>
    /// draft-02 规定的顺序是先 <c>linkpath</c> 后 <c>targetpath</c>，
    /// <b>但 OpenSSH 的实现把两者写反了</b>（OpenSSH bugzilla #861），
    /// 而 OpenSSH 是事实标准 —— 所有客户端都跟着它错，所有服务端都按它的顺序解析。
    /// </para>
    /// <para>
    /// 所以这里<b>按 OpenSSH 的顺序发</b>：先 <c>targetpath</c>，后 <c>linkpath</c>。
    /// 改成 draft 的顺序，结果是链接被建在你本想指向的位置上 ——
    /// 而且不报错，只是建错地方。
    /// </para>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §4.5〕<b>不提供「按 draft 顺序」的开关。</b>
    /// 没有已知的服务端按 draft 实现；加一个永远不该被打开的开关，
    /// 只会让人在排查别的问题时误开它。
    /// </para>
    /// </remarks>
    public static void WriteSymLink(
        IBufferWriter<byte> output, uint requestId, string targetPath, string linkPath)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(targetPath);   // ← OpenSSH 顺序：先目标
        writer.WriteUtf8String(linkPath);     // ← 再链接位置
        WriteFrame(output, SftpMessageType.SymLink, payload.WrittenSpan);
    }

    /// <summary>厂商扩展请求。</summary>
    public static void WriteExtended(
        IBufferWriter<byte> output, uint requestId, string extensionName, ReadOnlySpan<byte> extensionPayload)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(requestId);
        writer.WriteUtf8String(extensionName);
        writer.WriteRaw(extensionPayload);
        WriteFrame(output, SftpMessageType.Extended, payload.WrittenSpan);
    }

    // ------------------------------------------------------------ 应答

    /// <summary>解 <c>SSH_FXP_VERSION</c>。</summary>
    public static (uint Version, IReadOnlyDictionary<string, byte[]> Extensions) ReadVersion(
        ReadOnlySequence<byte> payload)
    {
        SshDataReader reader = new(payload);
        uint version = reader.ReadUInt32();

        // string 的默认相等比较器就是序数比较。
        Dictionary<string, byte[]> extensions = [];
        while (!reader.IsEmpty)
        {
            string name = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
            byte[] data = reader.ReadStringAsArray(SftpProtocol.MaxPathLength);
            extensions[name] = data;
        }

        return (version, extensions);
    }

    /// <summary>解 <c>SSH_FXP_STATUS</c>。</summary>
    /// <remarks>
    /// 服务端给的 <c>message</c> 文本必须原样留着：v3 里「目录非空」「文件已存在」
    /// 「磁盘满」全是同一个错误码 4，那段文本是唯一能区分它们的信息。
    /// </remarks>
    public static (SftpStatusCode Code, string Message) ReadStatus(ReadOnlySequence<byte> payloadAfterRequestId)
    {
        SshDataReader reader = new(payloadAfterRequestId);
        var code = (SftpStatusCode)reader.ReadUInt32();

        // 有些老服务端在 STATUS 里只给码，不给文本。那不是协议违规，别因此抛异常。
        string message = reader.IsEmpty ? "" : reader.ReadUtf8String(SftpProtocol.MaxPathLength);
        return (code, message);
    }

    /// <summary>解 <c>SSH_FXP_HANDLE</c>。</summary>
    public static byte[] ReadHandle(ReadOnlySequence<byte> payloadAfterRequestId)
    {
        SshDataReader reader = new(payloadAfterRequestId);
        byte[] handle = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength + 1);

        if (handle.Length > SftpProtocol.MaxHandleLength)
        {
            throw new SshProtocolException(
                SshPhase.Open,
                $"SFTP 句柄有 {handle.Length} 字节，超过 draft-02 规定的上限 " +
                $"{SftpProtocol.MaxHandleLength}。");
        }

        return handle;
    }

    /// <summary>解 <c>SSH_FXP_ATTRS</c>。</summary>
    public static SftpFileAttributes ReadAttrs(ReadOnlySequence<byte> payloadAfterRequestId)
    {
        SshDataReader reader = new(payloadAfterRequestId);
        return SftpFileAttributes.Read(ref reader);
    }

    /// <summary>解 <c>SSH_FXP_NAME</c>。</summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §4.4〕<b>不解析 <c>longname</c></b>，一切信息取自 ATTRS。
    /// 解析它是各家 SFTP 客户端 bug 的经典来源（时间格式、locale、列对齐全都因服务端而异）。
    /// 但原文保留下来，使用者需要时能拿到。
    /// </remarks>
    public static IReadOnlyList<SftpNameEntry> ReadName(ReadOnlySequence<byte> payloadAfterRequestId)
    {
        SshDataReader reader = new(payloadAfterRequestId);
        uint count = reader.ReadUInt32();

        List<SftpNameEntry> entries = [];
        for (uint i = 0; i < count; i++)
        {
            if (reader.IsEmpty)
            {
                // count 与实际项数不符是明确的协议违规 —— 继续读下去只会读出垃圾。
                throw new SshProtocolException(
                    SshPhase.Open,
                    $"SSH_FXP_NAME 声称有 {count} 项，实际只有 {i} 项。");
            }

            string fileName = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
            string longName = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
            var attributes = SftpFileAttributes.Read(ref reader);
            entries.Add(new SftpNameEntry(fileName, longName, attributes));
        }

        return entries;
    }

    /// <summary>解 <c>SSH_FXP_DATA</c>，直接拷进调用方的缓冲。</summary>
    /// <returns>实际读到的字节数。</returns>
    /// <exception cref="SshProtocolException">服务端给的数据比请求的还多。</exception>
    public static int ReadDataInto(ReadOnlySequence<byte> payloadAfterRequestId, Span<byte> destination)
    {
        SshDataReader reader = new(payloadAfterRequestId);
        ReadOnlySequence<byte> data = reader.ReadString(SftpProtocol.MaxMessageLength);

        if (data.Length > destination.Length)
        {
            throw new SshProtocolException(
                SshPhase.Open,
                $"SSH_FXP_DATA 给了 {data.Length} 字节，超过我们请求的 {destination.Length} 字节。");
        }

        data.CopyTo(destination);
        return (int)data.Length;
    }

    /// <summary>从一个有 request-id 的应答载荷里取出 request-id。</summary>
    public static uint ReadRequestId(ReadOnlySequence<byte> payload)
    {
        SshDataReader reader = new(payload);
        return reader.ReadUInt32();
    }
}

/// <summary><c>SSH_FXP_NAME</c> 里的一项。</summary>
/// <param name="FileName">文件名（<b>只是名字，不含路径</b>）。</param>
/// <param name="LongName">
/// <c>ls -l</c> 风格的一行文本。<b>格式未标准化，不要解析它。</b>
/// </param>
/// <param name="Attributes">属性。</param>
public readonly record struct SftpNameEntry(
    string FileName,
    string LongName,
    SftpFileAttributes Attributes);
