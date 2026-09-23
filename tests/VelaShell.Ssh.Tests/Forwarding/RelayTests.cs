// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §2.2、§三、§五、§六
//
// 〔决策 §六〕搬运层与 SSH 无关 —— 所以半关闭、计量、错误收尾这条主路径
// 不必架一台真服务器就能验证。这一整个文件都不需要任何 SSH。

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class RelayTests
{
    /// <summary>一个纯内存的搬运端点。</summary>
    private sealed class FakeEndpoint : IRelayEndpoint
    {
        private readonly Pipe _inbound = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _outbound = new(new PipeOptions(useSynchronizationContext: false));

        /// <inheritdoc />
        public PipeReader Input => _inbound.Reader;

        /// <inheritdoc />
        public PipeWriter Output => _outbound.Writer;

        /// <summary>被搬进来的内容从这里读。</summary>
        public PipeReader Received => _outbound.Reader;

        /// <summary>对面告诉过我们「不再发了」几次。</summary>
        public int SendCompletedCount { get; private set; }

        /// <summary>往这一端喂数据（模拟外部写入）。</summary>
        public async ValueTask FeedAsync(byte[] data)
        {
            await _inbound.Writer.WriteAsync(data);
        }

        /// <summary>这一端不再有数据了。</summary>
        public void FeedComplete() => _inbound.Writer.Complete();

        /// <inheritdoc />
        public ValueTask CompleteSendAsync(CancellationToken cancellationToken)
        {
            SendCompletedCount++;
            _outbound.Writer.Complete();
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _inbound.Writer.Complete();
            _inbound.Reader.Complete();
            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> ReadAllReceivedAsync()
        {
            ArrayBufferWriter<byte> buffer = new();
            while (true)
            {
                ReadResult read = await Received.ReadAsync();
                foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                {
                    buffer.Write(segment.Span);
                }
                Received.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }
            }
            return buffer.WrittenSpan.ToArray();
        }
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [TestMethod]
    public async Task 双向都搬到了()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        await left.FeedAsync(Text("从左到右"));
        left.FeedComplete();
        await right.FeedAsync(Text("从右到左"));
        right.FeedComplete();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        byte[] atRight = await right.ReadAllReceivedAsync();
        byte[] atLeft = await left.ReadAllReceivedAsync();
        RelayResult result = await relay;

        Assert.AreEqual("从左到右", Encoding.UTF8.GetString(atRight));
        Assert.AreEqual("从右到左", Encoding.UTF8.GetString(atLeft));
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task 一端发完之后另一端仍然能继续发()
    {
        // 这是半关闭的决定性用例。把 EOF 当成「连接结束」的实现会在这里
        // 把整条连接关掉 —— 症状是 curl 通过隧道 POST 完请求体后收不到响应。
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        // 左边发完就不发了。
        await left.FeedAsync(Text("请求体"));
        left.FeedComplete();

        // 右边**在这之后**才开始发 —— 响应。
        await Task.Delay(50);
        await right.FeedAsync(Text("响应体"));
        right.FeedComplete();

        byte[] atRight = await right.ReadAllReceivedAsync();
        byte[] atLeft = await left.ReadAllReceivedAsync();
        await relay;

        Assert.AreEqual("请求体", Encoding.UTF8.GetString(atRight));
        Assert.AreEqual("响应体", Encoding.UTF8.GetString(atLeft),
            "左端发完 EOF 之后，右端的数据必须照样能搬过来");
    }

    [TestMethod]
    public async Task 每个方向各自发出一次半关闭()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        left.FeedComplete();
        right.FeedComplete();

        await DuplexRelay.RunAsync(left, right);

        // 半关闭是**逐方向**的：左读完 → 告诉右「我发完了」，反之亦然。
        Assert.AreEqual(1, left.SendCompletedCount);
        Assert.AreEqual(1, right.SendCompletedCount);
    }

    [TestMethod]
    public async Task 字节计量分方向且不含协议开销()
    {
        FakeEndpoint left = new();
        FakeEndpoint right = new();

        await left.FeedAsync(new byte[1000]);
        left.FeedComplete();
        await right.FeedAsync(new byte[250]);
        right.FeedComplete();

        long up = 0;
        long down = 0;

        Task<RelayResult> relay = DuplexRelay.RunAsync(
            left, right,
            onBytesFromLeft: n => Interlocked.Add(ref up, n),
            onBytesFromRight: n => Interlocked.Add(ref down, n));

        _ = await right.ReadAllReceivedAsync();
        _ = await left.ReadAllReceivedAsync();
        RelayResult result = await relay;

        // 计量在搬运循环里累加 —— 通道层的数字含协议开销，
        // 而面板上要显示的是应用数据量。
        Assert.AreEqual(1000, up);
        Assert.AreEqual(250, down);
        Assert.AreEqual(1000, result.BytesFromLeft);
        Assert.AreEqual(250, result.BytesFromRight);
    }

    [TestMethod]
    public async Task 大块数据能完整搬过去()
    {
        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        FakeEndpoint left = new();
        FakeEndpoint right = new();

        Task<RelayResult> relay = DuplexRelay.RunAsync(left, right);

        Task feed = Task.Run(async () =>
        {
            await left.FeedAsync(payload);
            left.FeedComplete();
            right.FeedComplete();
        });

        byte[] atRight = await right.ReadAllReceivedAsync();
        _ = await left.ReadAllReceivedAsync();
        await feed;
        await relay;

        CollectionAssert.AreEqual(payload, atRight);
    }
}
