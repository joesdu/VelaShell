using System.Reflection;
using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Import;

/// <summary>
/// 导出文件直接序列化 <see cref="SessionProfile" />,新字段会自动进文件 —— 这条用例逼着加字段的人回答一句:
/// 它是普通配置、机密,还是本机状态?
/// </summary>
/// <remarks>
/// 自动导出省掉了「加字段要记得同步导出」这一处,但也意味着一个新的机密字段会悄悄以明文写进导出文件。
/// 所以这里手写一份完整清单,任何不在清单里的属性都直接红:归了类再放行。
/// </remarks>
[TestClass]
[TestCategory("SessionArchive")]
public sealed class SessionArchiveFieldTests
{
    /// <summary>照原样写进文件的配置。</summary>
    private static readonly HashSet<string> Exported =
    [
        nameof(SessionProfile.ConnectionType), nameof(SessionProfile.Id), nameof(SessionProfile.Name),
        nameof(SessionProfile.Host), nameof(SessionProfile.Port), nameof(SessionProfile.Username),
        nameof(SessionProfile.AuthMethod), nameof(SessionProfile.RememberPassword), nameof(SessionProfile.PrivateKeyPath),
        nameof(SessionProfile.CertificatePath), nameof(SessionProfile.CredentialSource), nameof(SessionProfile.GroupId),
        nameof(SessionProfile.IsPinned), nameof(SessionProfile.Tags), nameof(SessionProfile.Notes),
        nameof(SessionProfile.JumpHostProfileId), nameof(SessionProfile.PostAuthCommand),
        nameof(SessionProfile.PostAuthCommandDelaySeconds), nameof(SessionProfile.Ftp), nameof(SessionProfile.PluginProtocolId),
        nameof(SessionProfile.PluginSettings), nameof(SessionProfile.Terminal), nameof(SessionProfile.Ssh),
        nameof(SessionProfile.AutoStartTunnelIds)
    ];

    /// <summary>机密:不导出,或只进加密段。</summary>
    private static readonly HashSet<string> Secret =
    [
        nameof(SessionProfile.Password), nameof(SessionProfile.PrivateKeyPassphrase), nameof(SessionProfile.PluginSecrets)
    ];

    /// <summary>本机状态:搬到别的机器上没有意义。</summary>
    private static readonly HashSet<string> LocalOnly = [nameof(SessionProfile.LastConnectedAt)];

    [TestMethod]
    public void EverySessionProfileProperty_IsClassified()
    {
        List<string> unclassified =
        [
            .. typeof(SessionProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static p => p.CanWrite)
                .Select(static p => p.Name)
                .Where(name => !Exported.Contains(name) && !Secret.Contains(name) && !LocalOnly.Contains(name))
        ];

        Assert.IsEmpty(unclassified,
            "SessionProfile 新增了属性,而导出文件是直接序列化它的。请在 SessionArchiveFieldTests 里把它归到 Exported / Secret / LocalOnly,"
            + "是机密的还要在 SessionArchiveJson.StripForExport 与 SecretsOf 里处理:\n  " + string.Join("\n  ", unclassified));
    }

    [TestMethod]
    public void StripForExport_RemovesSecretsAndLocalState_AndKeepsTheRest()
    {
        SessionProfile source = FullyPopulated();

        SessionProfile stripped = SessionArchiveJson.StripForExport(source);

        foreach (string name in Secret.Concat(LocalOnly))
        {
            Assert.IsNull(typeof(SessionProfile).GetProperty(name)!.GetValue(stripped), $"{name} 不该出现在导出里");
        }
        Assert.AreEqual(source.Name, stripped.Name);
        Assert.AreEqual(source.PrivateKeyPath, stripped.PrivateKeyPath);
        Assert.AreEqual("pw", source.Password, "剥离的是副本,原对象不能被改。");
    }

    [TestMethod]
    public void EverySharedCredentialProperty_IsClassified()
    {
        HashSet<string> exported =
        [
            nameof(SharedCredential.Id), nameof(SharedCredential.Name), nameof(SharedCredential.Username),
            nameof(SharedCredential.AuthMethod), nameof(SharedCredential.PrivateKeyPath), nameof(SharedCredential.CertificatePath),
            nameof(SharedCredential.Notes)
        ];
        HashSet<string> secret = [nameof(SharedCredential.Password), nameof(SharedCredential.PrivateKeyPassphrase)];

        List<string> unclassified =
        [
            .. typeof(SharedCredential).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static p => p.CanWrite)
                .Select(static p => p.Name)
                .Where(name => !exported.Contains(name) && !secret.Contains(name))
        ];

        Assert.IsEmpty(unclassified, "SharedCredential 新增了属性,请确认它是不是机密并在导出里处理:\n  " + string.Join("\n  ", unclassified));
    }

    internal static SessionProfile FullyPopulated() =>
        new()
        {
            ConnectionType = ConnectionType.SSH,
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
            GroupId = Guid.NewGuid(),
            IsPinned = true,
            LastConnectedAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Tags = ["prod", "bastion"],
            Notes = "跳板机\n值班:运维二组",
            JumpHostProfileId = Guid.NewGuid(),
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
            PluginSettings = new() { ["region"] = "cn-north-1" },
            PluginSecrets = new() { ["secretKey"] = "s3cr3t" },
            Terminal = new() { Encoding = "GBK", KeepAliveSeconds = 15 },
            Ssh = new() { Compression = true, AgentForwarding = true },
            AutoStartTunnelIds = [Guid.NewGuid()]
        };
}
