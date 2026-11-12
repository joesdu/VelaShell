using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 插件连接存着的机密在这台设备上解不开(没开主密码,本机密钥换过 / 数据从别处拷来,载入时仍是密文原样):
/// 界面上的入口说清是哪一项、为什么(按本机加密密钥的解析结果)、怎么办,这次不连 —— 密文与空值都不交给插件,
/// 插件声明的机密字段也不弹一个填不了它的登录框。
/// </summary>
[TestClass]
public sealed class PluginUnreadableSecretFlowTests
{
    private const string Sealed = "enc1:AAAAsealed";

    /// <summary>终端协议:标签留着、写明原因(本机密钥不在这里),插件一次连接也没收到。</summary>
    [TestMethod]
    public async Task PluginTerminal_WithAnUnreadableSecret_SaysWhichAndWhy_AndNeverConnects()
    {
        var terminal = new RecordingTerminal();
        var registry = new PluginProtocolRegistry();
        registry.Register("test", new ProtocolDescriptor
        {
            Id = "test.telnet",
            DisplayName = "Telnet",
            DefaultPort = 23,
            Fields = [new() { Key = "token", Label = "Token", IsSecret = true }],
        }, terminal);
        IMasterKeyStatusSource keys = Substitute.For<IMasterKeyStatusSource>();
        keys.GetKeyStatus().Returns(new MasterKeyStatus { Protection = MasterKeyProtection.SystemKeychain, MissingKeyIds = ["k1"] });
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        var vm = new MainWindowViewModel(terminalEmulatorFactory: () => emulator, protocolRegistry: registry, masterKeyStatus: keys);
        SessionProfile profile = PluginProfile("test.telnet");
        profile.PluginSecrets = new(StringComparer.Ordinal) { ["token"] = Sealed };

        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

        Assert.IsNotNull(tab);
        Assert.AreNotEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreEqual(0, terminal.Count, "密文不交、空值也不交:插件那边一次连接也没有");
        Assert.AreEqual(Expected("Plugin_UnreadableSecretKeyMissing", "Token"), vm.LastConnectionError);
    }

    /// <summary>文件协议:不弹登录框(它填不了插件的机密字段),不连,说清原因(说不出原因时按「解不开」说)。</summary>
    [TestMethod]
    public async Task PluginFileDocument_WithAnUnreadableSecret_SaysWhichAndWhy_WithoutPromptingOrConnecting()
    {
        var registry = new PluginProtocolRegistry();
        registry.Register("test", new ProtocolDescriptor
        {
            Id = "test.s3",
            DisplayName = "S3",
            DefaultPort = 9000,
            Fields = [new() { Key = "secretKey", Label = "Secret key", IsSecret = true }],
        }, Substitute.For<IProtocolFileSystem>());
        IPluginProtocolSessionService plugins = Substitute.For<IPluginProtocolSessionService>();
        var vm = new MainWindowViewModel(sftpService: Substitute.For<ISftpService>(), pluginProtocolService: plugins, protocolRegistry: registry);
        int prompts = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompts++;
            return Task.FromResult<SessionProfile?>(p);
        };
        SessionProfile profile = PluginProfile("test.s3");
        profile.Password = "readable";
        profile.PluginSecrets = new(StringComparer.Ordinal) { ["secretKey"] = Sealed };

        Assert.IsNull(await vm.OpenPluginDocumentForProfileAsync(profile));

        Assert.AreEqual(0, prompts, "登录框填不了插件的机密字段,不弹");
        await plugins.DidNotReceiveWithAnyArgs().OpenSessionAsync(default!, default);
        Assert.AreEqual(Expected("Plugin_UnreadableSecretUnknown", "Secret key"), vm.LastConnectionError);
    }

    /// <summary>工作台:同上 —— 不弹登录框、插件一次打开也没收到,说清原因。</summary>
    [TestMethod]
    public async Task Workspace_WithAnUnreadableSecret_SaysWhichAndWhy_WithoutPromptingOrConnecting()
    {
        var registry = new PluginProtocolRegistry();
        IWorkspaceProvider provider = Substitute.For<IWorkspaceProvider>();
        registry.RegisterWorkspace("acme.cache", new WorkspaceDescriptor
        {
            Id = "acme.cache",
            DisplayName = "Acme Cache",
            DefaultPort = 6379,
            Fields = [new() { Key = "aclToken", Label = "ACL token", IsSecret = true }],
        }, provider);
        var vm = new MainWindowViewModel(protocolRegistry: registry, workspaceLauncher: new PluginWorkspaceLauncher(registry));
        int prompts = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompts++;
            return Task.FromResult<SessionProfile?>(p);
        };
        SessionProfile profile = PluginProfile("acme.cache");
        profile.Password = "readable";
        profile.PluginSecrets = new(StringComparer.Ordinal) { ["aclToken"] = Sealed };

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.AreEqual(0, prompts);
        await provider.DidNotReceiveWithAnyArgs().OpenAsync(default!, default);
        Assert.AreEqual(Expected("Plugin_UnreadableSecretUnknown", "ACL token"), vm.LastConnectionError);
    }

    // ---- 工具 ----

    private static string Expected(string reasonKey, string label) =>
        Strings.Format(reasonKey, Strings.Format("Plugin_UnreadableSecretSubject", Strings.Format("Plugin_SecretLabelQuoted", label)));

    private static SessionProfile PluginProfile(string protocolId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "box",
        Host = "box.example",
        Port = 9000,
        Username = "admin",
        AuthMethod = AuthMethod.Password,
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = protocolId,
    };

    /// <summary>记下每次连接请求的替身终端协议。</summary>
    private sealed class RecordingTerminal : IProtocolTerminal
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            throw new InvalidOperationException("the test terminal never connects");
        }
    }
}
