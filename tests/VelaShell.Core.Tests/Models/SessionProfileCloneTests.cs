using System.Collections;
using System.Reflection;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// <see cref="SessionProfile.Clone" /> 必须拷到每一个属性,而且不与源共享可变对象。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例的存在理由就是「加字段只改一处」这句话得有人守。此前全仓有三处逐字段手写拷贝
/// (仓储加密、会话树复制配置、连接工作流剥密码),加一个字段要三处同步补;
/// 漏一处的表现是"某个设置在复制配置之后莫名丢了",三处的症状还各不相同。
/// </para>
/// <para>
/// 手写一份"期望字段清单"没有意义 —— 那份清单本身也会忘记更新。这里改用<b>反射逐属性比对</b>:
/// 新加的属性自动进入检查范围,忘了在 <c>Clone</c> 里带上它就直接红。
/// </para>
/// </remarks>
[TestClass]
public sealed class SessionProfileCloneTests
{
    /// <summary>造一条每个字段都不是默认值的配置 —— 默认值相等分不出"拷了"和"没拷"。</summary>
    private static SessionProfile FullyPopulated() =>
        new()
        {
            ConnectionType = ConnectionType.FTP,
            Id = Guid.NewGuid(),
            Name = "prod-bastion",
            Host = "10.0.0.9",
            Port = 2222,
            Username = "ops",
            AuthMethod = AuthMethod.PrivateKey,
            Password = "pw",
            RememberPassword = false,
            PrivateKeyPath = @"C:\keys\id_ed25519",
            PrivateKeyPassphrase = "phrase",
            CertificatePath = @"C:\keys\id_ed25519-cert.pub",
            KeyStoreKey = new SystemKeyReference
            {
                Provider = SystemKeyReference.PlatformProvider,
                KeyName = "vela-tpm-key",
                MachineKey = true,
                PublicKey = "ecdsa-sha2-nistp256 AAAA"
            },
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid()),
            GroupId = Guid.NewGuid(),
            IsPinned = true,
            LastConnectedAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Tags = ["prod", "bastion"],
            Notes = "跳板机\n值班:运维二组",
            JumpHostProfileId = Guid.NewGuid(),
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
            Ftp = new()
            {
                EncryptionMode = FtpEncryptionMode.Implicit,
                DataConnectionMode = FtpDataConnectionMode.Active,
                Anonymous = true,
                TrustedCertificateThumbprint = "AB:CD",
                MaxConnections = 2,
                InitialRemotePath = "/srv",
                ServerUtcOffsetMinutes = 480
            },
            PluginProtocolId = "velashell.s3",
            PluginSettings = new() { ["region"] = "cn-north-1" },
            PluginSecrets = new() { ["secretKey"] = "s3cr3t" },
            Terminal = new()
            {
                Encoding = "GBK",
                TerminalType = "vt220",
                TabColor = "#E05252",
                TabIcon = "database",
                StartupDirectory = "/var/log",
                KeepAliveSeconds = 15,
                AntiIdleSeconds = 120
            },
            Ssh = new()
            {
                Compression = true,
                TerminalCompression = SshTerminalCompressionMode.Bidirectional,
                ObscureKeystrokeTiming = true,
                AgentForwarding = true,
                AgentForwardKeys = ["SHA256:abc"],
                AgentForwardConfirm = true,
                AgentEndpoint = @"\\.\pipe\vela-clone-agent",
                X11Forwarding = true,
                X11Display = "localhost:10.0",
                X11Trusted = false,
                LegacyAlgorithms = true,
                KexAlgorithms = "curve25519-sha256",
                HostKeyAlgorithms = "ssh-ed25519",
                Ciphers = "aes256-gcm@openssh.com",
                Macs = "hmac-sha2-256"
            },
            AutoStartTunnelIds = [Guid.NewGuid(), Guid.NewGuid()],
            DirectoryBookmarks =
            [
                new()
                {
                    Name = "release",
                    LocalPath = @"C:\\work\\site",
                    RemotePath = "/srv/site"
                }
            ]
        };

    /// <summary>
    /// 守卫的守卫:<see cref="FullyPopulated" /> 必须把<b>每一个</b>可写属性(连同 <c>Terminal</c> / <c>Ftp</c> / <c>Ssh</c> /
    /// 目录收藏里那一层)都设成非默认值。
    /// </summary>
    /// <remarks>
    /// 下面那条反射比对只能发现「源与副本不一样」;一个字段在样本里留着默认值,源与副本就都是默认值,
    /// <c>Clone</c> 漏抄它也照样全绿 —— 这条用例补上之前就是这样:<c>IsPinned</c> 留着 false、<c>Ssh</c> 整个没设,
    /// 两处都从没被真正比过。新加字段(比如 <c>TerminalOverrides.TabIcon</c>)忘了在样本里填,这里先红,
    /// 并点名是哪一个。
    /// </remarks>
    [TestMethod]
    public void FullyPopulatedSample_LeavesNoPropertyAtItsDefault()
    {
        SessionProfile sample = FullyPopulated();
        List<string> defaults = [];
        CollectDefaults(sample, new SessionProfile(), nameof(SessionProfile), defaults);
        CollectDefaults(sample.Terminal!, new TerminalOverrides(), nameof(SessionProfile.Terminal), defaults);
        CollectDefaults(sample.Ftp!, new FtpSettings(), nameof(SessionProfile.Ftp), defaults);
        CollectDefaults(sample.Ssh!, new SshSessionOptions(), nameof(SessionProfile.Ssh), defaults);
        CollectDefaults(sample.DirectoryBookmarks[0], new RemoteDirectoryBookmark(), nameof(SessionProfile.DirectoryBookmarks), defaults);

        Assert.IsEmpty(
            defaults,
            "FullyPopulated 里这些属性还是默认值 —— Clone 漏抄它们也查不出来,请在样本里给它们一个非默认值:"
            + Environment.NewLine + string.Join(Environment.NewLine, defaults));
    }

    /// <summary>逐个可写属性与一个新建实例比:相等就记下(Guid 一类构造时随机生成的,新建实例本就不同,自然不会被记)。</summary>
    private static void CollectDefaults(object sample, object fresh, string owner, List<string> defaults)
    {
        foreach (PropertyInfo property in sample.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }
            object? value = property.GetValue(sample);
            object? baseline = property.GetValue(fresh);
            bool emptyCollection = value is IEnumerable items and not string && !items.Cast<object>().Any();
            if (value is null || emptyCollection || ValueEquals(value, baseline))
            {
                defaults.Add($"  {owner}.{property.Name} = {Describe(value)}");
            }
        }
    }

    [TestMethod]
    public void CloneCopiesEveryProperty()
    {
        SessionProfile source = FullyPopulated();
        SessionProfile copy = source.Clone();

        List<string> missed = [];
        foreach (PropertyInfo property in typeof(SessionProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead)
            {
                continue;
            }
            object? a = property.GetValue(source);
            object? b = property.GetValue(copy);
            if (!ValueEquals(a, b))
            {
                missed.Add($"  {property.Name}:源 = {Describe(a)},副本 = {Describe(b)}");
            }
        }

        Assert.IsEmpty(
            missed,
            "以下属性没有被 SessionProfile.Clone 拷过去 —— 复制配置、保存到数据库、"
            + "剥密码这几条路径都会悄悄丢掉它:" + Environment.NewLine + string.Join(Environment.NewLine, missed));
    }

    /// <summary>
    /// 每一个可变对象都必须是独立的一份。
    /// </summary>
    /// <remarks>
    /// 浅拷贝的表现比漏拷更隐蔽:副本改一下,源跟着变 —— 而"源"往往是仓储缓存里那一条,
    /// 于是界面上没保存的编辑凭空生效了。
    /// </remarks>
    [TestMethod]
    public void CloneSharesNoMutableObjectWithTheSource()
    {
        SessionProfile source = FullyPopulated();
        SessionProfile copy = source.Clone();

        Assert.AreNotSame(source.Tags, copy.Tags);
        Assert.AreNotSame(source.Ftp, copy.Ftp);
        Assert.AreNotSame(source.PluginSettings, copy.PluginSettings);
        Assert.AreNotSame(source.PluginSecrets, copy.PluginSecrets);
        Assert.AreNotSame(source.Terminal, copy.Terminal);
        Assert.AreNotSame(source.Ssh, copy.Ssh);
        Assert.AreNotSame(source.Ssh!.AgentForwardKeys, copy.Ssh!.AgentForwardKeys);
        Assert.AreNotSame(source.AutoStartTunnelIds, copy.AutoStartTunnelIds);
        Assert.AreNotSame(source.DirectoryBookmarks, copy.DirectoryBookmarks);
        Assert.AreNotSame(source.DirectoryBookmarks[0], copy.DirectoryBookmarks[0]);

        copy.Tags.Add("touched");
        copy.PluginSettings!["region"] = "changed";
        copy.Terminal!.Encoding = "Big5";
        copy.AutoStartTunnelIds!.Clear();
        copy.DirectoryBookmarks[0].Name = "changed";

        Assert.HasCount(2, source.Tags);
        Assert.AreEqual("cn-north-1", source.PluginSettings!["region"]);
        Assert.AreEqual("GBK", source.Terminal!.Encoding);
        Assert.HasCount(2, source.AutoStartTunnelIds!);
        Assert.AreEqual("release", source.DirectoryBookmarks[0].Name);
    }

    [TestMethod]
    public void CloningAProfileWithoutOverridesKeepsThemNull()
    {
        // 没有覆盖项时落盘的就该是一个 null,而不是一个全空对象 ——
        // 后者会让"有没有覆盖"这件事有了两种表示,也给每条老配置的 JSON 平白多一段。
        SessionProfile bare = new() { Name = "plain" };

        SessionProfile copy = bare.Clone();

        Assert.IsNull(copy.Terminal);
        Assert.IsNull(copy.AutoStartTunnelIds);
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        // 集合按元素比:List/Dictionary 的 Equals 是引用相等,直接比会把每个集合属性都报成"没拷"。
        if (a is IEnumerable left and not string && b is IEnumerable right)
        {
            object[] leftItems = [.. left.Cast<object>()];
            object[] rightItems = [.. right.Cast<object>()];
            return leftItems.Length == rightItems.Length
                && leftItems.Zip(rightItems).All(pair => ValueEquals(pair.First, pair.Second));
        }
        // FtpSettings / TerminalOverrides / SshSessionOptions 是普通类(引用相等),逐属性往下比;
        // 下一层里的列表(SshSessionOptions.AgentForwardKeys)照样按元素比。
        // record(CredentialReference)自带值相等,不必下探。
        if (a.GetType() is { IsClass: true } type && type != typeof(string) && !IsRecord(type))
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                       .All(p => ValueEquals(p.GetValue(a), p.GetValue(b)));
        }
        return a.Equals(b);
    }

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;

    private static string Describe(object? value) =>
        value switch
        {
            null => "<null>",
            string s => $"\"{s}\"",
            IEnumerable items => $"[{string.Join(", ", items.Cast<object>())}]",
            _ => value.ToString() ?? "<?>"
        };
}
