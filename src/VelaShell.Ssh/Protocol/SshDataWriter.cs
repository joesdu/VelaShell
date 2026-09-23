// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4251 §5  "Data Type Representations Used in the SSH Protocols"
//   行为规格:    velashell-docs/zh/ssh/spec/00-overview.md §3

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Ssh.Protocol;

/// <summary>
/// 把 SSH 的七种 wire 数据类型写进一个 <see cref="IBufferWriter{T}"/>。
/// </summary>
/// <remarks>
/// <para>
/// 全部大端序。它只管编码，不管分帧 —— 帧头、填充与加密是
/// <c>Transport/</c> 与 <c>Crypto/</c> 的事。
/// </para>
/// <para>
/// 这是一个 <see langword="ref"/> <see langword="struct"/>：它只在一次编码过程中存活，
/// 不该被存进字段或捕获进闭包。
/// </para>
/// </remarks>
internal ref struct SshDataWriter(IBufferWriter<byte> output)
{
    private readonly IBufferWriter<byte> _output = output;

    /// <summary>已写出的字节数。</summary>
    public int BytesWritten { get; private set; }

    /// <summary>写一个字节。</summary>
    public void WriteByte(byte value)
    {
        Span<byte> span = _output.GetSpan(1);
        span[0] = value;
        Advance(1);
    }

    /// <summary>写消息编号。</summary>
    public void WriteMessageNumber(SshMessageNumber value) => WriteByte((byte)value);

    /// <summary>
    /// 写一个布尔值。
    /// </summary>
    /// <remarks>
    /// RFC 4251 §5：读取时非 0 均为真，但**发送时必须发 1**。
    /// 发别的非零值虽然合法，却会让签名输入与对端的期望产生差异
    /// （公钥认证的签名覆盖了这个字节，见 velashell-docs/zh/ssh/spec/04-authentication.md §4.3）。
    /// </remarks>
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>写一个 32 位无符号整数（大端）。</summary>
    public void WriteUInt32(uint value)
    {
        Span<byte> span = _output.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        Advance(sizeof(uint));
    }

    /// <summary>写一个 64 位无符号整数（大端）。</summary>
    public void WriteUInt64(ulong value)
    {
        Span<byte> span = _output.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        Advance(sizeof(ulong));
    }

    /// <summary>
    /// 写一个 <c>string</c>：4 字节长度前缀 + 原始字节。
    /// </summary>
    /// <remarks>
    /// SSH 的 <c>string</c> 是**字节串**，不是文本 —— 可以含 <c>\0</c>，
    /// 也不保证是合法的 UTF-8。要写文本用 <see cref="WriteUtf8String"/>。
    /// </remarks>
    public void WriteString(scoped ReadOnlySpan<byte> value)
    {
        WriteUInt32((uint)value.Length);
        if (value.IsEmpty)
        {
            return;
        }
        Span<byte> span = _output.GetSpan(value.Length);
        value.CopyTo(span);
        Advance(value.Length);
    }

    /// <summary>把文本按 UTF-8 编码后作为 <c>string</c> 写出。</summary>
    /// <remarks>
    /// **不做任何 Unicode 规范化**（NFC/NFKC）。服务端拿到的是什么就比什么 ——
    /// 对密码与用户名，规范化会让「看起来一样」的两个串在服务端变成不同的字节。
    /// </remarks>
    public void WriteUtf8String(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteUInt32((uint)byteCount);
        if (byteCount == 0)
        {
            return;
        }
        Span<byte> span = _output.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        Advance(byteCount);
    }

    /// <summary>
    /// 写一个 <c>mpint</c>：按二进制补码编码的大整数，外层是 <c>string</c>。
    /// </summary>
    /// <param name="magnitude">大端序的**无符号**数值。允许带前导零，会被去掉。</param>
    /// <remarks>
    /// <para>两条规则，也是 SSH 实现里最经典的两个 off-by-one（velashell-docs/zh/ssh/spec/00-overview.md §3.1）：</para>
    /// <list type="number">
    ///   <item>
    ///     **零编码为长度 0 的 string**（四个字节 <c>00 00 00 00</c>），
    ///     不是一个 <c>0x00</c> 字节。
    ///   </item>
    ///   <item>
    ///     去掉前导零之后，**最高位为 1 时必须补一个 <c>0x00</c>** ——
    ///     否则按二进制补码读会读成负数。
    ///   </item>
    /// </list>
    /// <para>
    /// 第 2 条对随机产生的 32 字节共享密钥意味着约 1/256 的概率触发。
    /// 漏掉它的症状是「大约每 256 次连接失败一次，且失败时只报签名验证不过」——
    /// 这种概率性失败极难排查，所以它有专门的单测。
    /// </para>
    /// </remarks>
    public void WriteMpint(scoped ReadOnlySpan<byte> magnitude)
    {
        // 规则 1 的前半：去掉前导零。
        int start = 0;
        while (start < magnitude.Length && magnitude[start] == 0)
        {
            start++;
        }

        ReadOnlySpan<byte> trimmed = magnitude[start..];

        // 规则 1：零就是空 string。
        if (trimmed.IsEmpty)
        {
            WriteUInt32(0);
            return;
        }

        // 规则 2：最高位为 1 时补 0x00，避免被读成负数。
        bool needsPad = (trimmed[0] & 0x80) != 0;
        WriteUInt32((uint)(trimmed.Length + (needsPad ? 1 : 0)));

        if (needsPad)
        {
            WriteByte(0);
        }

        Span<byte> span = _output.GetSpan(trimmed.Length);
        trimmed.CopyTo(span);
        Advance(trimmed.Length);
    }

    /// <summary>
    /// 写一个 <c>name-list</c>：逗号分隔的 US-ASCII 名字，外层是 <c>string</c>。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 任一名字为空、含逗号、或含非可打印 US-ASCII 字符。
    /// </exception>
    /// <remarks>
    /// 校验放在这里而不是调用方：算法名来自配置与扩展点，
    /// 一个含逗号的名字会**静默地**把一项变成两项，而那两项都不存在 ——
    /// 表现为协商失败，却看不出原因。宁可在编码时就炸掉。
    /// </remarks>
    public void WriteNameList(scoped ReadOnlySpan<string> names)
    {
        if (names.IsEmpty)
        {
            WriteUInt32(0);
            return;
        }

        int byteCount = names.Length - 1; // 逗号
        foreach (string name in names)
        {
            ValidateName(name);
            byteCount += name.Length;     // 已校验为 ASCII，字符数即字节数
        }

        WriteUInt32((uint)byteCount);
        Span<byte> span = _output.GetSpan(byteCount);
        int offset = 0;
        for (int i = 0; i < names.Length; i++)
        {
            if (i > 0)
            {
                span[offset++] = (byte)',';
            }
            offset += Encoding.ASCII.GetBytes(names[i], span[offset..]);
        }
        Advance(byteCount);
    }

    /// <summary>直接写出原始字节，不加任何长度前缀。</summary>
    /// <remarks>用于已经编码好的片段（例如整段保存下来的 KEXINIT 载荷）。</remarks>
    public void WriteRaw(scoped ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }
        Span<byte> span = _output.GetSpan(value.Length);
        value.CopyTo(span);
        Advance(value.Length);
    }

    private void Advance(int count)
    {
        _output.Advance(count);
        BytesWritten += count;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("name-list 中的名字不能为空。", nameof(name));
        }
        foreach (char c in name)
        {
            if (c == ',')
            {
                throw new ArgumentException($"name-list 中的名字不能含逗号: '{name}'。", nameof(name));
            }
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException($"name-list 中的名字只能是可打印 US-ASCII: '{name}'。", nameof(name));
            }
        }
    }
}
