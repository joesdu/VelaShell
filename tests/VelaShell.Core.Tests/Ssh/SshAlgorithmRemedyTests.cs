using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 协商失败时的一键补救(<see cref="SshAlgorithmRemedy" />):只把诊断点名的那几个写进那一跳配置的自定义清单,
/// 不开「允许老算法」总开关;写进去之后真能连上。
/// </summary>
[TestClass]
[TestCategory("SshConnection")]
public sealed class SshAlgorithmRemedyTests
{
    private const string Group14Sha1 = "diffie-hellman-group14-sha1";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static SshAlgorithmRemedy Kex(params string[] algorithms) => new(SshAlgorithmKind.KeyExchange, algorithms);

    [TestMethod]
    public void ApplyTo_NoListYet_AppendsOnlyTheNamedOnes_AndLeavesTheLegacySwitchOff()
    {
        SshSessionOptions applied = Kex(Group14Sha1).ApplyTo(null);

        Assert.AreEqual("+" + Group14Sha1, applied.KexAlgorithms);
        Assert.IsFalse(applied.LegacyAlgorithms, "⚠️ 不能顺手把整组老算法都打开。");
        Assert.IsNull(applied.HostKeyAlgorithms);
        Assert.IsNull(applied.Ciphers);
        Assert.IsNull(applied.Macs);
        Assert.Contains(Group14Sha1, SshAlgorithmPreferences.Build(applied).KeyExchange.ToList(), "写进去的清单真能装成库的算法集。");
        Assert.DoesNotContain("ssh-rsa", SshAlgorithmPreferences.Build(applied).HostKey.ToList(), "别的类不动。");
    }

    [TestMethod]
    [DataRow("+curve25519-sha256", "+curve25519-sha256," + Group14Sha1)]
    [DataRow("curve25519-sha256", "curve25519-sha256," + Group14Sha1)]
    [DataRow("+" + Group14Sha1, "+" + Group14Sha1)]
    public void ApplyTo_ExistingAppendOrReplaceList_IsExtendedWithoutDuplicates(string existing, string expected)
    {
        SshSessionOptions original = new() { KexAlgorithms = existing, Compression = true };

        SshSessionOptions applied = Kex(Group14Sha1).ApplyTo(original);

        Assert.AreEqual(expected, applied.KexAlgorithms);
        Assert.IsTrue(applied.Compression, "别的选项原样带过去。");
        Assert.AreEqual(existing, original.KexAlgorithms, "传入的那份不改。");
        Assert.IsNull(SshAlgorithmPreferences.Validate(SshAlgorithmKind.KeyExchange, applied.KexAlgorithms, legacy: false));
    }

    /// <summary><c>-</c> / <c>^</c> 一行里接不上追加:写成此刻生效的完整清单,再把这几个接在后面。</summary>
    [TestMethod]
    [DataRow("-curve25519*")]
    [DataRow("^ecdh-sha2-nistp256")]
    public void ApplyTo_RemoveOrMoveList_BecomesTheEffectiveListPlusTheNamedOnes(string existing)
    {
        SshAlgorithmPreferences.TryApply(SshAlgorithmKind.KeyExchange, existing, legacy: false, out IReadOnlyList<string> before, out _);

        SshSessionOptions applied = Kex(Group14Sha1).ApplyTo(new SshSessionOptions { KexAlgorithms = existing });

        Assert.AreEqual(string.Join(',', [.. before, Group14Sha1]), applied.KexAlgorithms);
        Assert.IsNull(SshAlgorithmPreferences.Validate(SshAlgorithmKind.KeyExchange, applied.KexAlgorithms, legacy: false));
    }

    [TestMethod]
    public void Find_NamesExactlyWhatTheDiagnosticNames()
    {
        SshNegotiationException negotiation = new(
            SshNegotiationCategory.KeyExchange,
            ["diffie-hellman-group1-sha1", Group14Sha1, "curve25519-sha256", Group14Sha1],
            ["curve25519-sha256", "mlkem768x25519-sha256"],
            "SSH-2.0-OldBox");
        Exception translated = new VelaSshConnectionException("x", new InvalidOperationException("wrapped", negotiation));

        SshAlgorithmRemedy remedy = SshAlgorithmRemedy.Find(translated)!;

        Assert.IsNotNull(remedy);
        Assert.AreEqual(SshAlgorithmKind.KeyExchange, remedy.Kind);
        // group1 本版没实现(放开也没用),curve25519 我们本来就报了:只剩 group14-sha1,且只一次。
        Assert.AreSequenceEqual([Group14Sha1], [.. remedy.Algorithms]);
        Assert.AreEqual(0, remedy.HopDepth);
        Assert.IsNull(remedy.HopTarget);
    }

