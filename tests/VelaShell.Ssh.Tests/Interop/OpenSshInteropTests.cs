// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/design/architecture.md §10.3（互操作矩阵）
//
// ⚠️ 这一组**要连一台真的 sshd**，所以标了 [TestCategory("Interop")]，
//    不进 PR 门禁。没有配好环境时它们会**跳过**而不是失败 ——
//    一个在本机永远红着的测试，很快就会被所有人忽略。
//
// 本机跑法（需要 docker）：
//   pwsh scripts/ssh/interop/Start-TestServer.ps1
//   dotnet test --filter "TestCategory=Interop"
//   pwsh scripts/ssh/interop/Stop-TestServer.ps1
//
// 环境变量：
//   VELASHELL_SSH_INTEROP_HOST      默认 127.0.0.1
//   VELASHELL_SSH_INTEROP_PORT      默认 2229(与 Start-TestServer.ps1 一致;2222–2228 是 docker-compose.test.yml 的靶机)
//   VELASHELL_SSH_INTEROP_USER      默认 velashell
//   VELASHELL_SSH_INTEROP_PASSWORD  默认 velashell
//   VELASHELL_SSH_INTEROP_KEY       可选：私钥文件路径

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Interop;

[TestClass]
[TestCategory("Interop")]
// ⚠️ **不并行。** 13 条用例同时连一台真的 sshd 会撞上 OpenSSH 的 MaxStartups
// （默认 10 个未认证并发连接），多出来的会在**发出版本标识串之前**就被丢掉 ——
// 症状是「对端在发出版本标识串之前关闭了连接」，看上去像我们的 bug。
[DoNotParallelize]
public sealed partial class OpenSshInteropTests
{
    /// <summary>MSTest 注入;带协作取消的 <c>[Timeout]</c> 到点时取消它的 <see cref="TestContext.CancellationToken" />。</summary>
    public TestContext TestContext { get; set; } = null!;

    private static string Host =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_HOST") ?? "127.0.0.1";

    private static int Port =>
        int.TryParse(
            Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_PORT"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            ? port
            : 2229;

    private static string User =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_USER") ?? "velashell";

    private static string Password =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_PASSWORD") ?? "velashell";

    private static string? KeyPath =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY");

    /// <summary>环境没配好就跳过，而不是失败。</summary>
    private static void RequireServer()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP") != "1")
        {
            Assert.Inconclusive(
                "互操作用例需要一台真的 sshd。设 VELASHELL_SSH_INTEROP=1 并起好服务端再跑" +
                "（见 scripts/ssh/interop/Start-TestServer.ps1）。");
        }
    }

    /// <summary>
    /// <c>VELASHELL_SSH_INTEROP_KEY_ONLY=1</c> 时默认凭据改用 <see cref="KeyPath"/> 的私钥，而不是口令 ——
    /// 本机没有 Docker、在 WSL 里起一台非 root 的 sshd 时用：非 root 的 sshd 只能让它自己那个用户登录，而且验不了口令。
    /// </summary>
    private static readonly Lazy<InMemorySshSigner?> KeyOnlySigner = new(() =>
        Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_ONLY") == "1" && KeyPath is { } path
            ? SshPrivateKeyFile.Parse(File.ReadAllText(path), passphrase: null, path)
            : null);

    private static IReadOnlyList<SshCredential> DefaultCredentials() =>
        KeyOnlySigner.Value is { } signer
            ? [new PublicKeyCredential(signer, KeyPath)]
            : [new PasswordCredential(Password)];

    private static SshConnectionOptions Options(
        SshAlgorithmSet? algorithms = null, IReadOnlyList<SshCredential>? credentials = null) =>
        new(User, Host, Port)
        {
            // 互操作测试里服务端每次重建，主机密钥每次都变 —— 这里不是在测 TOFU。
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = credentials ?? DefaultCredentials(),
            Algorithms = algorithms ?? SshAlgorithmSet.Default,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

    // ------------------------------------------------------------ 主线

    [TestMethod]
    public async Task 能连上真实的OpenSSH并跑命令()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        SshCommandResult result = await connection.RunAsync("echo 你好 && uname -s");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains("你好", result.StandardOutput);
        Assert.IsNotNull(connection.HostKey);
    }

    [TestMethod]
    public async Task 每一种密钥交换算法都能与OpenSSH握手()
    {
        RequireServer();

        List<string> unsupported = [];
        List<string> broken = [];

        foreach (string kex in SshAlgorithmSet.Default.KeyExchange)
        {
            if (SshAlgorithmNegotiator.IsIndicator(kex))
            {
                continue;
            }

            SshAlgorithmSet only = SshAlgorithmSet.Default with { KeyExchange = [kex] };

            try
            {
                await using SshConnection connection = await SshConnection.ConnectAsync(Options(only));
                SshCommandResult r = await connection.RunAsync("true");
                Assert.AreEqual(0, r.ExitCode, kex);
                Assert.AreEqual(kex, connection.Algorithms.KeyExchange, "谈成的不是要求的那一个");
            }
            catch (SshNegotiationException ex)
            {
                // 对端不支持某个算法是**正常的**（比如老 OpenSSH 没有后量子混合）—— 只有协商不成才算这一类。
                unsupported.Add($"{kex}：{ex.Message}");
            }
            catch (SshException ex)
            {
                // 谈成了却失败（验签失败、MAC 错、共享密钥算错）是我们坏了，不是对端不支持。
                // 曾经这里一概 catch (SshException)，再只要求「至少一个能通」—— sntrup761、ecdh-nistp384/521、
                // DH 坏了照样绿。
                broken.Add($"{kex}：{ex.Reason} {ex.Message}");
            }
        }

        Assert.IsEmpty(broken,
            "这些密钥交换谈成了却握不了手：" + Environment.NewLine + string.Join(Environment.NewLine, broken));

        // curve25519 与 ECDH 是 OpenSSH 各版本默认都开着的，必须能通：它们「不支持」只能是我们的清单或协商出了错。
        // 有限域 DH 不在其列 —— OpenSSH 10.0 的 sshd 默认已经不开它了。
        string[] required =
        [
            SshAlgorithmNames.Curve25519Sha256, SshAlgorithmNames.EcdhSha2Nistp256, SshAlgorithmNames.EcdhSha2Nistp384,
            SshAlgorithmNames.EcdhSha2Nistp521,
        ];
        Assert.DoesNotContain(u => required.Any(r => u.StartsWith(r + "：", StringComparison.Ordinal)), unsupported,
            "OpenSSH 默认就支持的密钥交换谈不成：" + Environment.NewLine + string.Join(Environment.NewLine, unsupported));

        Console.WriteLine(unsupported.Count == 0
            ? "全部密钥交换算法都通过了。"
            : "对端不支持这些（可能是正常的）：" + Environment.NewLine + string.Join(Environment.NewLine, unsupported));
    }

    [TestMethod]
    public async Task 每一种加密算法都能与OpenSSH收发()
    {
        RequireServer();

        List<string> unsupported = [];

        foreach (string cipher in SshAlgorithmSet.Default.EncryptionClientToServer)
        {
            SshAlgorithmSet only = SshAlgorithmSet.Default with
            {
                EncryptionClientToServer = [cipher],
                EncryptionServerToClient = [cipher],
            };

            try
            {
                await using SshConnection connection = await SshConnection.ConnectAsync(Options(only));
                SshCommandResult r = await connection.RunAsync("echo ok");
                Assert.AreEqual("ok\n", r.StandardOutput, cipher);
            }
            catch (SshNegotiationException ex)
            {
                unsupported.Add($"{cipher}：{ex.Message}");
            }
        }

        // 谈成了却收发失败的直接让用例失败（异常不接）；协商不成的只报出来。
        // 默认清单里的加密算法 OpenSSH 6.5 起全都支持，一个都不该谈不成。
        Assert.IsEmpty(unsupported,
            "这些加密算法与对端谈不成：" + Environment.NewLine + string.Join(Environment.NewLine, unsupported));
    }

    /// <summary>
    /// 每一种 MAC 都与真实的 OpenSSH 对一遍（固定用 aes256-ctr，这样 MAC 才真的被协商出来）。
    /// 曾经加密矩阵只变密码，CTR 下总是谈成 hmac-sha2-256-etm：sha2-512-etm、整条 MtE 路径（hmac-sha2-256/512）、
    /// hmac-sha1(-etm) 都只与本库自己的另一半对过 —— 两边错得一样时往返测试测不出来。
    /// </summary>
    [TestMethod]
    public async Task 每一种MAC都能与OpenSSH收发()
    {
        RequireServer();

        List<string> failed = [];
        foreach (string mac in SshAlgorithmSet.Default.WithLegacyInterop().MacClientToServer)
        {
            SshAlgorithmSet only = SshAlgorithmSet.Default with
            {
                EncryptionClientToServer = [SshAlgorithmNames.Aes256Ctr],
                EncryptionServerToClient = [SshAlgorithmNames.Aes256Ctr],
                MacClientToServer = [mac],
                MacServerToClient = [mac],
            };

            try
            {
                await using SshConnection connection = await SshConnection.ConnectAsync(Options(only));
                Assert.AreEqual(mac, connection.Algorithms.MacClientToServer, "谈成的不是要求的那个 MAC");

                // 两个方向各走一大段（几百个报文），不止一两个报文。
                SshCommandResult bulk = await connection.RunAsync("head -c 262144 /dev/zero | tr '\\0' 'm'");
                Assert.AreEqual(256 * 1024, bulk.StandardOutput.Length, mac);

                await using SshCommand echo = await connection.ExecuteAsync("wc -c");
                await echo.StandardInput.WriteAsync(new byte[200 * 1024]);
                await echo.CompleteStandardInputAsync();
                (_, string counted, _) = await echo.ReadToEndAsync();
                Assert.AreEqual("204800", counted.Trim(), mac);
            }
            catch (SshException ex)
            {
                failed.Add($"{mac}：{ex.Reason} {ex.Message}");
            }
        }

        Assert.IsEmpty(failed,
            "这些 MAC 与 OpenSSH 收发失败：" + Environment.NewLine + string.Join(Environment.NewLine, failed));
    }

