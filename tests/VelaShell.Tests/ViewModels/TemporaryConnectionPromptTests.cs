using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Startup;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 临时连接(外部拉起、「最近连接」重建的,不在库里)的登录框:不给「记住密码」(勾了也落不了盘,plan.md §252),
/// 改给「另存为连接」—— 存成正式配置之后才记住密码(维护者 2026-10-25 定,第二轮第 15 条)。
/// </summary>
/// <remarks>落库那几条用真的 SonnetDB 仓储:「存没存进去」只有落到同一个库里才看得出来。</remarks>
[TestClass]
public sealed class TemporaryConnectionPromptTests
{
    private string _directory = null!;
    private SonnetDbEngine _engine = null!;
    private SonnetDbSessionRepository _repository = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"velashell_tempconn_{Guid.NewGuid():N}");
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

    [TestMethod]
    public async Task TemporaryProfile_HidesRememberPassword_AndRemembersNothingUnlessSaved()
    {
        var vm = new AuthenticationDialogViewModel("bastion.example", 2222, "ops", temporaryProfile: true);
        await vm.NextCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.IsTemporaryProfile);
        Assert.IsFalse(vm.ShowRememberPassword, "临时连接上「记住密码」存不下,不该给");
        Assert.IsFalse(vm.SaveAsConnection, "默认不存:外部拉起带来的本来就是一次性的东西");
        Assert.IsTrue(vm.RememberPassword, "前提:那一勾的默认值是开着的 —— 临时连接不能因此被当成要记住");

        vm.Password = SecureStringConvert.FromPlaintext("once");
        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();

        Assert.IsFalse(result.RememberPassword);
        Assert.IsFalse(result.SaveAsConnection);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveAsConnection_RemembersTheCredentials_WhateverTheMethod(bool keyMethod)
    {
        var vm = new AuthenticationDialogViewModel("bastion.example", 2222, "ops", temporaryProfile: true);
        await vm.NextCommand.Execute().FirstAsync();
        if (keyMethod)
        {
            await vm.SelectKeyCommand.Execute().FirstAsync();
            vm.PrivateKeyPath = "/keys/id_ed25519";
            vm.PrivateKeyPassphrase = "pp";
        }
        else
        {
            vm.Password = SecureStringConvert.FromPlaintext("keep-me");
        }
        vm.SaveAsConnection = true;

        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();

        Assert.IsTrue(result.SaveAsConnection);
        Assert.IsTrue(result.RememberPassword, "存成正式配置之后才记住密码(仍受常规页的总开关约束)");
    }

    [TestMethod]
    public async Task SavedProfile_KeepsRememberPassword_AndNeverSavesAsConnection()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "root") { SaveAsConnection = true };
        await vm.NextCommand.Execute().FirstAsync();
        vm.Password = SecureStringConvert.FromPlaintext("pw");

        Assert.IsTrue(vm.ShowRememberPassword);
        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();

        Assert.IsTrue(result.RememberPassword);
        Assert.IsFalse(result.SaveAsConnection, "已经在库里的配置没有「另存」这回事");
    }

    [TestMethod]
    public async Task IsTemporary_TellsUnsavedProfilesApart()
    {
        SessionProfile saved = new() { Name = "saved", Host = "h", Port = 22, Username = "root" };
        await _repository.SaveSessionAsync(saved);
        SessionProfile launched = await ExternalLaunchCoordinator.ResolveProfileAsync(Request("lD9xKq2m"), _repository);

        Assert.IsFalse(await TemporaryConnectionPrompt.IsTemporaryAsync(_repository, saved));
        Assert.IsTrue(await TemporaryConnectionPrompt.IsTemporaryAsync(_repository, launched));
        Assert.IsFalse(await TemporaryConnectionPrompt.IsTemporaryAsync(null, launched), "没有仓储时退回以前的样子");
    }

    [TestMethod]
    public async Task SaveAsConnection_PutsTheProfileInTheTree_WithItsPassword_AndConnectingUpdatesIt()
    {
        var workflow = new ConnectionWorkflowService(_repository, ConnectingSsh());
        SessionProfile launched = await ExternalLaunchCoordinator.ResolveProfileAsync(Request(password: null), _repository);
        AuthenticationResult result = new("JMS-7f3a", AuthMethod.Password, null, null, null, null,
            RememberPassword: true, SaveAsConnection: true);

        SessionProfile copy = SharedCredentialPrompt.Apply(launched, credential: null, result, "typed-once", out _);
        await TemporaryConnectionPrompt.SaveAsConnectionAsync(workflow, copy);
        await workflow.ConnectProfileAsync(copy);

        SessionProfile? stored = await _repository.GetSessionAsync(launched.Id);
        Assert.IsNotNull(stored, "另存为连接之后它就在会话树里了");
        Assert.AreEqual("typed-once", stored.Password, "存成正式配置之后才记住密码");
        Assert.IsTrue(stored.RememberPassword);
        Assert.IsNotNull(stored.LastConnectedAt, "连上时工作流照常回写:它已经不是临时配置了");
        Assert.IsFalse(await TemporaryConnectionPrompt.IsTemporaryAsync(_repository, copy), "重试时登录框回到「记住密码」");
    }

    [TestMethod]
    public async Task SaveAsConnection_HonoursTheGlobalRememberPasswordsSwitch()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { General = { RememberPasswords = false } });
        var workflow = new ConnectionWorkflowService(_repository, ConnectingSsh(), settingsService: settings);
        SessionProfile launched = await ExternalLaunchCoordinator.ResolveProfileAsync(Request(password: null), _repository);
        AuthenticationResult result = new("JMS-7f3a", AuthMethod.Password, null, null, null, null,
            RememberPassword: true, SaveAsConnection: true);

        SessionProfile copy = SharedCredentialPrompt.Apply(launched, credential: null, result, "typed-once", out _);
        await TemporaryConnectionPrompt.SaveAsConnectionAsync(workflow, copy);

        SessionProfile? stored = await _repository.GetSessionAsync(launched.Id);
        Assert.IsNotNull(stored, "连接照样存下");
        Assert.IsNull(stored.Password, "总开关关着:密码不落盘");
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

    private static ISshConnectionService ConnectingSsh()
    {
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ssh.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
            .Returns(call => new SshSession { ConnectionInfo = (ConnectionInfo)call[0], Status = SessionStatus.Connected });
        return ssh;
    }
}
