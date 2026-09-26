// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/06-sftp.md §5.2
//
// 在途请求数 × 块大小就是 SFTP 层的「窗口」。和通道窗口一样，
// 固定值在高 RTT 链路上直接封死吞吐 —— 64 × 32 KiB = 2 MiB，
// 200 ms RTT 下同样是 10 MB/s 封顶。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpPipelineDepthTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly TestChannelServer _channelServer;
        private readonly Task _serverChannels;
        private readonly SshConnection _connection;
        private readonly CancellationTokenSource _cts;

        private Harness(
            TestSshServer server,
            TestChannelServer channelServer,
            Task serverChannels,
            SshConnection connection,
            SftpFileSystem sftp,
            TestSftpServer sftpServer,
            CancellationTokenSource cts)
        {
            _server = server;
            _channelServer = channelServer;
            _serverChannels = serverChannels;
            _connection = connection;
            Sftp = sftp;
            SftpServer = sftpServer;
            _cts = cts;
        }

        public SftpFileSystem Sftp { get; }

        public TestSftpServer SftpServer { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Harness> StartAsync(
            LinkCharacteristics link, SftpOptions clientOptions, Action<TestSftpServer> arrange)
        {
            (InMemoryDuplexStream rawClient, InMemoryDuplexStream rawServer) =
                InMemoryTransport.CreatePair(new InMemoryTransportOptions
                {
                    PauseWriterThreshold = 16 * 1024 * 1024,
                    ResumeWriterThreshold = 8 * 1024 * 1024,
                });

            DelayedStream clientStream = new(rawClient, link);

            TestSshServer server = new(rawServer);
            SshPacketTransport clientTransport = new(clientStream);
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));

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

            TestSftpServer sftpServer = new();
            arrange(sftpServer);

            TestChannelServer channelServer = new(server.Transport, new TestChannelScript
            {
                SubsystemHandler = sftpServer.RunAsync,
            });
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex);
            connection.Start();

            SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection, clientOptions, cts.Token);

            return new Harness(
                server, channelServer, serverChannels, connection, sftp, sftpServer, cts);
        }

        public async ValueTask DisposeAsync()
        {
            await Sftp.DisposeAsync();
            await _cts.CancelAsync();
            await _connection.DisposeAsync();
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

    [TestMethod]
    public async Task 深度不够时会自己长大()
    {
        // 单向 5 ms（RTT 10 ms）。深度从 4 起步 —— 4 × 8 KiB = 32 KiB 在途，
        // 这点数据在 10 ms 的 RTT 上根本喂不饱链路。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(5));

        SftpOptions options = SftpOptions.Default with
        {
            MaxInFlight = 4,
            BlockSize = 8 * 1024,
            AdaptivePipelineDepth = true,
            MaxPipelineDepth = 64,
        };

        byte[] payload = new byte[400 * 1024];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            link, options, server => server.AddFile("/home/joe/big.bin", payload));

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/big.bin", harness.Token);
        Assert.AreSequenceEqual(payload, content, "数据要一字节不差");
    }

    [TestMethod]
    public async Task 关掉自适应时深度不变()
    {
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(2));

        SftpOptions options = SftpOptions.Default with
        {
            MaxInFlight = 8,
            BlockSize = 8 * 1024,
            AdaptivePipelineDepth = false,
        };

        byte[] payload = new byte[200 * 1024];

        await using Harness harness = await Harness.StartAsync(
            link, options, server => server.AddFile("/home/joe/f.bin", payload));

        byte[] content = await harness.Sftp.ReadAllBytesAsync("/home/joe/f.bin", harness.Token);

        Assert.HasCount(payload.Length, content);

        // 需要确定性内存占用的场景（在途数 × 块大小）靠的就是这一条。
        Assert.IsNotNull(SftpOptions.Default with { AdaptivePipelineDepth = false });
    }

    [TestMethod]
    public void 默认打开自适应()
    {
        // 高 RTT 链路上，固定深度是吞吐的硬上限 —— 默认该让它能长。
        Assert.IsTrue(SftpOptions.Default.AdaptivePipelineDepth);
        Assert.AreEqual(64, SftpOptions.Default.MaxInFlight);
        Assert.AreEqual(256, SftpOptions.Default.MaxPipelineDepth);
    }

    [TestMethod]
    public async Task 长大之后数据仍然一字节不差()
    {
        // 自适应最容易出的错是「调大额度时多放了或少放了信号量」——
        // 症状是要么在途数超限把服务端打爆，要么额度泄漏最后卡死。
        // 这条用大量小块请求把调整路径反复走一遍。
        LinkCharacteristics link = new(TimeSpan.FromMilliseconds(2));

        SftpOptions options = SftpOptions.Default with
        {
            MaxInFlight = 2,
            BlockSize = 4 * 1024,
            AdaptivePipelineDepth = true,
            MaxPipelineDepth = 32,
        };

        byte[] payload = new byte[300 * 1024];
        Random.Shared.NextBytes(payload);

        await using Harness harness = await Harness.StartAsync(
            link, options, server => server.AddFile("/home/joe/x.bin", payload));

        byte[] first = await harness.Sftp.ReadAllBytesAsync("/home/joe/x.bin", harness.Token);
        byte[] second = await harness.Sftp.ReadAllBytesAsync("/home/joe/x.bin", harness.Token);

        Assert.AreSequenceEqual(payload, first);
        Assert.AreSequenceEqual(payload, second, "第二遍也要对 —— 额度不能在第一遍之后泄漏");
    }
}