    [TestMethod]
    public async Task 重协商之后每一种加密算法照常收发()
    {
        // 加密套件的状态在会话里复用（chacha20-poly1305 的两个 ChaCha 引擎与 Poly1305、
        // CTR 的计数器与 HMAC、GCM 的 nonce）。换钥之后新的一组必须从新钥重新起步：
        // 残留一点旧状态，第一个新钥报文就解不开 —— 而那只有对着另一份实现才看得出来，
        // 自己加密自己解密时两边错得一模一样。
        // 这里让一次重协商落在大块输出的**中途**：换钥前后的报文都在同一条通道里。
        RequireServer();

        List<string> failed = [];

        foreach (string cipher in SshAlgorithmSet.Default.EncryptionClientToServer)
        {
            SshAlgorithmSet only = SshAlgorithmSet.Default with
            {
                EncryptionClientToServer = [cipher],
                EncryptionServerToClient = [cipher],
            };

            try
            {
                await using SshConnection connection = await SshConnection.ConnectAsync(Options(only));

                Task<SshCommandResult> bulk = connection.RunAsync(
                    "head -c 8388608 /dev/zero | tr '\\0' 'a'").AsTask();
                await connection.StartRekeyAsync();

                using CancellationTokenSource rekeyTimeout = new(TimeSpan.FromSeconds(30));
                while (connection.RekeyCount == 0)
                {
                    await Task.Delay(20, rekeyTimeout.Token);
                }

                SshCommandResult output = await bulk;
                Assert.AreEqual(8 * 1024 * 1024, output.StandardOutput.Length, cipher);
                Assert.IsTrue(output.StandardOutput.All(c => c == 'a'), cipher);

                // 换钥之后两个方向都要再走一遍：上面那条命令的大部分报文可能在换钥之前就到了。
                await using SshCommand echo = await connection.ExecuteAsync("cat");
                await echo.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("换钥之后"));
                await echo.CompleteStandardInputAsync();
                (_, string echoed, _) = await echo.ReadToEndAsync();
                Assert.AreEqual("换钥之后", echoed, cipher);
            }
            catch (SshException ex)
            {
                failed.Add($"{cipher}：{ex.Message}");
            }
        }

