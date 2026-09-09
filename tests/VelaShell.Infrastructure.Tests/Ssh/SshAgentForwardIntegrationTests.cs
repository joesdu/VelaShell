using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Tmds.Ssh;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.DependencyInjection;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// SSH agent 转发的**端到端**验证:对着 <c>docker-compose.test.yml</c> 里那台真 sshd 跑。
/// </summary>
/// <remarks>
/// <para>
/// 上面那些用例把协议、策略闸门、端点解析各自钉住了,但它们都没有回答最要紧的那个问题:
/// <b>sshd 真的会建出那个套接字吗?远端的 <c>ssh-add</c> 真的会用上它吗?</b>
/// 这条路径横跨 Tmds.Ssh 的 <c>streamlocal-forward@openssh.com</c>、sshd 的转发策略、
/// 远端文件系统权限、以及 <c>SSH_AUTH_SOCK</c> 的语义 —— 任何一环猜错,单元测试全绿而功能是死的。
/// </para>
/// <para>
/// <b>本机 agent 用一个假的命名管道顶上</b>,不依赖机器上真有 ssh-agent 服务
/// (Windows 上那个服务默认是禁用的,启用还要管理员)。假 agent 只答一条身份列表 ——
/// 这已经足够证明整条链路通了:远端的 <c>ssh-add -l</c> 打出我们喂进去的那把钥匙,
/// 就说明字节确实从容器里绕了一圈回到了本机。
/// </para>
/// <para>
/// <b>没有容器就跳过</b>(<c>Assert.Inconclusive</c>),不让本类在没起 docker 的机器上变红:
/// <c>docker compose -f docker-compose.test.yml up -d</c> 起来之后再跑。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("SshIntegration")]
public class SshAgentForwardIntegrationTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;
    private const string User = "testuser";
    private const string Password = "testpass";

    /// <summary>测试上下文,MSTest 按约定注入。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 全链路:起转发 → 远端 <c>ssh-add -l</c> 列出本机 agent 里的钥匙 →
    /// 远端 <c>ssh-add -D</c> 被策略闸门拒掉 → 目录是 700 → 停掉之后套接字被清干净。
    /// </summary>
    [TestMethod]
    public async Task ForwardedAgentIsUsableFromTheRemoteHostAndStaysReadOnly()
    {
        CancellationToken token = TestContext.CancellationTokenSource.Token;
        await SkipUnlessTestServerIsUpAsync(token);

        // 一把货真价实的 ed25519 公钥 blob:body 必须是 32 字节,否则远端的 ssh-add
        // 解不出这把钥匙,列表就成了空的 —— 那会让这个用例因为"假数据不合法"而失败,
        // 与被测的转发链路毫无关系。
        byte[] blob = BuildKeyBlob("ssh-ed25519", RandomNumberGenerator.GetBytes(32));
        const string comment = "vela-integration@fake-agent";

        using var agentCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        string pipeName = $"vela-itest-agent-{Guid.NewGuid():N}";
        Task fakeAgent = RunFakeAgentAsync(pipeName, blob, comment, agentCts.Token);

        using TmdsSshClientWrapper client = CreateClient();
        await client.ConnectAsync(token);

        var agentClient = new LocalSshAgentClient(() => $@"\\.\pipe\{pipeName}");
        IAgentForwardHandle handle = await StartForwardOrSkipAsync(client, agentClient, token);
        string socket = handle.RemoteSocketPath;

        try
        {
            Assert.StartsWith("/config/.velashell/agent/", socket,
                "套接字要落在家目录下那个 700 的子目录里,不是 /tmp");

            // ① 远端真的能用上它。ssh-add -l 读的就是 SSH_AUTH_SOCK,
            //    它打出来的指纹是 OpenSSH 自己按同一把公钥 blob 算的。
            RemoteCommandResult listing = await client.RunCommandDetailedAsync(
                $"SSH_AUTH_SOCK='{socket}' ssh-add -l", token);

            Assert.AreEqual(0, listing.ExitCode,
                $"远端 ssh-add -l 应当成功。stdout={listing.StandardOutput} stderr={listing.StandardError}");
            Assert.Contains(SshAgentProtocol.Fingerprint(blob), listing.StandardOutput,
                "远端列出的指纹必须就是本机 agent 里那把钥匙的");
            Assert.Contains(comment, listing.StandardOutput);
            Assert.IsGreaterThanOrEqualTo(1, handle.TotalConnections, "远端至少连过一次转发套接字");

            // ② 但它只能读。ssh-add -D(清空 agent)必须被就地拒掉。
            //    这是本实现与 `ssh -A` 的实质差别,也是最值得端到端验一次的一条。
            RemoteCommandResult wipe = await client.RunCommandDetailedAsync(
                $"SSH_AUTH_SOCK='{socket}' ssh-add -D", token);

            Assert.AreNotEqual(0, wipe.ExitCode,
                $"远端不该清得掉本机 agent。stdout={wipe.StandardOutput} stderr={wipe.StandardError}");
            Assert.IsGreaterThanOrEqualTo(1, handle.DeniedRequests, "被拒的请求要计数,审计才有东西可记");

            // ③ 父目录必须是 700 —— 套接字自身的权限由 sshd 的 umask 决定(通常是
            //    srwxr-xr-x),管不住同机其它用户,只能靠父目录管。
            RemoteCommandResult mode = await client.RunCommandDetailedAsync(
                "stat -c %a \"$HOME/.velashell/agent\"", token);

            Assert.AreEqual("700", mode.StandardOutput.Trim(),
                "目录权限一旦松掉,同机任何用户都能连上来用你的钥匙签名");
        }
        finally
        {
            await handle.DisposeAsync();
            await agentCts.CancelAsync();
        }

        // ④ 停掉之后远端不该留下一个死套接字。
        RemoteCommandResult leftover = await client.RunCommandDetailedAsync(
            $"test -e '{socket}' && echo present || echo gone", token);
        Assert.AreEqual("gone", leftover.StandardOutput.Trim());

        await IgnoreCancellationAsync(fakeAgent);
    }

    /// <summary>
    /// 本机没有可用 agent 时,转发**不该**在远端留下任何东西。
    /// </summary>
    /// <remarks>
    /// 建了套接字却接不上本机 agent,比不开更糟:用户的 <c>ssh</c> 会以为有 agent,
    /// 试完 agent 才回落到密码,平白多一轮失败。所以先探本机、探不到就不动远端。
    /// </remarks>
    [TestMethod]
    public async Task WithoutALocalAgentNothingIsCreatedOnTheRemoteHost()
    {
        CancellationToken token = TestContext.CancellationTokenSource.Token;
        await SkipUnlessTestServerIsUpAsync(token);

        using TmdsSshClientWrapper client = CreateClient();
        await client.ConnectAsync(token);
        var missing = new LocalSshAgentClient(() => $@"\\.\pipe\vela-itest-absent-{Guid.NewGuid():N}");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.StartAgentForwardAsync(missing, token));

        RemoteCommandResult sockets = await client.RunCommandDetailedAsync(
            "ls \"$HOME/.velashell/agent\" 2>/dev/null | wc -l", token);
        Assert.AreEqual("0", sockets.StandardOutput.Trim(),
            "本机没有 agent 时不该在远端建出任何套接字(目录可能因为之前的用例已存在,里面必须是空的)");
    }

    /// <summary>
    /// 起转发;服务端把转发关掉了就跳过,并**把该改哪两条指令说清楚**。
    /// </summary>
    /// <remarks>
    /// 这条分支不是防御性代码,是实测撞出来的:<c>docker-compose.test.yml</c> 用的
    /// linuxserver/openssh-server 镜像模板默认 <c>AllowTcpForwarding no</c>,
    /// 而 sshd 把 streamlocal 的远程转发也挡在那道闸后面 —— 哪怕
    /// <c>AllowStreamLocalForwarding yes</c>。仓库里那份 compose 现在挂了一个
    /// <c>custom-cont-init.d</c> 脚本把它打开,但**用旧容器跑的人会撞上这条**,
    /// 那时候一句 "SSH_MSG_REQUEST_FAILURE" 帮不了任何人。
    /// </remarks>
    private static async Task<IAgentForwardHandle> StartForwardOrSkipAsync(
        TmdsSshClientWrapper client, ISshAgentClient agent, CancellationToken token)
    {
        try
        {
            return await client.StartAgentForwardAsync(agent, token);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("AllowTcpForwarding", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "测试用 sshd 把转发关掉了。重建容器以套用 tests/fixtures/ssh-init 里的开关:"
                + " docker compose -f docker-compose.test.yml down -v"
                + " && docker compose -f docker-compose.test.yml up -d"
                + $"（服务端原话:{ex.Message}）");
            throw; // 到不了:Assert.Inconclusive 自己会抛
        }
    }

    /// <summary>连不上测试容器就跳过,而不是红。</summary>
    private static async Task SkipUnlessTestServerIsUpAsync(CancellationToken token)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("假 agent 用的是命名管道;类 Unix 上要换成 AF_UNIX,本用例暂不覆盖。");
        }
        using var probe = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await probe.ConnectAsync(Host, Port, timeout.Token);
        }
        catch (Exception)
        {
            Assert.Inconclusive(
                $"测试用 sshd 没在 {Host}:{Port} 上:先跑 docker compose -f docker-compose.test.yml up -d");
        }
    }

    /// <summary>按测试容器的凭据建一个真客户端。主机指纹一律放行 —— 这是我们自己起的容器。</summary>
    private static TmdsSshClientWrapper CreateClient()
    {
        var settings = new SshClientSettings($"{User}@{Host}")
        {
            Port = Port,
            AutoConnect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            HostAuthentication = (_, _) => ValueTask.FromResult(true)
        };
        InfrastructureServiceCollectionExtensions.AddCredential(settings, new ConnectionInfo
        {
            Host = Host,
            Port = Port,
            Username = User,
            AuthMethod = AuthMethod.Password,
            Password = Password
        });
        return new(settings);
    }

    /// <summary>
    /// 假 agent:一条命名管道,逐连接答一份固定的身份列表,其余消息号一概不管
    /// (它们本来就到不了这里 —— 策略闸门在更前面就挡掉了)。
    /// </summary>
    private static async Task RunFakeAgentAsync(
        string pipeName, byte[] blob, string comment, CancellationToken token)
    {
        byte[] answer = SshAgentProtocol.Frame(BuildIdentitiesAnswer(blob, comment));
        while (!token.IsCancellationRequested)
        {
            // maxInstances 给足:远端一次 ssh-add 就可能开不止一条,串行会让它们互相等。
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(token);
            }
            catch (Exception)
            {
                await server.DisposeAsync();
                return; // 取消或管道已拆:收工
            }
            _ = ServeAsync(server, answer, token);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream server, byte[] answer, CancellationToken token)
    {
        try
        {
            while (await SshAgentRelay.ReadMessageAsync(server, token) is { } request)
            {
                if (request[0] != SshAgentProtocol.RequestIdentities)
                {
                    break; // 到不了这里就对了
                }
                await server.WriteAsync(answer, token);
                await server.FlushAsync(token);
            }
        }
        catch (Exception)
        {
            // 假 agent 的收尾噪声不该盖住被测行为。
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    // ---- SSH 线格式(uint32 大端长度 + 字节)----

    private static byte[] BuildKeyBlob(string keyType, byte[] body)
    {
        var blob = new List<byte>();
        AppendString(blob, Encoding.ASCII.GetBytes(keyType));
        AppendString(blob, body);
        return [.. blob];
    }

    private static byte[] BuildIdentitiesAnswer(byte[] blob, string comment)
    {
        var payload = new List<byte> { SshAgentProtocol.IdentitiesAnswer };
        payload.AddRange(SshAgentRelay.Uint32(1));
        AppendString(payload, blob);
        AppendString(payload, Encoding.UTF8.GetBytes(comment));
        return [.. payload];
    }

    private static void AppendString(List<byte> target, byte[] value)
    {
        target.AddRange(SshAgentRelay.Uint32((uint)value.Length));
        target.AddRange(value);
    }
}
