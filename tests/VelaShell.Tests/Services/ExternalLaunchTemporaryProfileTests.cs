using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Startup;
using VelaShell.Presentation.Services;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 外部拉起(Xshell 兼容登录)的一次性连接只活在本次运行:现搭的临时配置连上、重连都不落库 ——
/// 不进会话树,也就不随云同步上传(同步从仓储取配置);最近连接照记一条(不含密码)。
/// 没带凭据、库里有同目标配置时复用那一条,照常更新它的最近连接时间(既有路径不变)。
/// </summary>
/// <remarks>用真的 SonnetDB 仓储:「落没落库」只有落到同一个库里才看得出来。</remarks>
[TestClass]
[TestCategory("Startup")]
public sealed class ExternalLaunchTemporaryProfileTests
{
    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_extlaunch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _engine = SonnetDbTestTemplate.Open(Path.Combine(_directory, "sonnetdb"));
        _repository = new SonnetDbSessionRepository(_engine, new AesSecretProtector(Path.Combine(_directory, "secret.key")));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // 临时目录,删不掉不影响结论。
        }
    }

    private static ExternalLaunchRequest Request(string? password) => new()
    {
        Scheme = "ssh",
        ConnectionType = ConnectionType.SSH,
        Host = "bastion.example",
        Port = 2222,
        Username = "JMS-7f3a",
        Password = password,
    };

    private static SessionProfile Saved() => new()
    {
        Name = "bastion",
        Host = "bastion.example",
        Port = 2222,
        Username = "JMS-7f3a",
        AuthMethod = AuthMethod.Password,
    };

    private static ISshConnectionService ConnectingSsh()
    {
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ssh.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(call => new SshSession { ConnectionInfo = (ConnectionInfo)call[0], Status = SessionStatus.Connected });
        return ssh;
    }

    /// <summary>带了一次性口令:即便库里有同目标的配置也不碰它,现搭的那份连上、断线重连之后都不在库里。</summary>
    [TestMethod]
    public async Task WithCredentials_TheTemporaryProfileIsNeverSaved_AndTheSavedOneIsUntouched()
    {
        SessionProfile saved = Saved();
        await _repository.SaveSessionAsync(saved);
        IRecentConnectionService recent = Substitute.For<IRecentConnectionService>();
        var workflow = new ConnectionWorkflowService(_repository, ConnectingSsh(), recent);

        SessionProfile profile = await ExternalLaunchCoordinator.ResolveProfileAsync(Request("lD9xKq2m"), _repository);
        await workflow.ConnectProfileAsync(profile);
        await workflow.ReconnectProfileAutomaticallyAsync(profile);

        Assert.AreNotEqual(saved.Id, profile.Id, "带了凭据的一律现搭,不顶替已保存的那一条");
        Assert.IsFalse(profile.RememberPassword);
        List<SessionProfile> all = await _repository.GetAllSessionsAsync();
        Assert.AreSequenceEqual([saved.Id], [.. all.Select(p => p.Id)], "临时配置不进会话树(云同步也就带不上它)");
        Assert.IsNull(all[0].LastConnectedAt, "已保存的那一条这次没被用到,不该被改");
        await recent.Received(2).RecordAsync(Arg.Is<RecentConnectionEntry>(e =>
            e.ProfileId == profile.Id && e.Host == "bastion.example" && e.Port == 2222 && e.Username == "JMS-7f3a"));
    }

    /// <summary>没带凭据、库里也没有同目标的配置:同样现搭、同样不落库(以前连上之后会以一条无密码的配置落进会话树)。</summary>
    [TestMethod]
    public async Task WithoutCredentials_AndNothingSaved_StaysTemporary()
    {
        var workflow = new ConnectionWorkflowService(_repository, ConnectingSsh());

        SessionProfile profile = await ExternalLaunchCoordinator.ResolveProfileAsync(Request(password: null), _repository);
        profile.Password = "typed-into-the-login-dialog";   // 真实流程里缺密码先弹登录框,交回的副本带着它
        await workflow.ConnectProfileAsync(profile);

        Assert.IsEmpty(await _repository.GetAllSessionsAsync());
    }

    /// <summary>没带凭据、库里有同目标的配置:复用那一条(用户自己存的密码 / 密钥在那),连上照常更新它的最近连接时间。</summary>
    [TestMethod]
    public async Task WithoutCredentials_ReusesTheSavedProfile_AndUpdatesIt()
    {
        SessionProfile saved = Saved();
        await _repository.SaveSessionAsync(saved);
        var workflow = new ConnectionWorkflowService(_repository, ConnectingSsh());

        SessionProfile profile = await ExternalLaunchCoordinator.ResolveProfileAsync(Request(password: null), _repository);
        profile.Password = "typed-into-the-login-dialog";   // 同上:登录框交回的密码
        await workflow.ConnectProfileAsync(profile);

        Assert.AreEqual(saved.Id, profile.Id);
        SessionProfile stored = (await _repository.GetSessionAsync(saved.Id))!;
        Assert.IsNotNull(stored.LastConnectedAt);
        Assert.HasCount(1, await _repository.GetAllSessionsAsync());
    }
}
