using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 基于 ~/.ssh 目录的密钥管理:以 *.pub 公钥文件枚举密钥对,
/// 类型与 SHA256 指纹从公钥 blob 解析(与 OpenSSH `ssh-keygen -lf` 口径一致)。
/// </summary>
/// <param name="sshDirectory">密钥目录;<see langword="null" /> 为 ~/.ssh。</param>
/// <param name="connectAgent">连本机 agent;<see langword="null" /> 走与「SSH Agent」认证相同的端点与 3 秒上限。</param>
public sealed class SshKeyService(
    string? sshDirectory = null,
    Func<CancellationToken, ValueTask<SshAgentClient>>? connectAgent = null) : ISshKeyService
{
    private readonly Func<CancellationToken, ValueTask<SshAgentClient>> _connectAgent =
        connectAgent ?? SshConnectionAssembler.ConnectLocalAgentAsync;

    private readonly string _sshDirectory = sshDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>枚举 ~/.ssh 目录下的密钥对,按公钥文件解析类型与指纹后返回。</summary>
    public Task<List<SshKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
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
                SshKeyInfo? info = TryParsePublicKey(name, privatePath, pubFile);
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
    /// <b>没有 .pub 就明确拒绝。</b><see cref="ListKeysAsync" /> 是按 <c>*.pub</c> 枚举的,
    /// 只导私钥的话文件确实抄进去了,列表里却一条都看不到 —— 用户看到"导入成功"随后
    /// 密钥消失,只会以为程序把它弄丢了。与其如此,不如当场说清要连 <c>.pub</c> 一起选。
    /// </para>
    /// <para>
    /// <b>私钥权限。</b>生成路径一直会设 0600,导入路径以前不设 —— 而 OpenSSH 对
    /// 组/其他可读的私钥直接拒用(<c>UNPROTECTED PRIVATE KEY FILE</c>)。
    /// </para>
    /// </remarks>
    /// <param name="sourcePrivateKeyPath">源私钥路径(选中 <c>.pub</c> 时自动换成同名私钥)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入后的密钥;同名已存在时为 <see langword="null" />。</returns>
    /// <exception cref="FileNotFoundException">源私钥或其 <c>.pub</c> 不存在。</exception>
    /// <exception cref="InvalidDataException">源文件不是私钥,或公钥无法解析。</exception>
    public async Task<SshKeyInfo?> ImportKeyAsync(string sourcePrivateKeyPath, CancellationToken cancellationToken = default)
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
        if (!File.Exists(sourcePub))
        {
            throw new FileNotFoundException(
                Strings.Format("KeySvc_ImportPublicKeyMissing", Path.GetFileName(sourcePub)), sourcePub);
        }
        if (!await LooksLikePrivateKeyAsync(sourcePrivateKeyPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(Strings.Format("KeySvc_ImportNotAPrivateKey", name));
        }

        // ——— 再抄:两份一起,任一失败就把已抄的清掉,不留半套 ———
        try
        {
            File.Copy(sourcePrivateKeyPath, targetPrivate);
            ApplyPrivateKeyPermissions(targetPrivate);
            File.Copy(sourcePub, targetPub);
        }
        catch
        {
            TryDelete(targetPrivate);
            TryDelete(targetPub);
            throw;
        }

        // ——— 最后按真实解析结果回报。解析不出来说明 .pub 不是公钥,一并回滚 ———
        List<SshKeyInfo> keys = await ListKeysAsync(cancellationToken).ConfigureAwait(false);
        if (keys.FirstOrDefault(k => k.Name == name) is { } imported)
        {
            return imported;
        }
        TryDelete(targetPrivate);
        TryDelete(targetPub);
        throw new InvalidDataException(Strings.Format("KeySvc_ImportBadPublicKey", Path.GetFileName(sourcePub)));
    }

    /// <summary>
    /// 粗看一眼是不是私钥:够用来挡住"选错文件"。
    /// </summary>
    /// <remarks>
    /// 只读首行,不做完整解析 —— 私钥格式有 OpenSSH、PKCS#1、PKCS#8 好几种,而且可能加密,
    /// 真解析要密码短语。这里只要求它有 PEM 头,足以把 README、id_rsa.pub、截图挡在门外。
    /// </remarks>
    private static async Task<bool> LooksLikePrivateKeyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? first = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return first is not null
                   && first.StartsWith("-----BEGIN", StringComparison.Ordinal)
                   && first.Contains("PRIVATE KEY", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
            (string privatePem, byte[] blob, string algorithmName, string type) = algorithm switch
            {
                SshKeyAlgorithm.Ed25519 => CreateEd25519(comment),
                SshKeyAlgorithm.Ecdsa => CreateEcdsa(bits > 0 ? bits : 256, comment),
                SshKeyAlgorithm.Rsa => CreateRsa(bits > 0 ? bits : 4096, comment),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
            };

            File.WriteAllText(privatePath, privatePem);
            ApplyPrivateKeyPermissions(privatePath);
            string publicLine = $"{algorithmName} {Convert.ToBase64String(blob)} {comment}";
            File.WriteAllText(publicPath, publicLine + Environment.NewLine);
            return new SshKeyInfo(name, type, Fingerprint(blob), privatePath, publicLine);
        }, cancellationToken);
    }

    /// <summary>
    /// 现造一把 Ed25519:32 字节随机种子 → 由种子导出公钥。
    /// </summary>
    /// <remarks>
    /// 种子用 BCL 的 <see cref="RandomNumberGenerator" /> 取,标量乘法交给 BouncyCastle ——
    /// .NET 11 的 BCL 至今没有独立的 Ed25519(只有 <c>CompositeMLDsaAlgorithm</c> 里那个复合标识符),
    /// 而 BouncyCastle 本就是 VelaShell.Ssh 的依赖、早已在输出目录里,这里只是把它抬成显式引用。
    /// 自己手写曲线运算不在考虑之列:那是能把私钥悄悄写废的地方。
    /// </remarks>
    private static (string Pem, byte[] Blob, string AlgorithmName, string Type) CreateEd25519(string comment)
    {
        byte[] seed = RandomNumberGenerator.GetBytes(Ed25519PrivateKeyParameters.KeySize);
        try
        {
            byte[] publicKey = new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();
            return (OpenSshPrivateKey.SerializeEd25519(seed, publicKey, comment),
                    OpenSshPrivateKey.BuildEd25519PublicBlob(publicKey),
                    "ssh-ed25519",
                    "ED25519");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed); // 种子已经进了 PEM 文本,这份副本没必要再留在堆上
        }
    }

    /// <summary>现造一把 ECDSA。<paramref name="bits" /> 取 256 / 384 / 521,其余值抛。</summary>
    /// <remarks>
    /// 曲线名与坐标定长走 <see cref="OpenSshPrivateKey.DescribeCurve" /> —— 与导入转换共用一张表。
    /// 这里不像 RSA 那样接受任意位数:SSH 只定义了这三条 NIST 曲线的算法名,
    /// 给别的曲线连个能写进公钥行的名字都没有。
    /// </remarks>
    private static (string Pem, byte[] Blob, string AlgorithmName, string Type) CreateEcdsa(int bits, string comment)
    {
        (string? sshName, string? curveName, int fieldLength) = OpenSshPrivateKey.DescribeCurve(bits);
        if (sshName is null)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, null);
        }
        ECCurve curve = bits switch
        {
            256 => ECCurve.NamedCurves.nistP256,
            384 => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521
        };
        using var ecdsa = ECDsa.Create(curve);
        ECParameters parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        return (OpenSshPrivateKey.SerializeEcdsa(parameters, sshName, curveName!, fieldLength, comment),
                OpenSshPrivateKey.BuildEcdsaPublicBlob(parameters.Q, sshName, curveName!, fieldLength),
                sshName,
                $"ECDSA {bits}");
    }

    /// <summary>现造一把 RSA。留给不认 Ed25519 的老服务端。</summary>
    private static (string Pem, byte[] Blob, string AlgorithmName, string Type) CreateRsa(int bits, string comment)
    {
        using var rsa = RSA.Create(bits);
        RSAParameters parameters = rsa.ExportParameters(true);
        return (OpenSshPrivateKey.SerializeRsa(parameters, comment),
                BuildRsaPublicBlob(parameters),
                "ssh-rsa",
                $"RSA {bits}");
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
    public async Task<List<SshKeyInfo>> ListAgentKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SshAgentClient agent = await _connectAgent(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SshAgentIdentity> identities =
                await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

            List<SshKeyInfo> keys = [];
            foreach (SshAgentIdentity identity in identities)
            {
                byte[] blob = identity.PublicKey.Blob.ToArray();
                string algorithm = identity.PublicKey.KeyType;
                string line = $"{algorithm} {Convert.ToBase64String(blob)}";
                if (identity.Comment.Length > 0)
                {
                    line += " " + identity.Comment;
                }
                keys.Add(new SshKeyInfo(identity.Comment, DescribeType(algorithm, blob), Fingerprint(blob), "", line));
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

    private static SshKeyInfo? TryParsePublicKey(string name, string privatePath, string pubFile)
    {
        try
        {
            string line = File.ReadAllText(pubFile).Trim();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return null;
            }
            byte[] blob = Convert.FromBase64String(parts[1]);
            return new(name, DescribeType(parts[0], blob), Fingerprint(blob), privatePath, line);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            return null;
        }
    }

    private static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    private static string DescribeType(string algorithm, byte[] blob)
    {
        return algorithm switch
        {
            "ssh-rsa" => $"RSA {TryGetRsaBits(blob)}",
            "ssh-ed25519" => "ED25519",
            "ecdsa-sha2-nistp256" => "ECDSA 256",
            "ecdsa-sha2-nistp384" => "ECDSA 384",
            "ecdsa-sha2-nistp521" => "ECDSA 521",
            "ssh-dss" => "DSA",
            _ => algorithm
        };
    }

    /// <summary>从 ssh-rsa 公钥 blob(string algo, mpint e, mpint n)读取模数位数。</summary>
    private static int TryGetRsaBits(byte[] blob)
    {
        try
        {
            int offset = 0;
            ReadChunk(blob, ref offset); // algorithm name
            ReadChunk(blob, ref offset); // exponent
            byte[] modulus = ReadChunk(blob, ref offset);
            int length = modulus.Length;
            if (length > 0 && modulus[0] == 0)
            {
                length--; // mpint 前导零
            }
            return length * 8;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static byte[] ReadChunk(byte[] blob, ref int offset)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset, 4));
        offset += 4;
        byte[] chunk = blob.AsSpan(offset, length).ToArray();
        offset += length;
        return chunk;
    }

    /// <summary>构造 OpenSSH ssh-rsa 公钥 blob:string "ssh-rsa" ‖ mpint e ‖ mpint n。</summary>
    private static byte[] BuildRsaPublicBlob(RSAParameters parameters)
    {
        using var stream = new MemoryStream();
        WriteChunk(stream, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteChunk(stream, ToMpint(parameters.Exponent!));
        WriteChunk(stream, ToMpint(parameters.Modulus!));
        return stream.ToArray();
    }

    private static byte[] ToMpint(byte[] value)
    {
        // 最高位为 1 时补前导零,保持无符号语义。
        return value.Length > 0 && (value[0] & 0x80) != 0
                   ? [0, .. value]
                   : value;
    }

    private static void WriteChunk(MemoryStream stream, byte[] data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        stream.Write(lengthBytes);
        stream.Write(data);
    }
}
