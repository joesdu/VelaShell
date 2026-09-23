// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/00-overview.md §3(数据类型) 与 §5(通用安全下限)
//
// ⚠️ 这一层直面不可信输入。**越界与截断的用例比正常路径的用例更重要** ——
//    正常路径出错会被集成测试抓到，越界不校验只会在被攻击时才显形。

using System.Buffers;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Protocol;

[TestClass]
[TestCategory("Protocol")]
public sealed class SshDataReaderTests
{
    private static SshDataReader Reader(params byte[] bytes) => new(new ReadOnlySequence<byte>(bytes));

    /// <summary>对一个新建的 reader 执行动作。</summary>
    private delegate void ReaderAction(ref SshDataReader reader);

    /// <summary>断言在给定字节上执行动作会抛出 wire 格式错误。</summary>
    /// <remarks>
    /// <see cref="SshDataReader"/> 是 <c>ref struct</c>，**不能被 lambda 捕获**（CS8175）。
    /// 所以 reader 必须在 lambda 内部新建 —— 这个 helper 就是为了不把这件事
    /// 在八个用例里各写一遍。
    /// </remarks>
    private static void AssertWireError(byte[] bytes, ReaderAction action) =>
        Assert.ThrowsExactly<SshWireFormatException>(() =>
        {
            var reader = new SshDataReader(new ReadOnlySequence<byte>(bytes));
            action(ref reader);
        });

    /// <summary>把字节切成多段，用来验证跨段读取（真实的 PipeReader 经常给多段序列）。</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] bytes, int chunk)
    {
        Segment? first = null, last = null;
        for (int i = 0; i < bytes.Length; i += chunk)
        {
            byte[] part = bytes[i..Math.Min(i + chunk, bytes.Length)];
            Segment node = new(part, first is null ? 0 : last!.RunningIndex + last.Memory.Length);
            if (first is null) { first = node; }
            else { last!.SetNext(node); }
            last = node;
        }
        return first is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(Segment next) => Next = next;
    }

    // ------------------------------------------------------------------ 标量

    [TestMethod]
    public void UInt32_按大端读()
    {
        SshDataReader r = Reader(0x12, 0x34, 0x56, 0x78);
        Assert.AreEqual(0x12345678u, r.ReadUInt32());
        Assert.IsTrue(r.IsEmpty);
    }

    [TestMethod]
    public void UInt32_能读出高位为一的值而不溢出()
    {
        // TryReadBigEndian 给的是 int，直接用会变成负数。
        SshDataReader r = Reader(0xFF, 0xFF, 0xFF, 0xFF);
        Assert.AreEqual(uint.MaxValue, r.ReadUInt32());
    }

