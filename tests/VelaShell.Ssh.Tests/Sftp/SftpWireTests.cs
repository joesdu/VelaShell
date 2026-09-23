// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/06-sftp.md §二、§三、§四
//
// SftpWire 是纯函数（bytes ↔ 报文，无状态、无 I/O），所以这里能逐字节断言，
// 不需要任何对端。协议层的错误一旦漏到集成测试里，症状就只剩
// 「服务端说 BAD_MESSAGE」——那时候看不出是哪个字段错了。

using System.Buffers;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpWireTests
{
    private static ReadOnlySequence<byte> Seq(params byte[] bytes) => new(bytes);

    // ------------------------------------------------------------ 分帧

    [TestMethod]
    public void 外层帧的长度不含自身四字节()
    {
        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteInit(buffer, version: 3);

        byte[] bytes = buffer.WrittenSpan.ToArray();

        // length(4) + type(1) + version(4) = 9 字节，而 length 字段写的是 5。
        Assert.AreEqual(9, bytes.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 5 }, bytes[..4], "length 不含自身这 4 字节");
        Assert.AreEqual((byte)SftpMessageType.Init, bytes[4]);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 3 }, bytes[5..]);
    }

    [TestMethod]
    public void 数据不够一个完整帧时不消费任何字节()
    {
        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteInit(buffer);

        byte[] full = buffer.WrittenSpan.ToArray();

        for (int cut = 0; cut < full.Length; cut++)
        {
            ReadOnlySequence<byte> partial = new(full[..cut]);
            Assert.IsFalse(SftpWire.TryReadFrame(ref partial, out _), $"只有 {cut} 字节时不该解出帧");
            Assert.AreEqual(cut, partial.Length, "解不出来就不能消费任何字节");
        }

        ReadOnlySequence<byte> complete = new(full);
        Assert.IsTrue(SftpWire.TryReadFrame(ref complete, out SftpFrame frame));
        Assert.AreEqual(SftpMessageType.Init, frame.Type);
        Assert.AreEqual(0, complete.Length);
    }

    [TestMethod]
    public void 一次能连着解出多个帧()
    {
        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WritePathRequest(buffer, SftpMessageType.Stat, 1, "/a");
        SftpWire.WritePathRequest(buffer, SftpMessageType.LStat, 2, "/b");
        SftpWire.WritePathRequest(buffer, SftpMessageType.RealPath, 3, "/c");

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        List<SftpMessageType> types = [];

        while (SftpWire.TryReadFrame(ref input, out SftpFrame frame))
        {
            types.Add(frame.Type);
        }

        CollectionAssert.AreEqual(
            new[] { SftpMessageType.Stat, SftpMessageType.LStat, SftpMessageType.RealPath },
            types);
        Assert.AreEqual(0, input.Length);
    }

    [TestMethod]
    public void 长度字段超上限时立刻拒绝()
    {
        // 这不是「数据还没到齐」，是对端在让我们分配一块巨大的缓冲。
        // 老老实实等它到齐，就等于把内存交给对端支配。
        ReadOnlySequence<byte> input = Seq(0xFF, 0xFF, 0xFF, 0xFF);

        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => SftpWire.TryReadFrame(ref input, out _));

        StringAssert.Contains(error.Message, "超过上限");
    }

    [TestMethod]
    public void 长度为零的帧被拒绝()
    {
        ReadOnlySequence<byte> input = Seq(0, 0, 0, 0);

        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => SftpWire.TryReadFrame(ref input, out _));

        StringAssert.Contains(error.Message, "连类型字节都放不下");
    }

    [TestMethod]
    public void 跨段的帧也能解出来()
    {
        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WritePathRequest(buffer, SftpMessageType.Stat, 7, "/some/long/path/name");
        byte[] full = buffer.WrittenSpan.ToArray();

        // 真实的 PipeReader 给的就是分段的 ReadOnlySequence，
        // 只认 FirstSpan 的实现会在这里读出垃圾。
        ReadOnlySequence<byte> split = Split(full, 3);

        Assert.IsTrue(SftpWire.TryReadFrame(ref split, out SftpFrame frame));
        Assert.AreEqual(SftpMessageType.Stat, frame.Type);
        Assert.AreEqual(7U, SftpWire.ReadRequestId(frame.Payload));
    }

    // ------------------------------------------------------------ SYMLINK 的参数顺序

    [TestMethod]
    public void 建符号链接时先写目标再写链接位置()
    {
        // ⚠️⚠️ **SFTP 里最有名的一个坑。**
        //
        // draft-02 规定的顺序是先 linkpath 后 targetpath，
        // 但 OpenSSH 的实现把两者写反了（bugzilla #861），而 OpenSSH 是事实标准。
        //
        // 「顺手按 RFC 修正」它的后果：链接被建在你本想指向的位置上，
        // **而且不报错**。这条用例就是拦住那次「修正」的。
        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteSymLink(buffer, 42, targetPath: "/real/file", linkPath: "/the/link");

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        Assert.IsTrue(SftpWire.TryReadFrame(ref input, out SftpFrame frame));
        Assert.AreEqual(SftpMessageType.SymLink, frame.Type);

        VelaShell.Ssh.Protocol.SshDataReader reader = new(frame.Payload);
        Assert.AreEqual(42U, reader.ReadUInt32());
        Assert.AreEqual("/real/file", reader.ReadUtf8String(1024), "第一个字段是 targetpath（OpenSSH 顺序）");
        Assert.AreEqual("/the/link", reader.ReadUtf8String(1024), "第二个字段才是 linkpath");
    }

    // ------------------------------------------------------------ ATTRS

    [TestMethod]
    public void 空属性只写一个标志字段()
    {
        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteOpen(buffer, 1, "/f", SftpOpenMode.Read, SftpFileAttributes.Empty);

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        SftpWire.TryReadFrame(ref input, out SftpFrame frame);

        VelaShell.Ssh.Protocol.SshDataReader reader = new(frame.Payload);
        reader.ReadUInt32();                  // request-id
        reader.ReadUtf8String(1024);          // path
        reader.ReadUInt32();                  // pflags

        Assert.AreEqual(0U, reader.ReadUInt32(), "flags 为 0 时后面不该再有任何字段");
        Assert.IsTrue(reader.IsEmpty);
    }

    [TestMethod]
    public void 属性能原样往返()
    {
        SftpFileAttributes original = new()
        {
            Flags = SftpAttributeFields.Size | SftpAttributeFields.UidGid
                    | SftpAttributeFields.Permissions | SftpAttributeFields.Times,
            Size = 123_456_789_012,
            UserId = 1000,
            GroupId = 1000,
            Permissions = 0x81A4,             // 0o100644：普通文件 + 0644
            AccessTime = 1_700_000_000,
            ModifyTime = 1_700_000_001,
            Extended = [],
        };

        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteSetStat(buffer, 9, "/f", original);

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        SftpWire.TryReadFrame(ref input, out SftpFrame frame);

        // 跳过 request-id 与路径，剩下的就是 ATTRS。
        VelaShell.Ssh.Protocol.SshDataReader reader = new(frame.Payload);
        reader.ReadUInt32();
        reader.ReadUtf8String(1024);

        SftpFileAttributes roundTripped = SftpWire.ReadAttrs(frame.Payload.Slice(reader.Consumed));

        Assert.AreEqual(original.Size, roundTripped.Size);
        Assert.AreEqual(original.UserId, roundTripped.UserId);
        Assert.AreEqual(original.GroupId, roundTripped.GroupId);
        Assert.AreEqual(original.Permissions, roundTripped.Permissions);
        Assert.AreEqual(original.AccessTime, roundTripped.AccessTime);
        Assert.AreEqual(original.ModifyTime, roundTripped.ModifyTime);
    }

    [TestMethod]
    public void 属主与属组共用一个标志位()
    {
        // draft-02 §5：两者共用 0x02。只写一个会让后面所有字段错位。
        SftpFileAttributes attributes = new()
        {
            Flags = SftpAttributeFields.UidGid | SftpAttributeFields.Permissions,
            UserId = 0xAAAA,
            GroupId = 0xBBBB,
            Permissions = 0x81A4,
            Extended = [],
        };

        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteSetStat(buffer, 1, "/f", attributes);

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        SftpWire.TryReadFrame(ref input, out SftpFrame frame);

        VelaShell.Ssh.Protocol.SshDataReader reader = new(frame.Payload);
        reader.ReadUInt32();
        reader.ReadUtf8String(1024);

        Assert.AreEqual((uint)attributes.Flags, reader.ReadUInt32());
        Assert.AreEqual(0xAAAAU, reader.ReadUInt32(), "uid");
        Assert.AreEqual(0xBBBBU, reader.ReadUInt32(), "gid 紧跟在 uid 后面，同一个标志位");
        Assert.AreEqual(0x81A4U, reader.ReadUInt32(), "权限排在两者之后");
    }

    [TestMethod]
    public void 访问时间与修改时间共用一个标志位()
    {
        SftpFileAttributes attributes =
            SftpFileAttributes.WithTimes(
                DateTimeOffset.FromUnixTimeSeconds(111),
                DateTimeOffset.FromUnixTimeSeconds(222));

        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteSetStat(buffer, 1, "/f", attributes);

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        SftpWire.TryReadFrame(ref input, out SftpFrame frame);

        VelaShell.Ssh.Protocol.SshDataReader reader = new(frame.Payload);
        reader.ReadUInt32();
        reader.ReadUtf8String(1024);

        Assert.AreEqual((uint)SftpAttributeFields.Times, reader.ReadUInt32());
        Assert.AreEqual(111U, reader.ReadUInt32(), "atime");
        Assert.AreEqual(222U, reader.ReadUInt32(), "mtime 紧跟在 atime 后面");
        Assert.IsTrue(reader.IsEmpty);
    }

    [TestMethod]
    public void 文件类型只能从权限的高位取()
    {
        // v3 **没有**单独的类型字段。
        SftpFileAttributes file = new()
        {
            Flags = SftpAttributeFields.Permissions,
            Permissions = SftpProtocol.FileTypeRegular | 0b110_100_100,
            Extended = [],
        };
        SftpFileAttributes directory = new()
        {
            Flags = SftpAttributeFields.Permissions,
            Permissions = SftpProtocol.FileTypeDirectory | 0b111_101_101,
            Extended = [],
        };
        SftpFileAttributes link = new()
        {
            Flags = SftpAttributeFields.Permissions,
            Permissions = SftpProtocol.FileTypeSymbolicLink | 0b111_111_111,
            Extended = [],
        };

        Assert.IsTrue(file.IsRegularFile);
        Assert.IsTrue(directory.IsDirectory);
        Assert.IsTrue(link.IsSymbolicLink);

        Assert.AreEqual(0b110_100_100u, file.PermissionBits);
        Assert.AreEqual(0b111_101_101u, directory.PermissionBits);

        // 没带权限字段时**不能猜** —— 不知道就是不知道。
        SftpFileAttributes unknown = SftpFileAttributes.Empty;
        Assert.IsFalse(unknown.IsDirectory);
        Assert.IsFalse(unknown.IsRegularFile);
        Assert.IsFalse(unknown.IsSymbolicLink);
    }

    [TestMethod]
    public void 时间按有符号读()
    {
        // 2038 之后 OpenSSH 发的是负数（有符号溢出）。
        // 按无符号读能撑到 2106 年，但那会与对端对不上账。
        SftpFileAttributes attributes = new()
        {
            Flags = SftpAttributeFields.Times,
            AccessTime = -1,
            ModifyTime = -2,
            Extended = [],
        };

        ArrayBufferWriter<byte> buffer = new();
        SftpWire.WriteSetStat(buffer, 1, "/f", attributes);

        ReadOnlySequence<byte> input = new(buffer.WrittenSpan.ToArray());
        SftpWire.TryReadFrame(ref input, out SftpFrame frame);

        VelaShell.Ssh.Protocol.SshDataReader reader = new(frame.Payload);
        reader.ReadUInt32();
        reader.ReadUtf8String(1024);

        SftpFileAttributes back = SftpWire.ReadAttrs(frame.Payload.Slice(reader.Consumed));
        Assert.AreEqual(-1, back.AccessTime);
        Assert.AreEqual(-2, back.ModifyTime);
    }

    // ------------------------------------------------------------ 应答

    [TestMethod]
    public void 状态应答里没有文本时不报错()
    {
        // 有些老服务端只给码不给文本。那不是协议违规。
        ArrayBufferWriter<byte> payload = new();
        VelaShell.Ssh.Protocol.SshDataWriter writer = new(payload);
        writer.WriteUInt32((uint)SftpStatusCode.NoSuchFile);

        (SftpStatusCode code, string message) =
            SftpWire.ReadStatus(new ReadOnlySequence<byte>(payload.WrittenSpan.ToArray()));

        Assert.AreEqual(SftpStatusCode.NoSuchFile, code);
        Assert.AreEqual("", message);
    }

    [TestMethod]
    public void 句柄超过256字节时拒绝()
    {
        ArrayBufferWriter<byte> payload = new();
        VelaShell.Ssh.Protocol.SshDataWriter writer = new(payload);
        writer.WriteString(new byte[SftpProtocol.MaxHandleLength + 1]);

        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => SftpWire.ReadHandle(new ReadOnlySequence<byte>(payload.WrittenSpan.ToArray())));

        StringAssert.Contains(error.Message, "上限");
    }

    [TestMethod]
    public void NAME里的count与实际项数不符时报协议错()
    {
        ArrayBufferWriter<byte> payload = new();
        VelaShell.Ssh.Protocol.SshDataWriter writer = new(payload);
        writer.WriteUInt32(3);                // 声称有 3 项
        writer.WriteUtf8String("only-one");   // 实际只写了 1 项
        writer.WriteUtf8String("longname");
        writer.WriteUInt32(0);                // 空 ATTRS

        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => SftpWire.ReadName(new ReadOnlySequence<byte>(payload.WrittenSpan.ToArray())));

        StringAssert.Contains(error.Message, "声称有 3 项");
    }

    [TestMethod]
    public void DATA比请求的还多时报协议错()
    {
        ArrayBufferWriter<byte> payload = new();
        VelaShell.Ssh.Protocol.SshDataWriter writer = new(payload);
        writer.WriteString(new byte[100]);

        byte[] destination = new byte[50];

        SshProtocolException error = Assert.ThrowsExactly<SshProtocolException>(
            () => SftpWire.ReadDataInto(
                new ReadOnlySequence<byte>(payload.WrittenSpan.ToArray()), destination));

        StringAssert.Contains(error.Message, "超过我们请求的");
    }

    [TestMethod]
    public void VERSION里的扩展被原样收下()
    {
        ArrayBufferWriter<byte> payload = new();
        VelaShell.Ssh.Protocol.SshDataWriter writer = new(payload);
        writer.WriteUInt32(3);
        writer.WriteUtf8String(SftpExtensionNames.PosixRename);
        writer.WriteUtf8String("1");
        writer.WriteUtf8String("未来才定义的扩展");
        writer.WriteUtf8String("随便什么");

        (uint version, IReadOnlyDictionary<string, byte[]> extensions) =
            SftpWire.ReadVersion(new ReadOnlySequence<byte>(payload.WrittenSpan.ToArray()));

        Assert.AreEqual(3U, version);
        Assert.AreEqual(2, extensions.Count);
        Assert.IsTrue(extensions.ContainsKey(SftpExtensionNames.PosixRename));

        // 不认识的扩展也要收下 —— 生态就是这么演进的，
        // 为一个没见过的名字报错会让我们无法与新实现共处。
        Assert.IsTrue(extensions.ContainsKey("未来才定义的扩展"));
    }

    // ------------------------------------------------------------ 连续确认区间

    [TestMethod]
    public void 顺序确认时区间合并成一段()
    {
        AckedRangeSet probe = new();

        for (int i = 0; i < 100; i++)
        {
            probe.Add(i * 1000, 1000);
        }

        Assert.AreEqual(100_000, probe.DurableLength);
        Assert.AreEqual(1, probe.RangeCount,
            "顺序写入时集合大小恒为 1 —— 有序数组加尾部快路径就是为此");
    }

    [TestMethod]
    public void 乱序确认时连续长度停在第一个空洞()
    {
        AckedRangeSet probe = new();

        probe.Add(0, 100);
        probe.Add(300, 100);     // 跳过 100–300
        probe.Add(400, 100);

        Assert.AreEqual(100, probe.DurableLength, "只有 [0,100) 是连续可信的");
        Assert.AreEqual(500, probe.HighestAckedOffset, "而「服务端报告的文件长度」会是 500 —— 差的这 400 字节正是空洞");
        Assert.AreEqual(2, probe.RangeCount, "[0,100) 与 [300,500)");
    }

    [TestMethod]
    public void 空洞被补上之后区间合并()
    {
        AckedRangeSet probe = new();

        probe.Add(0, 100);
        probe.Add(200, 100);
        Assert.AreEqual(100, probe.DurableLength);

        probe.Add(100, 100);   // 把空洞填上
        Assert.AreEqual(300, probe.DurableLength, "补上空洞之后三段应当连成一片");
        Assert.AreEqual(1, probe.RangeCount);
    }

    [TestMethod]
    public void 开头就有空洞时一个字节都不可信()
    {
        AckedRangeSet probe = new();
        probe.Add(1000, 1000);

        // 从 0 开始不连续 → 续传只能从 0 开始。报别的数就是在骗调用方。
        Assert.AreEqual(0, probe.DurableLength);
    }

    [TestMethod]
    public void 乱序到达也能算对连续长度()
    {
        AckedRangeSet probe = new();

        // 倒着确认 —— 流水线里完全可能。
        int[] offsets = [9, 7, 5, 3, 1, 8, 6, 4, 2, 0];
        foreach (int i in offsets)
        {
            probe.Add(i * 100, 100);
        }

        Assert.AreEqual(1000, probe.DurableLength);
        Assert.AreEqual(1, probe.RangeCount, "全部到齐之后应当合并成一段");
    }

    private static ReadOnlySequence<byte> Split(byte[] data, int segmentSize)
    {
        SplitSegment? first = null;
        SplitSegment? last = null;

        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            int length = Math.Min(segmentSize, data.Length - offset);
            ReadOnlyMemory<byte> memory = data.AsMemory(offset, length);

            if (first is null)
            {
                first = new SplitSegment(memory, 0);
                last = first;
            }
            else
            {
                last = last!.Append(memory);
            }
        }

        return first is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private sealed class SplitSegment : ReadOnlySequenceSegment<byte>
    {
        public SplitSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public SplitSegment Append(ReadOnlyMemory<byte> memory)
        {
            SplitSegment segment = new(memory, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
    }
}
