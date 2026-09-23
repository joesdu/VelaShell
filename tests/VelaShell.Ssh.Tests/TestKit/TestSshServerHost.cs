// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>一条**走工厂建起来**的完整连接，以及它对面的测试服务端。</summary>
/// <remarks>
/// <para>
/// 与各测试类里那些手搭的 harness 的区别：这条连接是
/// <c>SshConnectionOptions.ConnectAsync</c> 建的，
/// 所以它带着工厂留下的东西 —— 协商结果、主机密钥、**以及重协商上下文**。
/// 重协商那一组用例只能用这一条路，手搭的连接拿不到那份上下文。
/// </para>
/// <para>
/// 服务端一侧的 <see cref="TestChannelServer"/> 也被交了出来（<see cref="Channels"/>），
/// 因为「服务端主动发起重协商」这件事要从它的收包循环里发起。
/// </para>
/// </remarks>
public sealed class TestSshServerHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly TestSshServer _server;
    private readonly Task _serverSide;

    private TestSshServerHost(
        CancellationTokenSource cts,
        TestSshServer server,
        TestChannelServer channels,
        Task serverSide,
        SshConnection connection)
    {
        _cts = cts;
        _server = server;
        Channels = channels;
        _serverSide = serverSide;
        Connection = connection;
    }

    /// <summary>客户端连接。</summary>
    public SshConnection Connection { get; }

    /// <summary>服务端的通道层 —— 从这里发起重协商。</summary>
    public TestChannelServer Channels { get; }

    /// <summary>用例的取消令牌（带整体超时）。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>起一条连接。</summary>
    /// <param name="script">通道剧本。</param>
    /// <param name="algorithms">客户端的算法清单；<see langword="null"/> 用默认。</param>
    /// <param name="rekey">
    /// 主动发起重协商的阈值；<see langword="null"/> 表示<b>不主动发起</b> ——
    /// 大多数用例不该被一个后台循环搅进来。
    /// </param>
    /// <param name="rekeyCheckInterval">阈值的检查间隔；验阈值的用例要把它调小。</param>
    public static async Task<TestSshServerHost> StartAsync(
        TestChannelScript? script = null,
        SshAlgorithmSet? algorithms = null,
        SshRekeyPolicy? rekey = null,
        TimeSpan? rekeyCheckInterval = null)
    {
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(25));

        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                // 水位要高于任何一个用例的数据量，不然管道自己成了瓶颈。
                PauseWriterThreshold = 8 * 1024 * 1024,
                ResumeWriterThreshold = 4 * 1024 * 1024,
            });

        // 服务端要宣告的算法清单必须与客户端一致，否则协商不上
        // （压缩那一组用例正是靠这一点让两边都谈成 zlib@openssh.com）。
        TestSshServer server = new(serverStream, new TestSshServerOptions
        {
            Algorithms = algorithms,
        });

        TestChannelServer channels = new(server.Transport, script);
        channels.AttachServer(server);

        // 服务端一侧整条线跑在一个任务上：握手 → 认证 → 通道层收包循环。
        Task serverSide = Task.Run(async () =>
        {
            TestSshServerHandshake handshake = await server.HandshakeAsync(cts.Token);

            TestAuthServer auth = new(
                server.Transport, handshake.ExchangeHash,
                new TestAuthPolicy { AcceptPassword = "hunter2" });
            await auth.RunAsync(cts.Token);

            // ⚠️ **认证成功之后才挂压缩** —— 与客户端对称。
            //
            // zlib@openssh.com 的语义就是「推迟到认证之后」，客户端在
            // SshConnectionFactory 里正是这么做的。服务端这一侧漏掉的话，
            // 客户端开始压而我们还在按明文读，症状是连上之后第一个报文就解不开
            // （表现成一个看不出原因的挂死）。
            //
            // 方向要对：客户端→服务端那一路是我们**收**，反之是我们**发**。
            // 只管延迟的那种：普通 zlib 在 NEWKEYS 时已经装上了。
            if (SshCompressorFactory.IsDelayed(handshake.Negotiated.CompressionClientToServer))
            {
                server.Transport.SetReceiveCompressor(
                    SshCompressorFactory.Create(handshake.Negotiated.CompressionClientToServer));
            }

            if (SshCompressorFactory.IsDelayed(handshake.Negotiated.CompressionServerToClient))
            {
                server.Transport.SetSendCompressor(
                    SshCompressorFactory.Create(handshake.Negotiated.CompressionServerToClient));
            }

            await channels.RunAsync(cts.Token);
        });

        SshConnectionOptions options = new("joe@test.invalid:22")
        {
            Dialer = new FixedStreamDialer(clientStream),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            Algorithms = algorithms ?? SshAlgorithmSet.Default,

            // 默认关掉主动发起：只验「接住对端发起」的用例不该被一个
            // 后台监视循环搅进来。要验阈值的用例自己传。
            Rekey = rekey ?? SshRekeyPolicy.Disabled,
            RekeyCheckInterval = rekeyCheckInterval ?? TimeSpan.FromSeconds(5),
        };

        SshConnection connection = await options.ConnectAsync(cts.Token);

        return new TestSshServerHost(cts, server, channels, serverSide, connection);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Channels.Observation.ClientGone = true;
        await Connection.DisposeAsync();
        await _cts.CancelAsync();

        try
        {
            await _serverSide;
        }
        catch (Exception)
        {
            // 收尾时被取消是预期的。
        }

        Channels.Dispose();
        await _server.DisposeAsync();
        _cts.Dispose();

        // 服务端如果是在用例跑到一半挂的，把真正的原因抬出来 ——
        // 别让它继续伪装成一个「客户端超时」。
        TestChannelObservation observed = Channels.Observation;
        if (observed.ServerFault is { } serverFault)
        {
            throw new InvalidOperationException(
                $"测试服务端的收包循环挂了：{serverFault.Message}", serverFault);
        }
        if (observed.ScriptFault is { } scriptFault)
        {
            throw new InvalidOperationException(
                $"测试服务端的剧本回放挂了：{scriptFault.Message}", scriptFault);
        }
        if (observed.SubsystemFault is { } subsystemFault)
        {
            throw new InvalidOperationException(
                $"测试服务端的子系统搬运挂了：{subsystemFault.Message}", subsystemFault);
        }
    }

    /// <summary>把工厂的拨号交回一条已经建好的流。</summary>
    private sealed class FixedStreamDialer(Stream stream) : ISshTransportDialer
    {
        private Stream? _stream = stream;

        public SshDialKind Kind => SshDialKind.Tcp;

        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken)
        {
            Stream? taken = Interlocked.Exchange(ref _stream, null);
            return taken is null
                ? throw new InvalidOperationException("这个拨号器只能用一次。")
                : ValueTask.FromResult(taken);
        }
    }
}
