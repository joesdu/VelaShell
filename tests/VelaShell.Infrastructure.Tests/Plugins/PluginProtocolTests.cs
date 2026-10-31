using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件协议的宿主侧接线:注册表(声明 → 注册 → 惰性激活 → 注销)与
/// <see cref="PluginProtocolFileService" />(宿主契约适配 + 异常翻译)。
/// <para>
/// 这一层守住的是"新增一种协议对文件浏览器/传输栈零改动"这条承诺:
/// 只要适配器把会话键与 DTO 翻译对了,上面那一整套就无感。
/// </para>
/// </summary>
[TestClass]
public sealed class PluginProtocolTests
{
    private const string PluginId = "acme.storage";
    private const string ProtocolId = "acme.storage";

    private static ProtocolDescriptor Descriptor(params ProtocolSettingField[] fields) =>
        new()
        {
            Id = ProtocolId,
            DisplayName = "Acme",
            DefaultPort = 8443,
            Features = ProtocolFeatures.AnonymousAccess,
            Fields = fields,
        };

    private static SessionProfile Profile(string? protocolId = ProtocolId) =>
        new()
        {
            ConnectionType = ConnectionType.Plugin,
            Host = "acme.example.com",
            Port = 8443,
            PluginProtocolId = protocolId,
        };

    /// <summary>清单声明的协议页签在**不装载程序集**的前提下就要出现,只是还没"就绪"。</summary>
    [TestMethod]
    public void Declare_MakesTheTabVisibleBeforeActivation()
    {
        var registry = new PluginProtocolRegistry();
        registry.Declare(PluginId, [new() { Id = ProtocolId, DisplayName = "Acme", DefaultPort = 8443 }]);

        PluginProtocolTab tab = registry.Tabs.Single();
        Assert.AreEqual(ProtocolId, tab.Id);
        Assert.AreEqual(8443, tab.DefaultPort);
        Assert.IsFalse(tab.IsReady, "插件还没激活,表单字段自然也还没到。");
        Assert.IsFalse(registry.TryGet(ProtocolId, out _));
    }

    /// <summary>激活后注册的实现覆盖掉声明,页签转为就绪。</summary>
    [TestMethod]
    public void Register_ReplacesTheDeclarationAndMarksItReady()
    {
        var registry = new PluginProtocolRegistry();
        registry.Declare(PluginId, [new() { Id = ProtocolId, DisplayName = "Acme", DefaultPort = 8443 }]);
        using var fileSystem = new FakeProtocolFileSystem();

        registry.Register(PluginId, Descriptor(), fileSystem);

        Assert.IsTrue(registry.Tabs.Single().IsReady);
        Assert.IsTrue(registry.TryGet(ProtocolId, out PluginProtocolRegistration registration));
        Assert.AreSame(fileSystem, registration.FileSystem);
    }

    /// <summary>只被声明过的协议在解析时触发惰性激活 —— 这正是"用户点到页签才装载插件"。</summary>
    [TestMethod]
    public async Task ResolveAsync_TriggersLazyActivation()
    {
        var registry = new PluginProtocolRegistry();
        registry.Declare(PluginId, [new() { Id = ProtocolId, DisplayName = "Acme", DefaultPort = 8443 }]);
        using var fileSystem = new FakeProtocolFileSystem();
        int activations = 0;
        registry.ActivationRequested = _ =>
        {
            activations++;
            registry.Register(PluginId, Descriptor(), fileSystem);
            return Task.FromResult(true);
        };

        PluginProtocolRegistration? resolved = await registry.ResolveAsync(ProtocolId);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(1, activations);
        // 已就绪后不再重复激活。
        await registry.ResolveAsync(ProtocolId);
        Assert.AreEqual(1, activations);
    }

    /// <summary>没被任何插件声明过的协议不该去"激活"什么,直接给不可用。</summary>
    [TestMethod]
    public async Task ResolveAsync_UnknownProtocol_DoesNotActivateAnything()
    {
        var registry = new PluginProtocolRegistry();
        bool asked = false;
        registry.ActivationRequested = _ => { asked = true; return Task.FromResult(true); };

        Assert.IsNull(await registry.ResolveAsync("nobody.knows"));
        Assert.IsFalse(asked);
    }

