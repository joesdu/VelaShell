// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测:SshAgentClient 列身份(draft-miller-ssh-agent 的 REQUEST_IDENTITIES / IDENTITIES_ANSWER)
//
// 真实 agent 里常有库不认识的身份:FIDO 钥是 sk-*,老机器上还有 ssh-dss,证书也可能坏掉或是不支持的类型。
// 其中任何一把都不该让整个列表失败 —— 列表失败意味着 agent 认证、agent 转发、自动加钥三条路一起断。
// ssh-add 会顺手把 id_*-cert.pub 证书一起加进去:那些证书现在认得,作为证书身份列出来。

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentListIdentitiesTests
{
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _serving;

        public Rig()
        {
            (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
            _serving = Task.Run(() => Agent.ServeAsync(theirs, _cts.Token));
            Client = SshAgentClient.FromStream(ours, "(测试 agent)");
        }

        public TestAgent Agent { get; } = new();

        public SshAgentClient Client { get; }

        public CancellationToken Token => _cts.Token;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _cts.CancelAsync();
            await _serving;
            _cts.Dispose();
        }
    }

    /// <summary>造一个只有类型串对、内容随意的公钥 blob。</summary>
    internal static byte[] OpaqueBlob(string keyType)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(keyType);
        writer.WriteString(RandomNumberGenerator.GetBytes(32));
        return buffer.WrittenSpan.ToArray();
    }

    [TestMethod]
    [DataRow("ssh-ed25519-cert-v01@openssh.com")]
    [DataRow("sk-ssh-ed25519@openssh.com")]
    [DataRow("ssh-dss")]
    public async Task 不认识的身份被跳过_其余照常列出(string opaqueType)
    {
        await using Rig rig = new();
        rig.Agent.AddOpaque(OpaqueBlob(opaqueType), "排在前面的那一把");
        using var key = InMemorySshSigner.GenerateEd25519();
        rig.Agent.Add(key, "id_ed25519");

        IReadOnlyList<SshAgentIdentity> identities = await rig.Client.ListIdentitiesAsync(rig.Token);

        SshAgentIdentity only = identities.Single();
        CollectionAssert.AreEqual(key.PublicKey.Blob.ToArray(), only.PublicKey.Blob.ToArray());
        Assert.AreEqual("id_ed25519", only.Comment);
    }

    [TestMethod]
    public async Task 证书身份被列出且RSA证书按SHA2签()
    {
        await using Rig rig = new();
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures");
        ISshSigner rsa = await SshPrivateKeyFile.LoadAsync(Path.Combine(fixtures, "hostcert-rsa"), cancellationToken: rig.Token);
        byte[] certificate = Convert.FromBase64String(
            File.ReadAllText(Path.Combine(fixtures, "hostcert-rsa-cert.pub")).Split(' ')[1]);
        rig.Agent.AddCertificate(rsa, certificate, "hostcert-rsa-cert.pub");

        SshAgentIdentity only = (await rig.Client.ListIdentitiesAsync(rig.Token)).Single();
        Assert.IsTrue(only.PublicKey.IsCertificate);
        Assert.AreEqual(SshAlgorithmNames.RsaSha512CertV01, only.PublicKey.SignatureAlgorithms[0]);

        // 证书的算法名带后缀：标志位要按去掉后缀的名字定，否则 agent 会签成 SHA-1 的 ssh-rsa。
        byte[] data = "agent 里的证书签的数据"u8.ToArray();
        byte[] signature = await rig.Client.SignAsync(only.PublicKey.Blob, data, SshAlgorithmNames.RsaSha512CertV01, rig.Token);
        Assert.IsTrue(only.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.RsaSha512CertV01));
    }

    [TestMethod]
    public async Task 格式坏掉的blob也只是跳过()
    {
        await using Rig rig = new();
        rig.Agent.AddOpaque([0, 0, 0, 11, (byte)'s', (byte)'s', (byte)'h'], "截断的 blob");
        using var key = InMemorySshSigner.GenerateEd25519();
        rig.Agent.Add(key, "id_ed25519");

        IReadOnlyList<SshAgentIdentity> identities = await rig.Client.ListIdentitiesAsync(rig.Token);

        Assert.HasCount(1, identities);
    }

    [TestMethod]
    public async Task 取凭据时证书不会让整条agent认证失败()
    {
        await using Rig rig = new();
        rig.Agent.AddOpaque(OpaqueBlob("ssh-ed25519-cert-v01@openssh.com"), "id_ed25519-cert.pub");
        using var key = InMemorySshSigner.GenerateEd25519();
        rig.Agent.Add(key, "id_ed25519");

        IReadOnlyList<SshCredential> credentials = await rig.Client.GetCredentialsAsync(rig.Token);

        Assert.HasCount(1, credentials);
    }
}