        Assert.IsEmpty(failed,
            "这些加密算法在重协商之后收发失败：" + Environment.NewLine + string.Join(Environment.NewLine, failed));
    }

    /// <summary>
    /// 双向延迟压缩在服务端发起的重协商后继续收发数据。
    /// </summary>
    /// <remarks>
    /// 要服务端配了 <c>RekeyLimit</c>（自家镜像与 <c>Start-TestServer.ps1 -X11</c> 配的是 1M）；没配、一次重协商都没发生时
    /// 报 Inconclusive，而不是当成通过 —— 数据对上了也说明不了换钥这一段。
    /// </remarks>
    [TestMethod]
    public async Task 双向压缩时服务端发起的重协商照常收发()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options(SshAlgorithmSet.Default.WithCompression(allowServerCompression: true)));
        Assert.AreEqual(SshAlgorithmNames.ZlibOpenSsh, connection.Algorithms.CompressionClientToServer);
        Assert.AreEqual(SshAlgorithmNames.ZlibOpenSsh, connection.Algorithms.CompressionServerToClient);

        // 随机数据压不小：base64 之后 8 MiB，越过服务端的 RekeyLimit 好几次。
        SshCommandResult bulk = await connection.RunAsync("head -c 6291456 /dev/urandom | base64 -w0");
        Assert.AreEqual(0, bulk.ExitCode, bulk.StandardError);
        Assert.AreEqual(8 * 1024 * 1024, bulk.StandardOutput.Length);
        Assert.IsTrue(bulk.StandardOutput.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/'), "换钥前后的数据要一字节不差");

        // 反方向也走一大段：换钥之后数据要完整。
        byte[] upload = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(upload);
        await using (SshCommand count = await connection.ExecuteAsync("wc -c"))
        {
            await count.StandardInput.WriteAsync(upload);
            await count.CompleteStandardInputAsync();
            (_, string counted, _) = await count.ReadToEndAsync();
            Assert.AreEqual(upload.Length.ToString(CultureInfo.InvariantCulture), counted.Trim());
        }

        if (connection.RekeyCount == 0)
        {
            Assert.Inconclusive("服务端一次重协商都没发起 —— 它没配 RekeyLimit，换钥这一段没跑到。");
        }
        Assert.IsTrue(connection.IsAlive);
    }

    /// <summary>
    /// 每一种主机密钥算法各跑一遍服务端发起的重协商（velashell-docs/zh/ssh/spec/03 §8.4）：钉住首次的主机密钥，
    /// 重协商只提议与它同类型的算法。曾经对着真 sshd 只跑过默认谈成的 ed25519 ——
    /// RSA 的两种签名算法（同一把钥、两个算法名）与 ECDSA 走的是另外的收窄与验签路径。
    /// </summary>
    /// <remarks>
    /// 清单保留全部算法，只把要测的那个排到最前：服务端照客户端的顺序挑，重协商时的收窄才是从一份完整的清单里收。
    /// 服务端没有这种主机密钥（谈不成）时报 Inconclusive。
    /// </remarks>
    [TestMethod]
    [DataRow(SshAlgorithmNames.SshEd25519)]
    [DataRow(SshAlgorithmNames.EcdsaSha2Nistp256)]
    [DataRow(SshAlgorithmNames.RsaSha512)]
    [DataRow(SshAlgorithmNames.RsaSha256)]
    public async Task 每种主机密钥算法下服务端发起的重协商都照常收发(string hostKeyAlgorithm)
    {
        RequireServer();

        SshAlgorithmSet preferred = SshAlgorithmSet.Default with
        {
            HostKey = [hostKeyAlgorithm, .. SshAlgorithmSet.Default.HostKey.Where(a => a != hostKeyAlgorithm)],
        };

        SshConnection? connection = null;
        string? refusal = null;
        try
        {
            connection = await SshConnection.ConnectAsync(Options(preferred));
        }
        catch (SshNegotiationException ex)
        {
            refusal = ex.Message;
        }
        if (connection is null)
        {
            Assert.Inconclusive($"服务端没有能用 {hostKeyAlgorithm} 的主机密钥：{refusal}");
            return;
        }

        await using (connection)
        {
            if (connection.Algorithms.HostKey != hostKeyAlgorithm)
            {
                Assert.Inconclusive($"服务端没有 {hostKeyAlgorithm} 的主机密钥，谈成的是 {connection.Algorithms.HostKey}。");
            }

            // 3 MiB 随机数据，base64 之后 4 MiB，越过服务端的 RekeyLimit（1M）好几次。
            SshCommandResult bulk = await connection.RunAsync("head -c 3145728 /dev/urandom | base64 -w0");
            Assert.AreEqual(0, bulk.ExitCode, bulk.StandardError);
            Assert.AreEqual(4 * 1024 * 1024, bulk.StandardOutput.Length);
            Assert.IsTrue(bulk.StandardOutput.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/'), "换钥前后的数据要一字节不差");

            if (connection.RekeyCount == 0)
            {
                Assert.Inconclusive("服务端一次重协商都没发起 —— 它没配 RekeyLimit，换钥这一段没跑到。");
            }
            Assert.AreEqual(hostKeyAlgorithm, connection.Algorithms.HostKey, "重协商之后谈成的仍是同一个主机密钥算法");
            Assert.AreEqual(0, (await connection.RunAsync("true")).ExitCode, "重协商之后照常可用");
        }
    }

    [TestMethod]
    public async Task 公钥认证能与OpenSSH对上()
    {
        RequireServer();

        if (string.IsNullOrEmpty(KeyPath) || !File.Exists(KeyPath))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_KEY。");
        }

        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(KeyPath);

        await using SshConnection connection =
            await SshConnection.ConnectAsync(Options(credentials: [new PublicKeyCredential(signer, KeyPath)]));

        SshCommandResult result = await connection.RunAsync("id -un");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(User, result.StandardOutput);
    }

    /// <summary>
    /// **加密的** OpenSSH 私钥能连上真实 OpenSSH。
    /// </summary>
    /// <remarks>
    /// 这条验的是 <c>bcrypt_pbkdf</c> 那一整条链路，而它只有拿真 <c>ssh-keygen</c>
    /// 写出来的文件才验得了：加密侧要是也由我们写，两边会一起错，
    /// 自加密自解密照样通过，拿到别人的钥才静默失败。
    /// 单元测试那边用的是提交在仓库里的样本，这里用的是**刚刚现生成**的那把 ——
    /// 覆盖的是「这台机器上这一版 ssh-keygen 的默认产物」。
    /// </remarks>
    [TestMethod]
    public async Task 加密的私钥能与OpenSSH对上()
    {
        RequireServer();

        string? encrypted = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_ENCRYPTED");
        string? passphrase = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_PASSPHRASE");

        if (string.IsNullOrEmpty(encrypted) || !File.Exists(encrypted) || string.IsNullOrEmpty(passphrase))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_KEY_ENCRYPTED / _PASSPHRASE。");
        }

        ISshSigner signer = await SshPrivateKeyFile.LoadAsync(encrypted, passphrase);

        await using SshConnection connection =
            await SshConnection.ConnectAsync(Options(credentials: [new PublicKeyCredential(signer, encrypted)]));

        SshCommandResult result = await connection.RunAsync("id -un");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(User, result.StandardOutput);
    }

    /// <summary>口令错了要报口令错，而不是连到一半才失败。</summary>
    [TestMethod]
    public async Task 加密私钥口令错时在本地就报出来()
    {
        RequireServer();

        string? encrypted = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_KEY_ENCRYPTED");
        if (string.IsNullOrEmpty(encrypted) || !File.Exists(encrypted))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_KEY_ENCRYPTED。");
        }

        SshPrivateKeyException ex = await Assert.ThrowsExactlyAsync<SshPrivateKeyException>(
            async () => await SshPrivateKeyFile.LoadAsync(encrypted, "definitely-not-it"));

        Assert.IsTrue(ex.NeedsPassphrase);
    }

    /// <summary>
    /// 用 OpenSSH 证书认证连上真实 OpenSSH（服务端配了 <c>TrustedUserCAKeys</c>）。
    /// </summary>
    /// <remarks>
    /// 证书认证有一处不对称，只有真服务端才验得出来：认证请求里那个「公钥算法名」
    /// 字段带 <c>-cert-v01@openssh.com</c> 后缀，而签名 blob 里写的是**普通**算法名。
    /// 两处写反的症状都是一句 <c>Permission denied (publickey)</c> ——
    /// 与「CA 不被信任」「主体不匹配」长得一模一样。
    /// </remarks>
    [TestMethod]
    public async Task 证书认证能与OpenSSH对上()
    {
        RequireServer();

        string? certPath = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_CERT");
        if (string.IsNullOrEmpty(KeyPath) || !File.Exists(KeyPath)
            || string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_CERT（Start-TestServer.ps1 会生成）。");
        }

        ISshSigner key = await SshPrivateKeyFile.LoadAsync(KeyPath);
        OpenSshCertificate certificate = await OpenSshCertificate.LoadAsync(certPath);
        var signer = SshCertificateSigner.Create(certificate, key);

        Assert.AreEqual(SshCertificateType.User, certificate.CertificateType);
        Assert.IsTrue(
            certificate.IsTimeValid(DateTimeOffset.UtcNow),
            "证书应当还在有效期内 —— 过期了这条用例验的就不是证书认证本身了。");

        await using SshConnection connection =
            await SshConnection.ConnectAsync(Options(credentials: [new PublicKeyCredential(signer, certPath)]));

        SshCommandResult result = await connection.RunAsync("id -un");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(User, result.StandardOutput);
    }

    [TestMethod]
    public async Task 主机证书能按known_hosts的CA验过()
    {
        // 主机证书由真 ssh-keygen 签（Start-TestServer.ps1），sshd 用 HostCertificate 出示它。
        // 自己签自己验只能证明两边一致；验签范围、字段边界错了，只有对着别人签的证书才看得出来。
        RequireServer();

        string? hostCa = Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_HOST_CA");
        if (string.IsNullOrEmpty(hostCa) || !File.Exists(hostCa))
        {
            Assert.Inconclusive("没有配 VELASHELL_SSH_INTEROP_HOST_CA（Start-TestServer.ps1 会生成）。");
        }

        string pattern = Port == 22 ? Host : $"[{Host}]:{Port}";
        string knownHosts = Path.Combine(Path.GetTempPath(), $"vela-interop-kh-{Guid.NewGuid():N}");
        SshConnectionOptions WithKnownHosts() => Options() with
        {
            // 没有 CA 担保就拒绝 —— 这条用例要的是「真的按证书验过」，不是 TOFU。
            HostKeyPolicy = new KnownHostsPolicy(knownHosts) { UnknownHost = UnknownHostBehavior.Reject },
        };

        try
        {
            await File.WriteAllTextAsync(knownHosts, $"@cert-authority {pattern} {(await File.ReadAllTextAsync(hostCa)).Trim()}\n");

            await using (SshConnection connection = await SshConnection.ConnectAsync(WithKnownHosts()))
            {
                Assert.IsTrue(connection.HostKey?.IsCertificate, $"应当谈成主机证书，实际 {connection.HostKey?.KeyType}");
                Assert.AreEqual("velashell-interop-host", connection.HostKey?.Certificate?.KeyId);
                Assert.AreEqual(0, (await connection.RunAsync("true")).ExitCode);

                // 重协商钉住的是整张证书：只能再谈成证书算法，谈成普通算法就会被当成换了主机密钥。
                await connection.StartRekeyAsync();
                using CancellationTokenSource rekeyTimeout = new(TimeSpan.FromSeconds(30));
                while (connection.RekeyCount == 0)
                {
                    await Task.Delay(20, rekeyTimeout.Token);
                }
                Assert.AreEqual(0, (await connection.RunAsync("true")).ExitCode, "证书主机重协商之后照常可用");
            }

            // 换成一把不相干的 CA：这台主机由 CA 管，出示的证书却没人担保 —— 拒绝（按「变了」），不去问、不去记。
            using var stranger = InMemorySshSigner.GenerateEd25519();
            await File.WriteAllTextAsync(
                knownHosts,
                $"@cert-authority {pattern} {stranger.PublicKey.KeyType} {Convert.ToBase64String(stranger.PublicKey.Blob.Span)}\n");

            SshException ex = await Assert.ThrowsAsync<SshException>(async () =>
            {
                await using SshConnection connection = await SshConnection.ConnectAsync(WithKnownHosts());
            });
            // 没有 CA 担保：证书里那把钥按普通钥判，这台主机只记着 CA —— 与「变了」同样处理（velashell-docs/zh/ssh/spec/03 §5.5 第 4 条）。
            Assert.AreEqual(SshFailureReason.HostKeyChanged, ex.Reason, ex.Message);
        }
        finally
        {
            File.Delete(knownHosts);
        }
    }

    // ------------------------------------------------------------ 各层

    [TestMethod]
    public async Task 大量输出能完整收回来()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        // 4 MiB 的可预测内容 —— 窗口回补、分帧、背压全都要走一遍。
        SshCommandResult result = await connection.RunAsync(
            "head -c 4194304 /dev/zero | tr '\\0' 'a'");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(4 * 1024 * 1024, result.StandardOutput.Length);
        Assert.IsTrue(result.StandardOutput.All(c => c == 'a'));
    }

    [TestMethod]
    public async Task 标准输入能送到真实的远端()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SshCommand command = await connection.ExecuteAsync("cat");

        await command.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("喂进去的内容"));
        await command.CompleteStandardInputAsync();

        (SshExitStatus result, string stdout, _) = await command.ReadToEndAsync();

        Assert.AreEqual("喂进去的内容", stdout);
        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public async Task 退出码与信号都能如实拿到()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        SshCommandResult code = await connection.RunAsync("exit 42");
        Assert.AreEqual(42, code.ExitCode);
        Assert.IsNull(code.ExitStatus.ExitSignalName);

        SshCommandResult killed = await connection.RunAsync("kill -TERM $$");

        // 被信号杀死时**没有退出码** —— 不能凭空造一个 143 出来。
        Assert.IsNull(killed.ExitCode, "被信号杀死时不该有退出码");
        Assert.AreEqual("TERM", killed.ExitStatus.ExitSignalName);
    }

    [TestMethod]
    public async Task 伪终端能开起来()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SshShell shell = await connection.OpenShellAsync(new SshShellOptions
        {
            TerminalType = "xterm-256color",
            Size = new SshTerminalSize(120, 40, 960, 800),
        });

        await shell.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("tty; exit\n"));
        await shell.StandardInput.FlushAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        StringBuilder output = new();

        while (true)
        {
            System.IO.Pipelines.ReadResult read = await shell.StandardOutput.ReadAsync(timeout.Token);
            output.Append(Encoding.UTF8.GetString(read.Buffer.FirstSpan));
            shell.StandardOutput.AdvanceTo(read.Buffer.End);

            if (read.IsCompleted || output.ToString().Contains("/dev/pts", StringComparison.Ordinal))
            {
                break;
            }
        }

        // 有 pty 的话 tty 会报 /dev/pts/N；没有的话它报 "not a tty"。
        Assert.Contains("/dev/pts", output.ToString());
    }

    /// <summary>
    /// 输出读够了就告诉服务端（<c>eow@openssh.com</c>）：真 OpenSSH 关掉远端进程的输出端，<c>yes</c> 收到 SIGPIPE 结束 ——
    /// 不告诉的话它永远跑下去（本端丢弃、窗口照常回补），这条用例会超时。
    /// </summary>
    [TestMethod]
    public async Task 输出读够了告诉服务端远端进程就提前结束()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SshCommand command = await connection.ExecuteAsync("yes");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        System.IO.Pipelines.ReadResult read = await command.StandardOutput.ReadAtLeastAsync(4096, timeout.Token);
        Assert.StartsWith("y\ny\n", Encoding.UTF8.GetString(read.Buffer.ToArray()));
        command.StandardOutput.AdvanceTo(read.Buffer.End);

        Assert.IsTrue(await command.StopStandardOutputAsync(timeout.Token), "对端是 OpenSSH，请求要发出去");
        SshExitStatus exit = await command.WaitAsync(timeout.Token);
        Assert.IsTrue(exit.ExitSignalName == "PIPE" || exit.ExitCode == 141, $"yes 应当被 SIGPIPE 结束，实际：{exit}");
    }

    /// <summary>BREAK（RFC 4335）：真 OpenSSH 在伪终端上执行，回 SUCCESS。</summary>
    [TestMethod]
    public async Task 伪终端上的Break真OpenSSH会执行()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SshShell shell = await connection.OpenShellAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        Assert.IsTrue(await shell.SendBreakAsync(cancellationToken: timeout.Token), "OpenSSH 对有伪终端的会话执行 BREAK");
    }

    /// <summary>在伪终端里跑一条命令（<c>ssh -t host tty</c>）：命令看得到终端，跑完通道就关、退出码照常取。</summary>
    [TestMethod]
    public async Task 在伪终端里跑命令()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SshShell shell = await connection.OpenShellAsync(new SshShellOptions { Command = "tty; exit 7" });

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        StringBuilder output = new();
        while (true)
        {
            System.IO.Pipelines.ReadResult read = await shell.StandardOutput.ReadAsync(timeout.Token);
            output.Append(Encoding.UTF8.GetString(read.Buffer.ToArray()));
            shell.StandardOutput.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                break;
            }
        }

        Assert.StartsWith("/dev/pts/", output.ToString(), "exec 前发了 pty-req，命令就有终端");
        Assert.AreEqual(7, (await shell.WaitAsync(timeout.Token)).ExitCode);
    }

    /// <summary>statvfs@openssh.com 与远端 <c>stat -f</c> 对得上（块大小、总块数、文件名上限；空闲块数会变，不比）。</summary>
    [TestMethod]
    public async Task SFTP的文件系统用量与远端stat_f一致()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);
        Assert.IsTrue(sftp.Capabilities.HasStatVfs, "OpenSSH 的 sftp-server 宣告 statvfs@openssh.com");

        SftpFileSystemInfo info = await sftp.GetFileSystemInfoAsync("/");
        SshCommandResult stat = await connection.RunAsync("stat -f -c '%S %b %l' /");
        string[] fields = stat.StandardOutput.Trim().Split(' ');

        Assert.AreEqual(ulong.Parse(fields[0], CultureInfo.InvariantCulture), info.FragmentSize, $"stat -f：{stat.StandardOutput}");
        Assert.AreEqual(ulong.Parse(fields[1], CultureInfo.InvariantCulture), info.TotalBlocks);
        Assert.AreEqual(ulong.Parse(fields[2], CultureInfo.InvariantCulture), info.MaxNameLength);
        Assert.IsLessThanOrEqualTo(info.FreeBlocks, info.AvailableBlocks);
    }

    /// <summary>users-groups-by-id：真 OpenSSH 翻出来的名字与远端 id 命令一致，不认识的 id 为 null。</summary>
    [TestMethod]
    public async Task SFTP把uid与gid翻成名字与远端id一致()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);
        Assert.IsTrue(sftp.Capabilities.HasUsersGroupsById, "OpenSSH 的 sftp-server 宣告 users-groups-by-id@openssh.com");

        string[] me = (await connection.RunAsync("id -u; id -g; id -un; id -gn")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        uint uid = uint.Parse(me[0], CultureInfo.InvariantCulture);
        uint gid = uint.Parse(me[1], CultureInfo.InvariantCulture);

        SftpIdNames names = await sftp.LookupUserAndGroupNamesAsync([0, uid, 4_242_424], [0, gid, 4_242_424]);

        Assert.AreSequenceEqual(["root", me[2], null], [.. names.UserNames]);
        Assert.AreSequenceEqual(["root", me[3], null], [.. names.GroupNames]);
    }

    /// <summary>服务端内复制（copy-data）：真 OpenSSH 上复制出来的文件与源的 sha256 一致。</summary>
    [TestMethod]
    public async Task SFTP服务端内复制与源一致()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);
        Assert.IsTrue(sftp.Capabilities.HasCopyData, "OpenSSH 的 sftp-server 宣告 copy-data");

        string dir = $"/tmp/vela-copy-{Guid.NewGuid():N}";
        await connection.RunAsync($"mkdir {dir} && head -c 3000000 /dev/urandom > {dir}/src.bin");
        sftp.CopySegmentBytes = 1024 * 1024;   // 三段

        long last = 0;
        await sftp.CopyFileAsync($"{dir}/src.bin", $"{dir}/dst.bin", progress: new SyncProgress<long>(v => last = v));

        string sums = (await connection.RunAsync($"sha256sum {dir}/src.bin {dir}/dst.bin | cut -c1-64; rm -rf {dir}")).StandardOutput;
        string[] lines = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines);
        Assert.AreEqual(lines[0], lines[1]);
        Assert.AreEqual(3_000_000, last);
    }

    /// <summary>展开 ~：真 OpenSSH 宣告 expand-path@openssh.com，~ 是登录用户的 $HOME，~root 是 /root。</summary>
    [TestMethod]
    public async Task SFTP展开波浪号与远端的HOME一致()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);
        Assert.IsTrue(sftp.Capabilities.HasExpandPath, "OpenSSH 的 sftp-server 宣告 expand-path@openssh.com");

        string home = (await connection.RunAsync("printf %s \"$HOME\"")).StandardOutput;
        Assert.AreEqual(home, await sftp.ExpandPathAsync("~"));
        Assert.AreEqual("/root", await sftp.ExpandPathAsync("~root"));
    }

    /// <summary>
    /// 按句柄设时间（FSETSTAT，关闭之前）与不跟随链接设时间（lsetstat@openssh.com）：远端 stat 看到的
    /// 是设下去的时间，链接的目标不被改到。
    /// </summary>
    [TestMethod]
    public async Task SFTP按句柄与不跟随链接设时间与远端stat一致()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);
        Assert.IsTrue(sftp.Capabilities.HasLSetStat, "OpenSSH 的 sftp-server 宣告 lsetstat@openssh.com");

        string dir = $"/tmp/vela-times-{Guid.NewGuid():N}";
        await sftp.CreateDirectoryAsync(dir);
        DateTimeOffset fileTime = new(2023, 5, 6, 7, 8, 9, TimeSpan.Zero);
        DateTimeOffset linkTime = new(2021, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await using (SftpFileStream file = await sftp.OpenWriteAsync($"{dir}/f.txt"))
        {
            await file.WriteAsync("payload"u8.ToArray());
            await file.SetTimesAsync(fileTime, fileTime);
        }
        await sftp.CreateSymbolicLinkAsync($"{dir}/link", "f.txt");
        await sftp.SetLinkAttributesAsync($"{dir}/link", SftpFileAttributes.WithTimes(linkTime, linkTime));

        string stat = (await connection.RunAsync($"stat -c %Y {dir}/f.txt {dir}/link; rm -rf {dir}")).StandardOutput;
        string[] lines = stat.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(fileTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), lines[0], "目标的修改时间是按句柄设的那个，不被链接改到");
        Assert.AreEqual(linkTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), lines[1], "链接自身的修改时间");
    }

    /// <summary>
    /// 〔T11〕链接、改名与落盘对真 sftp-server —— 不只对本库的测试桩（桩与实现按同一个理解写，对不出理解错了的地方）：
    /// SYMLINK 的两个参数照 OpenSSH 的实际口径（链接建在该建的地方、远端 readlink 读回指向的那个）、
    /// hardlink@openssh.com 的链接与目标共用一个 inode、posix-rename@openssh.com 原子覆盖已有的目标、
    /// fsync@openssh.com 之后内容在。
    /// </summary>
    [TestMethod]
    public async Task SFTP的链接改名与落盘对真sftp_server()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);
        Assert.IsTrue(sftp.Capabilities.HasHardLink, "OpenSSH 的 sftp-server 宣告 hardlink@openssh.com");
        Assert.IsTrue(sftp.Capabilities.HasPosixRename, "OpenSSH 的 sftp-server 宣告 posix-rename@openssh.com");
        Assert.IsTrue(sftp.Capabilities.HasFsync, "OpenSSH 的 sftp-server 宣告 fsync@openssh.com");

        string dir = $"/tmp/vela-links-{Guid.NewGuid():N}";
        await sftp.CreateDirectoryAsync(dir);
        try
        {
            await sftp.WriteAllBytesAsync($"{dir}/a.txt", "alpha"u8.ToArray());
            await sftp.WriteAllBytesAsync($"{dir}/b.txt", "bravo"u8.ToArray());
            await sftp.WriteAllBytesAsync($"{dir}/c.txt", "charlie"u8.ToArray());

            await sftp.CreateSymbolicLinkAsync($"{dir}/link", "a.txt");
            Assert.AreEqual("a.txt", await sftp.ReadSymbolicLinkAsync($"{dir}/link"), "本库读回的指向");

            await sftp.CreateHardLinkAsync($"{dir}/hard", $"{dir}/a.txt");
            await sftp.RenameAsync($"{dir}/b.txt", $"{dir}/c.txt", overwrite: true);

            await using (SftpFileStream file = await sftp.OpenWriteAsync($"{dir}/d.txt"))
            {
                await file.WriteAsync("durable"u8.ToArray());
                await file.FsyncAsync();
            }

            string[] remote = (await connection.RunAsync(
                $"readlink {dir}/link; stat -c %i {dir}/a.txt {dir}/hard; cat {dir}/c.txt; echo; test -e {dir}/b.txt && echo b-still-there; cat {dir}/d.txt"))
                .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.AreEqual("a.txt", remote[0], "远端 readlink：链接建在 link、指向 a.txt（两个参数没有颠倒）");
            Assert.AreEqual(remote[1], remote[2], "硬链接与目标是同一个 inode");
            Assert.AreEqual("bravo", remote[3], "posix-rename 覆盖了已有的 c.txt");
            Assert.AreEqual("durable", remote[4], "b.txt 已经不在，fsync 之后 d.txt 的内容在");
        }
        finally
        {
            await connection.RunAsync($"rm -rf {dir}");
        }
    }

    /// <summary>〔Q8〕2038 年之后的时间：真 sftp-server 发、收的都是无符号的秒数，读写照样是 2040 年。</summary>
    [TestMethod]
    public async Task SFTP的时间按无符号对真sftp_server()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);

        string dir = $"/tmp/vela-time-{Guid.NewGuid():N}";
        await connection.RunAsync($"mkdir {dir} && touch -d \"2040-01-01 00:00:00 UTC\" {dir}/old && touch {dir}/new");
        try
        {
            Assert.AreEqual(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero), (await sftp.GetAttributesAsync($"{dir}/old")).LastWriteTime);

            DateTimeOffset in2050 = new(2050, 6, 1, 12, 0, 0, TimeSpan.Zero);
            await sftp.SetAttributesAsync($"{dir}/new", SftpFileAttributes.WithTimes(in2050, in2050));
            string seconds = (await connection.RunAsync($"stat -c %Y {dir}/new")).StandardOutput.Trim();
            Assert.AreEqual(in2050.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), seconds);
        }
        finally
        {
            await connection.RunAsync($"rm -rf {dir}");
        }
    }

    [TestMethod]
    public async Task SFTP能与真实的sftp_server对话()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);

        Assert.IsGreaterThan(0, sftp.WorkingDirectory.Length);
        Console.WriteLine($"工作目录：{sftp.WorkingDirectory}；能力：" +
            $"posix-rename={sftp.Capabilities.HasPosixRename}，" +
            $"limits={sftp.Capabilities.HasLimits}，块大小={sftp.BlockSize}");

        string path = $"{sftp.WorkingDirectory}/velashell-interop-{Guid.NewGuid():N}.bin";

        byte[] payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);

        try
        {
            await sftp.WriteAllBytesAsync(path, payload);
            byte[] back = await sftp.ReadAllBytesAsync(path);

            Assert.AreSequenceEqual(payload, back, "1 MiB 往返要一字节不差");

            SftpFileAttributes attributes = await sftp.GetAttributesAsync(path);
            Assert.AreEqual((ulong)payload.Length, attributes.Size);
            Assert.IsTrue(attributes.IsRegularFile);
        }
        finally
        {
            try
            {
                await sftp.DeleteFileAsync(path);
            }
            catch (SftpException)
            {
                // 清理失败不该让测试变红。
            }
        }
    }

    [TestMethod]
    public async Task 列真实目录能拿到符号链接()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);

        int total = 0;
        int links = 0;

        await foreach (SftpDirectoryEntry entry in sftp.EnumerateDirectoryAsync("/usr/lib"))
        {
            total++;
            if (entry.IsSymbolicLink)
            {
                links++;
                Assert.IsNotNull(entry.LinkTarget, $"{entry.Name} 是链接却没有目标");
            }

            if (total > 500)
            {
                break;
            }
        }

        Assert.IsGreaterThan(0, total, "/usr/lib 不该是空的");
        Console.WriteLine($"列了 {total} 项，其中 {links} 个符号链接。");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 延迟压缩按方向与OpenSSH互通(bool receive)
    {
        RequireServer();

        SshAlgorithmSet withCompression = SshAlgorithmSet.Default.WithCompression(receive);

        await using SshConnection connection = await SshConnection.ConnectAsync(Options(withCompression));

        Assert.AreEqual(SshAlgorithmNames.ZlibOpenSsh, connection.Algorithms.CompressionClientToServer);
        Assert.AreEqual(receive ? SshAlgorithmNames.ZlibOpenSsh : SshAlgorithmNames.None, connection.Algorithms.CompressionServerToClient);
        SshCommandResult result = await connection.RunAsync(
            "for i in $(seq 1 2000); do echo '同一行反复出现，非常可压缩'; done");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);

        // ⚠️ 按**行数**断言，别按 `.Length`。
        // `.Length` 是字符数，而这一行是 13 个中日韩字符 + 换行 = 14 个字符、
        // 40 个字节。拿「> 50000」去卡字符数就会莫名其妙地挂 ——
        // 第一版正是这么写的，而它在真实服务端上一跑就红了。
        Assert.HasCount(
            2000, result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            "行数要一行不差");
        Assert.AreEqual(
            2000 * 40, Encoding.UTF8.GetByteCount(result.StandardOutput),
            "字节数也要一字节不差");
    }

    [TestMethod]
    public async Task 本地转发能穿过真实服务端()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        // 让服务端连它自己的 sshd —— 对端会回一个 SSH 版本标识串，
        // 那是一个不需要在容器里额外装东西就能验证的信号。
        //
        // ⚠️ 端口要用**容器里 sshd 实际监听的那个**（linuxserver 镜像固定是 2222），
        // 不是 22，也**不是映射到本机的那个**（`VELASHELL_SSH_INTEROP_PORT`）——
        // 隧道的目标是从服务端视角解析的。两者只在默认端口下恰好相等；
        // 换一个映射端口（比如 2222 被占用时 -Port 2224）就会得到「Connection refused」，
        // 看上去像转发坏了。
        const int sshdPortInsideContainer = 2222;
        await using SshChannel tunnel = await connection.OpenTcpTunnelAsync("127.0.0.1", sshdPortInsideContainer);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        System.IO.Pipelines.ReadResult read = await tunnel.StandardOutput.ReadAsync(timeout.Token);
        string banner = Encoding.ASCII.GetString(read.Buffer.FirstSpan);
        tunnel.StandardOutput.AdvanceTo(read.Buffer.End);

        Assert.StartsWith("SSH-2.0-", banner, "隧道对面应当是那台 sshd");
    }

    /// <summary>
    /// 拿真实的 sshd 当跳板，经它的 direct-tcpip 再连它自己 —— 跑一条完整的 SSH-in-SSH。
    /// </summary>
    /// <remarks>
    /// 目标写 <c>127.0.0.1:2222</c>：从跳板的视角解析（velashell-docs/zh/ssh/spec/09 §5），指的是容器里的 sshd 自己。
    /// </remarks>
    [TestMethod]
    public async Task 经真实服务端做跳板连上目标()
    {
        RequireServer();

        SshConnectionOptions target = new(User, "127.0.0.1", 2222)
        {
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = DefaultCredentials(),
            Dialer = Ssh.Transport.DialerChain.Jump(Options()),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        await using SshConnection connection = await SshConnection.ConnectAsync(target);
        SshCommandResult result = await connection.RunAsync("echo 经跳板");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains("经跳板", result.StandardOutput);
    }

    /// <summary>
    /// 往一个<b>真实的 OpenSSH agent</b> 里加钥（velashell-docs/zh/ssh/spec/07 §7.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单元测试里的 <c>TestAgent</c> 与实现出自对规格的同一份理解 —— 两边一起错时照样全绿。
    /// 这里让真的 <c>ssh-agent</c> 来收：在服务端起一个只为这条用例服务的 agent，
    /// 经 <c>direct-streamlocal</c> 把本地客户端接到它的套接字上。
    /// </para>
    /// <para>
    /// 三重核对：agent 回 SUCCESS；远端 <c>ssh-add -l</c> 列出的指纹与我们的公钥一致；
    /// agent 用它签出来的东西能用原公钥验过（私钥字段错位时前两条照样成立）。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task 往真实的OpenSSH_agent里加钥()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        string socket = $"/tmp/vela-agent-{Guid.NewGuid():N}.sock";
        SshCommandResult started = await connection.RunAsync($"ssh-agent -s -a {socket}");
        Assert.AreEqual(0, started.ExitCode, started.StandardError);

        // 输出形如「SSH_AGENT_PID=123; export SSH_AGENT_PID;」。
        string pid = started.StandardOutput.Split("SSH_AGENT_PID=")[1].Split(';')[0];

        try
        {
            SshChannel tunnel = await connection.OpenUnixSocketTunnelAsync(socket);
            await using var agent = SshAgentClient.FromStream(tunnel.AsStream(), socket);

            using var rsa = RSA.Create(3072);
            using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            using var p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
            InMemorySshSigner[] keys =
            [
                InMemorySshSigner.GenerateEd25519(),
                InMemorySshSigner.FromRsa(rsa),
                InMemorySshSigner.FromEcdsa(p256),
                InMemorySshSigner.FromEcdsa(p384),
                InMemorySshSigner.FromEcdsa(p521),
            ];

            byte[] data = Encoding.UTF8.GetBytes("真实 agent 签的数据");
            for (int i = 0; i < keys.Length; i++)
            {
                // 最后一把带有效期约束，走 25。
                SshAgentKeyConstraints? constraints =
                    i == keys.Length - 1 ? new SshAgentKeyConstraints { Lifetime = TimeSpan.FromMinutes(5) } : null;
                await agent.AddIdentityAsync(keys[i], "vela-" + keys[i].PublicKey.KeyType, constraints);

                string algorithm = keys[i].SignatureAlgorithms[0];
                byte[] signature = await agent.SignAsync(keys[i].PublicKey.Blob, data, algorithm);
                Assert.IsTrue(
                    keys[i].PublicKey.VerifySignature(signature, data, algorithm),
                    $"{keys[i].PublicKey.KeyType}：agent 手里的私钥与我们的不是同一把");
            }

            SshCommandResult listed = await connection.RunAsync($"SSH_AUTH_SOCK={socket} ssh-add -l");
            Assert.AreEqual(0, listed.ExitCode, listed.StandardError);
            foreach (InMemorySshSigner key in keys)
            {
                Assert.Contains(key.PublicKey.Sha256Fingerprint, listed.StandardOutput);
                key.Dispose();
            }
        }
        finally
        {
            await connection.RunAsync($"kill {pid}; rm -f {socket}");
        }
    }

    /// <summary>
    /// 真实的 OpenSSH agent 上的管理操作：「证书 + 私钥」一起加（三种钥，ssh-keygen 签的证书）、删一把、锁、解锁、清空。
    /// </summary>
    /// <remarks>
    /// 证书那条的报文形状（证书之后只放证书里没有的私钥部分）只有真 agent 裁决得了：错了 agent 回 FAILURE，
    /// 或者回 SUCCESS 却签不出能用原公钥验过的签名。远端 <c>ssh-add -l</c> 再从 agent 自己的角度看一遍。
    /// </remarks>
    [TestMethod]
    public async Task 真实的OpenSSH_agent上加证书_删钥_锁与清空()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        string socket = $"/tmp/vela-agent-{Guid.NewGuid():N}.sock";
        SshCommandResult started = await connection.RunAsync($"ssh-agent -s -a {socket}");
        Assert.AreEqual(0, started.ExitCode, started.StandardError);
        string pid = started.StandardOutput.Split("SSH_AGENT_PID=")[1].Split(';')[0];

        try
        {
            SshChannel tunnel = await connection.OpenUnixSocketTunnelAsync(socket);
            await using var agent = SshAgentClient.FromStream(tunnel.AsStream(), socket);

            byte[] data = Encoding.UTF8.GetBytes("证书身份签的数据");
            List<SshPublicKey> certificates = [];
            foreach (string name in new[] { "cert-ed25519", "cert-rsa", "cert-ecdsa" })
            {
                string fixture = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);
                using InMemorySshSigner key = await SshPrivateKeyFile.LoadAsync(fixture);
                var certificate = SshPublicKey.Parse(await File.ReadAllTextAsync(fixture + "-cert.pub"));
                await agent.AddIdentityAsync(key, certificate, "vela-" + name);

                string algorithm = certificate.SignatureAlgorithms[0];
                byte[] signature = await agent.SignAsync(certificate.Blob, data, algorithm);
                Assert.IsTrue(
                    key.PublicKey.VerifySignature(signature, data, SshPublicKey.StripCertificateSuffix(algorithm)),
                    $"{name}：agent 手里的私钥与证书证的不是同一把");
                certificates.Add(certificate);
            }

            string listed = (await connection.RunAsync($"SSH_AUTH_SOCK={socket} ssh-add -l")).StandardOutput;
            Assert.Contains("ED25519-CERT", listed);
            Assert.Contains("RSA-CERT", listed);
            Assert.Contains("ECDSA-CERT", listed);

            Assert.IsTrue(await agent.RemoveIdentityAsync(certificates[0]));
            Assert.IsFalse(await agent.RemoveIdentityAsync(certificates[0]), "删过一次就不在了");
            Assert.HasCount(2, await agent.ListIdentitiesAsync());

            Assert.IsTrue(await agent.LockAsync("离开座位"));
            Assert.IsEmpty(await agent.ListIdentitiesAsync(), "锁着的 agent 不列钥");
            await Assert.ThrowsAsync<SshAgentException>(async () => await agent.RemoveAllIdentitiesAsync());
            Assert.IsFalse(await agent.UnlockAsync("猜一个"));
            Assert.IsTrue(await agent.UnlockAsync("离开座位"));
            Assert.HasCount(2, await agent.ListIdentitiesAsync());

            await agent.RemoveAllIdentitiesAsync();
            Assert.IsEmpty(await agent.ListIdentitiesAsync());
        }
        finally
        {
            await connection.RunAsync($"kill {pid}; rm -f {socket}");
        }
    }

    /// <summary>
    /// 会话声明（<c>session-bind@openssh.com</c>）交给<b>真实的 OpenSSH agent</b>，并让 <c>ssh-add -h</c> 的约束真的起作用
    /// （velashell-docs/zh/ssh/spec/07 §7.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单元测试里的 <c>TestAgent</c> 与实现出自对规格的同一份理解，这里让真 agent 来裁决：
    /// 在服务端起一个 agent，用 <c>ssh-add -h</c> 加一把只许用于 <c>dest.example</c> 的钥，再经 <c>direct-streamlocal</c> 接上去。
    /// </para>
    /// <list type="number">
    ///   <item>声明成 <c>dest.example</c> 的会话：agent 回 SUCCESS，替这个会话的认证请求签名；</item>
    ///   <item>签名被篡改的声明：agent 拒绝 —— 说明它真的拿主机公钥验了签名，前一条的 SUCCESS 不是走过场；</item>
    ///   <item>声明成别的主机的会话：同一把钥拒签 —— 约束真的生效了。</item>
    /// </list>
    /// </remarks>
    [TestMethod]
    public async Task 会话声明经得起真实OpenSSH_agent的校验且目的地约束生效()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        string socket = $"/tmp/vela-agent-{Guid.NewGuid():N}.sock";
        string scratch = $"/tmp/vela-bind-{Guid.NewGuid():N}";
        SshCommandResult started = await connection.RunAsync($"ssh-agent -s -a {socket}");
        Assert.AreEqual(0, started.ExitCode, started.StandardError);
        string pid = started.StandardOutput.Split("SSH_AGENT_PID=")[1].Split(';')[0];

        // 「目的地」的主机密钥在本地造：agent 只按声明里的主机公钥验签名、查约束。
        using var destination = InMemorySshSigner.GenerateEd25519();
        using var elsewhere = InMemorySshSigner.GenerateEd25519();

        try
        {
            SshCommandResult prepared = await connection.RunAsync(
                $"mkdir -p {scratch} && ssh-keygen -q -t ed25519 -N '' -f {scratch}/id && " +
                $"printf 'dest.example %s\\n' '{destination.PublicKey.ToOpenSshFormat()}' > {scratch}/known_hosts && " +
                $"SSH_AUTH_SOCK={socket} ssh-add -H {scratch}/known_hosts -h dest.example {scratch}/id && cat {scratch}/id.pub");
            Assert.AreEqual(0, prepared.ExitCode, prepared.StandardError);
            var userKey = SshPublicKey.Parse(prepared.StandardOutput.Trim().Split('\n')[^1]);

            // ① 声明成 dest.example 的会话，替它的认证请求签名。
            SshSessionProof toDestination = await ProofAsync(destination);
            await using (var agent = SshAgentClient.FromStream((await connection.OpenUnixSocketTunnelAsync(socket)).AsStream(), socket))
            {
                Assert.IsTrue(
                    await agent.DeclareSessionAsync(toDestination, SshAgentConnectionPurpose.Authentication),
                    "真 agent 不接受我们的声明：三个字段的编码或签名不对");

                byte[] signature = await agent.SignAsync(
                    userKey.Blob, UserAuthRequest(toDestination.SessionId, userKey), SshAlgorithmNames.SshEd25519);
                Assert.IsNotEmpty(signature);
            }

            // ② 篡改过签名的声明：agent 必须拒绝。
            SshSessionProof forged = toDestination with { Signature = [.. toDestination.Signature[..^1], (byte)(toDestination.Signature[^1] ^ 0xFF)] };
            await using (var agent = SshAgentClient.FromStream((await connection.OpenUnixSocketTunnelAsync(socket)).AsStream(), socket))
            {
                Assert.IsFalse(await agent.DeclareSessionAsync(forged, SshAgentConnectionPurpose.Authentication));
            }

            // ③ 声明成别的主机的会话：受约束的钥拒签。
            SshSessionProof toElsewhere = await ProofAsync(elsewhere);
            await using (var agent = SshAgentClient.FromStream((await connection.OpenUnixSocketTunnelAsync(socket)).AsStream(), socket))
            {
                Assert.IsTrue(await agent.DeclareSessionAsync(toElsewhere, SshAgentConnectionPurpose.Authentication));

                SshAgentException refused = await Assert.ThrowsExactlyAsync<SshAgentException>(
                    async () => await agent.SignAsync(
                        userKey.Blob, UserAuthRequest(toElsewhere.SessionId, userKey), SshAlgorithmNames.SshEd25519));
                Assert.AreEqual(SshFailureReason.AgentRefused, refused.Reason);
            }

            // 不声明时 agent 怎么对待受约束的钥 —— 只记下来，不断言（这正是本库曾经的样子）。
            await using (var agent = SshAgentClient.FromStream((await connection.OpenUnixSocketTunnelAsync(socket)).AsStream(), socket))
            {
                string unbound;
                try
                {
                    await agent.SignAsync(userKey.Blob, UserAuthRequest(toDestination.SessionId, userKey), SshAlgorithmNames.SshEd25519);
                    unbound = "签了";
                }
                catch (SshAgentException ex)
                {
                    unbound = $"拒签（{ex.Reason}）";
                }
                Console.WriteLine($"不声明会话时，真实 agent 对 ssh-add -h 约束过的钥：{unbound}");
            }
        }
        finally
        {
            await connection.RunAsync($"kill {pid}; rm -rf {socket} {scratch}");
        }

        static async Task<SshSessionProof> ProofAsync(InMemorySshSigner hostKey)
        {
            byte[] sessionId = RandomNumberGenerator.GetBytes(32);
            byte[] signature = await hostKey.SignAsync(sessionId, SshAlgorithmNames.SshEd25519);
            return new SshSessionProof(hostKey.PublicKey.Blob.ToArray(), sessionId, signature);
        }

        // RFC 4252 §7 的签名输入：string session_id ‖ USERAUTH_REQUEST(publickey, has_signature = true)。
        static byte[] UserAuthRequest(byte[] sessionId, SshPublicKey key)
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteString(sessionId);
            writer.WriteByte((byte)SshMessageNumber.UserAuthRequest);
            writer.WriteUtf8String("vela");
            writer.WriteUtf8String(SshProtocolNames.ServiceConnection);
            writer.WriteUtf8String(SshProtocolNames.AuthPublicKey);
            writer.WriteBoolean(true);
            writer.WriteUtf8String(SshAlgorithmNames.SshEd25519);
            writer.WriteString(key.Blob.Span);
            return buffer.WrittenSpan.ToArray();
        }
    }

    /// <summary>往返时间：真 OpenSSH 回保活请求（REQUEST_FAILURE 也算），量出来的是个正的、合理的数，并记进 LastRoundTrip。</summary>
    [TestMethod]
    public async Task 量得到真实服务端的往返时间()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        TimeSpan rtt = await connection.MeasureRoundTripAsync();

        Assert.IsGreaterThan(TimeSpan.Zero, rtt);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), rtt, "本机的 Docker，往返不该到秒级");
        Assert.AreEqual(rtt, connection.LastRoundTrip);
    }

    /// <summary>FIPS 认可的清单与真 OpenSSH 谈得成，谈成的全是认可的算法。</summary>
    [TestMethod]
    public async Task FIPS清单与真OpenSSH谈得成()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options() with { Algorithms = SshAlgorithmSet.FipsApprovedOnly });
        SshNegotiatedAlgorithms negotiated = connection.Algorithms;

        Assert.Contains(negotiated.KeyExchange, SshAlgorithmSet.FipsApprovedOnly.KeyExchange);
        Assert.Contains(negotiated.HostKey, SshAlgorithmSet.FipsApprovedOnly.HostKey);
        Assert.Contains(negotiated.EncryptionClientToServer, SshAlgorithmSet.FipsApprovedOnly.EncryptionClientToServer);
        Assert.AreEqual("ok", (await connection.RunAsync("echo ok")).StandardOutput.Trim());
    }

    /// <summary>
    /// 群交换（RFC 4419）：只开它时与真 OpenSSH 谈得成 —— 线上的 34 / 31 / 32 / 33 与交换哈希多出的五项都对，
    /// 否则签名验不过。再连一次（服务端多半换一个群）、重协商一次，都照常。
    /// </summary>
    /// <summary>
    /// 远程动态转发：真 sshd 上的程序（OpenBSD nc 的 SOCKS5 客户端）经它连回本机 —— 名单里的目标双向搬运，
    /// 名单外的被拒（nc 退出码非 0）。目标写 127.0.0.1：在远端看是它自己，经这条转发就成了本机的环回。
    /// </summary>
    [TestMethod]
    public async Task 远程动态转发_远端程序经SOCKS连回本机()
    {
        RequireServer();
        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        using Socket echo = new(SocketType.Stream, ProtocolType.Tcp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        echo.Listen(4);
        int echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;
        var serving = Task.Run(async () =>
        {
            using Socket accepted = await echo.AcceptAsync();
            byte[] buffer = new byte[64];
            int read;
            while ((read = await accepted.ReceiveAsync(buffer)) > 0)
            {
                await accepted.SendAsync(buffer.AsMemory(0, read));
            }
        });

        RemotePortForwarder forwarder;
        try
        {
            forwarder = await RemotePortForwarder.StartDynamicAsync(
                connection, RemoteOpenPolicy.Allow($"127.0.0.1:{echoPort}"), new RemotePortForwardOptions { BindPort = 0 });
        }
        catch (SshForwardException ex) when (ex.Reason == SshFailureReason.ForwardRejected)
        {
            Assert.Inconclusive("服务端不许远程转发（AllowTcpForwarding no）。");
            return;
        }

        await using (forwarder)
        {
            SshCommandResult allowed = await connection.RunAsync(
                $"printf 'via-socks\\n' | nc -N -X 5 -x 127.0.0.1:{forwarder.BoundPort} -w 5 127.0.0.1 {echoPort}");
            Assert.AreEqual("via-socks", allowed.StandardOutput.Trim(), allowed.StandardError);

            SshCommandResult denied = await connection.RunAsync(
                $"nc -X 5 -x 127.0.0.1:{forwarder.BoundPort} -w 5 10.0.0.1 22 </dev/null; echo rc=$?");
            Assert.DoesNotContain("rc=0", denied.StandardOutput, "名单外的目标必须被拒");
        }
        await serving;
    }

    /// <summary>
    /// Unix 域套接字的本地转发，两头都是套接字：本机在一个套接字文件上监听，经 direct-streamlocal 转到真服务端上
    /// <c>ssh-agent</c> 的套接字；经它往 agent 里加一把钥，服务端 <c>ssh-add -l</c> 列得出来。
    /// </summary>
    [TestMethod]
    public async Task 本地转发在两头的Unix套接字之间搬运()
    {
        RequireServer();
        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        string remoteSocket = $"/tmp/vela-agent-{Guid.NewGuid():N}.sock";
        SshCommandResult started = await connection.RunAsync($"ssh-agent -s -a {remoteSocket}");
        Assert.AreEqual(0, started.ExitCode, started.StandardError);
        string pid = started.StandardOutput.Split("SSH_AGENT_PID=")[1].Split(';')[0];

        string localSocket = Path.Combine(Path.GetTempPath(), $"vs-{Guid.NewGuid():N}"[..11] + ".sock");
        try
        {
            await using var forwarder = LocalPortForwarder.StartToUnixSocket(
                connection, remoteSocket, new LocalPortForwardOptions { ListenSocketPath = localSocket });

            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(localSocket));
            await using var agent = SshAgentClient.FromStream(new NetworkStream(client, ownsSocket: false), localSocket);
            using var key = InMemorySshSigner.GenerateEd25519();
            await agent.AddIdentityAsync(key, "via-unix-forward");

            string listed = (await connection.RunAsync($"SSH_AUTH_SOCK={remoteSocket} ssh-add -l")).StandardOutput;
            Assert.Contains(key.PublicKey.Sha256Fingerprint, listed);
        }
        finally
        {
            await connection.RunAsync($"kill {pid}; rm -f {remoteSocket}");
        }
        Assert.IsFalse(File.Exists(localSocket), "释放之后本机的套接字文件要删掉");
    }

    /// <summary>
    /// 通用的扩展请求对真 sftp-server：limits@openssh.com 经它发、应答原样交回（四个 uint64，与库内置的解读一致）；
    /// 不认识的扩展名是 OperationUnsupported。
    /// </summary>
    [TestMethod]
    public async Task 通用扩展请求对真sftp_server()
    {
        RequireServer();
        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection);

        byte[] reply = await sftp.SendExtendedAsync(SftpExtensionNames.Limits, ReadOnlyMemory<byte>.Empty);
        Assert.HasCount(32, reply);
        Assert.AreEqual(sftp.Capabilities.Limits.MaxReadLength, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8)));

        SftpException unknown = await Assert.ThrowsAsync<SftpException>(
            async () => await sftp.SendExtendedAsync("nope@vendor.example", ReadOnlyMemory<byte>.Empty));
        Assert.AreEqual(SftpStatusCode.OperationUnsupported, unknown.StatusCode);
    }

    /// <summary>
    /// 主机密钥轮换（UpdateHostKeys）对真 sshd：头一次连接按 TOFU 记下谈成的那把钥；sshd 认证之后宣告它的全部主机密钥，
    /// 我们请它证明没记过的那几把、验过签名、补记进 known_hosts。再连一次就没有新钥了。
    /// 证明里签的是什么（字段顺序、RSA 用哪种摘要）只有真 sshd 裁决得了：错一处签名就验不过、一把都不记。
    /// 〔Q4〕记着、sshd 却不再有的旧钥，连上之后删掉。
    /// </summary>
    [TestMethod]
    public async Task 主机密钥轮换_真sshd宣告的新钥证实之后补记()
    {
        RequireServer();
        string knownHosts = Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}");
        try
        {
            KnownHostsPolicy policy = new(knownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };

            SshHostKeyUpdate first = await ConnectAndAwaitRotationAsync(policy);
            Assert.IsNull(first.Skipped, first.Skipped);
            Assert.IsNotEmpty(first.Added, "sshd 有好几把主机密钥，头一次只记了谈成的那把");
            string[] lines = await File.ReadAllLinesAsync(knownHosts);
            Assert.HasCount(1 + first.Added.Count, lines);
            foreach (SshPublicKey added in first.Added)
            {
                Assert.Contains(l => l.Contains(added.ToOpenSshFormat().Split(' ')[1], StringComparison.Ordinal), lines);
            }

            SshHostKeyUpdate second = await ConnectAndAwaitRotationAsync(policy);
            Assert.IsEmpty(second.Added, "再连就都认得了");
            Assert.HasCount(lines.Length, await File.ReadAllLinesAsync(knownHosts));

            // 〔Q4〕记着一把 sshd 没有的钥（换下来的旧钥）：真 sshd 的宣告里没有它，连上之后从 known_hosts 删掉，别的行不动。
            using var retired = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
            var retiredKey = SshPublicKey.Decode(retired.PublicKeyBlob);
            await File.AppendAllTextAsync(knownHosts, $"{lines[0].Split(' ')[0]} {retiredKey.KeyType} {Convert.ToBase64String(retiredKey.Blob.Span)}\n");
            // 新的策略实例：上面那个缓存着文件改动之前的内容。
            SshHostKeyUpdate third = await ConnectAndAwaitRotationAsync(
                new KnownHostsPolicy(knownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true });
            Assert.IsNull(third.Skipped, third.Skipped);
            Assert.AreSequenceEqual([retiredKey.Sha256Fingerprint], [.. third.RemovedFingerprints]);
            Assert.AreSequenceEqual(lines, await File.ReadAllLinesAsync(knownHosts));
        }
        finally
        {
            File.Delete(knownHosts);
        }

        static async Task<SshHostKeyUpdate> ConnectAndAwaitRotationAsync(KnownHostsPolicy policy)
        {
            await using SshConnection connection = await SshConnection.ConnectAsync(Options() with { HostKeyPolicy = policy });
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            while (connection.LastHostKeyUpdate is null)
            {
                await Task.Delay(20, timeout.Token);
            }
            await connection.HostKeyRotation;
            return connection.LastHostKeyUpdate;
        }
    }

    /// <summary>
    /// Windows CNG 密钥库里<b>不可导出</b>的钥登录真 sshd：公钥临时加进 authorized_keys，只给这一个凭据去连，签名由密钥库做。
    /// </summary>
    [TestMethod]
    public async Task 用CNG密钥库里不可导出的钥登录真sshd()
    {
        RequireServer();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("CNG 只在 Windows 上。");
            return;
        }

        string name = Auth.CngSignerTests.CreateKey(CngAlgorithm.Rsa, 3072);
        await using SshConnection admin = await SshConnection.ConnectAsync(Options());
        string marker = $"vela-cng-{Guid.NewGuid():N}";
        try
        {
            using var signer = CngSshSigner.Open(name);
            SshCommandResult added = await admin.RunAsync(
                $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '{signer.PublicKey.ToOpenSshFormat()} {marker}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
            Assert.AreEqual(0, added.ExitCode, added.StandardError);

            await using SshConnection connection = await SshConnection.ConnectAsync(
                Options(credentials: [new PublicKeyCredential(signer)]));
            Assert.AreEqual("cng", (await connection.RunAsync("echo cng")).StandardOutput.Trim());
        }
        finally
        {
            await admin.RunAsync($"sed -i '/{marker}/d' ~/.ssh/authorized_keys");
            Auth.CngSignerTests.DeleteKey(name);
        }
    }

    /// <summary>
    /// 传输层的 PING / PONG（ping@openssh.com，OpenSSH 9.5 起）：认证之后的 EXT_INFO 里宣告了它，量往返时间就用 PING；
    /// 连量几次都有 PONG 回来（对得上序号）。
    /// </summary>
    [TestMethod]
    public async Task 真sshd宣告ping_往返时间用PING量()
    {
        RequireServer();
        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!connection.PeerSupportsPing)
        {
            await Task.Delay(20, timeout.Token);
        }

        for (int i = 0; i < 3; i++)
        {
            TimeSpan rtt = await connection.MeasureRoundTripAsync(timeout.Token);
            Assert.IsGreaterThan(TimeSpan.Zero, rtt);
        }
        Assert.AreEqual("ok", (await connection.RunAsync("echo ok")).StandardOutput.Trim(), "PING 之后连接照常");
    }

    /// <summary>
    /// 按键时序混淆对真 sshd：一个字一个字敲进 shell，命令照常跑、输出照常回；打字期间发了掩护 PING（sshd 认 ping@openssh.com），
    /// 连接一直好好的。
    /// </summary>
    [TestMethod]
    public async Task 按键时序混淆对真sshd照常工作()
    {
        RequireServer();
        await using SshConnection connection = await SshConnection.ConnectAsync(Options());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!connection.PeerSupportsPing)
        {
            await Task.Delay(20, timeout.Token);
        }

        await using SshShell shell = await connection.OpenShellAsync(
            new SshShellOptions { ObscureKeystrokeTiming = TimeSpan.FromMilliseconds(20) }, timeout.Token);
        foreach (char c in "echo obscured-$((6*7))\n")
        {
            await shell.StandardInput.WriteAsync(new[] { (byte)c }, timeout.Token);
            await Task.Delay(25, timeout.Token);
        }

        StringBuilder output = new();
        while (!output.ToString().Contains("obscured-42", StringComparison.Ordinal))
        {
            System.IO.Pipelines.ReadResult read = await shell.StandardOutput.ReadAsync(timeout.Token);
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
            {
                output.Append(Encoding.UTF8.GetString(segment.Span));
            }
            shell.StandardOutput.AdvanceTo(read.Buffer.End);
        }
        Assert.IsGreaterThan(0, shell.KeystrokeChaffSent, "打字期间要有掩护");
        Assert.IsTrue(connection.IsAlive);
    }

    [TestMethod]
    public async Task 群交换与真OpenSSH谈得成()
    {
        RequireServer();
        SshAlgorithmSet gexOnly = SshAlgorithmSet.Default with { KeyExchange = [SshAlgorithmNames.DiffieHellmanGroupExchangeSha256] };

        for (int round = 0; round < 2; round++)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            SshConnection connection;
            try
            {
                connection = await SshConnection.ConnectAsync(Options() with { Algorithms = gexOnly });
            }
            catch (SshNegotiationException ex) when (!ex.OfferedByPeer.Contains(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256))
            {
                // OpenSSH 10 起服务端默认不开 DH 那几种；Start-TestServer.ps1 会用 kex-gex.sh 打开。
                Assert.Inconclusive($"服务端没开群交换（它给的是 {string.Join(", ", ex.OfferedByPeer)}）。用 Start-TestServer.ps1 起服务端。");
                return;
            }
            await using SshConnection _ = connection;
            Console.WriteLine($"第 {round + 1} 次连接（含素性检验）：{elapsed.ElapsedMilliseconds} ms");

            Assert.AreEqual(SshAlgorithmNames.DiffieHellmanGroupExchangeSha256, connection.Algorithms.KeyExchange);
            Assert.AreEqual("ok", (await connection.RunAsync("echo ok")).StandardOutput.Trim());

            await connection.StartRekeyAsync();
            using CancellationTokenSource rekeyTimeout = new(TimeSpan.FromSeconds(30));
            while (connection.RekeyCount == 0)
            {
                await Task.Delay(20, rekeyTimeout.Token);
            }
            Assert.AreEqual("again", (await connection.RunAsync("echo again")).StandardOutput.Trim());
        }
    }

    [TestMethod]
    public async Task 保活探测能被真实服务端应答()
    {
        RequireServer();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        bool alive = await connection.SendKeepAliveAsync();

        // OpenSSH 不认识 keepalive@openssh.com 作为客户端发来的请求时会回
        // REQUEST_FAILURE —— **那也算有应答**，链路是活的。
        Assert.IsTrue(connection.IsAlive);
        Console.WriteLine($"保活应答：{alive}");
    }

    // ------------------------------------------------------------ X11 转发

    /// <summary>X11 用例还需要服务端开了 <c>X11Forwarding</c> 且装了 <c>xauth</c>。</summary>
    /// <remarks>
    /// 单独一个开关，因为常见镜像（比如 linuxserver/openssh-server）默认两样都没有。
    /// 用 <c>Start-TestServer.ps1 -X11</c> 起服务端。
    /// </remarks>
    private static void RequireX11Server()
    {
        RequireServer();

        if (Environment.GetEnvironmentVariable("VELASHELL_SSH_INTEROP_X11") != "1")
        {
            Assert.Inconclusive(
                "X11 互操作用例需要服务端开了 X11Forwarding 且装了 xauth。" +
                "用 scripts/ssh/interop/Start-TestServer.ps1 -X11 起，然后设 VELASHELL_SSH_INTEROP_X11=1。");
        }
    }

    [TestMethod]
    public async Task 真实服务端接受x11_req并把假cookie存进xauth()
    {
        // ⚠️ 这一条验的是**十六进制编码那个坑**。
        // x11-req 的 cookie 字段是十六进制文本；发成原始字节的话，
        // 远端 xauth 存进去的和我们校验的就对不上，
        // 而错误信息只会说「连接被拒绝」—— 指不到这里。
        RequireX11Server();

        await using SshConnection connection = await SshConnection.ConnectAsync(Options());

        SshCommandOptions options = new()
        {
            X11Forwarding = new X11ForwardOptions { IsTrusted = true, Display = X11Display.Parse(":0") },
        };

        await using SshCommand command = await connection.ExecuteAsync(
            "echo DISPLAY=$DISPLAY; xauth list 2>/dev/null", options);

        Assert.IsNotNull(command.X11, "请求了就该拿得到转发器");
        string expected = Convert.ToHexStringLower(command.X11!.FakeCookie);

        (SshExitStatus result, string output, _) = await command.ReadToEndAsync();
        Assert.AreEqual(0, result.ExitCode);

        Assert.Contains("DISPLAY=localhost:", output, "sshd 应当给这条会话配上转发的显示");
        Assert.Contains(XAuthority.MitMagicCookie1, output);

        // 远端 xauth 里存的那个 cookie，必须**正好是我们发出去的假 cookie**。
        Assert.Contains(expected, output, $"远端 xauth 存的应当是我们发的假 cookie（{expected}）—— 对不上就是十六进制编码写错了");
    }

    [TestMethod]
    public async Task 真实服务端开回的x11通道会被接受并换成真cookie()
    {
        // 端到端：容器里的进程连上 sshd 配的 DISPLAY，sshd 于是开一条 x11
        // 通道回来；我们核对假 cookie、换成真 cookie，转给本机一个假的 X server。
        RequireX11Server();

        using var xserver = FakeXServer.Start();
        string xauthority = WriteXAuthority(out byte[] realCookie);

        try
        {
            await using SshConnection connection = await SshConnection.ConnectAsync(Options());

            SshCommandOptions options = new()
            {
                X11Forwarding = new X11ForwardOptions
                {
                    IsTrusted = true,
                    Display = xserver.Display,
                    XAuthorityPath = xauthority,
                },
            };

            // ⚠️ 建立报文要**从 stdin 喂进去**，不能拼进命令行 ——
            // 报文里的假 cookie 只有在 x11-req 发完之后才知道，
            // 而命令行必须在那之前就定下来。
            await using SshCommand command = await connection.ExecuteAsync(
                "base64 -d | nc -w 5 localhost 6010 >/dev/null 2>&1", options);

            Assert.IsNotNull(command.X11);

            byte[] setup = BuildX11Setup(command.X11!.FakeCookie.ToArray());
            byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(setup));

            await command.StandardInput.WriteAsync(encoded);
            await command.StandardInput.FlushAsync();
            await command.CompleteStandardInputAsync();

            byte[] arrived = await xserver.ReadSetupAsync();

            Assert.IsTrue(
                X11SetupMessage.TryParse(new ReadOnlySequence<byte>(arrived), out X11SetupMessage.Parsed parsed),
                "落到本机 X server 上的应当是一个完整的建立报文");

            Assert.AreSequenceEqual(
                realCookie, parsed.ProtocolData, "转给本机 X server 的必须是**真** cookie —— 假的那个只在 SSH 线上出现");

            Assert.AreEqual(1, command.X11!.AcceptedChannels);
            Assert.AreEqual(0, command.X11!.RejectedChannels);

            _ = await command.ReadToEndAsync();
        }
        finally
        {
            try { File.Delete(xauthority); } catch (IOException) { }
        }
    }

    /// <summary>拼一个带指定 cookie 的 X11 连接建立报文。</summary>
    private static byte[] BuildX11Setup(byte[] cookie)
    {
        byte[] name = Encoding.ASCII.GetBytes(XAuthority.MitMagicCookie1);
        int paddedName = (name.Length + 3) & ~3;
        int paddedData = (cookie.Length + 3) & ~3;

        byte[] message = new byte[X11SetupMessage.HeaderLength + paddedName + paddedData];
        message[0] = (byte)'B';                                            // 大端
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 11);      // protocol major
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)cookie.Length);

        name.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength));
        cookie.CopyTo(message.AsSpan(X11SetupMessage.HeaderLength + paddedName));
        return message;
    }

    /// <summary>摆一份只含通配条目的 <c>.Xauthority</c>，并交出里面那个真 cookie。</summary>
    private static string WriteXAuthority(out byte[] realCookie)
    {
        realCookie = RandomNumberGenerator.GetBytes(16);
        string path = Path.Combine(Path.GetTempPath(), $"velashell-xauth-{Guid.NewGuid():N}");

        ArrayBufferWriter<byte> buffer = new();
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(two, XAuthority.FamilyWild);
        buffer.Write(two);

        WriteBlock(buffer, []);                       // address（通配族不看）
        WriteBlock(buffer, []);                       // display number（通配）
        WriteBlock(buffer, Encoding.ASCII.GetBytes(XAuthority.MitMagicCookie1));
        WriteBlock(buffer, realCookie);

        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
        return path;

        static void WriteBlock(ArrayBufferWriter<byte> target, byte[] value)
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
            target.Write(length);
            target.Write(value);
        }
    }

    /// <summary>一个只做一件事的假 X server：收下建立报文。</summary>
    private sealed class FakeXServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly TaskCompletionSource<byte[]> _setup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Socket? _accepted;

        private FakeXServer(Socket listener, X11Display display)
        {
            _listener = listener;
            Display = display;
            _ = Task.Run(AcceptAsync);
        }

        public X11Display Display { get; }

        public static FakeXServer Start()
        {
            for (int number = 40; number < 80; number++)
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, X11Display.TcpPortBase + number));
                    socket.Listen(4);
                    return new FakeXServer(socket, X11Display.Parse($"localhost:{number}"));
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }

            throw new InvalidOperationException("找不到可用的 X11 端口。");
        }

        public async Task<byte[]> ReadSetupAsync() =>
            await _setup.Task.WaitAsync(TimeSpan.FromSeconds(20));

        private async Task AcceptAsync()
        {
            try
            {
                // 套接字要留着 —— 读完就关的话，搬运那一侧立刻拿到
                // 「对端已关闭读取端」，而那与被测行为毫无关系。
                _accepted = await _listener.AcceptAsync();

                byte[] buffer = new byte[256];
                int read = await _accepted.ReceiveAsync(buffer);
                _setup.TrySetResult(buffer[..read]);
            }
            catch (Exception ex)
            {
                _setup.TrySetException(ex);
            }
        }

        public void Dispose()
        {
            _accepted?.Dispose();
            _listener.Dispose();
        }
    }
}
