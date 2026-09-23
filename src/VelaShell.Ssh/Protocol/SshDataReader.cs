// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4251 §5  "Data Type Representations Used in the SSH Protocols"
//   RFC 4251 §6  文本字段的 UTF-8 约定
//   行为规格:    velashell-docs/zh/ssh/spec/00-overview.md §3 与 §5

using System.Buffers;
using System.Text;

namespace VelaShell.Ssh.Protocol;

/// <summary>
/// 从一段 <see cref="ReadOnlySequence{T}"/> 里读 SSH 的 wire 数据类型。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里的每一个字节都来自不可信的对端</b>（velashell-docs/zh/ssh/spec/00-overview.md §5）。因此：
/// </para>
/// <list type="bullet">
///   <item>所有长度字段先校验再使用，越界一律抛 <see cref="SshWireFormatException"/>；</item>
///   <item><b>禁止按长度字段预分配内存</b> —— 先确认数据确实可读，再分配；</item>
///   <item>解析失败不做「跳过这个字段继续读」的容错。报文是定长拼接的，
///         一处错位之后的所有内容都不可信。</item>
/// </list>
/// <para>
/// 读取尽量返回 <see cref="ReadOnlySequence{T}"/> 或 <see cref="ReadOnlySpan{T}"/> 的切片，
/// 不复制 —— 零拷贝是 Pipelines 这条路的主要收益。
/// </para>
/// </remarks>
internal ref struct SshDataReader
{
    private SequenceReader<byte> _reader;

    /// <summary>在给定序列上开始读取。</summary>
    public SshDataReader(ReadOnlySequence<byte> sequence) => _reader = new SequenceReader<byte>(sequence);

    /// <summary>在给定 span 上开始读取。</summary>
    public SshDataReader(ReadOnlySpan<byte> span) => _reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(span.ToArray()));

    /// <summary>剩余未读字节数。</summary>
    public readonly long Remaining => _reader.Remaining;

    /// <summary>是否已读到末尾。</summary>
    public readonly bool IsEmpty => _reader.Remaining == 0;

    /// <summary>已消费的字节数。</summary>
    public readonly long Consumed => _reader.Consumed;

    /// <summary>读一个字节。</summary>
    public byte ReadByte()
    {
        if (!_reader.TryRead(out byte value))
        {
            throw SshWireFormatException.Truncated("byte", 1, _reader.Remaining);
        }
        return value;
    }

    /// <summary>读一个消息编号。</summary>
    public SshMessageNumber ReadMessageNumber() => (SshMessageNumber)ReadByte();

    /// <summary>
    /// 读并断言消息编号是期望的那个。
    /// </summary>
    /// <exception cref="SshWireFormatException">编号不符。</exception>
    public SshMessageNumber ReadMessageNumber(SshMessageNumber expected)
    {
        SshMessageNumber actual = ReadMessageNumber();
        if (actual != expected)
        {
            throw new SshWireFormatException(
                $"期望消息编号 {expected}({(byte)expected})，实际是 {actual}({(byte)actual})。");
        }
        return actual;
    }

    /// <summary>
    /// 读一个布尔值：<b>非 0 均为真</b>（RFC 4251 §5）。
    /// </summary>
    public bool ReadBoolean() => ReadByte() != 0;

    /// <summary>读一个 32 位无符号整数（大端）。</summary>
    public uint ReadUInt32()
    {
        if (!_reader.TryReadBigEndian(out int value))
        {
            throw SshWireFormatException.Truncated("uint32", sizeof(uint), _reader.Remaining);
        }
        return unchecked((uint)value);
    }

    /// <summary>读一个 64 位无符号整数（大端）。</summary>
    public ulong ReadUInt64()
    {
        if (!_reader.TryReadBigEndian(out long value))
        {
            throw SshWireFormatException.Truncated("uint64", sizeof(ulong), _reader.Remaining);
        }
        return unchecked((ulong)value);
    }

    /// <summary>
    /// 读一个 <c>string</c>，返回它在原序列上的切片（<b>不复制</b>）。
    /// </summary>
    /// <param name="maxLength">
    /// 允许的最大长度。<b>必须给</b> —— 规格里没有「这个字段不会太大」这种理由
    /// （velashell-docs/zh/ssh/spec/00-overview.md §5.1）。
    /// </param>
    public ReadOnlySequence<byte> ReadString(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        uint length = ReadUInt32();
        if (length > (uint)maxLength)
        {
            throw new SshWireFormatException($"string 长度 {length} 超过上限 {maxLength}。");
        }
        // 先确认数据确实可读，再切片 —— 绝不按长度字段预分配。
        if (length > _reader.Remaining)
        {
            throw SshWireFormatException.Truncated("string", length, _reader.Remaining);
        }

        ReadOnlySequence<byte> slice = _reader.UnreadSequence.Slice(0, length);
        _reader.Advance(length);
        return slice;
    }

    /// <summary>读一个 <c>string</c> 并复制成数组。</summary>
    /// <remarks>只在确实需要保留内容（如 KEXINIT 载荷、主机密钥 blob）时用。</remarks>
    public byte[] ReadStringAsArray(int maxLength) => ReadString(maxLength).ToArray();

    /// <summary>
    /// 读一个 <c>string</c> 并按 UTF-8 解码。
    /// </summary>
    /// <param name="maxLength">字节数上限。</param>
    /// <param name="strict">
    /// <see langword="true"/> 时，解码失败抛 <see cref="SshWireFormatException"/>；
    /// <see langword="false"/>（默认）时用替换字符。
    /// </param>
    /// <remarks>
    /// 〔决策，velashell-docs/zh/ssh/spec/00-overview.md §3.3〕默认**宽容**：解码失败的字段通常是横幅、
    /// 错误消息、文件名这类展示性内容，因为一个乱码字节把整条连接打掉对用户是纯粹的损失。
    /// <para>
    /// <b>但用户名与算法名必须用 <paramref name="strict"/> = <see langword="true"/></b> ——
    /// 它们参与协议判定与签名输入，解码失败必须视为协议错误。
    /// </para>
    /// </remarks>
    public string ReadUtf8String(int maxLength, bool strict = false)
    {
        ReadOnlySequence<byte> slice = ReadString(maxLength);
        if (slice.IsEmpty)
        {
            return string.Empty;
        }

        Encoding encoding = strict
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            : Encoding.UTF8;

        try
        {
            if (slice.IsSingleSegment)
            {
                return encoding.GetString(slice.FirstSpan);
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent((int)slice.Length);
            try
            {
                slice.CopyTo(rented);
                return encoding.GetString(rented.AsSpan(0, (int)slice.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new SshWireFormatException("字段不是合法的 UTF-8。", ex);
        }
    }

    /// <summary>
    /// 读一个 <c>mpint</c>，返回它的**无符号大端数值**（已去掉补码用的前导 <c>0x00</c>）。
    /// </summary>
    /// <remarks>
    /// 负数（最高位为 1 且未补零）在 SSH 里没有正当用途，一律视为协议错误 ——
    /// 接受它只会让一个被篡改的字段悄悄变成一个巨大的正数。
    /// </remarks>
    public ReadOnlySequence<byte> ReadMpint(int maxLength)
    {
        ReadOnlySequence<byte> raw = ReadString(maxLength);
        if (raw.IsEmpty)
        {
            return raw; // 零
        }

        byte first = raw.FirstSpan.IsEmpty ? raw.Slice(0, 1).ToArray()[0] : raw.FirstSpan[0];
        if ((first & 0x80) != 0)
        {
            throw new SshWireFormatException("mpint 为负数，SSH 中没有正当用途。");
        }
        // 去掉唯一那个补码用的前导零（若有）。更多前导零是非规范编码，容忍读入。
        return first == 0 && raw.Length > 1 ? raw.Slice(1) : raw;
    }

    /// <summary>读一个 <c>name-list</c>。</summary>
    /// <param name="maxLength">整个列表的字节数上限。</param>
    /// <remarks>
    /// 空列表返回空数组。名字区分大小写，原样返回，<b>不做任何规范化</b> ——
    /// 协商是按字节比对的。
    /// </remarks>
    public string[] ReadNameList(int maxLength)
    {
        ReadOnlySequence<byte> slice = ReadString(maxLength);
        if (slice.IsEmpty)
        {
            return [];
        }

        // name-list 按定义只含可打印 US-ASCII；非法字节视为协议错误。
        string text = slice.IsSingleSegment
            ? DecodeAscii(slice.FirstSpan)
            : DecodeAscii(slice.ToArray());

        return text.Split(',');

        static string DecodeAscii(ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes)
            {
                if (b is < 0x20 or > 0x7E)
                {
                    throw new SshWireFormatException("name-list 含非可打印 US-ASCII 字节。");
                }
            }
            return Encoding.ASCII.GetString(bytes);
        }
    }

    /// <summary>跳过指定字节数。</summary>
    public void Skip(long count)
    {
        if (count > _reader.Remaining)
        {
            throw SshWireFormatException.Truncated("skip", count, _reader.Remaining);
        }
        _reader.Advance(count);
    }

    /// <summary>取剩余的全部字节（不复制）。</summary>
    public ReadOnlySequence<byte> ReadRemaining()
    {
        ReadOnlySequence<byte> rest = _reader.UnreadSequence;
        _reader.Advance(rest.Length);
        return rest;
    }

    /// <summary>
    /// 断言已经读到末尾。
    /// </summary>
    /// <remarks>
    /// 解析完一个报文后调用。<b>多出来的字节意味着我们对这个报文的理解有误</b>，
    /// 沉默地忽略它们会让一个真实的解析错误永远不被发现。
    /// <para>
    /// 例外：允许有尾随数据的报文（协议留了扩展位的那些）显式不调用它。
    /// </para>
    /// </remarks>
    public readonly void ExpectEnd(string messageName)
    {
        if (_reader.Remaining != 0)
        {
            throw new SshWireFormatException($"{messageName} 解析完仍余 {_reader.Remaining} 字节。");
        }
    }
}
