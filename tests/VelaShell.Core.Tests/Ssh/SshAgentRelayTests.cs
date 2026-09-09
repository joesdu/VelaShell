using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// agent 转发的搬运循环:远端 ⇄ 策略闸门 ⇄ 本机 agent。
/// </summary>
/// <remarks>
/// 这条路径在真机上极难复现 —— 要一台开着 agent 的本机、一台允许 streamlocal 转发的远端,
/// 还要远端上有人恰好去 <c>ssh-add -D</c>。而它出错的代价是本机 agent 被远端拿去做了不该做的事。
/// <see cref="SshAgentRelay" /> 因此只依赖两条 <see cref="Stream" />,在这里用内存流逐条钉住。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class SshAgentRelayTests
{
    /// <summary>
    /// 远端想清空 agent:必须就地回 FAILURE,而且**本机 agent 一次都不能被打开** ——
    /// "连都没连上去"才是这道闸门的意义,只是把请求转过去再忽略应答是不够的。
    /// </summary>
    [TestMethod]
    public async Task Pump_RemoveAllIdentities_IsRefusedWithoutTouchingTheAgent()
    {
        var remote = new DuplexPipe(SshAgentProtocol.Frame([19])); // REMOVE_ALL_IDENTITIES
        int agentOpened = 0;
        var events = new List<AgentForwardRequestEvent>();

        int handled = await SshAgentRelay.PumpAsync(
            remote,
            _ =>
            {
                agentOpened++;
                return Task.FromResult<Stream>(new MemoryStream());
            },
            events.Add,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, handled);
        Assert.AreEqual(0, agentOpened, "被拒绝的请求不该惊动本机 agent");
        Assert.HasCount(1, events);
        Assert.IsFalse(events[0].Allowed);
        AssertSingleReply(remote, SshAgentProtocol.Failure);
    }

    /// <summary>签名请求照常转发,应答原样送回,并且事件里带上了用哪把钥匙签的。</summary>
    [TestMethod]
    public async Task Pump_SignRequest_IsForwardedAndAnswerRelayed()
    {
        byte[] blob = SshAgentProtocolTests.BuildKeyBlob("ssh-ed25519", [7, 7]);
        byte[] request = SshAgentProtocolTests.BuildSignRequest(blob, "payload"u8.ToArray(), 0);
        byte[] answer = [SshAgentProtocol.SignResponse, 1, 2, 3];
        var remote = new DuplexPipe(SshAgentProtocol.Frame(request));
        var agent = new ScriptedAgent(answer);
        var events = new List<AgentForwardRequestEvent>();

        int handled = await SshAgentRelay.PumpAsync(
            remote, _ => Task.FromResult<Stream>(agent), events.Add, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, handled);
        Assert.HasCount(1, events);
        Assert.IsTrue(events[0].Allowed);
        Assert.AreEqual(SshAgentProtocol.Fingerprint(blob), events[0].Fingerprint);
        CollectionAssert.AreEqual(SshAgentProtocol.Frame(answer), remote.Written);
        CollectionAssert.AreEqual(SshAgentProtocol.Frame(request), agent.Received);
    }

    /// <summary>
    /// 一条转发连接上会有多个请求(对端的一次 <c>ssh</c> 先列身份再逐把试签名),
    /// 本机 agent 的流按连接复用一条 —— 每条请求各开一次是白白多几十次 IPC。
    /// </summary>
    [TestMethod]
    public async Task Pump_MultipleRequests_ReuseOneAgentConnection()
    {
        byte[] listing = SshAgentProtocolTests.BuildIdentitiesAnswer();
        byte[] signed = [SshAgentProtocol.SignResponse, 42];
        var remote = new DuplexPipe(
            [
                .. SshAgentProtocol.Frame([SshAgentProtocol.RequestIdentities]),
                .. SshAgentProtocol.Frame(
                    SshAgentProtocolTests.BuildSignRequest(
                        SshAgentProtocolTests.BuildKeyBlob("ssh-rsa", [1]), [1], 0))
            ]);
        var agent = new ScriptedAgent(listing, signed);
        int agentOpened = 0;

        int handled = await SshAgentRelay.PumpAsync(
            remote,
            _ =>
            {
                agentOpened++;
                return Task.FromResult<Stream>(agent);
            },
            null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(2, handled);
        Assert.AreEqual(1, agentOpened);
    }

    /// <summary>
    /// 谎报长度的报文:断开这条连接,不抛、不读、不申请那块内存。
    /// (<c>0xFFFFFFFF</c> 是一条四字节就能发出去的拒绝服务。)
    /// </summary>
    [TestMethod]
    public async Task Pump_LyingLengthPrefix_EndsTheConnectionQuietly()
    {
        var remote = new DuplexPipe([0xFF, 0xFF, 0xFF, 0xFF, 11]);

        int handled = await SshAgentRelay.PumpAsync(
            remote, _ => Task.FromResult<Stream>(new MemoryStream()), null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, handled);
        Assert.IsEmpty(remote.Written);
    }

    /// <summary>说了有 N 字节却没给够 —— 同样按断开处理,而不是拿半条报文去猜。</summary>
    [TestMethod]
    public async Task Pump_TruncatedMessage_EndsTheConnectionQuietly()
    {
        var remote = new DuplexPipe([0, 0, 0, 10, SshAgentProtocol.RequestIdentities]);

        int handled = await SshAgentRelay.PumpAsync(
            remote, _ => Task.FromResult<Stream>(new MemoryStream()), null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, handled);
    }

    /// <summary>本机 agent 半路没了:告诉远端这次失败,并收掉这条连接(复用的流已不可信)。</summary>
    [TestMethod]
    public async Task Pump_AgentGoesAway_RepliesFailureAndStops()
    {
        var remote = new DuplexPipe(
            [
                .. SshAgentProtocol.Frame([SshAgentProtocol.RequestIdentities]),
                .. SshAgentProtocol.Frame([SshAgentProtocol.RequestIdentities])
            ]);
        var events = new List<AgentForwardRequestEvent>();

        int handled = await SshAgentRelay.PumpAsync(
            remote,
            _ => Task.FromResult<Stream>(new ScriptedAgent()), // 一条应答都没有
            events.Add,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, handled, "第一条就断,不该继续读第二条");
        Assert.HasCount(1, events);
        Assert.IsFalse(events[0].Allowed);
        AssertSingleReply(remote, SshAgentProtocol.Failure);
    }

    /// <summary>测试上下文,MSTest 按约定注入。</summary>
    public TestContext TestContext { get; set; } = null!;

    private static void AssertSingleReply(DuplexPipe remote, byte expectedType)
    {
        CollectionAssert.AreEqual(SshAgentProtocol.FrameSingle(expectedType), remote.Written);
    }

    /// <summary>远端那一侧:读走预置的字节,写下的都留在 <see cref="Written" /> 里。</summary>
    private sealed class DuplexPipe(byte[] incoming) : Stream
    {
        private readonly MemoryStream _in = new(incoming);
        private readonly MemoryStream _out = new();

        public byte[] Written => _out.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            _in.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            _out.WriteAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    }

    /// <summary>本机 agent 那一侧:按脚本逐条应答,收到的请求原样留在 <see cref="Received" /> 里。</summary>
    private sealed class ScriptedAgent(params byte[][] answers) : Stream
    {
        private readonly Queue<byte[]> _answers = new(answers);
        private readonly MemoryStream _pending = new();
        private readonly MemoryStream _received = new();

        public byte[] Received => _received.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await _received.WriteAsync(buffer, cancellationToken);
            // 一条请求换一条应答:请求写完(含长度前缀)就把下一条脚本排进读缓冲。
            if (_answers.Count > 0)
            {
                long position = _pending.Position;
                _pending.Position = _pending.Length;
                byte[] framed = SshAgentProtocol.Frame(_answers.Dequeue());
                await _pending.WriteAsync(framed, cancellationToken);
                _pending.Position = position;
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            _pending.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _pending.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _received.Write(buffer, offset, count);
    }
}