    [TestMethod]
    public void Find_NothingToEnable_OrNotANegotiationFailure_GivesNothing()
    {
        // 只剩 CBC 的老设备:放开也没用,不给按钮(诊断照旧说「放开也没用」)。
        SshNegotiationException cbcOnly = new(
            SshNegotiationCategory.EncryptionClientToServer, ["aes128-cbc", "3des-cbc"], ["aes128-ctr"], "SSH-2.0-OldBox");
        Assert.IsNull(SshAlgorithmRemedy.Find(new VelaSshConnectionException("x", cbcOnly)));
        Assert.IsNull(SshAlgorithmRemedy.Find(new VelaSshConnectionException("x", new IOException("reset"))));
        Assert.IsNull(SshAlgorithmRemedy.Find(null));
    }

    /// <summary>端到端:只认 group14-sha1 的服务端先谈不成、诊断点名;补救写进配置后真能连上,谈成的就是它。</summary>
    [TestMethod]
    public async Task OldServer_FailsWithANamedRemedy_ThenConnectsOnceItIsApplied()
    {
        await using LoopbackSshHost host = new(options: OnlyGroup14Sha1());
        ConnectionInfo info = Info(host.Port, ssh: null);

        Exception failure = await Assert.ThrowsAsync<Exception>(() => ConnectAsync(info));

        SshAlgorithmRemedy remedy = SshAlgorithmRemedy.Find(failure)!;
        Assert.IsNotNull(remedy, failure.ToString());
        Assert.AreSequenceEqual([Group14Sha1], [.. remedy.Algorithms]);
        Assert.AreEqual(0, remedy.HopDepth);
        Assert.Contains(Strings.Format("Ssh_AlgoMismatchEnable", Group14Sha1), failure.Message, "诊断点名的与按钮写进去的是同一组。");

        await using VelaSshClientWrapper connected = await ConnectAsync(Info(host.Port, remedy.ApplyTo(info.Ssh)));
        Assert.AreEqual(Group14Sha1, connected.GetConnectionDetails()!.Algorithms.KeyExchange);
    }

    /// <summary>谈不成的是跳板:补救指向那一跳(深度 1、跳板的地址),改的是跳板的配置,终点的不动。</summary>
    [TestMethod]
    public async Task OldJumpHost_RemedyPointsAtTheHop()
    {
        await using LoopbackSshHost jump = new(options: OnlyGroup14Sha1())
        {
            JumpTarget = (new TestChannelScript { CloseAfterScript = false }, null),
        };
        ConnectionInfo jumpInfo = Info(jump.Port, ssh: null, user: "jumper");
        ConnectionInfo target = TargetVia(jumpInfo);

        Exception failure = await Assert.ThrowsAsync<Exception>(() => ConnectAsync(target));

        SshAlgorithmRemedy remedy = SshAlgorithmRemedy.Find(failure)!;
        Assert.IsNotNull(remedy, failure.ToString());
        Assert.AreEqual(1, remedy.HopDepth);
        Assert.AreEqual($"jumper@127.0.0.1:{jump.Port}", remedy.HopTarget);
        Assert.AreSequenceEqual([Group14Sha1], [.. remedy.Algorithms]);

        await using VelaSshClientWrapper connected =
            await ConnectAsync(TargetVia(Info(jump.Port, remedy.ApplyTo(jumpInfo.Ssh), user: "jumper")));
        Assert.AreEqual(Group14Sha1, connected.GetConnectionDetails()!.Hops.Single().KeyExchange);
    }

    private static TestSshServerOptions OnlyGroup14Sha1() => new()
    {
        Algorithms = SshAlgorithmSet.Default with { KeyExchange = [Group14Sha1] },
    };

    private static ConnectionInfo Info(int port, SshSessionOptions? ssh, string user = "joe") => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = user,
        AuthMethod = AuthMethod.Password,
        Password = LoopbackSshHost.Password,
        Ssh = ssh,
    };

    private static ConnectionInfo TargetVia(ConnectionInfo jump) => new()
    {
        Host = "target.internal",
        Port = 22,
        Username = "joe",
        AuthMethod = AuthMethod.Password,
        Password = LoopbackSshHost.Password,
        JumpHost = jump,
    };

    private static async Task<VelaSshClientWrapper> ConnectAsync(ConnectionInfo info)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh) { Route = assembled.Route };
        using CancellationTokenSource cts = new(Deadline);
        try
        {
            await wrapper.ConnectAsync(cts.Token);
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }
}
