// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §七
//
// agent 转发是一把上膛的枪：转发期间，远端主机上的 root 可以用你的私钥
// **签任何东西**。所以这一组里最要紧的不是「能转发」，
// 而是那三条安全约束确实拦得住。

using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class AgentForwardTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestSshServer _server;
        private readonly Task _serverChannels;
        private readonly CancellationTokenSource _cts;
        private readonly List<Task> _agentTasks = [];

        private Harness(
            TestSshServer server,
            TestChannelServer channelServer,
            Task serverChannels,
            SshConnection connection,
            TestAgent agent,
            CancellationTokenSource cts)
        {
            _server = server;
            ChannelServer = channelServer;
            _serverChannels = serverChannels;
            Connection = connection;
            Agent = agent;
            _cts = cts;
        }

        public SshConnection Connection { get; }

        public TestChannelServer ChannelServer { get; }

        public TestAgent Agent { get; }

        public CancellationToken Token => _cts.Token;

        /// <summary>造一个「连到本机测试 agent」的客户端。</summary>
        public ValueTask<SshAgentClient> ConnectAgentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();

            lock (_agentTasks)
            {
                _agentTasks.Add(Task.Run(() => Agent.ServeAsync(theirs, _cts.Token), CancellationToken.None));
            }

            return ValueTask.FromResult(SshAgentClient.FromStream(ours, "(测试 agent)"));
        }

        public static async Task<Harness> StartAsync(TestChannelScript? script = null)
        {
            (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) =
                InMemoryTransport.CreatePair();

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

            TestChannelServer channelServer = new(
                server.Transport, script ?? new TestChannelScript { CloseAfterScript = false, ExitCode = null });
            Task serverChannels = channelServer.RunAsync(cts.Token);

            SshConnection connection = new(clientTransport, kex);
            connection.Start();

            return new Harness(server, channelServer, serverChannels, connection, new TestAgent(), cts);
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

            foreach (Task task in _agentTasks)
            {
                try
                {
                    await task;
                }
                catch (Exception)
                {
                    // 同上。
                }
            }

            ChannelServer.Dispose();
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>起一条 session 通道、请求 agent 转发、再让服务端开回一条 agent 通道。</summary>
    private static async Task<(AgentForwarder Forwarder, Stream RemoteSide)> SetUpAsync(
        Harness harness, AgentForwardOptions? policy = null)
    {
        SshChannel session = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);

        AgentForwarder forwarder = await AgentForwarder.RequestAsync(
            harness.Connection, session,
            (policy ?? AgentForwardOptions.Default) with { LocalConnector = harness.ConnectAgentAsync }, harness.Token);

        Stream? remote = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelAuthAgent, default, harness.Token);

        Assert.IsNotNull(remote, "客户端应当接受 auth-agent 通道");
        return (forwarder, remote);
    }

    // ------------------------------------------------------------ 主线

    [TestMethod]
    public async Task 远端能列到本机agent里的密钥()
    {
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "~/.ssh/id_ed25519");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            IReadOnlyList<(SshPublicKey Key, string Comment)> listed =
                await TestRemoteAgentClient.ListAsync(remote, harness.Token);

            Assert.HasCount(1, listed);
            Assert.AreSequenceEqual(key.PublicKey.Blob.ToArray(), listed[0].Key.Blob.ToArray());
            Assert.AreEqual("~/.ssh/id_ed25519", listed[0].Comment);
        }

        Assert.AreEqual(1, harness.ChannelServer.Observation.AgentForwardRequests);
        Assert.AreEqual(1, forwarder.IdentityListings);
        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task 本机agent里有证书时远端照样能列到并用认识的钥()
    {
        await using Harness harness = await Harness.StartAsync();

        // 证书排在前面 —— 曾经就是它让整个列表抛异常，通道一声不吭地断掉。
        harness.Agent.AddOpaque(
            Keys.AgentListIdentitiesTests.OpaqueBlob("ssh-ed25519-cert-v01@openssh.com"), "id_ed25519-cert.pub");
        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "id_ed25519");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            IReadOnlyList<(SshPublicKey Key, string Comment)> listed =
                await TestRemoteAgentClient.ListAsync(remote, harness.Token);
            Assert.HasCount(1, listed);
            Assert.AreSequenceEqual(key.PublicKey.Blob.ToArray(), listed[0].Key.Blob.ToArray());

            byte[] data = Encoding.UTF8.GetBytes("x");
            byte[]? signature = await TestRemoteAgentClient.SignAsync(
                remote, key.PublicKey, data, flags: 0, harness.Token);
            Assert.IsNotNull(signature);
        }

        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task 释放转发器之后已经打开的agent通道也断开()
    {
        // 关掉开了 agent 转发的 shell，连接却还开着（SFTP、别的会话）。
        // 远端早先打开的那条 agent 通道不能继续替它签名。
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "id_ed25519");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            byte[] data = Encoding.UTF8.GetBytes("释放之前");
            Assert.IsNotNull(await TestRemoteAgentClient.SignAsync(remote, key.PublicKey, data, 0, harness.Token));

            await forwarder.DisposeAsync();

            await Assert.ThrowsExactlyAsync<EndOfStreamException>(
                () => TestRemoteAgentClient.SignAsync(remote, key.PublicKey, data, 0, harness.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), harness.Token));
        }

        Assert.AreEqual(1, harness.Agent.SignRequests, "释放之后的签名请求不该到达本机 agent");
        Assert.IsTrue(harness.Connection.IsAlive, "断的是那条 agent 通道，不是整条连接");
    }

    [TestMethod]
    public async Task 远端能让本机agent签名且签名可验()
    {
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "id_ed25519");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            byte[] data = Encoding.UTF8.GetBytes("远端想签的内容");
            byte[]? signature = await TestRemoteAgentClient.SignAsync(
                remote, key.PublicKey, data, flags: 0, harness.Token);

            Assert.IsNotNull(signature);

            // 私钥从没离开本进程 —— 但「用私钥做事的能力」确实转出去了。
            Assert.IsTrue(
                key.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519),
                "远端拿到的签名必须是真的");
        }

        Assert.AreEqual(1, forwarder.SignatureRequests);
        Assert.AreEqual(1, harness.Agent.SignRequests);
        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task 比32KiB长的签名请求不会卡住()
    {
        // 报文收齐之前转发器一个字节都不消费，而窗口只随消费回补 ——
        // 窗口比报文小，就是对端等窗口、我们等报文。ssh-keygen -Y sign 签一个文件就是这么长。
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "id_ed25519");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            byte[] data = new byte[100 * 1024];
            Random.Shared.NextBytes(data);

            byte[]? signature = await TestRemoteAgentClient
                .SignAsync(remote, key.PublicKey, data, flags: 0, harness.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsNotNull(signature);
            Assert.IsTrue(key.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
        }

        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task RSA的摘要算法由标志位决定()
    {
        await using Harness harness = await Harness.StartAsync();

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var key = InMemorySshSigner.FromRsa(rsa);
        harness.Agent.Add(key, "id_rsa");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            byte[] data = Encoding.UTF8.GetBytes("x");

            byte[]? sha512 = await TestRemoteAgentClient.SignAsync(
                remote, key.PublicKey, data, flags: 0x04, harness.Token);
            Assert.IsNotNull(sha512);
            Assert.IsTrue(key.PublicKey.VerifySignature(sha512, data, SshAlgorithmNames.RsaSha512));

            byte[]? sha256 = await TestRemoteAgentClient.SignAsync(
                remote, key.PublicKey, data, flags: 0x02, harness.Token);
            Assert.IsNotNull(sha256);
            Assert.IsTrue(key.PublicKey.VerifySignature(sha256, data, SshAlgorithmNames.RsaSha256));
        }

        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task RSA证书经转发签名同样按标志位用SHA2()
    {
        // 证书的类型串是 ssh-rsa-cert-v01@openssh.com：曾经按 KeyType 判断是不是 RSA，
        // 远端要的 SHA-2 标志位被忽略，证书被签成 SHA-1。
        await using Harness harness = await Harness.StartAsync();

        string fixtures = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures");
        ISshSigner rsa = await SshPrivateKeyFile.LoadAsync(Path.Combine(fixtures, "hostcert-rsa"), cancellationToken: harness.Token);
        byte[] blob = Convert.FromBase64String(File.ReadAllText(Path.Combine(fixtures, "hostcert-rsa-cert.pub")).Split(' ')[1]);
        harness.Agent.AddCertificate(rsa, blob, "id_rsa-cert.pub");
        SshPublicKey certificate = SshPublicKey.Decode(blob);

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            byte[] data = Encoding.UTF8.GetBytes("x");

            byte[]? sha512 = await TestRemoteAgentClient.SignAsync(remote, certificate, data, flags: 0x04, harness.Token);
            Assert.IsNotNull(sha512);
            Assert.IsTrue(certificate.VerifySignature(sha512, data, SshAlgorithmNames.RsaSha512CertV01));
        }

        await forwarder.DisposeAsync();
    }

    // ------------------------------------------------------------ 安全约束

    [TestMethod]
    public async Task 只转发指定的密钥其余的远端看不到()
    {
        await using Harness harness = await Harness.StartAsync();

        using var allowed = InMemorySshSigner.GenerateEd25519();
        using var secret = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(allowed, "给跳板机用的");
        harness.Agent.Add(secret, "生产环境的钥匙");

        AgentForwardOptions policy = new() { AllowedKeys = [allowed.PublicKey] };
        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness, policy);

        await using (remote)
        {
            IReadOnlyList<(SshPublicKey Key, string Comment)> listed =
                await TestRemoteAgentClient.ListAsync(remote, harness.Token);

            // 一台跳板机没有理由能用到你所有的密钥 —— 它只需要下一跳那一把。
            Assert.HasCount(1, listed, "只该看到放行的那一把");
            Assert.AreEqual("给跳板机用的", listed[0].Comment);

            // 看不到还不够 —— **直接拿 blob 来签也必须被拒**。
            // 远端可能从别处知道这把公钥（比如 authorized_keys）。
            byte[]? sneaky = await TestRemoteAgentClient.SignAsync(
                remote, secret.PublicKey, Encoding.UTF8.GetBytes("x"), 0, harness.Token);

            Assert.IsNull(sneaky, "没放行的密钥，哪怕远端知道它的公钥也不能用");
        }

        Assert.AreEqual(1, forwarder.KeysHidden);
        Assert.AreEqual(1, forwarder.SignaturesDenied);
        Assert.AreEqual(0, harness.Agent.SignRequests, "被拒的请求根本不该到达本机 agent");

        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task 逐次签名确认能拦下签名()
    {
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "~/.ssh/id_ed25519");

        List<AgentSignatureRequest> asked = [];
        bool approve = false;

        AgentForwardOptions policy = new()
        {
            ConfirmEachSignature = (request, _) =>
            {
                asked.Add(request);
                return ValueTask.FromResult(approve);
            },
        };

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness, policy);
        await using (remote)
        {
            byte[] data = Encoding.UTF8.GetBytes("x");

            byte[]? denied = await TestRemoteAgentClient.SignAsync(
                remote, key.PublicKey, data, 0, harness.Token);
            Assert.IsNull(denied, "使用者没批准就不能签");

            approve = true;
            byte[]? granted = await TestRemoteAgentClient.SignAsync(
                remote, key.PublicKey, data, 0, harness.Token);
            Assert.IsNotNull(granted, "批准之后才签");
        }

        Assert.HasCount(2, asked, "每一次都要问，不是只问第一次");

        // 弹窗里要说得出是哪把钥 —— 「有人要用某把 ed25519 签名」没什么用。
        Assert.AreEqual("~/.ssh/id_ed25519", asked[0].Comment);
        Assert.AreEqual(SshAlgorithmNames.SshEd25519, asked[0].Key.KeyType);

        Assert.AreEqual(1, forwarder.SignaturesDenied);
        Assert.AreEqual(1, harness.Agent.SignRequests, "被拒的那次不该到达本机 agent");

        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task 改动agent状态的请求一律不转发()
    {
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "k");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await using (remote)
        {
            // 17 = ADD_IDENTITY，22 = LOCK ——
            // 远端没有任何理由改动我们本机 agent 的状态。
            foreach (byte messageType in new byte[] { 17, 18, 19, 22, 23 })
            {
                byte[] response = await TestRemoteAgentClient.ExchangeAsync(
                    remote, [messageType], harness.Token);

                Assert.HasCount(1, response, $"报文 {messageType}");
                Assert.AreEqual(5, response[0], $"报文 {messageType} 应当被拒（FAILURE）");
            }
        }

        Assert.AreEqual(0, harness.Agent.SignRequests);
        Assert.AreEqual(0, harness.Agent.ListRequests, "这些请求根本不该到达本机 agent");
        Assert.AreEqual(0, harness.Agent.AddRequests, "加钥请求尤其不该到达本机 agent");

        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task 服务端拒绝转发时抛出并且不留处理器()
    {
        await using Harness harness = await Harness.StartAsync(new TestChannelScript
        {
            CloseAfterScript = false,
            ExitCode = null,
            RejectAgentForward = true,
        });

        SshChannel session = await harness.Connection.OpenSessionChannelAsync(null, harness.Token);

        SshForwardException error = await Assert.ThrowsExactlyAsync<SshForwardException>(
            async () => await AgentForwarder.RequestAsync(
                harness.Connection, session,
                AgentForwardOptions.Default with { LocalConnector = harness.ConnectAgentAsync }, harness.Token));

        Assert.Contains("AllowAgentForwarding", error.Message);

        // 失败之后不能把处理器留在会话上 —— 否则服务端随后发来的
        // auth-agent 通道会被一个没人管的处理器接住。
        Stream? sneaky = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelAuthAgent, default, harness.Token);

        Assert.IsNull(sneaky, "转发请求失败之后，agent 通道必须被拒绝");
    }

    [TestMethod]
    public async Task 没请求过转发时agent通道被拒()
    {
        await using Harness harness = await Harness.StartAsync();

        // 没有任何人请求过 agent 转发 —— 服务端主动开一条也不行。
        Stream? channel = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelAuthAgent, default, harness.Token);

        Assert.IsNull(channel, "没登记处理器的通道类型必须被明确拒绝");
        Assert.IsTrue(harness.Connection.IsAlive, "拒绝一条通道不该连累会话");
    }

    [TestMethod]
    public async Task 释放之后agent通道不再被接受()
    {
        await using Harness harness = await Harness.StartAsync();

        using var key = InMemorySshSigner.GenerateEd25519();
        harness.Agent.Add(key, "k");

        (AgentForwarder forwarder, Stream remote) = await SetUpAsync(harness);
        await remote.DisposeAsync();
        await forwarder.DisposeAsync();

        Stream? after = await harness.ChannelServer.OpenChannelToClientAsync(
            SshProtocolNames.ChannelAuthAgent, default, harness.Token);

        Assert.IsNull(after, "释放之后就不该再接 agent 通道了");
    }
}
