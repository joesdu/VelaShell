// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.3
//
// 加进去的钥是不是「同一把」，只有 agent 用它签出来的东西能证明 ——
// 所以每种密钥都走一遍「加钥 → 列出来 → 让 agent 签 → 用原公钥验」。
// 私钥字段写错位置时 agent 照样回 SUCCESS（spec 里那条 ⚠️），光看应答是验不出来的。

using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentAddIdentityTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("交给 agent 签的一段数据");

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

    public static IEnumerable<object[]> Keys()
    {
        yield return ["ed25519"];
        yield return ["rsa-2048"];
        yield return ["ecdsa-256"];
        yield return ["ecdsa-384"];
        yield return ["ecdsa-521"];
    }

    private static InMemorySshSigner Create(string kind) => kind switch
    {
        "ed25519" => InMemorySshSigner.GenerateEd25519(),
        "rsa-2048" => InMemorySshSigner.FromRsa(RSA.Create(2048)),
        "ecdsa-256" => InMemorySshSigner.FromEcdsa(ECDsa.Create(ECCurve.NamedCurves.nistP256)),
        "ecdsa-384" => InMemorySshSigner.FromEcdsa(ECDsa.Create(ECCurve.NamedCurves.nistP384)),
        "ecdsa-521" => InMemorySshSigner.FromEcdsa(ECDsa.Create(ECCurve.NamedCurves.nistP521)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [TestMethod]
    [DynamicData(nameof(Keys))]
    public async Task 加进去的钥能被列出并能签出原公钥验得过的签名(string kind)
    {
        await using Rig rig = new();
        using InMemorySshSigner key = Create(kind);

        await rig.Client.AddIdentityAsync(key, "C:/Users/joe/.ssh/id_" + kind, cancellationToken: rig.Token);

        IReadOnlyList<SshAgentIdentity> identities = await rig.Client.ListIdentitiesAsync(rig.Token);
        SshAgentIdentity only = identities.Single();
        CollectionAssert.AreEqual(key.PublicKey.Blob.ToArray(), only.PublicKey.Blob.ToArray());
        Assert.AreEqual("C:/Users/joe/.ssh/id_" + kind, only.Comment);

        string algorithm = key.SignatureAlgorithms[0];
        byte[] signature = await rig.Client.SignAsync(key.PublicKey.Blob, Data, algorithm, rig.Token);
        Assert.IsTrue(
            key.PublicKey.VerifySignature(signature, Data, algorithm),
            "agent 手里的不是同一把私钥 —— 私钥字段的顺序或布局写错了");
    }

    [TestMethod]
    public async Task 不带约束时发17而不是空约束的25()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();

        await rig.Client.AddIdentityAsync(key, "k", new SshAgentKeyConstraints(), rig.Token);

        Assert.AreEqual((byte)17, rig.Agent.LastAddMessageType);
        Assert.IsEmpty(rig.Agent.LastConstraints);
    }

    [TestMethod]
    public async Task 约束按报文写进25()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();

        await rig.Client.AddIdentityAsync(
            key,
            "k",
            new SshAgentKeyConstraints { Lifetime = TimeSpan.FromSeconds(90.2), ConfirmEachUse = true },
            rig.Token);

        Assert.AreEqual((byte)25, rig.Agent.LastAddMessageType);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, rig.Agent.LastConstraints.ToArray());
        Assert.AreEqual(91u, rig.Agent.LastLifetimeSeconds, "不足一秒向上取整，不能把有效期截短");
    }

    [TestMethod]
    public async Task agent拒绝时抛SshAgentException()
    {
        await using Rig rig = new();
        rig.Agent.RejectAdditions = true;
        using var key = InMemorySshSigner.GenerateEd25519();

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await rig.Client.AddIdentityAsync(key, "k", cancellationToken: rig.Token));

        StringAssert.Contains(error.Message, "锁定");
        Assert.AreEqual(1, rig.Agent.AddRequests);
        Assert.IsEmpty(rig.Agent.Keys);
    }

    [TestMethod]
    public async Task 同一把钥加两次由agent处理不重复登记()
    {
        await using Rig rig = new();
        using var key = InMemorySshSigner.GenerateEd25519();

        await rig.Client.AddIdentityAsync(key, "旧注释", cancellationToken: rig.Token);
        await rig.Client.AddIdentityAsync(key, "新注释", cancellationToken: rig.Token);

        Assert.AreEqual(2, rig.Agent.AddRequests, "库不查重，两次都要发出去");
        Assert.AreEqual("新注释", rig.Agent.Keys.Single().Comment);
    }
}
