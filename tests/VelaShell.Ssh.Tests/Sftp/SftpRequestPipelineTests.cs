// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/06-sftp.md §5、§九
//
// 在途额度只有应答才还得回来。于是两件事必须做对：
//   · **没发出去的请求当场还额度** —— 它永远等不到应答；
//   · **流水线收工时放出所有排队的人** —— 收工之后也不会再有应答。
// 任何一条做错，症状都一样：SFTP 操作一个接一个地挂住，没有异常、没有日志。

using System.Buffers;
using System.Buffers.Binary;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpRequestPipelineTests
{
    /// <summary>
    /// 一个「不读、不答、不回补窗口」的 sftp 子系统 —— 远端 sftp-server 卡死时的样子。
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly TestChannelServer _channelServer;
        private readonly Task _serverChannels;
        private readonly CancellationTokenSource _cts;

        private Harness(
            TestSshServer server,
            TestChannelServer channelServer,
            Task serverChannels,
            SshConnection connection,
            CancellationTokenSource cts)
        {
            _server = server;
            _channelServer = channelServer;
            _serverChannels = serverChannels;
            Connection = connection;
            _cts = cts;
        }

        public SshConnection Connection { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Harness> StartAsync()
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();

            TestSshServer server = new(serverStream);
            SshPacketTransport clientTransport = new(clientStream);
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

            Task<TestSshServerHandshake> serverHandshake = server.HandshakeAsync(cts.Token);
            SshVersionExchangeResult versions =
                await SshVersionExchange.ExchangeAsync(clientTransport, cancellationToken: cts.Token);
            SshKeyExchangeRunner runner = new(
                clientTransport, SshAlgorithmSet.Default, new DangerousAcceptAnyHostKeyPolicy());
            SshKeyExchangeResult kex =
                await runner.RunAsync(versions, "test.invalid", 22, cancellationToken: cts.Token);
            TestSshServerHandshake handshake = await serverHandshake;

            TestAuthServer authServer = new(
                server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "p" });
            Task<bool> serverAuth = authServer.RunAsync(cts.Token);
            SshAuthenticator authenticator = new(clientTransport, "joe", kex.SessionId);
            await authenticator.AuthenticateAsync([new PasswordCredential("p")], cts.Token);
            await serverAuth;

            TestChannelServer channelServer = new(server.Transport, new TestChannelScript
            {
                InitialWindow = 32 * 1024,
                WithholdWindowAdjust = true,
                CloseAfterScript = false,
                SubsystemHandler = static (_, _, token) => Task.Delay(Timeout.Infinite, token),
            });
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex.SessionId);
            connection.Start();

            return new Harness(server, channelServer, serverChannels, connection, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Connection.DisposeAsync();
            try
            {
                await _serverChannels;
            }
            catch (Exception)
            {
                // 收尾时被取消是预期的。
            }
            _channelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>写一个 SFTP 帧：长度、类型（READ）、request-id、再垫上 <paramref name="bodyBytes"/> 字节。</summary>
    private static void WriteFrame(IBufferWriter<byte> writer, uint requestId, int bodyBytes)
    {
        int length = 1 + 4 + bodyBytes;
        Span<byte> header = writer.GetSpan(9);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)length);
        header[4] = (byte)SftpMessageType.Read;
        BinaryPrimitives.WriteUInt32BigEndian(header[5..], requestId);
        writer.Advance(9);

        writer.GetSpan(bodyBytes)[..bodyBytes].Clear();
        writer.Advance(bodyBytes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    [TestMethod]
    public async Task 排队等发送时被取消的请求当场还回在途额度()
    {
        await using Harness harness = await Harness.StartAsync();
        SshChannel channel = await harness.Connection.OpenSubsystemAsync("sftp", null, harness.Token);
        SftpRequestPipeline pipeline = new(channel, maxInFlight: 4, adaptive: false, ceiling: 4);
        pipeline.Start();

        // 200 KiB 的请求：窗口只有 32 KiB 且不回补，它一直卡在背压上、占着发送锁。
        Task<SftpResponse> stuck = pipeline.SendAsync(
            static (w, id) => WriteFrame(w, id, 200 * 1024), cancellationToken: harness.Token).AsTask();
        await WaitUntilAsync(() => pipeline.InFlightCount == 1, harness.Token);

        // 第二个请求排在发送锁上，还没上线 —— 这时取消它。
        using CancellationTokenSource cancel = new();
        Task<SftpResponse> queued = pipeline.SendAsync(
            static (w, id) => WriteFrame(w, id, 16), cancellationToken: cancel.Token).AsTask();
        await WaitUntilAsync(() => pipeline.InFlightCount == 2, harness.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);

        Assert.AreEqual(1, pipeline.InFlightCount, "没发出去的请求永远等不到应答，它的额度必须当场还回来");

        await pipeline.DisposeAsync();
        await Assert.ThrowsAsync<Exception>(() => stuck.WaitAsync(TimeSpan.FromSeconds(10), harness.Token));
        await channel.DisposeAsync();
    }

    [TestMethod]
    public async Task 流水线释放时排在在途额度上的调用方被放出来()
    {
        await using Harness harness = await Harness.StartAsync();
        SshChannel channel = await harness.Connection.OpenSubsystemAsync("sftp", null, harness.Token);
        SftpRequestPipeline pipeline = new(channel, maxInFlight: 1, adaptive: false, ceiling: 1);
        pipeline.Start();

        // 唯一的额度被一个永远等不到应答的请求占着。
        Task<SftpResponse> first = pipeline.SendAsync(
            static (w, id) => WriteFrame(w, id, 16), cancellationToken: harness.Token).AsTask();
        await WaitUntilAsync(() => pipeline.InFlightCount == 1, harness.Token);

        // 第二个请求**不带令牌**地排在额度上。
        Task<SftpResponse> waiting = pipeline.SendAsync(static (w, id) => WriteFrame(w, id, 16)).AsTask();
        await Task.Delay(100, harness.Token);
        Assert.IsFalse(waiting.IsCompleted);

        await pipeline.DisposeAsync();

        Exception error = await Assert.ThrowsAsync<Exception>(
            () => waiting.WaitAsync(TimeSpan.FromSeconds(10), harness.Token));
        Assert.IsTrue(
            error is ObjectDisposedException or SftpUnavailableException,
            $"排队的调用方应当拿到「流水线已收工」，而不是超时：{error}");

        await Assert.ThrowsAsync<Exception>(() => first.WaitAsync(TimeSpan.FromSeconds(10), harness.Token));
        await channel.DisposeAsync();
    }
}
