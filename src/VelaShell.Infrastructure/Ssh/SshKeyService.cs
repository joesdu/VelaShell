using System.Text;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 基于 ~/.ssh 目录的密钥管理:以 *.pub 公钥文件枚举密钥对,
/// 类型与 SHA256 指纹从公钥 blob 解析(与 OpenSSH `ssh-keygen -lf` 口径一致)。
/// </summary>
/// <param name="sshDirectory">密钥目录;<see langword="null" /> 为 ~/.ssh。</param>
/// <param name="connectAgent">
/// 连 agent(参数是按连接指定的端点,<see langword="null" /> 为系统默认);<see langword="null" /> 走与「SSH Agent」认证相同的端点与 3 秒上限。
/// </param>
public sealed class SshKeyService(
    string? sshDirectory = null,
    Func<string?, CancellationToken, ValueTask<SshAgentClient>>? connectAgent = null) : ISshKeyService
{
    private readonly Func<string?, CancellationToken, ValueTask<SshAgentClient>> _connectAgent =
        connectAgent ?? (static (endpoint, ct) => SshAgentClient.ConnectAsync(SshAgentEndpoints.Resolve(endpoint), ct));

    private readonly string _sshDirectory = sshDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>枚举 ~/.ssh 目录下的密钥对,按公钥文件解析类型与指纹后返回。</summary>
    public Task<List<SshKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            var keys = new List<SshKeyInfo>();
            if (!Directory.Exists(_sshDirectory))
            {
                return keys;
            }
            foreach (string pubFile in Directory.EnumerateFiles(_sshDirectory, "*.pub").OrderBy(f => f))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(pubFile);
                string privatePath = Path.Combine(_sshDirectory, name);
                SshKeyInfo? info = await TryReadPublicKeyAsync(name, privatePath, pubFile, cancellationToken).ConfigureAwait(false);
                if (info is not null)
                {
                    keys.Add(info);
                }
            }
            return keys;
        }, cancellationToken);
    }

    /// <summary>
    /// 将外部私钥及其同名公钥复制到 <c>~/.ssh</c> 导入;目标同名已存在时返回 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>先验后抄,失败回滚。</b>旧实现是"能抄就抄":源私钥不存在时 <c>if (File.Exists(…))</c>
    /// 直接跳过复制,却照样返回一条 <c>Unknown</c> 条目 —— 界面显示"已导入 xxx",
    /// 而 <c>~/.ssh</c> 里什么都没多。挑中一个随便的文本文件同样一路成功。
    /// </para>
    /// <para>
    /// <b>没有 .pub 就从私钥文件里读出公钥、写一份。</b><see cref="ListKeysAsync" /> 是按 <c>*.pub</c> 枚举的,
    /// 只抄私钥的话列表里一条都看不到。OpenSSH 容器的公钥段与 PuTTY <c>.ppk</c> 的 <c>Public-Lines</c> 是明文,
    /// 私钥加了密也读得出(<see cref="SshPrivateKeyFile.TryReadPublicKey" />);只有加密的 PKCS#8 这类
    /// 公钥也在密文里的,才要求连 <c>.pub</c> 一起选。曾经一律要求 <c>.pub</c>,而 PuTTY 用户手里通常只有一个 <c>.ppk</c>。
    /// </para>
    /// <para>
    /// <b>认格式交给库。</b>曾经只看首行是不是 <c>-----BEGIN … PRIVATE KEY</c>,库能读的 <c>.ppk</c> 被当成「不是私钥」挡在门外。
    /// </para>
    /// <para>
    /// <b>认完格式还要整把解析一遍</b>(<see cref="ValidatePrivateKey" />)。只认格式时,头尾对、中间截断或改坏了的文件
    /// 照样导进来,要等到连接时才报「私钥格式不对」。加了口令的不问口令:库在派生密钥之前就报「要口令」,
    /// 那说明容器结构是好的,照收并告诉界面;坏的、不受支持的(过时的 PEM 加密、陌生的加密算法)拒收。
    /// </para>
    /// <para>
    /// <b>私钥权限。</b>生成路径一直会设 0600,导入路径以前不设 —— 而 OpenSSH 对
    /// 组/其他可读的私钥直接拒用(<c>UNPROTECTED PRIVATE KEY FILE</c>)。
    /// </para>
    /// </remarks>
    /// <param name="sourcePrivateKeyPath">源私钥路径(选中 <c>.pub</c> 时自动换成同名私钥)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入后的密钥与它要不要口令;同名已存在时为 <see langword="null" />。</returns>
    /// <exception cref="FileNotFoundException">源私钥不存在,或 <c>.pub</c> 不存在而私钥文件里又读不出公钥。</exception>
    /// <exception cref="InvalidDataException">源文件不是私钥、私钥损坏或不受支持,或公钥无法解析、与私钥不配对。</exception>
    public async Task<SshKeyImportResult?> ImportKeyAsync(string sourcePrivateKeyPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_sshDirectory);
        string name = Path.GetFileName(sourcePrivateKeyPath);
        if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
            sourcePrivateKeyPath = sourcePrivateKeyPath[..^4];
        }
        string sourcePub = sourcePrivateKeyPath + ".pub";
        string targetPrivate = Path.Combine(_sshDirectory, name);
        string targetPub = targetPrivate + ".pub";
        if (File.Exists(targetPrivate) || File.Exists(targetPub))
        {
            return null;
        }

        // ——— 先验:任何一条不过就当场退出,此时 ~/.ssh 一个字节都没动过 ———
        if (!File.Exists(sourcePrivateKeyPath))
        {
            throw new FileNotFoundException(
                Strings.Format("KeySvc_ImportPrivateKeyMissing", sourcePrivateKeyPath), sourcePrivateKeyPath);
        }
        if (await ReadPrivateKeyTextAsync(sourcePrivateKeyPath, cancellationToken).ConfigureAwait(false) is not { } privateText)
        {
            throw new InvalidDataException(Strings.Format("KeySvc_ImportNotAPrivateKey", name));
        }
        (bool needsPassphrase, SshPublicKey? keyPublic) = await Task.Run(
            () => ValidatePrivateKey(privateText, name, cancellationToken), cancellationToken).ConfigureAwait(false);
        string publicText;
        SshPublicKey importedPublic;
        if (File.Exists(sourcePub))
        {
            // .pub 要真是公钥,而且与私钥是同一把:拼错的一对导进来,列表里显示的指纹是 .pub 的,
            // 认证用的却是私钥 —— 服务端只回一句「不接受这把公钥」,用户无从查起。
            try
            {
                publicText = await SshPrivateKeyFile.ReadKeyTextAsync(sourcePub, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException(Strings.Format("KeySvc_ImportBadPublicKey", Path.GetFileName(sourcePub)), ex);
            }
            if (!SshPublicKey.TryParse(publicText.Trim(), out SshPublicKey? filePublic))
            {
                throw new InvalidDataException(Strings.Format("KeySvc_ImportBadPublicKey", Path.GetFileName(sourcePub)));
            }
            if (keyPublic is not null && !keyPublic.Blob.Span.SequenceEqual(filePublic.Blob.Span))
            {
                throw new InvalidDataException(
                    Strings.Format("KeySvc_ImportPublicKeyMismatch", Path.GetFileName(sourcePub), name));
            }
            importedPublic = filePublic;
        }
        else
        {
            if (keyPublic is null)
            {
                throw new FileNotFoundException(
                    Strings.Format("KeySvc_ImportPublicKeyMissing", Path.GetFileName(sourcePub)), sourcePub);
            }
            importedPublic = keyPublic;
            publicText = keyPublic.ToOpenSshFormat(name) + Environment.NewLine;
        }

        var keyInfo = new SshKeyInfo(name, DescribeType(importedPublic), importedPublic.Sha256Fingerprint, targetPrivate, publicText.Trim());
        if (!await WriteNewKeyAsync(targetPrivate, privateText, isPrivate: true, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            if (await WriteNewKeyAsync(targetPub, publicText, isPrivate: false, cancellationToken).ConfigureAwait(false))
            {
                return new SshKeyImportResult(keyInfo, needsPassphrase);
            }
        }
        catch
        {
            TryDelete(targetPrivate);
            throw;
        }
        TryDelete(targetPrivate);
        return null;
    }

    private static async Task<bool> WriteNewKeyAsync(string path, string text, bool isPrivate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // UTF-16 的注释换成 UTF-8 可能超出重新读取时的字节上限,这种情况保留 BOM 与 UTF-16。
        Encoding encoding = new UTF8Encoding(false);
        if (encoding.GetByteCount(text) > SshPrivateKeyFile.MaxFileBytes)
        {
            encoding = Encoding.Unicode;
            if (encoding.Preamble.Length + encoding.GetByteCount(text) > SshPrivateKeyFile.MaxFileBytes)
            {
                throw new InvalidDataException(Strings.Format(isPrivate ? "KeySvc_ImportNotAPrivateKey" : "KeySvc_ImportBadPublicKey", Path.GetFileName(path)));
            }
        }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (isPrivate && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        FileStream stream;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        try
        {
            using (stream)
            {
                using StreamWriter writer = new(stream, encoding);
                await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// 读出私钥文件的文本;认不出是私钥(或读不了)时为 <see langword="null" />。够用来挡住"选错文件"。
    /// </summary>
    /// <remarks>
    /// 格式由库认(<see cref="SshPrivateKeyFile.DetectFormat" />:OpenSSH、PKCS#1、PKCS#8、SEC1、PuTTY <c>.ppk</c>),
    /// 不做完整解析 —— 私钥可能加了密,真解析要口令。这足以把 README、id_rsa.pub、截图挡在门外。
    /// </remarks>
    private static async Task<string?> ReadPrivateKeyTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string text = await SshPrivateKeyFile.ReadKeyTextAsync(path, cancellationToken).ConfigureAwait(false);
            return SshPrivateKeyFile.DetectFormat(text) == SshPrivateKeyFormat.Unknown ? null : text;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 用 SSH 库把私钥整把解析一遍(不给口令),交回它要不要口令、以及能拿到的公钥(拿不到为 null)。
    /// </summary>
    /// <remarks>
    /// 口令留空时库在派生密钥之前就报 <see cref="SshFailureReason.KeyPassphraseRequired" /> —— 不会白跑一遍 KDF;
    /// 能走到那一步说明容器结构读通了,这就是「格式对、需口令」。加了口令的,公钥从明文段读
    /// (OpenSSH 容器、<c>.ppk</c>),加密的 PKCS#8 公钥也在密文里,读不出就交给 .pub。
    /// 出错时不转述库的原话(那是给日志看的中文),按失败分类给界面文案。
    /// </remarks>
    /// <exception cref="InvalidDataException">私钥损坏,或格式 / 加密方式不受支持。</exception>
    internal static (bool NeedsPassphrase, SshPublicKey? PublicKey) ValidatePrivateKey(
        string privateText, string name, CancellationToken cancellationToken)
    {
        try
        {
            using InMemorySshSigner signer = SshPrivateKeyFile.Parse(privateText, ReadOnlySpan<char>.Empty, null, cancellationToken);
            return (false, signer.PublicKey);
        }
        catch (SshPrivateKeyException ex) when (ex.NeedsPassphrase)
        {
            return (true, SshPrivateKeyFile.TryReadPublicKey(privateText, out SshPublicKey? publicKey) ? publicKey : null);
        }
        catch (SshPrivateKeyException ex) when (ex.Reason == SshFailureReason.Unsupported)
        {
            throw new InvalidDataException(Strings.Format("KeySvc_ImportUnsupportedKey", name), ex);
        }
        catch (SshPrivateKeyException ex)
        {
            throw new InvalidDataException(Strings.Format("KeySvc_ImportInvalidPrivateKey", name), ex);
        }
    }

    /// <inheritdoc />
    public async Task ExportPublicKeyAsync(SshKeyInfo key, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        string line = key.PublicKeyLine
                      ?? (await SshPrivateKeyFile.ReadKeyTextAsync(key.PrivateKeyPath + ".pub", cancellationToken).ConfigureAwait(false)).Trim();
        await File.WriteAllTextAsync(destinationPath, line + "\n", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Unix 上先收窄、再写内容:目标已存在时先把它改成 0600,新建的直接以 0600 建 —— 不留一段
    /// 「私钥已经写进去、文件却还是 0644」的窗口。用的是字节流原样复制,不经解析,带口令的照旧带着口令。
    /// </para>
    /// <para>
    /// Windows 上不改 ACL:文件继承所在目录的权限(「下载」与用户目录本来就只给本人),
    /// 选到共享目录是用户自己的决定,导出前的确认框已经说过。
    /// </para>
    /// </remarks>
    public async Task ExportPrivateKeyAsync(SshKeyInfo key, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        if (!File.Exists(key.PrivateKeyPath))
        {
            throw new FileNotFoundException(
                Strings.Format("KeySvc_ImportPrivateKeyMissing", key.PrivateKeyPath), key.PrivateKeyPath);
        }
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            if (File.Exists(destinationPath))
            {
                File.SetUnixFileMode(destinationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var source = new FileStream(key.PrivateKeyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = new FileStream(destinationPath, options);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把私钥收成仅属主可读写。OpenSSH 对更宽的权限直接拒用。</summary>
    private static void ApplyPrivateKeyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚是尽力而为:清不掉也不该把原始失败原因盖掉。
        }
    }

    /// <summary>
    /// 在 ~/.ssh 目录生成指定名称的密钥对(默认 Ed25519),并写出 OpenSSH 格式的私钥与公钥。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>私钥写 OpenSSH 格式</b>(<c>-----BEGIN OPENSSH PRIVATE KEY-----</c>)。
    /// 这一条曾经是硬性的:上一版底层库的解析器只认这一种,<c>ExportRSAPrivateKeyPem()</c> 产出的
    /// PKCS#1(<c>-----BEGIN RSA PRIVATE KEY-----</c>)与 PKCS#8 都会被判 "Unsupported format"
    /// 而当作无可用凭据【跳过】—— 认证遂以 "These methods were skipped: publickey" 失败,用户表现为
    /// 用本应用生成的密钥怎么都登不上。换到 VelaShell.Ssh 之后这三种格式都原生读得出
    /// (见 <c>LegacyPrivateKeyFormatTests</c>),但生成端仍只写 OpenSSH 格式:Ed25519 在 BCL 里
    /// 根本没有 PEM 导出,而这也正是今天 <c>ssh-keygen</c> 的产物,拷到别处照样能用。
    /// </para>
    /// <para>
    /// <b>生成与写出都交给 SSH 库</b>(<see cref="InMemorySshSigner" /> 的 <c>Generate*</c> 与
    /// <see cref="SshPrivateKeyFile.Format" />)。曾经宿主手写了一份 OpenSSH 私钥容器、拿 BouncyCastle 现造 Ed25519,
    /// 中间导出的 <c>RSAParameters</c> / <c>ECParameters</c> 也不清零;库的写出侧用完即清。
    /// </para>
    /// <para>
    /// <b>默认给 Ed25519,而不是 RSA。</b>OpenSSH 自 6.5(2014)起支持,`ssh-keygen` 自 9.5(2023)
    /// 起也已默认给它:私钥 32 字节、生成几乎不耗时(RSA 4096 要秒级),强度还不比 RSA 4096 差。
    /// 留着 <see cref="SshKeyAlgorithm.Rsa" /> 只是为了对付把 <c>ssh-rsa</c> 写死进白名单的老堡垒机。
    /// </para>
    /// </remarks>
    public Task<SshKeyInfo> GenerateKeyAsync(
        string name,
        SshKeyAlgorithm algorithm = SshKeyAlgorithm.Ed25519,
        int bits = 0,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(Strings.Get("KeySvc_InvalidName"), nameof(name));
            }
            Directory.CreateDirectory(_sshDirectory);
            string privatePath = Path.Combine(_sshDirectory, name);
            string publicPath = privatePath + ".pub";
            if (File.Exists(privatePath) || File.Exists(publicPath))
            {
                throw new IOException(Strings.Format("KeySvc_AlreadyExists", name));
            }
            string comment = $"velashell@{Environment.MachineName}";
            // ECDSA 只有 256 / 384 / 521 三条曲线,别的位数由库抛 ArgumentOutOfRangeException ——
            // SSH 给别的曲线连个能写进公钥行的名字都没有。RSA 宿主默认 4096,比 ssh-keygen 的 3072 保守一档。
            using InMemorySshSigner key = algorithm switch
            {
                SshKeyAlgorithm.Ed25519 => InMemorySshSigner.GenerateEd25519(),
                SshKeyAlgorithm.Ecdsa => InMemorySshSigner.GenerateEcdsa(bits > 0 ? bits : 256),
                SshKeyAlgorithm.Rsa => InMemorySshSigner.GenerateRsa(bits > 0 ? bits : 4096),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
            };

            File.WriteAllText(privatePath, SshPrivateKeyFile.Format(key, comment: comment));
            ApplyPrivateKeyPermissions(privatePath);
            SshPublicKey publicKey = key.PublicKey;
            string publicLine = publicKey.ToOpenSshFormat(comment);
            File.WriteAllText(publicPath, publicLine + Environment.NewLine);
            return new SshKeyInfo(name, DescribeType(publicKey), publicKey.Sha256Fingerprint, privatePath, publicLine);
        }, cancellationToken);
    }

    /// <summary>删除指定名称密钥对的私钥与公钥文件(存在则删除)。</summary>
    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            string privatePath = Path.Combine(_sshDirectory, name);
            string publicPath = privatePath + ".pub";
            if (File.Exists(privatePath))
            {
                File.Delete(privatePath);
            }
            if (File.Exists(publicPath))
            {
                File.Delete(publicPath);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 公钥行按 <c>类型 base64 注释</c> 拼出来,与 <c>ssh-add -L</c> 的输出同一格式 ——
    /// 连接配置里「只转发指定密钥」存的就是它。
    /// </remarks>
    public async Task<List<SshKeyInfo>> ListAgentKeysAsync(string? agentEndpoint = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SshAgentClient agent = await _connectAgent(agentEndpoint, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SshAgentIdentity> identities =
                await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

            List<SshKeyInfo> keys = [];
            foreach (SshAgentIdentity identity in identities)
            {
                SshPublicKey key = identity.PublicKey;
                keys.Add(new SshKeyInfo(
                    identity.Comment, DescribeType(key), key.Sha256Fingerprint, "", key.ToOpenSshFormat(identity.Comment)));
            }
            return keys;
        }
        catch (Exception ex) when (ex is SshAgentException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // agent 没在跑(Windows 上服务默认是停着的)是常态,不是错误。
            return [];
        }
    }

    /// <remarks>
    /// 解析、指纹、位数都交给 SSH 库的 <see cref="SshPublicKey" /> —— 宿主不另写一份 blob 解析。
    /// 库认不出的类型(早已弃用的 DSA 之类)不列出:列出来也连不上。
    /// </remarks>
    private static async Task<SshKeyInfo?> TryReadPublicKeyAsync(string name, string privatePath, string pubFile, CancellationToken cancellationToken)
    {
        try
        {
            string line = (await SshPrivateKeyFile.ReadKeyTextAsync(pubFile, cancellationToken).ConfigureAwait(false)).Trim();
            return SshPublicKey.TryParse(line, out SshPublicKey? key)
                ? new(name, DescribeType(key), key.Sha256Fingerprint, privatePath, line)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>给人看的类型与位数,如 <c>RSA 4096</c>、<c>ED25519</c>、<c>ECDSA 256</c>。证书照原样显示它的类型串。</summary>
    private static string DescribeType(SshPublicKey key) =>
        key.IsCertificate
            ? key.KeyType
            : key.PlainKeyType switch
            {
                "ssh-rsa" => $"RSA {key.KeyBits}",
                "ssh-ed25519" => "ED25519",
                // FIDO / U2F 安全密钥(经 agent 用),与 ssh-add -l 的叫法一致。
                "sk-ssh-ed25519@openssh.com" => "ED25519-SK",
                "sk-ecdsa-sha2-nistp256@openssh.com" => "ECDSA-SK",
                _ when key.PlainKeyType.StartsWith("ecdsa-", StringComparison.Ordinal) => $"ECDSA {key.KeyBits}",
                _ => key.PlainKeyType,
            };
}
