// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/00-overview.md §3(数据类型) 与 §3.1(mpint 的两个坑)

using System.Buffers;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Protocol;

[TestClass]
[TestCategory("Protocol")]
public sealed class SshDataWriterTests
{
    private static byte[] Write(Action<SshDataWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SshDataWriter(buffer);
        write(writer);
        return buffer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------ 标量

    [TestMethod]
    public void UInt32_是大端序()
    {
        CollectionAssert.AreEqual(
            new byte[] { 0x12, 0x34, 0x56, 0x78 },
            Write(w => w.WriteUInt32(0x12345678)));
    }

    [TestMethod]
    public void UInt64_是大端序()
    {
        CollectionAssert.AreEqual(
            new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF },
            Write(w => w.WriteUInt64(0x0123456789ABCDEF)));
    }

    [TestMethod]
    public void 布尔的真必须发1而不是别的非零值()
    {
        // RFC 4251 §5 读取时非 0 均为真，但发送必须发 1 ——
        // 这个字节参与公钥认证的签名输入（spec/04 §4.3），发别的值会让签名对不上。
        CollectionAssert.AreEqual(new byte[] { 1 }, Write(w => w.WriteBoolean(true)));
        CollectionAssert.AreEqual(new byte[] { 0 }, Write(w => w.WriteBoolean(false)));
    }

    // ------------------------------------------------------------------ string

    [TestMethod]
    public void String_是长度前缀加原始字节()
    {
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c' },
            Write(w => w.WriteString("abc"u8)));
    }

    [TestMethod]
    public void 空String_只有四字节零长度()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Write(w => w.WriteString([])));
    }

    [TestMethod]
    public void String_是字节串_可以含零字节()
    {
        // SSH 的 string 不是 C 字符串，内嵌 \0 是合法的。
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 3, 0x61, 0x00, 0x62 },
            Write(w => w.WriteString([0x61, 0x00, 0x62])));
    }

    [TestMethod]
    public void Utf8String_按字节数而不是字符数给长度()
    {
        // "中" 是 3 字节。按字符数给长度是一个经典错误。
        byte[] actual = Write(w => w.WriteUtf8String("中"));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 3, 0xE4, 0xB8, 0xAD }, actual);
    }

    // ------------------------------------------------------------ mpint（重点）

    [TestMethod]
    public void Mpint_零编码为长度零的串_不是一个零字节()
    {
        // spec/00 §3.1 规则 1。写成 { 0,0,0,1, 0x00 } 是错的。
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Write(w => w.WriteMpint([])));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Write(w => w.WriteMpint([0x00])));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Write(w => w.WriteMpint([0x00, 0x00, 0x00])));
    }

    [TestMethod]
    public void Mpint_最高位为一时补零字节()
    {
        // spec/00 §3.1 规则 2。不补的话 0x80 会被读成负数。
        //
        // ⚠️ 这条规则对随机产生的共享密钥意味着约 1/256 的触发概率。
        //    漏掉它的症状是「大约每 256 次连接失败一次，且只报签名验证不过」——
        //    概率性失败极难排查，所以这条用例存在的价值远高于它的长度。
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 2, 0x00, 0x80 },
            Write(w => w.WriteMpint([0x80])));

        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 3, 0x00, 0xFF, 0x01 },
            Write(w => w.WriteMpint([0xFF, 0x01])));
    }

    [TestMethod]
    public void Mpint_最高位为零时不补()
    {
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 1, 0x7F },
            Write(w => w.WriteMpint([0x7F])));
    }

    [TestMethod]
    public void Mpint_去掉前导零之后再判断是否要补()
    {
        // 0x00 0x80 → 去前导零得 0x80 → 最高位为 1 → 补回一个 0x00。
        // 顺序写反（先判断再去零）会得到 { 0,0,0,2, 0x00, 0x80 } 之外的结果。
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 2, 0x00, 0x80 },
            Write(w => w.WriteMpint([0x00, 0x00, 0x80])));

        // 0x00 0x7F → 去前导零得 0x7F → 不补。
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 1, 0x7F },
            Write(w => w.WriteMpint([0x00, 0x7F])));
    }

    [TestMethod]
    public void Mpint_RFC_4251_示例表()
    {
        // RFC 4251 §5 的示例表（只取非负的那几条 —— 我们只写无符号数值，
        // 负数在 SSH 里没有正当用途，读取侧会拒绝，见 SshDataReaderTests）。
        //
        //   value               representation
        //   0                   00 00 00 00
        //   0x9a378f9b2e332a7   00 00 00 08 09 a3 78 f9 b2 e3 32 a7   ← 63 位，高字节是 09
        //   0x80                00 00 00 02 00 80
        (byte[] Value, byte[] Expected)[] cases =
        [
            ([], [0, 0, 0, 0]),
            ([0x09, 0xA3, 0x78, 0xF9, 0xB2, 0xE3, 0x32, 0xA7],
             [0, 0, 0, 8, 0x09, 0xA3, 0x78, 0xF9, 0xB2, 0xE3, 0x32, 0xA7]),
            ([0x80], [0, 0, 0, 2, 0x00, 0x80]),
        ];

        foreach ((byte[] value, byte[] expected) in cases)
        {
            CollectionAssert.AreEqual(expected, Write(w => w.WriteMpint(value)),
                $"mpint({Convert.ToHexString(value)}) 编码错误");
        }
    }

    // --------------------------------------------------------------- name-list

    [TestMethod]
    public void NameList_逗号分隔()
    {
        byte[] actual = Write(w => w.WriteNameList(["zlib", "none"]));
        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 9, (byte)'z', (byte)'l', (byte)'i', (byte)'b', (byte)',',
                         (byte)'n', (byte)'o', (byte)'n', (byte)'e' },
            actual);
    }

    [TestMethod]
    public void 空NameList_是长度零的串()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Write(w => w.WriteNameList([])));
    }

    [TestMethod]
    public void NameList_拒绝含逗号的名字()
    {
        // 放过它会**静默地**把一项变成两项，而那两项都不存在 ——
        // 表现为协商失败却看不出原因。宁可在编码时就炸掉。
        Assert.ThrowsExactly<ArgumentException>(() => Write(w => w.WriteNameList(["a,b"])));
    }

    [TestMethod]
    public void NameList_拒绝空名字与非ASCII()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Write(w => w.WriteNameList([""])));
        Assert.ThrowsExactly<ArgumentException>(() => Write(w => w.WriteNameList(["中文"])));
    }

    // ------------------------------------------------------------------ 计数

    [TestMethod]
    public void BytesWritten_累计所有写出的字节()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SshDataWriter(buffer);
        writer.WriteByte(1);            // 1
        writer.WriteUInt32(2);          // 4
        writer.WriteString("abc"u8);    // 4 + 3
        Assert.AreEqual(12, writer.BytesWritten);
        Assert.AreEqual(12, buffer.WrittenCount);
    }
}
