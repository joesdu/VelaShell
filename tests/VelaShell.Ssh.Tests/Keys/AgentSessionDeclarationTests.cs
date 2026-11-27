// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.4（session-bind@openssh.com，OpenSSH PROTOCOL.agent §1）

using System.Net.Sockets;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentSessionDeclarationTests
{
    /// <summary>一份 agent 验得过的会话证明：主机公钥对会话标识的签名。</summary>
    private static async Task<SshSessionProof> ProofAsync()
    {
        using var hostKey = InMemorySshSigner.GenerateEd25519();
        byte[] sessionId = new byte[32];
        Random.Shared.NextBytes(sessionId);
        byte[] signature = await hostKey.SignAsync(sessionId, SshAlgorithmNames.SshEd25519);
        return new SshSessionProof(hostKey.PublicKey.Blob.ToArray(), sessionId, signature);
    }

    [TestMethod]
    public async Task 声明的字段照PROTOCOL_agent排且同一个会话只发一次()
    {
        TestAgent agent = new();
        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task serving = agent.ServeAsync(theirs, cts.Token);

        SshSessionProof proof = await ProofAsync();
        await using (var client = SshAgentClient.FromStream(ours))
        {
            Assert.IsTrue(await client.DeclareSessionAsync(proof, SshAgentConnectionPurpose.Forwarding, cts.Token));

            // 同一条 agent 连接上的几把钥共用一次声明 —— 第二次不再发。
            Assert.IsTrue(await client.DeclareSessionAsync(proof, SshAgentConnectionPurpose.Forwarding, cts.Token));
        }

        TestSessionDeclaration declaration = Assert.ContainsSingle(agent.Declarations);
        Assert.IsTrue(declaration.IsForwarding);
        Assert.IsTrue(declaration.SignatureVerified, "agent 用主机公钥验得过签名，说明三个字段的顺序与内容都对");
        Assert.AreSequenceEqual(proof.SessionId, declaration.SessionId);

        await cts.CancelAsync();
        await serving;
    }

    [TestMethod]
    public async Task Agent回FAILURE时交回false且后面照常能用()
    {
        TestAgent agent = new() { DeclarationReply = TestDeclarationReply.Reject };
        using var key = InMemorySshSigner.GenerateEd25519();
        agent.Add(key, "k");

        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task serving = agent.ServeAsync(theirs, cts.Token);

        await using (var client = SshAgentClient.FromStream(ours))
        {
            Assert.IsFalse(await client.DeclareSessionAsync(
                await ProofAsync(), SshAgentConnectionPurpose.Authentication, cts.Token));
            Assert.HasCount(1, await client.ListIdentitiesAsync(cts.Token));
        }

        await cts.CancelAsync();
        await serving;
    }

    [TestMethod]
    public async Task 自己连上的客户端在声明把连接弄断时重开且不再声明()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这台机器不支持 Unix 套接字。");
        }

        // 个别 agent 收到不认识的报文就断开。认证那一路的签名器拿的就是这一个客户端：
        // 它不能跟着一起失效，不然这把钥就白白用不了。
        TestAgent agent = new() { DeclarationReply = TestDeclarationReply.Disconnect };
        using var key = InMemorySshSigner.GenerateEd25519();
        agent.Add(key, "k");

        // 文件名要短：macOS 的临时目录本身就有约 50 个字符，Unix 套接字路径上限是 104。
        string path = Path.Combine(Path.GetTempPath(), $"vsa-{Guid.NewGuid().ToString("N")[..8]}.sock");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();

        List<Task> sessions = [];
        var accepting = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await listener.AcceptAsync(cts.Token);
                }
                catch (SocketException ex) when (cts.IsCancellationRequested && ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    // Windows 上取消挂起的 Accept 也可能直接返回套接字错误。
                    break;
                }
                lock (sessions)
                {
                    sessions.Add(agent.ServeAsync(new NetworkStream(accepted, ownsSocket: true), cts.Token));
                }
            }
        });

        try
        {
            await using SshAgentClient client = await SshAgentClient.ConnectAsync(path, cts.Token);
            SshSessionProof proof = await ProofAsync();

            Assert.IsFalse(await client.DeclareSessionAsync(proof, SshAgentConnectionPurpose.Authentication, cts.Token));
            Assert.HasCount(1, await client.ListIdentitiesAsync(cts.Token), "重开之后照常能列钥");
            Assert.IsFalse(await client.DeclareSessionAsync(proof, SshAgentConnectionPurpose.Authentication, cts.Token));

            Assert.HasCount(1, agent.Declarations, "重开之后不再声明");
            Assert.AreEqual(2, agent.Connections);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await accepting;
            }
            catch (OperationCanceledException)
            {
                // 收尾。
            }

            Task[] pending;
            lock (sessions)
            {
                pending = [.. sessions];
            }
            await Task.WhenAll(pending);
            File.Delete(path);
        }
    }

    [TestMethod]
    public void 只认session_bind这一个扩展()
    {
        static byte[] Extension(string name)
        {
            System.Buffers.ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteByte(27);
            writer.WriteUtf8String(name);
            writer.WriteUtf8String("payload");
            return buffer.WrittenSpan.ToArray();
        }

        Assert.IsTrue(SshAgentClient.IsSessionDeclaration(Extension("session-bind@openssh.com")));
        Assert.IsFalse(SshAgentClient.IsSessionDeclaration(Extension("session-bind@openssh.com.evil")));
        Assert.IsFalse(SshAgentClient.IsSessionDeclaration(Extension("query")));
        Assert.IsFalse(SshAgentClient.IsSessionDeclaration([27, 0, 0]));
        Assert.IsFalse(SshAgentClient.IsSessionDeclaration([13]));
    }
}
