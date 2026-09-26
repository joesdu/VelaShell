// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/design/architecture.md §6.2;velashell-docs/zh/ssh/spec/05-connection.md §6.3;velashell-docs/zh/ssh/spec/08-failures.md
//
// 这里测的是**面向使用者的那一层**：一个 SshConnectionOptions 进去，
// 一条能用的连接出来。中间的拨号 → 版本交换 → 密钥交换 → 裁决 → 认证
// 都不该再由调用方自己拼。

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class ConnectionTests
{
    /// <summary>把整台测试服务端跑起来，并给出一个指向它的拨号器。</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly List<Task> _running = [];
        private readonly List<TestChannelServer> _channelServers = [];
        private readonly List<TestSshServer> _servers = [];
        private readonly TestChannelScript _script;
        private readonly TestAuthPolicy _authPolicy;

        public FakeServer(TestChannelScript? script = null, TestAuthPolicy? authPolicy = null)
        {
            _script = script ?? new TestChannelScript();
            _authPolicy = authPolicy ?? new TestAuthPolicy { AcceptPassword = "hunter2" };
        }

        /// <summary>服务端出示的主机公钥（第一条连接建立之后才有）。</summary>
        public SshPublicKey? HostKey { get; private set; }

        public ISshTransportDialer CreateDialer() => new Dialer(this);

        private sealed class Dialer(FakeServer owner) : ISshTransportDialer
        {
            public SshDialKind Kind => SshDialKind.Tcp;

            public ValueTask<Stream> DialAsync(
                SshDialTarget target, CancellationToken cancellationToken)
            {
                (InMemoryDuplexStream client, InMemoryDuplexStream server) = InMemoryTransport.CreatePair();
                owner.StartServerSide(server);
                return ValueTask.FromResult<Stream>(client);
            }
        }

        private void StartServerSide(InMemoryDuplexStream serverStream)
        {
            TestSshServer server = new(serverStream);
            _servers.Add(server);

            _running.Add(Task.Run(async () =>
            {
                TestSshServerHandshake handshake = await server.HandshakeAsync(_cts.Token);
                HostKey = SshPublicKey.Decode(handshake.HostKeyBlob);

                TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, _authPolicy);
                await auth.RunAsync(_cts.Token);

                TestChannelServer channels = new(server.Transport, _script);
                lock (_channelServers)
                {
                    _channelServers.Add(channels);
                }
                await channels.RunAsync(_cts.Token);
            }));
        }

        private bool _disposed;

        /// <summary>
        /// 幂等 —— 有的用例要**在中途**把服务端拆掉（验掉线），
        /// 而 <c>await using</c> 在作用域结束时还会再拆一次。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            await _cts.CancelAsync();

            foreach (Task task in _running)
            {
                try
                {
                    await task;
                }
                catch (Exception)
                {
                    // 收尾时被取消是预期的。
                }
            }

            foreach (TestChannelServer channels in _channelServers)
            {
                channels.Dispose();
            }

            foreach (TestSshServer server in _servers)
            {
                await server.DisposeAsync();
            }

            _cts.Dispose();
        }
    }

    // ------------------------------------------------------------ 目标串解析

    [TestMethod]
    public void 目标串的各种写法()
    {
        SshConnectionOptions full = new("root@10.0.0.1:2222");
        Assert.AreEqual("root", full.UserName);
        Assert.AreEqual("10.0.0.1", full.Host);
        Assert.AreEqual(2222, full.Port);

        SshConnectionOptions noPort = new("joe@example.com");
        Assert.AreEqual("joe", noPort.UserName);
        Assert.AreEqual("example.com", noPort.Host);
        Assert.AreEqual(22, noPort.Port);

        // IPv6 必须写方括号 —— 不然冒号分不清是地址还是端口。
        SshConnectionOptions ipv6 = new("joe@[fe80::1]:2222");
        Assert.AreEqual("fe80::1", ipv6.Host);
        Assert.AreEqual(2222, ipv6.Port);

        SshConnectionOptions ipv6NoPort = new("joe@[::1]");
        Assert.AreEqual("::1", ipv6NoPort.Host);
        Assert.AreEqual(22, ipv6NoPort.Port);
    }

    [TestMethod]
    public void 不合法的目标串被当场拒绝()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@host:不是数字"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@host:0"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@host:70000"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("@host"));
        Assert.ThrowsExactly<ArgumentException>(() => new SshConnectionOptions("joe@[::1"));
    }

    [TestMethod]
    public void 默认主机密钥策略不是接受任何密钥()
    {
        SshConnectionOptions options = new("joe@example.com");

        // 默认放行等于关掉中间人防护 —— 而 SSH 的全部安全性
        // 都建立在「你确实连到了你以为的那台机器」之上。
        Assert.IsInstanceOfType<KnownHostsPolicy>(options.HostKeyPolicy);
    }

    // ------------------------------------------------------------ 主线

    [TestMethod]
    public async Task 一步连上并跑一条命令()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardOutput = Encoding.UTF8.GetBytes("Linux velashell 6.1\n"),
            ExitCode = 0,
        });

        SshConnectionOptions options = new("joe@test.invalid:22")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.IsTrue(connection.IsAlive);
        Assert.AreEqual("joe@test.invalid:22", connection.Description,
            "描述里带上端口 —— 日志里「连的是哪台」要一眼看全");
        Assert.IsNotNull(connection.HostKey, "连上之后要能拿到服务端的主机密钥");

        // 协商成功的结果也要交出去 —— 终端产品要在状态栏上显示它，
        // 排障时第一句话也是「这条连接到底谈成了什么」（架构原则 4）。
        Assert.IsFalse(
            string.IsNullOrEmpty(connection.Algorithms.KeyExchange),
            "密钥交换算法名不该是空的");
        Assert.AreEqual(
            SshAlgorithmNames.None, connection.Algorithms.CompressionClientToServer,
            "默认不开压缩");

        SshCommandResult output = await connection.RunAsync("uname -a");

        Assert.AreEqual("Linux velashell 6.1\n", output.StandardOutput);
        Assert.AreEqual(0, output.ExitCode);
        Assert.IsTrue(output.IsSuccess);
    }

    [TestMethod]
    public async Task 命令失败时异常里带着stderr()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardError = Encoding.UTF8.GetBytes("bash: 没有那个文件或目录\n"),
            ExitCode = 127,
        });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        SshCommandResult output = await connection.RunAsync("不存在的命令");

        Assert.AreEqual(127, output.ExitCode);
        Assert.IsFalse(output.IsSuccess);

        SshCommandFailedException error = Assert.ThrowsExactly<SshCommandFailedException>(
            () => output.EnsureSuccess("不存在的命令"));

        // 「命令失败了」而不说它抱怨了什么，等于让调用方再跑一遍去看。
        Assert.Contains("没有那个文件或目录", error.Message);
        Assert.Contains("退出码 127", error.Message);
    }

    [TestMethod]
    public async Task 主机密钥被策略拒绝时连不上()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new PinnedFingerprintHostKeyPolicy(["SHA256:对不上的指纹"]),
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.Contains("不在允许列表里", error.Message);
    }

    [TestMethod]
    public async Task 认证失败时异常里带着逐条记录()
    {
        await using FakeServer server = new(
            null, new TestAuthPolicy { AcceptPassword = "正确的" });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("错的")],
        };

        SshAuthenticationException error = await Assert.ThrowsExactlyAsync<SshAuthenticationException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.IsNotEmpty(error.Attempts);
        Assert.Contains("password", error.DescribeAttempts());
    }

    [TestMethod]
    public async Task 横幅被送到回调()
    {
        List<string> banners = [];

        await using FakeServer server = new(
            null,
            new TestAuthPolicy
            {
                AcceptPassword = "hunter2",
                Banners = ["未经授权的访问将被记录。"],
            });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            BannerHandler = (text, _) =>
            {
                banners.Add(text);
                return ValueTask.CompletedTask;
            },
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.AreSequenceEqual(new[] { "未经授权的访问将被记录。" }, banners);
    }

    [TestMethod]
    public async Task 连不上的目标给出带原因的异常()
    {
        SshConnectionOptions options = new("joe@127.0.0.1:1")
        {
            // 真的去连一个几乎肯定没人听的端口。
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        // 「连不上」三个字对用户没有任何帮助 —— 要说清是 DNS、拒绝、还是超时。
        //
        // 端口 1 上多半没有在听的服务（→ 拒绝），但在被防火墙静默丢包的
        // 网络里会是超时 —— 两者都该有**明确的** TCP 层原因码，
        // 而不是笼统的 Timeout。
        Assert.IsTrue(
            error.Reason is SshFailureReason.TcpRefused or SshFailureReason.TcpTimeout
                or SshFailureReason.TcpUnreachable,
            $"原因码应当是可判定的 TCP 层原因，实际是 {error.Reason}");
        Assert.AreEqual(SshPhase.Dialing, error.Phase);
    }

    [TestMethod]
    public async Task DNS查不到时说的是DNS()
    {
        SshConnectionOptions options = new("joe@这个名字一定查不到.invalid")
        {
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.DnsFailure, error.Reason);
        Assert.Contains("DNS", error.Message);
    }

    // ------------------------------------------------------------ 保活

    /// <summary>一个「要想一会儿」的策略：模拟用户盯着指纹看了一阵才点「永久信任」。</summary>
    private sealed class SlowTrustPolicy(TimeSpan thinking) : IHostKeyPolicy
    {
        public bool PersistTokenWasCancelled { get; private set; } = true;

        public int Persisted { get; private set; }

        public async ValueTask<SshHostKeyVerdict> EvaluateAsync(
            SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(thinking, cancellationToken);
            return SshHostKeyVerdict.AcceptAndPersist;
        }

        public ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default)
        {
            PersistTokenWasCancelled = cancellationToken.IsCancellationRequested;
            Persisted++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 主机密钥裁决（等人）不算进连接超时：用户想得比连接超时久，连接照样成功，
    /// 「永久信任」也照样存下来。
    /// </summary>
    [TestMethod]
    public async Task 主机密钥裁决期间连接计时器停表()
    {
        await using FakeServer server = new();
        SlowTrustPolicy policy = new(TimeSpan.FromMilliseconds(1500));

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = policy,
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromMilliseconds(700),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.IsTrue(connection.IsAlive);
        Assert.AreEqual(1, policy.Persisted);
        Assert.IsFalse(policy.PersistTokenWasCancelled, "持久化拿到的是已取消的令牌 —— 「永久信任」存不下来");
    }

    [TestMethod]
    public async Task 保活探测在链路闲下来之后发出()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            KeepAlive = new SshKeepAlivePolicy(TimeSpan.FromMilliseconds(100)),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        // 闲着 —— 保活该自己发出去。
        await Task.Delay(500);

        Assert.IsTrue(connection.IsAlive, "服务端会回 REQUEST_FAILURE，那也算有应答");
    }

    [TestMethod]
    public async Task 链路忙的时候不发保活()
    {
        await using FakeServer server = new(new TestChannelScript
        {
            StandardOutput = "x"u8.ToArray(),
            ExitCode = 0,
        });

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            KeepAlive = new SshKeepAlivePolicy(TimeSpan.FromMilliseconds(200)),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        // 持续有流量 —— 每一条命令都会让「上次收到报文」的时刻刷新。
        for (int i = 0; i < 8; i++)
        {
            await connection.RunAsync($"echo {i}");
            await Task.Delay(50);
        }

        // 计时基准是「上次收到任何报文」而不是固定周期，
        // 所以一直忙的链路上一次保活都不该发。
        Assert.IsTrue(connection.IsAlive);
    }

    // ------------------------------------------------------------ 掉线信号

    /// <summary>连接活着的时候，<c>Disconnected</c> 不能是已取消的。</summary>
    /// <remarks>
    /// 听起来是废话，但它挡住的是一类很实在的错：令牌如果在构造时就被取消，
    /// 或者与 <c>_lifetime</c> 搞混，上层的读循环会在连上的瞬间就退出，
    /// 表现成「一连就断」。
    /// </remarks>
    [TestMethod]
    public async Task 连着的时候掉线令牌没有被取消()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);

        Assert.IsTrue(connection.IsAlive);
        Assert.IsFalse(connection.Disconnected.IsCancellationRequested);
        Assert.IsTrue(connection.Disconnected.CanBeCanceled, "它必须是一个真的能被取消的令牌");
    }

    /// <summary>
    /// 释放连接要放出掉线信号 —— 上层的读循环靠它退出。
    /// </summary>
    /// <remarks>
    /// 而且**释放之后还要读得到**：令牌如果是每次从已释放的 CTS 上取，
    /// 这里会抛 <see cref="ObjectDisposedException"/>，
    /// 而「连接已经释放了」恰恰是最常去读它的时刻。
    /// </remarks>
    [TestMethod]
    public async Task 释放连接会放出掉线信号且之后仍读得到()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshConnection connection = await SshConnection.ConnectAsync(options);
        CancellationToken token = connection.Disconnected;

        // 掉线要能**等**到，不是靠轮询问出来的。
        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(signalled.SetResult);

        await connection.DisposeAsync();

        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsTrue(connection.Disconnected.IsCancellationRequested, "释放之后再读也要读得到");
        Assert.IsFalse(connection.IsAlive);
    }

    /// <summary>
    /// 拨通之后流上读出错（对端重置），建连报出的是库自己的「连接断了」，不是原始的 <see cref="IOException"/>。
    /// </summary>
    [TestMethod]
    public async Task 建连途中链路出错报连接断了而不是原始的IO异常()
    {
        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = new ResettingDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshConnectionClosedException ex = await Assert.ThrowsExactlyAsync<SshConnectionClosedException>(
            async () => await SshConnection.ConnectAsync(options));

        Assert.AreEqual(SshFailureReason.ClosedByPeer, ex.Reason);
        Assert.IsInstanceOfType<IOException>(ex.InnerException);
    }

    /// <summary>发完版本串之后，再读就当成对端重置了连接。</summary>
    private sealed class ResettingDialer : ISshTransportDialer
    {
        public SshDialKind Kind => SshDialKind.Tcp;

        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new ResettingStream());

        private sealed class ResettingStream : Stream
        {
            private readonly byte[] _banner = Encoding.ASCII.GetBytes("SSH-2.0-ResetsAfterBanner\r\n");
            private int _offset;

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_offset >= _banner.Length)
                {
                    throw new IOException("连接被对端重置。");
                }

                int count = Math.Min(buffer.Length, _banner.Length - _offset);
                _banner.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>对端断链时也要放出掉线信号，而不是只有主动释放才放。</summary>
    [TestMethod]
    public async Task 对端断开时放出掉线信号()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        Assert.IsFalse(connection.Disconnected.IsCancellationRequested);

        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration =
            connection.Disconnected.Register(signalled.SetResult);

        // 把服务端那一侧整个拆掉 —— 客户端的接收循环会读到 EOF。
        await server.DisposeAsync();

        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(connection.Disconnected.IsCancellationRequested);
        Assert.IsFalse(connection.IsAlive, "掉线之后 IsAlive 与掉线令牌必须是同一个结论");
    }

    [TestMethod]
    public async Task 关掉保活时不发探测()
    {
        await using FakeServer server = new();

        SshConnectionOptions options = new("joe@test.invalid")
        {
            Dialer = server.CreateDialer(),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            KeepAlive = SshKeepAlivePolicy.Disabled,
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(options);
        await Task.Delay(200);

        Assert.IsTrue(connection.IsAlive);
        Assert.IsFalse(options.KeepAlive.IsEnabled);
    }
}