    /// <summary>停用/卸载插件要连声明一起撤,并通知文件服务收掉它名下的会话。</summary>
    [TestMethod]
    public void RemovePlugin_DropsDeclarationsAndRegistrations()
    {
        var registry = new PluginProtocolRegistry();
        registry.Declare(PluginId, [new() { Id = ProtocolId, DisplayName = "Acme", DefaultPort = 8443 }]);
        using var fileSystem = new FakeProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        List<string> unregistered = [];
        registry.Unregistered += unregistered.Add;

        registry.RemovePlugin(PluginId);

        Assert.IsEmpty(registry.Tabs);
        Assert.IsFalse(registry.TryGet(ProtocolId, out _));
        Assert.Contains(ProtocolId, unregistered);
    }

    /// <summary>
    /// 协议 id 会落进用户的会话配置,必须以插件 id 为前缀、且全小写,防插件间冒名与大小写歧义。
    /// **测的是真实的 <see cref="ProtocolsCapability" />**,不是测试里另写一份规则的替身 ——
    /// 后者只能证明"抄来的规则被遵守了"。
    /// </summary>
    [TestMethod]
    public void ProtocolsCapability_RejectsForeignAndMalformedProtocolIds()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        using var capability = new ProtocolsCapability(PluginId, registry, new CollectingLogger());

        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { Id = "someone.else" }, fileSystem), "别家的 id 不许冒名。");
        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { Id = PluginId + ".V2" }, fileSystem), "大写会在注册表与界面之间造成歧义。");
        // 自己的 id 与它的小写子协议都放行。
        capability.Register(Descriptor(), fileSystem);
        capability.Register(Descriptor() with { Id = PluginId + ".v2" }, fileSystem);
    }

    /// <summary>越界的默认端口在真机上是"协议页签点了就报错",要挡在注册这一步。</summary>
    [TestMethod]
    public void ProtocolsCapability_RejectsOutOfRangeDefaultPort()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        using var capability = new ProtocolsCapability(PluginId, registry, new CollectingLogger());

        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { DefaultPort = 0 }, fileSystem));
        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { DefaultPort = 70000 }, fileSystem));
    }

    /// <summary>
    /// 指纹要写回的字段必须真实存在,否则用户点了"信任该证书"等于没点 ——
    /// 这类错配只在真机上暴露,代价是对着同一个弹窗点到重试上限。
    /// </summary>
    [TestMethod]
    public void ProtocolsCapability_RejectsThumbprintKeyThatIsNotAField()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        using var capability = new ProtocolsCapability(PluginId, registry, new CollectingLogger());

        Assert.ThrowsExactly<ArgumentException>(() => capability.Register(
            Descriptor(new ProtocolSettingField { Key = "thumb", Label = "thumb" })
                with
            { TrustedThumbprintSettingKey = "typo" },
            fileSystem));
        // 指到真实字段就放行。
        capability.Register(
            Descriptor(new ProtocolSettingField { Key = "thumb", Label = "thumb" })
                with
            { TrustedThumbprintSettingKey = "thumb" },
            fileSystem);
    }

    /// <summary>
    /// 同 id 重注册按替换处理:换成**另一个**实现时要发注销(旧实现名下的会话得有人收尾),
    /// 换成同一个实例(插件为刷新文案而重注册)则不能发 —— 那会把用户正开着的标签页全掐掉。
    /// </summary>
    [TestMethod]
    public void Register_ReplacementNotifiesOnlyWhenTheImplementationActuallyChanged()
    {
        var registry = new PluginProtocolRegistry();
        using var first = new FakeProtocolFileSystem();
        using var second = new FakeProtocolFileSystem();
        List<string> unregistered = [];
        IDisposable handle = registry.Register(PluginId, Descriptor(), first);
        registry.Unregistered += unregistered.Add;

        // 同一实例重注册(切语言的场景):不发注销。
        registry.Register(PluginId, Descriptor(), first);
        Assert.IsEmpty(unregistered);

        // 换成另一个实现:发一次注销,让文件服务收掉旧实现名下的会话。
        registry.Register(PluginId, Descriptor(), second);
        Assert.AreSequenceEqual([ProtocolId], unregistered);

        // 旧句柄此刻已不是表里那一份,释放它是空操作 —— 不能把新注册撤掉。
        handle.Dispose();
        Assert.IsTrue(registry.TryGet(ProtocolId, out PluginProtocolRegistration current));
        Assert.AreSame(second, current.FileSystem);
    }

    /// <summary>会话建立后,宿主契约上的调用要按会话键路由到插件实现,DTO 逐字段翻译。</summary>
    [TestMethod]
    public async Task FileService_RoutesHostCallsToThePlugin()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);

        Guid sessionId = await service.OpenSessionAsync(Profile());

        Assert.IsTrue(service.OwnsSession(sessionId));
        Assert.AreEqual("acme.example.com", fileSystem.LastRequest!.Host);
        List<RemoteFileInfo> entries = await service.ListDirectoryAsync(sessionId, "/data");
        Assert.AreEqual("readme.txt", entries.Single().Name);
        Assert.AreEqual("/data/readme.txt", entries.Single().FullPath);
        Assert.AreEqual(42, entries.Single().Size);

        await service.CloseSessionAsync(sessionId);
        Assert.IsFalse(service.OwnsSession(sessionId));
    }

    /// <summary>
    /// 声明了 NoCredentials 的协议没有用户名口令可交:配置里残留的那一份(两栏收起之前填的、旧版本存下的)
    /// 用户看不见,不交给插件;没声明的协议照交。
    /// </summary>
    [TestMethod]
    public async Task FileService_NoCredentialsProtocol_NeverForwardsStoredUsernameOrPassword()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        ProtocolDescriptor descriptor = Descriptor();
        descriptor = new()
        {
            Id = descriptor.Id,
            DisplayName = descriptor.DisplayName,
            DefaultPort = descriptor.DefaultPort,
            Features = ProtocolFeatures.NoCredentials,
            Fields = descriptor.Fields,
        };
        registry.Register(PluginId, descriptor, fileSystem);
        var service = new PluginProtocolFileService(registry);
        SessionProfile profile = Profile();
        profile.Username = "admin";
        profile.Password = "hidden-pw";

        Guid sessionId = await service.OpenSessionAsync(profile);

        Assert.AreEqual(string.Empty, fileSystem.LastRequest!.Username);
        Assert.AreEqual(string.Empty, fileSystem.LastRequest.Password);
        await service.CloseSessionAsync(sessionId);
    }

    [TestMethod]
    public async Task FileService_CredentialProtocol_StillForwardsUsernameAndPassword()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        SessionProfile profile = Profile();
        profile.Username = "admin";
        profile.Password = "pw";

        Guid sessionId = await service.OpenSessionAsync(profile);

        Assert.AreEqual("admin", fileSystem.LastRequest!.Username);
        Assert.AreEqual("pw", fileSystem.LastRequest.Password);
        await service.CloseSessionAsync(sessionId);
    }

    /// <summary>
    /// 协议不可用要与"连接失败"分开:前者提示去装插件,后者才是重试。
    /// 配置本身完好无损 —— 卸载一个插件绝不该毁掉用户的连接配置。
    /// </summary>
    [TestMethod]
    public async Task FileService_UnavailableProtocol_ReportsItAsSuch()
    {
        var service = new PluginProtocolFileService(new());

        PluginProtocolUnavailableException failure =
            await Assert.ThrowsExactlyAsync<PluginProtocolUnavailableException>(
                () => service.OpenSessionAsync(Profile()));

        Assert.AreEqual(ProtocolId, failure.ProtocolId);
    }

    /// <summary>SDK 异常族要翻成 Core 中立异常族 —— 插件 SDK 的类型不越过 Infrastructure 边界。</summary>
    [TestMethod]
    public async Task FileService_TranslatesSdkExceptions()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem
        {
            ConnectFailure = new ProtocolCertificateTrustException(
                "untrusted", "CN=minio", "CN=minio", DateTimeOffset.UnixEpoch, "AABB", "RemoteCertificateNameMismatch"),
        };
        registry.Register(PluginId, Descriptor(new ProtocolSettingField { Key = "thumb", Label = "thumb", IsHidden = true })
            with
        { TrustedThumbprintSettingKey = "thumb" }, fileSystem);
        var service = new PluginProtocolFileService(registry);

        PluginProtocolCertificateException failure =
            await Assert.ThrowsExactlyAsync<PluginProtocolCertificateException>(
                () => service.OpenSessionAsync(Profile()));

        Assert.AreEqual("AABB", failure.Thumbprint);
        // 指纹要写回协议自己声明的那个隐藏字段,否则"信任该证书"点了等于没点。
        Assert.AreEqual("thumb", failure.SettingKey);
    }

    /// <summary>字段默认值要先铺一层:老配置是在插件加字段之前存的,少了那个键不能变成空串。</summary>
    [TestMethod]
    public async Task FileService_FillsDeclaredDefaultsForOlderProfiles()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        registry.Register(PluginId, Descriptor(
            new ProtocolSettingField { Key = "region", Label = "Region", DefaultValue = "us-east-1" },
            new ProtocolSettingField { Key = "token", Label = "Token", IsSecret = true }), fileSystem);
        var service = new PluginProtocolFileService(registry);

        SessionProfile profile = Profile();
        profile.PluginSecrets = new(StringComparer.Ordinal) { ["token"] = "s3cr3t" };
        await service.OpenSessionAsync(profile);

        Assert.AreEqual("us-east-1", fileSystem.LastRequest!.GetString("region"));
        Assert.AreEqual("s3cr3t", fileSystem.LastRequest.GetString("token"));
    }

    /// <summary>插件兼实现了从流上传:宿主契约上的调用按会话键原样转过去,进度经桥换成宿主的进度。</summary>
    [TestMethod]
    public async Task FileService_UploadStream_RoutesToAPluginThatSupportsIt()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new StreamingProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        Guid sessionId = await service.OpenSessionAsync(Profile());
        var reports = new List<TransferProgress>();

        await service.UploadStreamAsync(sessionId, new MemoryStream([1, 2, 3, 4]), "/bucket/a.bin", 4,
            progress: new SynchronousProgress(reports.Add));

        Assert.AreEqual("/bucket/a.bin", fileSystem.LastPath);
        Assert.AreEqual(4L, fileSystem.LastLength);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4 }, fileSystem.Received);
        Assert.IsNotEmpty(reports, "插件报的进度要经桥交到宿主。");
    }

    /// <summary>插件没实现这一面:如实报不支持,而不是假装成功。</summary>
    [TestMethod]
    public async Task FileService_UploadStream_WithoutPluginSupport_IsNotSupported()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        Guid sessionId = await service.OpenSessionAsync(Profile());

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => service.UploadStreamAsync(sessionId, new MemoryStream([1]), "/bucket/a.bin", 1));
    }

    /// <summary>
    /// SDK 的这一面不续传,就核实不了目标上那个较短的同名文件是不是上一次的半截:按「对不上」报,
    /// 交回同名冲突策略。悄悄整份重传会不经询问覆盖一个可能与此无关的文件。
    /// </summary>
    [TestMethod]
    public async Task FileService_UploadStream_AskedToResume_ReportsItCannotVerify()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new StreamingProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        Guid sessionId = await service.OpenSessionAsync(Profile());

        await Assert.ThrowsExactlyAsync<Core.Ssh.VelaSftpResumeMismatchException>(
            () => service.UploadStreamAsync(sessionId, new MemoryStream(new byte[10]), "/bucket/a.bin", 10, resumeOffset: 4));
        Assert.IsNull(fileSystem.LastPath, "核实不了就一个字节都不写。");
    }

    /// <summary>
    /// 协议条目上的链接字段(SDK 2.1.3)原样进宿主模型:文件面板据此画链接、删除与递归只处理链接本身。
    /// 非链接的条目不带目标文本,哪怕插件填了。
    /// </summary>
    [TestMethod]
    public async Task FileService_CarriesSymbolicLinkFieldsIntoTheHostModel()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new FakeProtocolFileSystem
        {
            Entries =
            [
                new("logs", "/data/logs", IsDirectory: true, 0, DateTimeOffset.UnixEpoch, "", "", "") { IsSymbolicLink = true, LinkTarget = "../var/log" },
                new("plain", "/data/plain", IsDirectory: false, 1, DateTimeOffset.UnixEpoch, "", "", "") { LinkTarget = "ignored" }
            ]
        };
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        Guid sessionId = await service.OpenSessionAsync(Profile());

        List<RemoteFileInfo> entries = await service.ListDirectoryAsync(sessionId, "/data");

        Assert.IsTrue(entries[0].IsSymbolicLink);
        Assert.IsTrue(entries[0].IsDirectory, "指向目录的链接照常能点进去");
        Assert.AreEqual("../var/log", entries[0].LinkTarget);
        Assert.IsFalse(entries[1].IsSymbolicLink);
        Assert.IsNull(entries[1].LinkTarget);
    }

    /// <summary>
    /// 协议兼实现了改修改时间(<see cref="IProtocolLastWriteTime" />):目录同步上传后对齐时间的调用按会话键转过去,
    /// UTC 原样成为绝对时刻;没实现、或这条会话做不到(插件抛 ProtocolUnsupportedException)都如实报不支持。
    /// </summary>
    [TestMethod]
    public async Task FileService_SetLastWriteTime_RoutesToAPluginThatSupportsIt()
    {
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new CapableProtocolFileSystem();
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        Guid sessionId = await service.OpenSessionAsync(Profile());
        var when = new DateTime(2026, 10, 25, 8, 30, 15, DateTimeKind.Utc);

        await service.SetLastWriteTimeAsync(sessionId, "/bucket/a.bin", when);

        Assert.AreEqual("/bucket/a.bin", fileSystem.TimedPath);
        Assert.AreEqual(new DateTimeOffset(when), fileSystem.TimedAt);
        Assert.AreEqual(TimeSpan.Zero, fileSystem.TimedAt!.Value.Offset);

        fileSystem.Unsupported = true;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => service.SetLastWriteTimeAsync(sessionId, "/bucket/a.bin", when));

        var plain = new PluginProtocolRegistry();
        using var basic = new FakeProtocolFileSystem();
        plain.Register(PluginId, Descriptor(), basic);
        var basicService = new PluginProtocolFileService(plain);
        Guid basicSession = await basicService.OpenSessionAsync(Profile());
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => basicService.SetLastWriteTimeAsync(basicSession, "/x", when));
    }

    /// <summary>
    /// 协议兼实现了服务器端摘要(<see cref="IProtocolChecksum" />):按 SHA-256 请求,结果不全信 ——
    /// 请求里没有的键丢掉,缺的键补 null,不是 64 位小写十六进制的值当「算不出」(否则同步逐字比会把相同内容判成不同)。
    /// 没实现、或这条会话做不到,都如实报不支持,由同步回退到大小与时间。
    /// </summary>
    [TestMethod]
    public async Task FileService_ComputeSha256_NormalizesWhatThePluginReports()
    {
        string good = new('a', 64);
        var registry = new PluginProtocolRegistry();
        using var fileSystem = new CapableProtocolFileSystem
        {
            Digests = new Dictionary<string, string?>
            {
                ["/a"] = good,
                ["/upper"] = good.ToUpperInvariant(),
                ["/short"] = "abc",
                ["/unrequested"] = good
            }
        };
        registry.Register(PluginId, Descriptor(), fileSystem);
        var service = new PluginProtocolFileService(registry);
        Guid sessionId = await service.OpenSessionAsync(Profile());

        IReadOnlyDictionary<string, string?> result = await service.ComputeSha256Async(sessionId, ["/a", "/upper", "/short", "/missing"]);

        Assert.AreEqual(RemoteChecksumAlgorithm.Sha256, fileSystem.RequestedAlgorithm);
        Assert.HasCount(4, result);
        Assert.AreEqual(good, result["/a"]);
        Assert.IsNull(result["/upper"]);
        Assert.IsNull(result["/short"]);
        Assert.IsNull(result["/missing"]);
        Assert.IsFalse(result.ContainsKey("/unrequested"));
        Assert.IsEmpty(await service.ComputeSha256Async(sessionId, []));

        fileSystem.Unsupported = true;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => service.ComputeSha256Async(sessionId, ["/a"]));

        var plain = new PluginProtocolRegistry();
        using var basic = new FakeProtocolFileSystem();
        plain.Register(PluginId, Descriptor(), basic);
        var basicService = new PluginProtocolFileService(plain);
        Guid basicSession = await basicService.OpenSessionAsync(Profile());
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => basicService.ComputeSha256Async(basicSession, ["/a"]));
    }

    /// <summary>兼实现了改修改时间与服务器端摘要的协议替身;<see cref="Unsupported" /> 模拟「这条会话做不到」。</summary>
    private sealed class CapableProtocolFileSystem : FakeProtocolFileSystemBase, IProtocolLastWriteTime, IProtocolChecksum
    {
        public bool Unsupported { get; set; }

        public string? TimedPath { get; private set; }

        public DateTimeOffset? TimedAt { get; private set; }

        public IReadOnlyDictionary<string, string?> Digests { get; init; } = new Dictionary<string, string?>();

        public RemoteChecksumAlgorithm? RequestedAlgorithm { get; private set; }

        public Task SetLastWriteTimeAsync(string sessionId, string path, DateTimeOffset lastWriteTime, CancellationToken cancellationToken = default)
        {
            if (Unsupported) return Task.FromException(new ProtocolUnsupportedException("no MFMT"));
            TimedPath = path;
            TimedAt = lastWriteTime;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string?>> ComputeChecksumsAsync(string sessionId, RemoteChecksumAlgorithm algorithm,
            IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            if (Unsupported) return Task.FromException<IReadOnlyDictionary<string, string?>>(new ProtocolUnsupportedException("no HASH"));
            RequestedAlgorithm = algorithm;
            return Task.FromResult(Digests);
        }
    }

    /// <summary>同步回调的进度接收器(<see cref="Progress{T}" /> 会把回调投递到别的线程)。</summary>
    private sealed class SynchronousProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    /// <summary>兼实现了从流上传的协议替身:读完流并报一次进度。</summary>
    private sealed class StreamingProtocolFileSystem : FakeProtocolFileSystemBase, IProtocolStreamUpload
    {
        public string? LastPath { get; private set; }

        public long LastLength { get; private set; }

        public byte[] Received { get; private set; } = [];

        public async Task UploadStreamAsync(string sessionId, Stream source, string path, long length,
            IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, cancellationToken);
            Received = copy.ToArray();
            LastPath = path;
            LastLength = length;
            progress?.Report(new(Received.Length, length));
        }
    }

    /// <summary>只记录调用的协议实现替身。</summary>
    private sealed class FakeProtocolFileSystem : FakeProtocolFileSystemBase;

    /// <summary>只记录调用的协议实现替身的共同部分。</summary>
    private abstract class FakeProtocolFileSystemBase : IProtocolFileSystem, IDisposable
    {
        public ProtocolConnectRequest? LastRequest { get; private set; }

        public Exception? ConnectFailure { get; init; }

        /// <summary>列目录返回的条目;缺省是一个普通文件 <c>readme.txt</c>。</summary>
        public IReadOnlyList<RemoteFileEntry>? Entries { get; init; }

        public event EventHandler<ProtocolSessionStateChange>? SessionStateChanged;

        public Task ConnectAsync(string sessionId, ProtocolConnectRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ConnectFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public Task DisconnectAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GetHomePathAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult("/");

        public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries ?? [new("readme.txt", path + "/readme.txt", IsDirectory: false, 42, DateTimeOffset.UnixEpoch, "", "", "")]);

        public Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<RemoteFileEntry?>(null);

        public Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<Stream> OpenReadAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stream.Null);

        public Task UploadFileAsync(string sessionId, string localPath, string remotePath, IProgress<RemoteTransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DownloadFileAsync(string sessionId, string remotePath, string localPath, IProgress<RemoteTransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string sessionId, string path, IProgress<ProtocolDeleteProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateFileAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CopyAsync(string sessionId, string sourcePath, string destinationPath, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetPermissionsAsync(string sessionId, string path, short octalMode, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvokeActionAsync(string sessionId, string actionId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>替身可主动上报一次状态,用于驱动宿主的会话状态转发。</summary>
        public void Raise(string sessionId, ProtocolSessionState state) =>
            SessionStateChanged?.Invoke(this, new(sessionId, state));

        public void Dispose() => SessionStateChanged = null;
    }
}
