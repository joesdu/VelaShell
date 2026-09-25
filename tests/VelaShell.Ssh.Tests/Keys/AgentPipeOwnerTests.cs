// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测:Windows 上连命名管道形态的 agent 之前，确认管道的属主可信
//
// OpenSSH agent 的管道名是固定的。服务没在跑时，本机任何用户都能先把这个名字建出来，
// 之后收到我们的签名请求 —— 开了「自动加载密钥到 Agent」的话还有明文私钥。

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentPipeOwnerTests
{
    [TestMethod]
    public void 只信当前用户_SYSTEM与Administrators()
    {
        if (OperatingSystem.IsWindows())
        {
            AssertOwners();
        }
        else
        {
            Assert.Inconclusive("命名管道的属主检查只在 Windows 上存在。");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwners()
    {
        SecurityIdentifier me = new("S-1-5-21-1111111111-2222222222-3333333333-1001");
        SecurityIdentifier someoneElse = new("S-1-5-21-1111111111-2222222222-3333333333-1002");

        Assert.IsTrue(SshAgentClient.IsTrustedPipeOwner(me, me), "当前用户（1Password、Pageant 之类）");
        Assert.IsTrue(SshAgentClient.IsTrustedPipeOwner(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), me), "SYSTEM（OpenSSH 的 agent 服务）");
        Assert.IsTrue(SshAgentClient.IsTrustedPipeOwner(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), me), "Administrators");

        Assert.IsFalse(SshAgentClient.IsTrustedPipeOwner(someoneElse, me), "别的用户抢先建的管道");
        Assert.IsFalse(SshAgentClient.IsTrustedPipeOwner(null, me));
    }

    [TestMethod]
    public async Task 同一用户建的管道agent照常能连()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("只在 Windows 上走命名管道。");
        }

        // 属主检查不能把正常的 agent 挡在外面：同一用户建的管道，属主就是当前用户
        // （提权的进程则是 Administrators）。
        string name = $"velashell-test-agent-{Guid.NewGuid():N}";
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        TestAgent agent = new();
        using InMemorySshSigner key = InMemorySshSigner.GenerateEd25519();
        agent.Add(key, "id_ed25519");

        await using NamedPipeServerStream server = new(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await agent.ServeAsync(server, cts.Token);
        });

        await using (SshAgentClient client = await SshAgentClient.ConnectAsync($@"\\.\pipe\{name}", cts.Token))
        {
            IReadOnlyList<SshAgentIdentity> identities = await client.ListIdentitiesAsync(cts.Token);
            Assert.HasCount(1, identities);
        }

        await cts.CancelAsync();
        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
            // 收尾。
        }
    }
}
