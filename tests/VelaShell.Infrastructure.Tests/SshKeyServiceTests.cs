using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class SshKeyServiceTests : IDisposable
{
    private readonly SshKeyService _service;
    private readonly string _sshDir;

    /// <summary>扮演 ~/.ssh 之外的地方(用户从别处挑文件导入)。</summary>
    private readonly string _external;

    public SshKeyServiceTests()
    {
        string root = Path.Combine(Path.GetTempPath(), $"velashell_sshkeys_{Guid.NewGuid():N}");
        _sshDir = Path.Combine(root, "ssh");
        _external = Path.Combine(root, "elsewhere");
        _service = new(_sshDir);
    }

    public void Dispose()
    {
        string? root = Path.GetDirectoryName(_sshDir);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>不指定算法时给的是 Ed25519 —— 生成、列举、删除走一圈,类型与公钥前缀都要对上。</summary>
    [TestMethod]
    public async Task Generate_DefaultsToEd25519_AndRoundTrips()
    {
        SshKeyInfo generated = await _service.GenerateKeyAsync("test_key");
        Assert.AreEqual("ED25519", generated.Type);
        Assert.StartsWith("SHA256:", generated.Fingerprint);
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key")));
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key.pub")));
        List<SshKeyInfo> listed = await _service.ListKeysAsync();
        Assert.HasCount(1, listed);
        Assert.AreEqual("ED25519", listed[0].Type, "列举时从公钥 blob 解析出的类型应一致");
        Assert.AreEqual(generated.Fingerprint, listed[0].Fingerprint);
        Assert.StartsWith("ssh-ed25519 ", listed[0].PublicKeyLine);
        await _service.DeleteKeyAsync("test_key");
        Assert.IsEmpty(await _service.ListKeysAsync());
    }

    /// <summary>三条 NIST 曲线各生成一把,类型标签与公钥行的算法名都要对上。</summary>
    [TestMethod]
    [DataRow(256, "ECDSA 256", "ecdsa-sha2-nistp256")]
    [DataRow(384, "ECDSA 384", "ecdsa-sha2-nistp384")]
    [DataRow(521, "ECDSA 521", "ecdsa-sha2-nistp521")]
    public async Task Generate_Ecdsa_UsesTheRequestedCurve(int bits, string expectedType, string expectedAlgorithm)
    {
        SshKeyInfo generated = await _service.GenerateKeyAsync($"ec{bits}", SshKeyAlgorithm.Ecdsa, bits);
        Assert.AreEqual(expectedType, generated.Type);
        Assert.StartsWith(expectedAlgorithm + " ", generated.PublicKeyLine);

        // 列举走的是另一条路(从 .pub 的 blob 反解),两边必须给出同一个答案。
        SshKeyInfo listed = (await _service.ListKeysAsync()).Single(k => k.Name == $"ec{bits}");
        Assert.AreEqual(expectedType, listed.Type);
        Assert.AreEqual(generated.Fingerprint, listed.Fingerprint);
    }

    /// <summary>bits 给 0 表示「按算法取默认值」:ECDSA 落到 256,RSA 落到 4096。</summary>
    [TestMethod]
    public async Task Generate_ZeroBits_FallsBackToTheAlgorithmDefault()
    {
        Assert.AreEqual("ECDSA 256", (await _service.GenerateKeyAsync("ec_default", SshKeyAlgorithm.Ecdsa)).Type);
        Assert.AreEqual("RSA 4096", (await _service.GenerateKeyAsync("rsa_default", SshKeyAlgorithm.Rsa)).Type);
    }

    /// <summary>SSH 只定义了三条 NIST 曲线的算法名,别的位数连个能写进公钥行的名字都没有。</summary>
    [TestMethod]
    public async Task Generate_Ecdsa_WithUnsupportedCurve_Throws() =>
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => _service.GenerateKeyAsync("ec_bad", SshKeyAlgorithm.Ecdsa, 512));

    [TestMethod]
    public async Task Generate_List_Delete_RoundTrips()
    {
        SshKeyInfo generated = await _service.GenerateKeyAsync("test_key", SshKeyAlgorithm.Rsa, 2048);
        Assert.AreEqual("test_key", generated.Name);
        Assert.AreEqual("RSA 2048", generated.Type);
        Assert.StartsWith("SHA256:", generated.Fingerprint);
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key")));
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "test_key.pub")));
        List<SshKeyInfo> listed = await _service.ListKeysAsync();
        Assert.HasCount(1, listed);
        Assert.AreEqual(generated.Fingerprint, listed[0].Fingerprint, "列举解析的指纹应与生成时一致");
        Assert.AreEqual("RSA 2048", listed[0].Type, "公钥 blob 解析出的位数应一致");
        Assert.StartsWith("ssh-rsa ", listed[0].PublicKeyLine);
        await _service.DeleteKeyAsync("test_key");
        Assert.IsEmpty(await _service.ListKeysAsync());
    }

    [TestMethod]
    public async Task Generate_DuplicateName_Throws()
    {
        await _service.GenerateKeyAsync("dup", SshKeyAlgorithm.Rsa, 2048);
        await Assert.ThrowsExactlyAsync<IOException>(() => _service.GenerateKeyAsync("dup", SshKeyAlgorithm.Rsa, 2048));
    }

    [TestMethod]
    public async Task List_EmptyDirectory_ReturnsEmpty() => Assert.IsEmpty(await _service.ListKeysAsync());

    // ———————————————————— 导入:不许伪报成功 ————————————————————
    //
    // 导入以前是"能抄就抄":源文件不在就跳过复制,却照样返回一条 Unknown 条目。
    // 界面于是说"已导入 xxx",而 ~/.ssh 里什么都没多 —— 用户下次连接才发现密钥根本不存在。

    /// <summary>正常导入:私钥 + 公钥都在,导入后能被列举出来。</summary>
    [TestMethod]
    public async Task Import_WithBothFiles_ShowsUpInTheList()
    {
        (string privatePath, _) = await CreateExternalKeyAsync("outside_key");

        SshKeyInfo? imported = (await _service.ImportKeyAsync(privatePath))?.Key;

        Assert.IsNotNull(imported);
        Assert.AreEqual("outside_key", imported.Name);
        Assert.StartsWith("SHA256:", imported.Fingerprint, "应当报告真实解析出的指纹,而不是 Unknown。");
        Assert.ContainsSingle(await _service.ListKeysAsync(), "导入后刷新列表应当看得到它。");
    }

    /// <summary>源私钥不存在:必须报错,而不是返回一条查无实据的条目。</summary>
    [TestMethod]
    public async Task Import_WhenThePrivateKeyIsMissing_Throws()
    {
        string missing = Path.Combine(_external, "not_here");

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _service.ImportKeyAsync(missing));
        Assert.IsEmpty(await _service.ListKeysAsync());
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "not_here")), "失败却留下了半份文件。");
    }

    /// <summary>
    /// 只有 OpenSSH 私钥、没有 .pub:从私钥文件的公钥段读出公钥、写一份,照样进列表。
    /// </summary>
    /// <remarks>
    /// 列举是按 <c>*.pub</c> 走的,只抄私钥会"导入成功 → 列表里没有"。曾经因此一律拒绝没有 .pub 的导入。
    /// </remarks>
    [TestMethod]
    public async Task Import_OpenSshKeyWithoutItsPub_WritesThePublicKey()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("lonely");
        string expected = (await new SshKeyService(_external).ListKeysAsync()).Single().Fingerprint;
        File.Delete(publicPath);

        SshKeyInfo? imported = (await _service.ImportKeyAsync(privatePath))?.Key;

        Assert.IsNotNull(imported);
        Assert.AreEqual(expected, imported.Fingerprint);
        Assert.IsTrue(File.Exists(Path.Combine(_sshDir, "lonely.pub")));
    }

    /// <summary>
    /// PuTTY 用户手里通常只有一个 .ppk(这一把还加了密):认得出是私钥,公钥从 Public-Lines 读出。
    /// 曾经只认 <c>-----BEGIN … PRIVATE KEY</c> 的首行,.ppk 被当成「不是私钥」。
    /// </summary>
    [TestMethod]
    public async Task Import_PuttyKeyWithoutPub_IsAccepted()
    {
        Directory.CreateDirectory(_external);
        string ppk = Path.Combine(_external, "putty_key.ppk");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Ssh", "Fixtures", "putty-ed25519-v3-hi-enc.ppk"), ppk);
        Assert.IsTrue(SshPublicKey.TryParse(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Ssh", "Fixtures", "putty-ed25519-v3-hi-enc.pub")),
            out SshPublicKey? expected));

        SshKeyInfo? imported = (await _service.ImportKeyAsync(ppk))?.Key;

        Assert.IsNotNull(imported);
        Assert.AreEqual("putty_key.ppk", imported.Name);
        Assert.AreEqual(expected.Sha256Fingerprint, imported.Fingerprint);
    }

    /// <summary>加密的 PKCS#8 公钥也在密文里:没有 .pub 时仍然明确拒绝,不留下私钥副本。</summary>
    [TestMethod]
    public async Task Import_EncryptedPkcs8WithoutPub_Throws()
    {
        Directory.CreateDirectory(_external);
        string path = Path.Combine(_external, "sealed");
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(path, rsa.ExportEncryptedPkcs8PrivateKeyPem(
                "pw", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
        }

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _service.ImportKeyAsync(path));
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "sealed")), "拒绝之后不该留下私钥副本。");
    }

    /// <summary>挑中一个普通文本文件:不是私钥就不该被当成密钥收下。</summary>
    [TestMethod]
    public async Task Import_OfSomethingThatIsNotAKey_Throws()
    {
        Directory.CreateDirectory(_external);
        string bogus = Path.Combine(_external, "notes.txt");
        await File.WriteAllTextAsync(bogus, "just some notes\n");
        await File.WriteAllTextAsync(bogus + ".pub", "ssh-rsa AAAA notes\n");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(bogus));
        Assert.IsEmpty(await _service.ListKeysAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Import_RejectsAnOversizedKeyFileBeforeWriting(bool oversizedPublic)
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("large");
        string path = oversizedPublic ? publicPath : privatePath;
        string original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.PadRight(SshPrivateKeyFile.MaxFileBytes + 1));

        InvalidDataException ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(privatePath));

        Assert.AreEqual(oversizedPublic ? Strings.Format("KeySvc_ImportBadPublicKey", "large.pub")
            : Strings.Format("KeySvc_ImportNotAPrivateKey", "large"), ex.Message);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_sshDir));
    }

    [TestMethod]
    public async Task Import_PreservesAReadablySizedUnicodePublicKey()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("unicode");
        string privateText = await File.ReadAllTextAsync(privatePath);
        string publicLine = (await File.ReadAllTextAsync(publicPath)).Trim() + new string('界', 360_000);
        await File.WriteAllTextAsync(privatePath, privateText, Encoding.Unicode);
        await File.WriteAllTextAsync(publicPath, publicLine, Encoding.Unicode);

        SshKeyImportResult? result = await _service.ImportKeyAsync(privatePath);

        Assert.IsNotNull(result);
        Assert.AreEqual(privateText, await SshPrivateKeyFile.ReadKeyTextAsync(result.Key.PrivateKeyPath));
        Assert.AreEqual(publicLine, (await _service.ListKeysAsync()).Single().PublicKeyLine);
    }

    [TestMethod]
    public async Task ConcurrentImports_KeepOneCompletePair()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("shared");
        File.Delete(publicPath);

        SshKeyImportResult?[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _service.ImportKeyAsync(privatePath)));

        SshKeyImportResult? winner = results.Single(result => result is not null);
        Assert.IsNotNull(winner);
        Assert.AreEqual(winner.Key, (await _service.ListKeysAsync()).Single());
        Assert.AreEqual(await File.ReadAllTextAsync(privatePath), await File.ReadAllTextAsync(winner.Key.PrivateKeyPath));
    }

    /// <summary>.pub 解析不出来时整体回滚,不留下一份进不了列表的私钥。</summary>
    [TestMethod]
    public async Task Import_WithAnUnreadablePublicKey_RollsBack()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("bad_pub");
        await File.WriteAllTextAsync(publicPath, "this is not a public key\n");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(privatePath));
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "bad_pub")), "私钥副本没有随失败一起清掉。");
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "bad_pub.pub")));
    }

    /// <summary>同名已存在:返回 null(不是异常),且不覆盖现有密钥。</summary>
    [TestMethod]
    public async Task Import_WhenTheNameIsTaken_ReturnsNullAndKeepsTheExistingKey()
    {
        SshKeyInfo existing = await _service.GenerateKeyAsync("taken", SshKeyAlgorithm.Rsa, 2048);
        (string privatePath, _) = await CreateExternalKeyAsync("taken");

        Assert.IsNull(await _service.ImportKeyAsync(privatePath));
        Assert.AreEqual(existing.Fingerprint, (await _service.ListKeysAsync()).Single().Fingerprint);
    }

    /// <summary>导入的私钥要收成仅属主可读写,否则 OpenSSH 直接拒用。</summary>
    /// <remarks>
    /// OSCondition 只管运行时跳过,平台兼容性分析器(CA1416)不认它;CI 以 -warnaserror 构建时
    /// Get/SetUnixFileMode 会报错。UnsupportedOSPlatform 把同一个限制声明给分析器。
    /// </remarks>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Import_TightensPermissionsOnThePrivateKey()
    {
        (string privatePath, _) = await CreateExternalKeyAsync("perm_key");
        File.SetUnixFileMode(
            privatePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await _service.ImportKeyAsync(privatePath);

        UnixFileMode mode = File.GetUnixFileMode(Path.Combine(_sshDir, "perm_key"));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    // ———————————————————— 导入:用 SSH 库把私钥整把解析一遍 ————————————————————
    //
    // 以前只认格式(看 BEGIN 行):头尾对、中间坏了的文件照样导进来,连接时才报错;
    // .pub 与私钥拼错了也照收,列表里的指纹是 .pub 的,认证用的却是另一把。

    /// <summary>头尾都对、正文被截断的私钥:拒收,说清是「坏了」而不是「不是私钥」,不留副本。</summary>
    [TestMethod]
    public async Task Import_OfADamagedPrivateKey_IsRejectedAsDamaged()
    {
        (string privatePath, _) = await CreateExternalKeyAsync("damaged");
        string[] lines = await File.ReadAllLinesAsync(privatePath);
        // 留住 BEGIN / END 两行,砍掉正文的后半段 —— 复制粘贴丢了几行就是这样。
        string[] body = lines[1..^1];
        await File.WriteAllLinesAsync(privatePath, [lines[0], .. body[..(body.Length / 2)], lines[^1]]);

        InvalidDataException ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(privatePath));

        Assert.AreEqual(Strings.Format("KeySvc_ImportInvalidPrivateKey", "damaged"), ex.Message);
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "damaged")), "拒收之后不该留下私钥副本。");
    }

    /// <summary>加了口令的 OpenSSH 私钥:格式对、要口令 —— 照收,并标明要口令;导入时不问口令。</summary>
    [TestMethod]
    public async Task Import_OfAPassphraseProtectedKey_IsAcceptedAndFlagged()
    {
        Directory.CreateDirectory(_external);
        string path = Path.Combine(_external, "locked");
        using (var key = InMemorySshSigner.GenerateEd25519())
        {
            await File.WriteAllTextAsync(path, SshPrivateKeyFile.Format(key, "correct horse".AsSpan(), "test"));
            await File.WriteAllTextAsync(path + ".pub", key.PublicKey.ToOpenSshFormat("test") + "\n");
        }

        SshKeyImportResult? result = await _service.ImportKeyAsync(path);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.NeedsPassphrase);
        Assert.AreEqual("ED25519", result.Key.Type);
    }

    /// <summary>加了口令、又没有 .pub:公钥从 OpenSSH 容器的明文段读,照收。</summary>
    [TestMethod]
    public async Task Import_OfAPassphraseProtectedKeyWithoutPub_ReadsThePublicSection()
    {
        Directory.CreateDirectory(_external);
        string path = Path.Combine(_external, "locked_alone");
        string fingerprint;
        using (var key = InMemorySshSigner.GenerateEd25519())
        {
            await File.WriteAllTextAsync(path, SshPrivateKeyFile.Format(key, "pw".AsSpan(), "test"));
            fingerprint = key.PublicKey.Sha256Fingerprint;
        }

        SshKeyImportResult? result = await _service.ImportKeyAsync(path);

        Assert.IsTrue(result!.NeedsPassphrase);
        Assert.AreEqual(fingerprint, result.Key.Fingerprint);
    }

    /// <summary>.pub 与私钥不是同一把:拒收,一个文件都不留。</summary>
    [TestMethod]
    public async Task Import_WithAPubOfAnotherKey_IsRejected()
    {
        (string privatePath, string publicPath) = await CreateExternalKeyAsync("mismatch");
        (_, string otherPub) = await CreateExternalKeyAsync("someone_else");
        File.Copy(otherPub, publicPath, overwrite: true);

        InvalidDataException ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(privatePath));

        Assert.AreEqual(Strings.Format("KeySvc_ImportPublicKeyMismatch", "mismatch.pub", "mismatch"), ex.Message);
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "mismatch")));
        Assert.IsFalse(File.Exists(Path.Combine(_sshDir, "mismatch.pub")));
    }

    /// <summary>过时的传统加密 PEM(Proc-Type: 4,ENCRYPTED):库不读,说清是「不受支持」。</summary>
    [TestMethod]
    public async Task Import_OfALegacyEncryptedPem_IsRejectedAsUnsupported()
    {
        Directory.CreateDirectory(_external);
        string path = Path.Combine(_external, "legacy");
        await File.WriteAllTextAsync(path,
            "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: DES-EDE3-CBC,0123456789ABCDEF\n\nAAAA\n-----END RSA PRIVATE KEY-----\n");
        await File.WriteAllTextAsync(path + ".pub", "ssh-rsa AAAA legacy\n");

        InvalidDataException ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.ImportKeyAsync(path));

        Assert.AreEqual(Strings.Format("KeySvc_ImportUnsupportedKey", "legacy"), ex.Message);
    }

    // ———————————————————— 导出 ————————————————————

    [TestMethod]
    public async Task ExportPublicKey_WritesThePublicKeyLine()
    {
        SshKeyInfo key = await _service.GenerateKeyAsync("to_share");
        string target = Path.Combine(_external, "shared.pub");
        Directory.CreateDirectory(_external);

        await _service.ExportPublicKeyAsync(key, target);

        Assert.AreEqual(key.PublicKeyLine, (await File.ReadAllTextAsync(target)).Trim());
    }

    [TestMethod]
    public async Task OversizedPublicKey_IsSkippedByListAndRejectedByExport()
    {
        SshKeyInfo key = await _service.GenerateKeyAsync("large_pub");
        await File.WriteAllTextAsync(key.PrivateKeyPath + ".pub", key.PublicKeyLine!.PadRight(SshPrivateKeyFile.MaxFileBytes + 1));
        string destination = Path.Combine(_sshDir, "exported");

        Assert.IsEmpty(await _service.ListKeysAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            _service.ExportPublicKeyAsync(key with { PublicKeyLine = null }, destination));
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task ExportPrivateKey_CopiesTheFileAsItIs_AndOverwrites()
    {
        SshKeyInfo key = await _service.GenerateKeyAsync("backup_me");
        Directory.CreateDirectory(_external);
        string target = Path.Combine(_external, "backup_me");
        await File.WriteAllTextAsync(target, "old content that is longer than nothing at all");

        await _service.ExportPrivateKeyAsync(key, target);

        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(key.PrivateKeyPath), await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public async Task ExportPrivateKey_WhenTheKeyIsGone_Throws()
    {
        SshKeyInfo key = await _service.GenerateKeyAsync("gone");
        File.Delete(key.PrivateKeyPath);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => _service.ExportPrivateKeyAsync(key, Path.Combine(_sshDir, "gone_copy")));
    }

    /// <summary>导出的私钥只给属主读写;目标早就存在、权限还宽的,也要先收窄再写。</summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ExportPrivateKey_IsOwnerOnlyOnUnix()
    {
        SshKeyInfo key = await _service.GenerateKeyAsync("perm_export");
        Directory.CreateDirectory(_external);
        string fresh = Path.Combine(_external, "fresh");
        string existing = Path.Combine(_external, "existing");
        await File.WriteAllTextAsync(existing, "x");
        File.SetUnixFileMode(existing, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await _service.ExportPrivateKeyAsync(key, fresh);
        await _service.ExportPrivateKeyAsync(key, existing);

        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fresh));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(existing));
    }

    /// <summary>在 ~/.ssh 之外先造一对真密钥,再把它当作"外部密钥"导入。</summary>
    private async Task<(string PrivatePath, string PublicPath)> CreateExternalKeyAsync(string name)
    {
        var source = new SshKeyService(_external);
        await source.GenerateKeyAsync(name, SshKeyAlgorithm.Rsa, 2048);
        return (Path.Combine(_external, name), Path.Combine(_external, name + ".pub"));
    }
}
