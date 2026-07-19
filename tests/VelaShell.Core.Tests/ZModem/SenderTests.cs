using System.Text;
using VelaShell.Core.ZModem.Abstractions;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Core.Tests.ZModem;

/// <summary>内存文件来源:直接给出待发送内容,不碰磁盘。</summary>
internal sealed class InMemoryFileSource((string Name, byte[] Data)[] files) : IZModemFileSource
{
    private readonly Dictionary<string, byte[]> _data =
        files.ToDictionary(f => f.Name, f => f.Data);

    public ValueTask<IReadOnlyList<ZModemOutgoingFile>> GetFilesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ZModemOutgoingFile>>(
            [.. _data.Select(kv => new ZModemOutgoingFile($"/tmp/{kv.Key}", kv.Key, kv.Value.Length, null))]);

    public ValueTask<Stream> OpenReadAsync(ZModemOutgoingFile file, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream(_data[file.RemoteName], writable: false));
}

/// <summary>
/// 发送方(远端 <c>rz</c> 场景)的状态机测试:把 <see cref="ZModemSender" /> 与
/// <see cref="ZModemReceiver" /> 在内存双工上对接跑完整批次,验证字节保真与批量收束。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class SenderTests
{
    private static async Task<(ZModemSession Send, ZModemSession Receive, InMemoryFileSink Sink)> RoundTripAsync(
        (string Name, byte[] Data)[] files)
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<ZModemSession> receive = new ZModemReceiver(b, sink).ReceiveAsync(cts.Token);
        Task<ZModemSession> send = new ZModemSender(a, new InMemoryFileSource(files)).SendAsync(cts.Token);

        await Task.WhenAll(receive, send);
        return (send.Result, receive.Result, sink);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Send_SingleTextFile_ArrivesIntact()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello from the sender\n");
        (ZModemSession send, ZModemSession receive, InMemoryFileSink sink) =
            await RoundTripAsync([("upload.txt", content)]);

        Assert.AreEqual(ZModemTransferStatus.Completed, send.Status);
        Assert.AreEqual(ZModemTransferStatus.Completed, receive.Status);
        CollectionAssert.AreEqual(content, sink.Completed["upload.txt"]);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Send_BinaryFileSpanningManySubpackets_ArrivesIntact()
    {
        // 覆盖 ZDLE 转义路径:包含 0x18/0x10/0x11/0x13 与全部字节值。
        byte[] content = new byte[64 * 1024];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 256);
        }

        (ZModemSession send, _, InMemoryFileSink sink) = await RoundTripAsync([("blob.bin", content)]);

        Assert.AreEqual(ZModemTransferStatus.Completed, send.Status);
        CollectionAssert.AreEqual(content, sink.Completed["blob.bin"]);
    }

    /// <summary>文件长度恰好是子包大小的整数倍:尾包边界最容易被写错(多发/少发一包)。</summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task Send_FileExactlyMultipleOfSubpacketSize_ArrivesIntact()
    {
        byte[] content = new byte[ZModemOptions.Default.SubpacketSize * 4];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i * 7 % 251);
        }

        (ZModemSession send, _, InMemoryFileSink sink) = await RoundTripAsync([("aligned.bin", content)]);

        Assert.AreEqual(ZModemTransferStatus.Completed, send.Status);
        CollectionAssert.AreEqual(content, sink.Completed["aligned.bin"]);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Send_EmptyFile_Succeeds()
    {
        (ZModemSession send, _, InMemoryFileSink sink) = await RoundTripAsync([("empty.txt", [])]);

        Assert.AreEqual(ZModemTransferStatus.Completed, send.Status);
        Assert.IsEmpty(sink.Completed["empty.txt"]);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Send_BatchOfFiles_AllArrive()
    {
        (string, byte[])[] files =
        [
            ("a.txt", Encoding.UTF8.GetBytes("first")),
            ("b.txt", Encoding.UTF8.GetBytes("second")),
            ("c.bin", [0x18, 0x10, 0x11, 0x13, 0x00, 0xFF])
        ];

        (ZModemSession send, _, InMemoryFileSink sink) = await RoundTripAsync(files);

        Assert.AreEqual(ZModemTransferStatus.Completed, send.Status);
        foreach ((string name, byte[] data) in files)
        {
            CollectionAssert.AreEqual(data, sink.Completed[name], $"{name} 内容不符");
        }
    }

    /// <summary>接收方拒收(ZSKIP)时,发送方应跳过该文件并继续批次。</summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task Send_ReceiverSkipsFile_SenderContinues()
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var sink = new InMemoryFileSink { NextDisposition = ZModemFileDisposition.Skip };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<ZModemSession> receive = new ZModemReceiver(b, sink).ReceiveAsync(cts.Token);
        Task<ZModemSession> send = new ZModemSender(
            a,
            new InMemoryFileSource([("skipped.txt", Encoding.UTF8.GetBytes("nope"))])).SendAsync(cts.Token);

        await Task.WhenAll(receive, send);

        Assert.AreEqual(ZModemTransferStatus.Completed, send.Result.Status);
        Assert.AreEqual(ZModemTransferStatus.Skipped, send.Result.Items[0].Status);
        Assert.IsFalse(sink.Completed.ContainsKey("skipped.txt"));
    }

    /// <summary>
    /// 用户在文件选择框点取消(空清单)时:发送方应走优雅收尾(发 ZFIN),而不是发 CAN 中止序列。
    /// ZFIN 让远端 rz 干净退回 shell,不会卡在 "rz waiting to receive." 或打印中止错误。
    /// </summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task Send_UserCancelsFilePicker_FinishesGracefullyWithZfinNotCancel()
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        var options = new ZModemOptions
        {
            PostCancelDrainIdle = TimeSpan.FromMilliseconds(150),
            PostCancelDrainMax = TimeSpan.FromMilliseconds(600)
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 先让对端把 ZRINIT 放进链路,发送方才能完成握手再问文件。
        await b.WriteAsync(
            ZModemFrameWriter.Write(
                new ZModemHeader(ZModemFrameType.ZRINIT, 0, 0, 0, ZModemCapabilities.CANFC32),
                ZModemHeaderFormat.Hex),
            cts.Token);

        ZModemSession session = await new ZModemSender(a, new InMemoryFileSource([]), options).SendAsync(cts.Token);

        Assert.AreEqual(ZModemTransferStatus.Cancelled, session.Status);

        byte[] outbound = await a.DrainOutboundAsync();
        // 应发过 ZFIN(优雅收尾),不应发 CAN 中止序列。
        byte[] zfin = ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZFIN), ZModemHeaderFormat.Hex);
        Assert.IsTrue(ContainsSequence(outbound, zfin), "取消应发 ZFIN 优雅收尾");
        Assert.IsFalse(
            ContainsSequence(outbound, [ZModemConstants.CAN, ZModemConstants.CAN, ZModemConstants.CAN, ZModemConstants.CAN, ZModemConstants.CAN]),
            "取消不应发 CAN 中止序列(那会让 rz 报错而非干净退出)");
    }

    /// <summary>
    /// 端到端:发送方取消(空文件)时,对接的真实接收方状态机应把会话判定为「完成」(收到 ZFIN 干净收束),
    /// 而不是一直挂着等文件。这精确复刻 rz 一侧收到 ZFIN 后退回 shell 的行为。
    /// </summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task Send_CancelWithEmptyBatch_ReceiverEndsCleanly()
    {
        (InMemoryByteDuplex a, InMemoryByteDuplex b) = InMemoryByteDuplex.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // b 端扮演 rz(接收方状态机):它发 ZRINIT、等文件;收到 ZFIN 应干净结束。
        var receiverSink = new InMemoryFileSink();
        Task<ZModemSession> receive = new ZModemReceiver(b, receiverSink).ReceiveAsync(cts.Token);
        Task<ZModemSession> send = new ZModemSender(a, new InMemoryFileSource([])).SendAsync(cts.Token);

        await Task.WhenAll(receive, send);

        Assert.AreEqual(ZModemTransferStatus.Cancelled, send.Result.Status);
        Assert.AreEqual(ZModemTransferStatus.Completed, receive.Result.Status, "接收方收到 ZFIN 应干净收束,而非挂起");
        Assert.IsEmpty(receiverSink.Completed, "取消批次不应落地任何文件");
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return true;
            }
        }
        return false;
    }
}