    [TestMethod]
    public void UInt64_能读出高位为一的值而不溢出()
    {
        SshDataReader r = Reader(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Assert.AreEqual(ulong.MaxValue, r.ReadUInt64());
    }

    [TestMethod]
    public void 布尔的非零一律为真()
    {
        // RFC 4251 §5：读取时非 0 均为真。只认 1 是错的。
        Assert.IsTrue(Reader(1).ReadBoolean());
        Assert.IsTrue(Reader(2).ReadBoolean());
        Assert.IsTrue(Reader(0xFF).ReadBoolean());
        Assert.IsFalse(Reader(0).ReadBoolean());
    }

    // ------------------------------------------------------------------ string

    [TestMethod]
    public void String_读出长度前缀之后的字节()
    {
        SshDataReader r = Reader(0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c');
        CollectionAssert.AreEqual("abc"u8.ToArray(), r.ReadString(64).ToArray());
        Assert.IsTrue(r.IsEmpty);
    }

    [TestMethod]
    public void String_能跨段读取()
    {
        // PipeReader 给的常是多段序列。按单段假设写的代码在这里会漏。
        byte[] raw = [0, 0, 0, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'];
        var r = new SshDataReader(Segmented(raw, chunk: 2));
        CollectionAssert.AreEqual("hello"u8.ToArray(), r.ReadString(64).ToArray());
    }

    [TestMethod]
    public void String_超过上限时抛出()
    {
        // 上限是**必须**给的参数：规格里没有「这个字段不会太大」这种理由。
        AssertWireError([0, 0, 0, 100], (ref SshDataReader r) => r.ReadString(maxLength: 10));
    }

    [TestMethod]
    public void String_长度字段撒谎时抛出而不是预分配()
    {
        // 声称 1 GiB 但后面只有 2 字节。按长度预分配的实现在这里会当场吃掉 1 GiB。
        AssertWireError([0x40, 0x00, 0x00, 0x00, 0x61, 0x62],
            (ref SshDataReader r) => r.ReadString(maxLength: int.MaxValue));
    }

    [TestMethod]
    public void String_截断时抛出()
    {
        AssertWireError([0, 0, 0, 10, (byte)'a'], (ref SshDataReader r) => r.ReadString(64));
    }

    // -------------------------------------------------------------- utf8 解码

    [TestMethod]
    public void Utf8String_默认宽容_乱码不抛异常()
    {
        // spec/00 §3.3：横幅、错误消息这类展示性内容，
        // 因为一个乱码字节把整条连接打掉对用户是纯粹的损失。
        SshDataReader r = Reader(0, 0, 0, 2, 0xFF, 0xFE);
        string s = r.ReadUtf8String(64);
        Assert.IsFalse(string.IsNullOrEmpty(s), "宽容模式应当返回替换字符而不是空");
    }

    [TestMethod]
    public void Utf8String_严格模式下乱码抛出()
    {
        // 用户名与算法名参与协议判定与签名输入，解码失败必须视为协议错误。
        AssertWireError([0, 0, 0, 2, 0xFF, 0xFE],
            (ref SshDataReader r) => r.ReadUtf8String(64, strict: true));
    }

    [TestMethod]
    public void Utf8String_多字节字符往返正确()
    {
        byte[] raw = [0, 0, 0, 3, 0xE4, 0xB8, 0xAD];
        SshDataReader r = new(new ReadOnlySequence<byte>(raw));
        Assert.AreEqual("中", r.ReadUtf8String(64, strict: true));
    }

    // ------------------------------------------------------------------ mpint

    [TestMethod]
    public void Mpint_零是空序列()
    {
        SshDataReader r = Reader(0, 0, 0, 0);
        Assert.IsTrue(r.ReadMpint(64).IsEmpty);
    }

    [TestMethod]
    public void Mpint_去掉补码用的前导零()
    {
        SshDataReader r = Reader(0, 0, 0, 2, 0x00, 0x80);
        CollectionAssert.AreEqual(new byte[] { 0x80 }, r.ReadMpint(64).ToArray());
    }

    [TestMethod]
    public void Mpint_没有前导零时原样返回()
    {
        SshDataReader r = Reader(0, 0, 0, 1, 0x7F);
        CollectionAssert.AreEqual(new byte[] { 0x7F }, r.ReadMpint(64).ToArray());
    }

    [TestMethod]
    public void Mpint_负数被拒绝()
    {
        // 负数在 SSH 里没有正当用途。接受它只会让一个被篡改的字段
        // 悄悄变成一个巨大的正数。
        AssertWireError([0, 0, 0, 1, 0x80], (ref SshDataReader r) => r.ReadMpint(64));
    }

    [TestMethod]
    public void Mpint_写了再读能还原()
    {
        byte[][] values =
        [
            [0x01],
            [0x7F],
            [0x80],
            [0xFF, 0x00, 0x01],
            [0x09, 0xA3, 0x78, 0xF9, 0xB2, 0xE3, 0x32, 0xA7],
        ];

        foreach (byte[] value in values)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var w = new SshDataWriter(buffer);
            w.WriteMpint(value);

            var r = new SshDataReader(new ReadOnlySequence<byte>(buffer.WrittenMemory));
            CollectionAssert.AreEqual(value, r.ReadMpint(1024).ToArray(),
                $"mpint 往返失真: {Convert.ToHexString(value)}");
            Assert.IsTrue(r.IsEmpty, "往返之后应当恰好读完");
        }
    }

    // --------------------------------------------------------------- name-list

    [TestMethod]
    public void NameList_按逗号切分()
    {
        byte[] raw = [0, 0, 0, 9, (byte)'z', (byte)'l', (byte)'i', (byte)'b', (byte)',',
                      (byte)'n', (byte)'o', (byte)'n', (byte)'e'];
        SshDataReader r = new(new ReadOnlySequence<byte>(raw));
        CollectionAssert.AreEqual(new[] { "zlib", "none" }, r.ReadNameList(1024));
    }

    [TestMethod]
    public void 空NameList_返回空数组()
    {
        SshDataReader r = Reader(0, 0, 0, 0);
        Assert.AreEqual(0, r.ReadNameList(1024).Length);
    }

    [TestMethod]
    public void NameList_含非可打印字节时抛出()
    {
        AssertWireError([0, 0, 0, 3, (byte)'a', 0x00, (byte)'b'],
            (ref SshDataReader r) => r.ReadNameList(1024));
    }

    [TestMethod]
    public void NameList_区分大小写且不做规范化()
    {
        // 协商是按字节比对的。任何大小写折叠都会让协商结果与对端的理解产生分歧。
        byte[] raw = [0, 0, 0, 7, (byte)'A', (byte)'e', (byte)'S', (byte)'1', (byte)'2', (byte)'8', (byte)'x'];
        SshDataReader r = new(new ReadOnlySequence<byte>(raw));
        Assert.AreEqual("AeS128x", r.ReadNameList(1024)[0]);
    }

    // ------------------------------------------------------------------ 收尾

    [TestMethod]
    public void ExpectEnd_在有剩余字节时抛出()
    {
        // 多出来的字节意味着我们对这个报文的理解有误。
        // 沉默地忽略它们，会让一个真实的解析错误永远不被发现。
        AssertWireError([0, 0, 0, 0, 0xAA], (ref SshDataReader r) =>
        {
            _ = r.ReadUInt32();
            r.ExpectEnd("TestMessage");
        });
    }

    [TestMethod]
    public void ExpectEnd_读完时通过()
    {
        SshDataReader r = Reader(0, 0, 0, 0);
        _ = r.ReadUInt32();
        r.ExpectEnd("TestMessage");
    }

    [TestMethod]
    public void 消息编号不符时抛出()
    {
        AssertWireError([(byte)SshMessageNumber.ChannelData],
            (ref SshDataReader r) => r.ReadMessageNumber(SshMessageNumber.ChannelClose));
    }
}
