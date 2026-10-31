using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 系统密钥库认证(<see cref="AuthMethod.KeyStore" />):Windows 的 CNG 密钥库里一把导不出来的钥,宿主列得出来、配置里只存引用、
/// 连接时经 SSH 库的 CNG 签名器签名登得上。
/// </summary>
/// <remarks>
/// 用例在当前用户的软件密钥库里现建一把<b>不可导出</b>的 ECDSA P-256 测试钥(名字带随机后缀),用完删掉;
/// 证书存储那一条再拿它签一张自签证书放进「个人」存储,用完同样删掉。非 Windows 记 Inconclusive。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
[DoNotParallelize]
public sealed class SystemKeyStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------ 引用的写法(各平台)

    [TestMethod]
    public void 引用的文本写法往返()
    {
        SystemKeyReference user = new() { Provider = SystemKeyReference.SoftwareProvider, KeyName = "deploy/2026" };
        SystemKeyReference machine = new() { Provider = SystemKeyReference.PlatformProvider, KeyName = "tpm-key", MachineKey = true };

        Assert.AreEqual("cng:Microsoft Software Key Storage Provider/deploy/2026", user.ToText());
        Assert.IsTrue(SystemKeyReference.TryParse(user.ToText(), out SystemKeyReference? readUser));
        Assert.AreEqual(user, readUser, "钥名里的 / 不影响:按第一个 / 分开");
        Assert.IsTrue(SystemKeyReference.TryParse(machine.ToText(), out SystemKeyReference? readMachine));
        Assert.IsTrue(readMachine.MachineKey);
        Assert.IsFalse(SystemKeyReference.TryParse(@"C:\keys\id_ed25519", out _));
        Assert.IsFalse(SystemKeyReference.TryParse("cng:no-slash", out _));
        Assert.IsFalse(SystemKeyReference.TryParse("cng:/name", out _));
    }

    [TestMethod]
    public void 只有引用就算带着认证材料()
    {
        Assert.IsTrue(CredentialMaterial.HasInline(new SessionProfile
        {
            AuthMethod = AuthMethod.KeyStore,
            KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.SoftwareProvider, KeyName = "k" },
        }));
        Assert.IsFalse(CredentialMaterial.HasInline(new SessionProfile { AuthMethod = AuthMethod.KeyStore, PrivateKeyPath = "/k" }),
            "系统密钥库看的是钥的引用,不是私钥文件");
    }

    // ------------------------------------------------------------ Windows CNG

    /// <summary>软件密钥库里现建的不可导出的钥:列得出来,公钥与钥本身的一致。</summary>
    [TestMethod]
    public async Task 列得出软件密钥库里的钥()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using var key = TestKey.Create();

        IReadOnlyList<SystemKeyInfo> keys = await new SystemKeyStore().ListKeysAsync(TestContext.CancellationToken);

        string name = key.Name;
        SystemKeyInfo found = keys.Single(k => k.Reference.KeyName == name);
        Assert.AreEqual(SystemKeyReference.SoftwareProvider, found.Reference.Provider);
        Assert.IsFalse(found.Reference.MachineKey);
        Assert.AreEqual("ecdsa-sha2-nistp256", found.KeyType);
        Assert.AreEqual(256, found.KeyBits);
        StringAssert.StartsWith(found.Reference.PublicKey, "ecdsa-sha2-nistp256 ");
        Assert.AreEqual(key.PublicKeyBlob, Convert.ToBase64String(VelaShell.Ssh.HostKeys.SshPublicKey.Parse(found.Reference.PublicKey!).Blob.Span));
    }

    /// <summary>
    /// 端到端:配置里只有引用,连接时经 CNG 签名器签名,环回上的测试服务端验过签名、放行。
    /// 钥是不可导出的 —— 能登上就说明签名是密钥库做的,而不是把私钥读进了本进程。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 用密钥库里的钥登得上()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using var key = TestKey.Create();
        await using LoopbackSshHost host = new()
        {
            AuthPolicy = new TestAuthPolicy
            {
                RequiredMethods = ["publickey"],
                AcceptedPublicKeys = [Convert.FromBase64String(key.PublicKeyBlob)],
            },
        };

        await using VelaSshClientWrapper ssh = await ConnectAsync(KeyStoreInfo(host.Port, key.Name));

        Assert.IsTrue(ssh.IsConnected);
    }

    /// <summary>
    /// 配了证书:出示的是 CA 给这把钥签的用户证书(服务端只认证书 blob,不认那把裸公钥),签名照旧由密钥库做。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 配了证书时出示证书登录()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using var key = TestKey.Create();
        string certificatePath = Path.Combine(Path.GetTempPath(), $"vela-keystore-{Guid.NewGuid():N}-cert.pub");
        (string line, byte[] blob) = key.UserCertificate("joe");
        await File.WriteAllTextAsync(certificatePath, line + "\n", TestContext.CancellationToken);
        try
        {
            await using LoopbackSshHost host = new()
            {
                AuthPolicy = new TestAuthPolicy
                {
                    RequiredMethods = ["publickey"],
                    // 只认证书:出示的若是裸公钥就登不上 —— 能登上就说明证书配上了。
                    AcceptedPublicKeys = [blob],
                },
            };

            await using VelaSshClientWrapper ssh = await ConnectAsync(KeyStoreInfo(host.Port, key.Name, certificatePath));

            Assert.IsTrue(ssh.IsConnected);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    /// <summary>证书签的不是这把钥:建连之前就如实说「证书与钥不是一对」,不去连服务器(而不是换来一句笼统的 publickey 被拒)。</summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 证书与密钥库里的钥不是一对时如实说()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using var key = TestKey.Create();
        using var other = TestKey.Create();
        string certificatePath = Path.Combine(Path.GetTempPath(), $"vela-keystore-{Guid.NewGuid():N}-cert.pub");
        await File.WriteAllTextAsync(certificatePath, other.UserCertificate("joe").Line + "\n", TestContext.CancellationToken);
        try
        {
            await using LoopbackSshHost host = new();
            string name = key.Name;

            VelaSshAuthenticationException ex = await Assert.ThrowsAsync<VelaSshAuthenticationException>(
                async () => await ConnectAsync(KeyStoreInfo(host.Port, name, certificatePath)));

            Assert.AreEqual(VelaShell.Ssh.Keys.SshKeyProblemKind.CertificateKeyMismatch,
                Assert.IsInstanceOfType<VelaShell.Ssh.Keys.SshCertificateException>(ex.InnerException).ProblemKind);
            Assert.AreEqual(0, host.Accepted, "证书配错了就不去连服务器");
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    /// <summary>
    /// 没人看着的探测(会话健康巡检)以「不许弹框」打开密钥库里的钥(<see cref="CngKeyOpenOptions.Silent" />):
    /// 用不着问人的钥照样登得上;要 PIN / 确认的钥打开或签名时直接失败,而不是一台一个系统框。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 巡检以不弹框的方式打开密钥库里的钥_用不着问人的照样登得上()
    {
        SystemKeyReference reference = new() { Provider = SystemKeyReference.SoftwareProvider, KeyName = "k", MachineKey = true };
        Assert.AreEqual(CngKeyOpenOptions.MachineKey | CngKeyOpenOptions.Silent, SshConnectionAssembler.CngOpenOptions(reference, silent: true));
        Assert.AreEqual(CngKeyOpenOptions.MachineKey, SshConnectionAssembler.CngOpenOptions(reference, silent: false), "有人看着时照旧,PIN 框由 Windows 在签名那一刻问");
        if (!WindowsOrSkip())
        {
            return;
        }
        using var key = TestKey.Create();
        await using LoopbackSshHost host = new()
        {
            AuthPolicy = new TestAuthPolicy
            {
                RequiredMethods = ["publickey"],
                AcceptedPublicKeys = [Convert.FromBase64String(key.PublicKeyBlob)],
            },
        };

        ConnectionInfo info = KeyStoreInfo(host.Port, key.Name, unattended: true);
        await using VelaSshClientWrapper ssh = await ConnectAsync(info);

        Assert.IsTrue(ssh.IsConnected);
    }

    /// <summary>引用的钥在这台电脑上不在(别的电脑上建的、卡没插):如实说「打不开系统密钥库里的这把钥」,不去连服务器。</summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task 钥不在时如实说打不开()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        await using LoopbackSshHost host = new();

        VelaSshAuthenticationException ex = await Assert.ThrowsAsync<VelaSshAuthenticationException>(
            async () => await ConnectAsync(KeyStoreInfo(host.Port, "vela-no-such-key-" + Guid.NewGuid().ToString("N"))));

        Assert.Contains(Strings.Get("SshErr_KeyStoreUnavailable"), ex.Message);
        Assert.AreEqual(0, host.Accepted, "钥打不开就不去连服务器");
    }

    /// <summary>证书存储里有证书用这把钥时,列表带上证书主题(读证书记着的密钥位置,不去取私钥 —— 智能卡证书取私钥会弹框)。</summary>
    [TestMethod]
    public async Task 证书存储里的证书带上主题()
    {
        if (!WindowsOrSkip())
        {
            return;
        }
        using var key = TestKey.Create();
        string subject = "VelaShell key store test " + Guid.NewGuid().ToString("N")[..8];
        using X509Certificate2 certificate = key.SelfSignedCertificate(subject);
        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        try
        {
            IReadOnlyList<SystemKeyInfo> keys = await new SystemKeyStore().ListKeysAsync(TestContext.CancellationToken);

            string name = key.Name;
            Assert.AreEqual(subject, keys.Single(k => k.Reference.KeyName == name).CertificateSubject);
        }
        finally
        {
            store.Remove(certificate);
        }
    }

    // ------------------------------------------------------------ 辅助

    private static ConnectionInfo KeyStoreInfo(int port, string keyName, string? certificatePath = null, bool unattended = false) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "joe",
        AuthMethod = AuthMethod.KeyStore,
        KeyStoreKey = new SystemKeyReference { Provider = SystemKeyReference.SoftwareProvider, KeyName = keyName },
        CertificatePath = certificatePath,
        IsUnattended = unattended,
    };

    private static async Task<VelaSshClientWrapper> ConnectAsync(ConnectionInfo info)
    {
        SshConnectionAssembler.Assembled assembled = SshConnectionAssembler.Create(
            info, hostKey: null, settings: null, prompt: null, alerts: null, proxyResolver: null);
        VelaSshClientWrapper wrapper = new(assembled.Connect, assembled.ConnectTimeout, info.Ssh);
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

    /// <summary>不是 Windows 就记 Inconclusive(全绿的报告里不能混着一行断言都没跑的用例)。</summary>
    [SupportedOSPlatformGuard("windows")]
    private static bool WindowsOrSkip()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("[SKIP] 系统密钥库(CNG)只有 Windows 有。");
        }
        return OperatingSystem.IsWindows();
    }

    /// <summary>当前用户软件密钥库里一把不可导出的 ECDSA P-256 测试钥;释放时删掉。</summary>
    [SupportedOSPlatform("windows")]
    private sealed class TestKey : IDisposable
    {
        private readonly CngKey _key;

        private TestKey(CngKey key, string name)
        {
            _key = key;
            Name = name;
            using ECDsaCng ecdsa = new(key);
            ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            PublicKeyBlob = Convert.ToBase64String(EcdsaBlob(parameters));
        }

        public string Name { get; }

        /// <summary>SSH 公钥 blob(base64)。</summary>
        public string PublicKeyBlob { get; }

        public static TestKey Create()
        {
            string name = "vela-keystore-test-" + Guid.NewGuid().ToString("N");
            var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            });
            return new TestKey(key, name);
        }

        public X509Certificate2 SelfSignedCertificate(string subject)
        {
            using ECDsaCng ecdsa = new(_key);
            CertificateRequest request = new($"CN={subject}", ecdsa, HashAlgorithmName.SHA256);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        }

        /// <summary>
        /// 拿一把现建的 CA 钥(ECDSA P-256,只在内存里)给这把钥签一张用户证书(OpenSSH PROTOCOL.certkeys 的
        /// <c>ecdsa-sha2-nistp256-cert-v01@openssh.com</c>),有效期前后各一小时。
        /// </summary>
        /// <returns><c>-cert.pub</c> 那一行,与证书 blob(测试服务端按它认人)。</returns>
        public (string Line, byte[] Blob) UserCertificate(string principal)
        {
            using ECDsaCng subject = new(_key);
            ECParameters parameters = subject.ExportParameters(includePrivateParameters: false);
            using var ca = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using MemoryStream blob = new();
            WriteString(blob, "ecdsa-sha2-nistp256-cert-v01@openssh.com"u8);
            WriteString(blob, RandomNumberGenerator.GetBytes(32));                       // nonce
            WriteString(blob, "nistp256"u8);
            WriteString(blob, [0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);
            WriteUInt64(blob, 1);                                                         // serial
            WriteUInt32(blob, 1);                                                         // 1 = 用户证书
            WriteString(blob, "vela-keystore-test"u8);                                    // key id
            using (MemoryStream principals = new())
            {
                WriteString(principals, System.Text.Encoding.UTF8.GetBytes(principal));
                WriteString(blob, principals.ToArray());
            }
            WriteUInt64(blob, (ulong)(now - 3600));                                       // valid after
            WriteUInt64(blob, (ulong)(now + 3600));                                       // valid before
            WriteString(blob, []);                                                        // critical options
            WriteString(blob, []);                                                        // extensions
            WriteString(blob, []);                                                        // reserved
            WriteString(blob, EcdsaBlob(ca.ExportParameters(includePrivateParameters: false)));

            // CA 的签名覆盖前面的全部字段(RFC 5656 §3.1.2 的签名格式:类型串 + (mpint r, mpint s))。
            byte[] signed = blob.ToArray();
            byte[] rs = ca.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            using MemoryStream signature = new();
            WriteString(signature, "ecdsa-sha2-nistp256"u8);
            using (MemoryStream pair = new())
            {
                WriteMpint(pair, rs.AsSpan(0, 32));
                WriteMpint(pair, rs.AsSpan(32));
                WriteString(signature, pair.ToArray());
            }
            WriteString(blob, signature.ToArray());

            byte[] certificate = blob.ToArray();
            return ($"ecdsa-sha2-nistp256-cert-v01@openssh.com {Convert.ToBase64String(certificate)} vela-keystore-test", certificate);
        }

        /// <summary>RFC 5656 §3.1 的 ecdsa-sha2-nistp256 公钥 blob。</summary>
        private static byte[] EcdsaBlob(ECParameters parameters)
        {
            using MemoryStream blob = new();
            WriteString(blob, "ecdsa-sha2-nistp256"u8);
            WriteString(blob, "nistp256"u8);
            byte[] point = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
            WriteString(blob, point);
            return blob.ToArray();
        }

        private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
        {
            WriteUInt32(stream, (uint)value.Length);
            stream.Write(value);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteUInt64(Stream stream, ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        /// <summary>RFC 4251 §5 的 mpint(正数):去掉前导零,最高位是 1 时补一个 0。</summary>
        private static void WriteMpint(Stream stream, ReadOnlySpan<byte> magnitude)
        {
            magnitude = magnitude.TrimStart((byte)0);
            byte[] value = magnitude.Length > 0 && (magnitude[0] & 0x80) != 0 ? [0, .. magnitude] : magnitude.ToArray();
            WriteString(stream, value);
        }

        public void Dispose()
        {
            _key.Delete();
            _key.Dispose();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
