// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.key  openssh-key-v1 容器格式
//   RFC 8017 §A.1.2       PKCS#1 RSAPrivateKey
//   RFC 5958              PKCS#8
//   RFC 5915              SEC1 EC 私钥
//   RFC 8410              PKCS#8 里的 Ed25519
//   行为规格:             velashell-docs/zh/ssh/design/architecture.md §8 第 5 项

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>从文件或文本里读私钥。</summary>
public static class SshPrivateKeyFile
{
    private const string OpenSshMagic = "openssh-key-v1\0";

    /// <summary>可导入密钥文件的最大字节数。</summary>
    public const int MaxFileBytes = BoundedFileReader.MaxBytes;

    /// <summary>KDF 轮数的上限。</summary>
    /// <remarks>
    /// <para>
    /// 轮数来自文件本身，也就是来自不可信输入。<c>ssh-keygen</c> 默认写 16，
    /// <c>-a</c> 调到几百已经算重的了；这个上限是给「文件被改过」准备的。
    /// </para>
    /// <para>
    /// 曾经是一百万：实测一轮十几毫秒，一百万轮是好几个小时 —— 上限只是把「几天」变成了「几小时」，
    /// 而这段计算是同步的、中途停不下来。4096 轮是默认值的 256 倍，在最慢的机器上也是一分钟以内。
    /// </para>
    /// </remarks>
    internal const uint MaxKdfRounds = 4096;

    /// <summary>加密 PKCS#8 的 PBKDF2（以及 PBES1 / PKCS#12 PBE）迭代数上限。</summary>
    /// <remarks>
    /// <para>
    /// 迭代数同样来自文件。<b>.NET 导入加密 PKCS#8 时不设上限</b>（实测 300 万次照常导入）：
    /// 一个被改成 <c>int.MaxValue</c> 的文件按每秒约七百万次算要跑五分钟，同步、停不下来，
    /// 而且下面「先按 RSA 试、再按 ECDSA 试」可能算两遍。
    /// </para>
    /// <para>
    /// 常见的取值是 OpenSSL 的 2048、OWASP 建议的 60 万；一千万是后者的十几倍，最坏也就一两秒。
    /// </para>
    /// </remarks>
    internal const int MaxPkcs8Iterations = 10_000_000;

    /// <summary>加密数据不超过这么多字节的 PKCS#8 先按 ECDSA 试。</summary>
    /// <remarks>
    /// 加密 PKCS#8 里看不出钥的类型（算法标识在密文里），只能逐个试，而每试一次都要把 KDF 整个跑一遍。
    /// 椭圆曲线钥连 P-521 带公钥也不到 260 字节，RSA 最小的 512 位钥也有三百四十多字节 ——
    /// 按大小排个先后，口令对的时候就只算一遍。
    /// </remarks>
    private const int EcFirstEncryptedBytes = 320;

    /// <summary>认一下这段 PEM 是什么格式。</summary>
    public static SshPrivateKeyFormat DetectFormat(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);

        if (pem.Length > BoundedFileReader.MaxBytes)
        {
            return SshPrivateKeyFormat.Unknown;
        }
        if (PuttyPrivateKeyFile.IsPuttyKey(pem))
        {
            return SshPrivateKeyFormat.Putty;
        }
        if (pem.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.OpenSsh;
        }
        if (pem.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Pkcs8Encrypted;
        }
        if (pem.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Pkcs8;
        }
        if (pem.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Pkcs1Rsa;
        }
        if (pem.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal))
        {
            return SshPrivateKeyFormat.Sec1Ec;
        }

        return SshPrivateKeyFormat.Unknown;
    }

    /// <summary>不用口令读出私钥文件里的公钥。</summary>
    /// <param name="pem">私钥文件的文本。</param>
    /// <param name="publicKey">读出的公钥。</param>
    /// <returns>
    /// 读得出为 <see langword="true"/>。加密的 PKCS#8 与传统加密 PEM 不带明文公钥，读不出；
    /// 认不出的格式、内容不完整的文件同样返回 <see langword="false"/>，不抛。
    /// </returns>
    /// <remarks>
    /// <para>
    /// OpenSSH 容器的公钥段与 <c>.ppk</c> 的 <c>Public-Lines</c> 本来就是明文，私钥加了密也读得出；
    /// 未加密的 PKCS#1 / SEC1 / PKCS#8 由私钥导出。用处是导入只有私钥、没有 <c>.pub</c> 的文件 ——
    /// PuTTY 用户手里通常只有一个 <c>.ppk</c>。
    /// </para>
    /// <para>
    /// 明文的那一份<b>没有</b>与私钥核对过（核对要先解密）：拿它显示指纹、写 <c>.pub</c> 可以；
    /// 认证用的是 <see cref="Parse(string, ReadOnlySpan{char}, string?, CancellationToken)"/> 解出的那一把，那里会核对。
    /// </para>
    /// </remarks>
    public static bool TryReadPublicKey(string pem, [NotNullWhen(true)] out SshPublicKey? publicKey)
    {
        ArgumentNullException.ThrowIfNull(pem);
        publicKey = null;

        try
        {
            byte[]? blob = DetectFormat(pem) switch
            {
                SshPrivateKeyFormat.OpenSsh => ReadOpenSshPublicSection(pem),
                SshPrivateKeyFormat.Putty => PuttyPrivateKeyFile.ReadPublicBlob(pem),
                SshPrivateKeyFormat.Pkcs8 or SshPrivateKeyFormat.Pkcs1Rsa or SshPrivateKeyFormat.Sec1Ec => DerivePublicBlob(pem),
                _ => null,   // 加密的 PKCS#8：公钥也在密文里
            };
            if (blob is null)
            {
                return false;
            }

            publicKey = SshPublicKey.Decode(blob);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 文件是外来输入：读不出就是读不出，不分原因（与 Parse 把它们归成 KeyFormatInvalid 同理）。
            return false;
        }
    }

    /// <summary>OpenSSH 容器的公钥段（明文）。不加密的容器里私钥区也是明文，读过就清零。</summary>
    private static byte[] ReadOpenSshPublicSection(string pem)
    {
        OpenSshContainer container = ReadOpenSshContainer(pem, where: "");
        CryptographicOperations.ZeroMemory(container.PrivateSection);
        return container.PublicSection;
    }

    /// <summary>写私钥文件时口令派生的默认轮数（与 <c>ssh-keygen</c> 一致）。</summary>
    public const int DefaultKdfRounds = 16;

    /// <summary>写私钥文件时用的加密算法（<c>ssh-keygen</c> 的默认）。</summary>
    private const string WriteCipher = "aes256-ctr";

    /// <summary>把一把私钥写成 OpenSSH 私钥文件（<c>-----BEGIN OPENSSH PRIVATE KEY-----</c>），可以带口令。</summary>
    /// <param name="key">私钥。</param>
    /// <param name="passphrase">口令；空就不加密。调用方可以把它放在自己的 <c>char[]</c> 里，用完自己清零。</param>
    /// <param name="comment">注释（写在私钥区里，<c>ssh-keygen -l</c> 显示的那一列）。</param>
    /// <param name="kdfRounds"><c>bcrypt_pbkdf</c> 的轮数（只在有口令时用），1–<see cref="MaxKdfRounds"/>。</param>
    /// <returns>可以直接写进文件的文本（<c>\n</c> 换行）。文件要只给属主读写（Unix 上 0600），那是调用方的事。</returns>
    /// <exception cref="ArgumentOutOfRangeException">轮数不在范围内。</exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/04 §4.6〕<c>openssh-key-v1</c> 容器，与读取一侧同一份规格：有口令时
    /// <c>bcrypt</c> KDF（16 字节随机盐）+ <c>aes256-ctr</c>，没有时 <c>none</c> / <c>none</c>；私钥区以两个相同的随机校验字开头，
    /// 末尾按分组（加密时 16、否则 8）填充 1、2、3……。正文按 70 列折行。
    /// </para>
    /// <para>
    /// 写出侧只装配现成的 KDF 与加密：对不对，用例拿真 <c>ssh-keygen</c> 读本库写的文件来验，而不是本库自己读自己。
    /// 曾经库只能读、不能写，宿主手写了一份只能写<b>未加密</b>私钥的容器。中间的明文私钥区、派生出的密钥都清零。
    /// </para>
    /// </remarks>
    public static string Format(
        InMemorySshSigner key, ReadOnlySpan<char> passphrase = default, string comment = "", int kdfRounds = DefaultKdfRounds)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentOutOfRangeException.ThrowIfLessThan(kdfRounds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kdfRounds, (int)MaxKdfRounds);

        bool encrypt = !passphrase.IsEmpty;
        int blockBytes = encrypt ? 16 : 8;

        // 私钥区：校验字 ×2 ‖ 钥 ‖ 注释 ‖ 填充。
        ArrayBufferWriter<byte> section = new(16 * 1024);   // 一次给够（16384 位 RSA 的私钥区也只有七八 KiB）：扩容会把明文留在旧缓冲里
        byte[]? plain = null;
        byte[]? material = null;
        try
        {
            SshDataWriter fields = new(section);
            uint check = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
            fields.WriteUInt32(check);
            fields.WriteUInt32(check);
            key.WriteOpenSshPrivateFields(ref fields);
            fields.WriteUtf8String(comment);
            for (byte pad = 1; section.WrittenCount % blockBytes != 0; pad++)
            {
                fields.WriteByte(pad);
            }
            plain = section.WrittenSpan.ToArray();

            byte[] kdfOptions = [];
            byte[] privateSection = plain;
            if (encrypt)
            {
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                ArrayBufferWriter<byte> options = new();
                SshDataWriter optionsWriter = new(options);
                optionsWriter.WriteString(salt);
                optionsWriter.WriteUInt32((uint)kdfRounds);
                kdfOptions = options.WrittenSpan.ToArray();

                OpenSshKeyCipher.CipherShape shape = OpenSshKeyCipher.Describe(WriteCipher)!.Value;
                material = new byte[shape.KeyBytes + shape.IvBytes];
                byte[] passphraseBytes = Utf8(passphrase);
                try
                {
                    BcryptPbkdf.DeriveKey(passphraseBytes, salt, kdfRounds, material);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(passphraseBytes);
                }
                privateSection = OpenSshKeyCipher.EncryptCounterMode(WriteCipher, plain, material);
            }

            // 容器：魔数 ‖ cipher ‖ kdf ‖ kdfoptions ‖ 钥数 1 ‖ 公钥 ‖ 私钥区。
            ArrayBufferWriter<byte> container = new();
            SshDataWriter writer = new(container);
            writer.WriteRaw(Encoding.ASCII.GetBytes(OpenSshMagic));
            writer.WriteUtf8String(encrypt ? WriteCipher : "none");
            writer.WriteUtf8String(encrypt ? "bcrypt" : "none");
            writer.WriteString(kdfOptions);
            writer.WriteUInt32(1);
            writer.WriteString(key.PublicKey.Blob.Span);
            writer.WriteString(privateSection);

            string body = Convert.ToBase64String(container.WrittenSpan);
            StringBuilder pem = new(body.Length + 128);
            pem.Append("-----BEGIN OPENSSH PRIVATE KEY-----\n");
            for (int offset = 0; offset < body.Length; offset += 70)
            {
                pem.Append(body.AsSpan(offset, Math.Min(70, body.Length - offset))).Append('\n');
            }
            pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
            return pem.ToString();
        }
        finally
        {
            section.Clear();   // ArrayBufferWriter.Clear 把写过的部分清零
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
            if (material is not null)
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
    }

    /// <summary>未加密的 PKCS#1 / SEC1 / PKCS#8：解出私钥、取它的公钥。传统加密 PEM 在这里被拒。</summary>
    private static byte[] DerivePublicBlob(string pem)
    {
        using InMemorySshSigner signer = Parse(pem, []);
        return signer.PublicKey.Blob.ToArray();
    }

    /// <summary>从文件读一把私钥。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="passphrase">口令；不需要就给 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// <b>读文件是异步的，解密是同步的 CPU 计算。</b>加密的 OpenSSH 私钥要跑
    /// <c>bcrypt_pbkdf</c>（默认 16 轮，高轮数可达秒级），PuTTY 的 <c>.ppk</c> v3 要跑 Argon2id ——
    /// 那是纯计算，没有可以 <c>await</c> 的 IO。
    /// </para>
    /// <para>
    /// 库<b>不替调用方</b>把它丢到线程池（那是「在库里 <c>Task.Run</c> 假装异步」，
    /// 而且服务端场景里只会多一次线程切换）。在 UI 线程上加载高轮数的加密私钥时，
    /// 由调用方决定是否 <c>await Task.Run(() =&gt; SshPrivateKeyFile.LoadAsync(...))</c>。
    /// </para>
    /// </remarks>
    [OverloadResolutionPriority(1)]
    public static ValueTask<InMemorySshSigner> LoadAsync(
        string path, string? passphrase = null, CancellationToken cancellationToken = default) =>
        LoadAsync(path, passphrase.AsMemory(), cancellationToken);

    /// <summary>从文件读一把私钥；口令以字符缓冲给。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="passphrase">口令；不需要就给空。</param>
    /// <param name="cancellationToken">取消令牌 —— 也交给口令派生（<c>bcrypt_pbkdf</c> 逐轮检查）。</param>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/04 §5.2〕口令可以放在调用方自己的 <c>char[]</c> 里，用完自己清零 ——
    /// <see cref="string"/> 版本的口令是不可变的，清不掉。库里由口令派生出的中间副本（UTF-8 字节、派生出的密钥）都会清零。
    /// 其余说明见 <see cref="LoadAsync(string, string?, CancellationToken)"/>。
    /// </remarks>
    public static async ValueTask<InMemorySshSigner> LoadAsync(
        string path, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string pem;
        try
        {
            pem = await ReadKeyTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw OversizedKey(path, ex);
        }
        catch (IOException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFileUnreadable, $"读不了私钥文件 {path}：{ex.Message}", ex)
            {
                ProblemKind = SshKeyProblemKind.FileUnreadable,
                Origin = path,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFileUnreadable,
                $"没有权限读私钥文件 {path}。（Unix 上私钥应当是 0600。）", ex)
            {
                ProblemKind = SshKeyProblemKind.FileAccessDenied,
                Origin = path,
            };
        }

        return Parse(pem, passphrase.Span, path, cancellationToken);
    }

    /// <summary>读取至多 1 MiB 的密钥文本，默认使用 UTF-8，并识别 BOM。</summary>
    /// <exception cref="InvalidDataException">文件超过大小限制。</exception>
    public static ValueTask<string> ReadKeyTextAsync(string path, CancellationToken cancellationToken = default) =>
        BoundedFileReader.ReadAsync(path, static (bytes, length) =>
        {
            using MemoryStream input = new(bytes, 0, length, writable: false);
            using StreamReader reader = new(input);
            return reader.ReadToEnd();
        }, cancellationToken);

    private static SshPrivateKeyException OversizedKey(string? origin, Exception? cause = null) =>
        new(SshFailureReason.KeyFormatInvalid, $"私钥超过 1 MiB 的大小限制（{origin}）。", cause)
        {
            Origin = origin,
            ProblemKind = SshKeyProblemKind.Malformed,
        };

    /// <summary>从一段 PEM 文本解出私钥。</summary>
    /// <param name="pem">PEM 文本。</param>
    /// <param name="passphrase">口令；不需要就给 <see langword="null"/>。</param>
    /// <param name="origin">出错消息里用来标明来源（通常是文件路径）。</param>
    [OverloadResolutionPriority(1)]
    public static InMemorySshSigner Parse(string pem, string? passphrase = null, string? origin = null) =>
        Parse(pem, passphrase.AsSpan(), origin);

    /// <summary>从一段 PEM 文本解出私钥；口令以字符缓冲给，可以中途取消。</summary>
    /// <param name="pem">PEM 文本。</param>
    /// <param name="passphrase">口令；不需要就给空。调用方可以把它放在自己的 <c>char[]</c> 里，用完自己清零。</param>
    /// <param name="origin">出错消息里用来标明来源（通常是文件路径）。</param>
    /// <param name="cancellationToken">取消令牌：口令派生是同步的纯计算，<c>bcrypt_pbkdf</c> 逐轮检查它，Argon2 与 PBKDF2 开算之前检查。</param>
    /// <exception cref="OperationCanceledException">被取消。</exception>
    public static InMemorySshSigner Parse(
        string pem, ReadOnlySpan<char> passphrase, string? origin = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pem);
        cancellationToken.ThrowIfCancellationRequested();

        if (pem.Length > BoundedFileReader.MaxBytes)
        {
            throw OversizedKey(origin);
        }
        SshPrivateKeyFormat format = DetectFormat(pem);
        string where = origin is null ? "" : $"（{origin}）";

        try
        {
            return format switch
            {
                SshPrivateKeyFormat.Putty => PuttyPrivateKeyFile.Parse(pem, passphrase, origin, cancellationToken),
                SshPrivateKeyFormat.OpenSsh => ParseOpenSsh(pem, passphrase, where, cancellationToken),
                SshPrivateKeyFormat.Pkcs8 or SshPrivateKeyFormat.Pkcs8Encrypted
                    or SshPrivateKeyFormat.Pkcs1Rsa or SshPrivateKeyFormat.Sec1Ec =>
                    ParseWithBcl(pem, passphrase, format, where, cancellationToken),
                _ => throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                    $"认不出这个私钥格式{where}。支持的有：OpenSSH（BEGIN OPENSSH PRIVATE KEY）、" +
                    "PKCS#8、PKCS#1（BEGIN RSA PRIVATE KEY）、SEC1（BEGIN EC PRIVATE KEY）、" +
                    "以及 PuTTY 的 .ppk（v2 / v3）。")
                {
                    ProblemKind = SshKeyProblemKind.FormatUnrecognized,
                },
            };
        }
        catch (SshPrivateKeyException ex) when (origin is not null)
        {
            // 解析的内层只拿到拼进消息的那段「（来源）」，结构化的来源在这里补上（界面按它显示是哪个文件）。
            ex.Origin ??= origin;
            throw;
        }
        catch (Exception ex) when (ex is not (SshPrivateKeyException or OperationCanceledException))
        {
            // 〔velashell-docs/zh/ssh/spec/04 §4.6〕私钥文件是外来输入：截断（复制粘贴丢了尾行、base64 恰好在 4 字符边界断开）、
            // 字段畸形（`Public-Lines: abc`、RSA 的 p 或 q 为 1）都只该是「格式不对」。曾经让解析层的 internal 异常
            // 或 BCL 的 FormatException / DivideByZeroException 原样漏出去 —— 调用方只接 SshPrivateKeyException，
            // 那就一路漏到了界面上。
            throw new SshPrivateKeyException(
                SshFailureReason.KeyFormatInvalid, $"私钥文件的内容不完整或格式不对{where}：{ex.Message}", ex)
            {
                ProblemKind = SshKeyProblemKind.Malformed,
                Origin = origin,
            };
        }
    }

    // ------------------------------------------------------------ OpenSSH 格式

    /// <summary>解 <c>openssh-key-v1</c> 容器。</summary>
    /// <remarks>
    /// <code>
    /// "openssh-key-v1\0"
    /// string   ciphername
    /// string   kdfname
    /// string   kdfoptions
    /// uint32   密钥数 N
    /// string   公钥 1 … N
    /// string   加密并填充过的私钥区
    /// </code>
    /// </remarks>
    private static InMemorySshSigner ParseOpenSsh(
        string pem, ReadOnlySpan<char> passphrase, string where, CancellationToken cancellationToken)
    {
        (string cipherName, string kdfName, byte[] kdfOptions, byte[] publicSection, byte[] privateSection, byte[] tag) =
            ReadOpenSshContainer(pem, where);

        InMemorySshSigner signer;
        if (cipherName == "none" && kdfName == "none")
        {
            signer = ParseOpenSshPrivateSection(privateSection, encrypted: false, where);
        }
        else
        {
            byte[] decrypted = DecryptOpenSshSection(
                privateSection, tag, cipherName, kdfName, kdfOptions, passphrase, where, cancellationToken);
            try
            {
                signer = ParseOpenSshPrivateSection(decrypted, encrypted: true, where);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }

        // 〔velashell-docs/zh/ssh/spec/04 §4.6〕公钥段（文件里明文的那一份）必须与私钥导出的是同一把。
        // 曾经直接丢掉不看：公钥段被改过、或者两个文件拼错了，拿到的是「不是你以为的那把」钥 ——
        // 症状只是服务端一句「不接受这把公钥」，而 ssh-keygen -y、agent 列出来的都是公钥段那一把。
        if (!signer.PublicKey.Blob.Span.SequenceEqual(publicSection))
        {
            signer.Dispose();
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $"OpenSSH 私钥文件里的公钥与私钥不是一对{where} —— 文件被改过，或者拼错了。")
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }
        return signer;
    }

    /// <summary><c>openssh-key-v1</c> 容器拆开后的各段；私钥区还没解密。</summary>
    private readonly record struct OpenSshContainer(
        string CipherName, string KdfName, byte[] KdfOptions, byte[] PublicSection, byte[] PrivateSection, byte[] Tag);

    /// <summary>拆开 <c>openssh-key-v1</c> 容器（布局见 <see cref="ParseOpenSsh"/>）。</summary>
    private static OpenSshContainer ReadOpenSshContainer(string pem, string where)
    {
        byte[] blob = DecodePemBody(pem, "OPENSSH PRIVATE KEY", where);

        byte[] magic = Encoding.ASCII.GetBytes(OpenSshMagic);
        if (blob.Length < magic.Length || !blob.AsSpan(0, magic.Length).SequenceEqual(magic))
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"OpenSSH 私钥的魔数不对{where}。")
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }

        SshDataReader reader = new(new ReadOnlySequence<byte>(blob.AsMemory(magic.Length)));
        string cipherName = reader.ReadUtf8String(1024);
        string kdfName = reader.ReadUtf8String(1024);
        byte[] kdfOptions = reader.ReadStringAsArray(64 * 1024);
        uint keyCount = reader.ReadUInt32();

        if (keyCount != 1)
        {
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"这个文件里有 {keyCount} 把密钥{where}，本库只处理一把。")
            {
                ProblemKind = SshKeyProblemKind.MultipleKeys,
            };
        }

        byte[] publicSection = reader.ReadStringAsArray(64 * 1024);   // 公钥：解出私钥之后拿来核对
        byte[] privateSection = reader.ReadStringAsArray(1024 * 1024);

        // AEAD 的认证标签在那个 string 的**外面** —— 容器末尾的裸字节，不带长度前缀。
        // 非 AEAD 时这里是空的。误把它当成密文的尾部，会得到一个「开头解得出、
        // 标签永远验不过」的结果（流密码解前缀照样是对的），查起来极其费劲。
        byte[] tag = reader.ReadRemaining().ToArray();

        return new(cipherName, kdfName, kdfOptions, publicSection, privateSection, tag);
    }

    /// <summary>用 <c>bcrypt_pbkdf</c> 派生密钥，再解开私钥区。</summary>
    /// <remarks>
    /// <para>
    /// kdfoptions 的布局是 <c>string salt ‖ uint32 rounds</c>。
    /// 密钥与 IV 是**一次**派生出来的一整段材料，前面是密钥、紧接着是 IV ——
    /// 分两次派生会得到完全不同的值。
    /// </para>
    /// <para>
    /// 口令按 UTF-8 编码。ASCII 口令下这与任何实现都一致；非 ASCII 口令在各家客户端
    /// 之间本就不可移植（OpenSSH 直接用终端给的字节），而 UTF-8 是这里唯一讲得清的选择。
    /// </para>
    /// </remarks>
    private static byte[] DecryptOpenSshSection(
        byte[] section, byte[] tag, string cipherName, string kdfName, byte[] kdfOptions,
        ReadOnlySpan<char> passphrase, string where, CancellationToken cancellationToken)
    {
        if (kdfName != "bcrypt")
        {
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"不支持的 KDF “{kdfName}”{where}。OpenSSH 的加密私钥用的是 bcrypt。")
            {
                ProblemKind = SshKeyProblemKind.UnsupportedKdf,
                AlgorithmName = kdfName,
            };
        }

        if (OpenSshKeyCipher.Describe(cipherName) is not { } shape)
        {
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"不支持的加密算法 “{cipherName}”{where}。本库支持：" +
                string.Join("、", OpenSshKeyCipher.SupportedCipherNames) + "。" + Environment.NewLine +
                "换一种即可：ssh-keygen -p -Z aes256-ctr -f <私钥文件>")
            {
                ProblemKind = SshKeyProblemKind.UnsupportedCipher,
                AlgorithmName = cipherName,
            };
        }

        if (passphrase.IsEmpty)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyPassphraseRequired, $"这是一把加密的 OpenSSH 私钥{where}，需要口令。")
            {
                ProblemKind = SshKeyProblemKind.PassphraseRequired,
            };
        }

        byte[] salt;
        uint rounds;
        try
        {
            SshDataReader options = new(new ReadOnlySequence<byte>(kdfOptions));
            salt = options.ReadStringAsArray(1024);
            rounds = options.ReadUInt32();
        }
        catch (SshWireFormatException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"OpenSSH 私钥的 kdfoptions 读不出来{where}。", ex)
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }

        if (salt.Length == 0 || rounds is 0 or > MaxKdfRounds)
        {
            // 这两个值来自文件，也就是来自不可信输入。上限不是洁癖：
            // 一个被改过的 rounds 能让「解一把私钥」挂在那里跑上几天。
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $"OpenSSH 私钥的 KDF 参数不合理{where}（盐 {salt.Length} 字节、{rounds} 轮，上限 {MaxKdfRounds} 轮）。" +
                "文件可能被改过；确实用 ssh-keygen -a 设过这么多轮的话，请用更少的轮数重新加密这把钥。")
            {
                ProblemKind = SshKeyProblemKind.KdfCostTooHigh,
            };
        }

        if (section.Length == 0 || section.Length % shape.BlockBytes != 0)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $"OpenSSH 私钥的密文长度 {section.Length} 与算法 {cipherName} 对不上{where}，文件多半损坏了。")
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }

        if (tag.Length != shape.TagBytes)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $"OpenSSH 私钥的认证标签应当是 {shape.TagBytes} 字节，实际 {tag.Length} 字节{where}。")
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }

        byte[] material = new byte[shape.KeyBytes + shape.IvBytes];
        byte[] passphraseBytes = Utf8(passphrase);
        try
        {
            BcryptPbkdf.DeriveKey(passphraseBytes, salt, (int)rounds, material, cancellationToken);
            return OpenSshKeyCipher.Decrypt(cipherName, section, tag, material);
        }
        catch (CryptographicException ex)
        {
            // 认证类算法（GCM / ChaCha20-Poly1305）在这一步就能判定口令不对；
            // CTR / CBC 没有标签，要等下面校验字对不上才知道。
            throw new SshPrivateKeyException(
                SshFailureReason.KeyPassphraseIncorrect, $"私钥解不开{where} —— 口令多半不对。", ex)
            {
                ProblemKind = SshKeyProblemKind.PassphraseIncorrect,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    /// <summary>口令的 UTF-8 字节（用完要清零）。</summary>
    internal static byte[] Utf8(ReadOnlySpan<char> passphrase)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(passphrase)];
        Encoding.UTF8.GetBytes(passphrase, bytes);
        return bytes;
    }

    /// <summary>把私钥的中间副本清零（velashell-docs/zh/ssh/spec/04 §5.2）。</summary>
    /// <remarks>
    /// 只清得了数组：<see cref="BigInteger"/> 是不可变的，清不掉；口令以 <see cref="string"/> 给的也一样 ——
    /// 要清零口令，用收 <c>ReadOnlySpan&lt;char&gt;</c> / <c>ReadOnlyMemory&lt;char&gt;</c> 的重载，把它放在自己的 <c>char[]</c> 里。
    /// </remarks>
    private static void Clear(params ReadOnlySpan<byte[]?> secrets)
    {
        foreach (byte[]? secret in secrets)
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    /// <summary>解私钥区（已经是明文的那一份）。</summary>
    /// <remarks>
    /// <code>
    /// uint32   checkint1
    /// uint32   checkint2   —— 必须与 checkint1 相同
    /// string   密钥类型
    /// …        类型相关字段
    /// string   注释
    /// byte[]   填充 1,2,3,…
    /// </code>
    /// </remarks>
    /// <param name="section">明文私钥区。</param>
    /// <param name="encrypted">这一份是不是刚解密出来的（决定校验字对不上时怎么报）。</param>
    /// <param name="where">出错消息里的来源标记。</param>
    private static InMemorySshSigner ParseOpenSshPrivateSection(byte[] section, bool encrypted, string where)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(section));

        uint check1 = reader.ReadUInt32();
        uint check2 = reader.ReadUInt32();

        if (check1 != check2)
        {
            // 两个校验字必然相同。CTR / CBC 没有认证标签，口令错了也照样“解”得出一堆
            // 随机字节 —— 这一步就是它们唯一的口令校验点，所以要按「口令不对」去报，
            // 而不是笼统地说文件坏了。
            throw new SshPrivateKeyException(
                encrypted ? SshFailureReason.KeyPassphraseIncorrect : SshFailureReason.KeyFormatInvalid,
                encrypted
                    ? $"私钥解不开{where} —— 口令多半不对（校验字 {check1:X8} ≠ {check2:X8}）。"
                    : $"OpenSSH 私钥的校验字不匹配{where}（{check1:X8} ≠ {check2:X8}），文件多半损坏了。")
            {
                ProblemKind = encrypted ? SshKeyProblemKind.PassphraseIncorrect : SshKeyProblemKind.Malformed,
            };
        }

        string keyType = reader.ReadUtf8String(1024);

        return keyType switch
        {
            SshAlgorithmNames.SshEd25519 => ReadEd25519(ref reader, where),
            SshAlgorithmNames.SshRsa => ReadRsa(ref reader, where),
            SshAlgorithmNames.EcdsaSha2Nistp256 or SshAlgorithmNames.EcdsaSha2Nistp384
                or SshAlgorithmNames.EcdsaSha2Nistp521 => ReadEcdsa(ref reader, keyType, where),
            _ => throw new SshPrivateKeyException(SshFailureReason.Unsupported, $"不支持的密钥类型 {keyType}{where}。")
            {
                ProblemKind = SshKeyProblemKind.UnsupportedKeyType,
                AlgorithmName = keyType,
            },
        };
    }

    private static InMemorySshSigner ReadEd25519(scoped ref SshDataReader reader, string where)
    {
        byte[] publicKey = reader.ReadStringAsArray(64);            // 公钥
        byte[] secret = reader.ReadStringAsArray(128);              // 种子 ‖ 公钥

        try
        {
            if (secret.Length != 64)
            {
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                    $"Ed25519 私钥应当是 64 字节（种子 32 + 公钥 32），实际 {secret.Length} 字节{where}。")
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }

            // 私钥区里公钥出现两次（单独一份、种子后面一份），种子还能导出第三份 —— 三份必须是同一把。
            if (!secret.AsSpan(32).SequenceEqual(publicKey))
            {
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                    $"Ed25519 私钥区里的两份公钥对不上{where}，文件多半损坏了。")
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }

            // 签名器复制一份归自己所有；这一份由这里清。
            var signer = InMemorySshSigner.FromEd25519(secret.AsSpan(0, 32));
            if (!signer.PublicKey.Blob.Span[^32..].SequenceEqual(publicKey))
            {
                signer.Dispose();
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                    $"Ed25519 的种子导出的公钥与文件里写的不一致{where}，文件多半损坏了。")
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }
            return signer;
        }
        finally
        {
            Clear(secret);
        }
    }

    private static InMemorySshSigner ReadRsa(scoped ref SshDataReader reader, string where)
    {
        byte[] n = reader.ReadMpint(4096).ToArray();
        byte[] e = reader.ReadMpint(512).ToArray();
        byte[] d = reader.ReadMpint(4096).ToArray();
        byte[] iqmp = reader.ReadMpint(4096).ToArray();
        byte[] p = reader.ReadMpint(4096).ToArray();
        byte[] q = reader.ReadMpint(4096).ToArray();

        RSAParameters parameters = default;
        try
        {
            // OpenSSH 存的是 n/e/d/iqmp/p/q，而 RSAParameters 还要 DP 与 DQ。
            // 它们由 CRT 定义直接算出来：dp = d mod (p-1)、dq = d mod (q-1)。
            // 这是模运算，不是密码学原语。
            BigInteger bigD = ToPositive(d);
            BigInteger bigP = ToPositive(p);
            BigInteger bigQ = ToPositive(q);

            // n 必须正好是 p·q（p、q 都大于 1）：不核对的话，坏文件的症状是签名验不过 ——「服务端不接受这把公钥」。
            if (bigP <= BigInteger.One || bigQ <= BigInteger.One || bigP * bigQ != ToPositive(n))
            {
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                    $"RSA 私钥的 p·q 与 n 对不上{where}，文件多半损坏了。")
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }

            byte[] dp = ToFixedLength(bigD % (bigP - BigInteger.One), p.Length);
            byte[] dq = ToFixedLength(bigD % (bigQ - BigInteger.One), q.Length);

            parameters = new()
            {
                Modulus = TrimLeadingZero(n),
                Exponent = TrimLeadingZero(e),
                D = ToFixedLength(bigD, TrimLeadingZero(n).Length),
                P = TrimLeadingZero(p),
                Q = TrimLeadingZero(q),
                DP = dp,
                DQ = dq,
                InverseQ = ToFixedLength(ToPositive(iqmp), TrimLeadingZero(p).Length),
            };

            var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return InMemorySshSigner.FromRsa(rsa);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"RSA 私钥的参数不成立{where}：{ex.Message}", ex)
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }
        finally
        {
            // RSA 对象已经导入了自己的一份；这里读出来、算出来的中间值都是明文私钥。
            Clear(d, iqmp, p, q, parameters.D, parameters.P, parameters.Q, parameters.DP, parameters.DQ, parameters.InverseQ);
        }
    }

    private static InMemorySshSigner ReadEcdsa(scoped ref SshDataReader reader, string keyType, string where)
    {
        string curveName = reader.ReadUtf8String(64);
        byte[] point = reader.ReadStringAsArray(512);

        (ECCurve curve, int coordinateBytes) = curveName switch
        {
            "nistp256" => (ECCurve.NamedCurves.nistP256, 32),
            "nistp384" => (ECCurve.NamedCurves.nistP384, 48),

            // nistp521 的坐标是 **66** 字节（521 位向上取整），不是 64。
            "nistp521" => (ECCurve.NamedCurves.nistP521, 66),
            _ => throw new SshPrivateKeyException(SshFailureReason.Unsupported, $"不支持的曲线 {curveName}{where}。")
            {
                ProblemKind = SshKeyProblemKind.UnsupportedCurve,
                AlgorithmName = curveName,
            },
        };

        if (keyType != $"ecdsa-sha2-{curveName}")
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                $"密钥类型是 {keyType}，曲线却写的是 {curveName}{where} —— 两者对不上，文件多半损坏了。")
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }

        if (point.Length != 1 + (coordinateBytes * 2) || point[0] != 0x04)
        {
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"{keyType} 的公开点必须是未压缩形式（0x04 ‖ X ‖ Y），" +
                $"期望 {1 + (coordinateBytes * 2)} 字节，实际 {point.Length} 字节{where}。")
            {
                ProblemKind = SshKeyProblemKind.UnsupportedPointFormat,
                AlgorithmName = keyType,
            };
        }

        // 曲线与公开点先验过、再读私钥标量：验不过时手上还没有要清的明文。
        byte[] d = reader.ReadMpint(512).ToArray();
        byte[]? fixedD = null;
        try
        {
            fixedD = ToFixedLength(ToPositive(d), coordinateBytes);
            ECParameters parameters = new()
            {
                Curve = curve,
                Q = new ECPoint
                {
                    X = point.AsSpan(1, coordinateBytes).ToArray(),
                    Y = point.AsSpan(1 + coordinateBytes, coordinateBytes).ToArray(),
                },
                D = fixedD,
            };

            var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(parameters);
            return InMemorySshSigner.FromEcdsa(ecdsa);
        }
        catch (CryptographicException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"ECDSA 私钥的参数不成立{where}：{ex.Message}", ex)
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }
        finally
        {
            Clear(d, fixedD);
        }
    }

    // ------------------------------------------------------------ BCL 能直接读的格式

    private static InMemorySshSigner ParseWithBcl(
        string pem, ReadOnlySpan<char> passphrase, SshPrivateKeyFormat format, string where, CancellationToken cancellationToken)
    {
        // 传统加密 PEM（Proc-Type: 4,ENCRYPTED —— OpenSSH 7.8 之前 ssh-keygen 加口令时的默认）：
        // 口令只过一遍 MD5 就成了密钥，常配 3DES。这种过时格式本库不读，而是**在要口令之前**就说清楚、
        // 给出转换办法。曾经把它交给 BCL，BCL 不认这种格式，报出来的却是「口令多半不对」——
        // 用户会一直怀疑自己的口令，问题其实在格式。
        if (pem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
        {
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"这把私钥是过时的传统加密 PEM 格式（Proc-Type: 4,ENCRYPTED，口令只经一次 MD5 派生）{where}，本库不读。" +
                "用「ssh-keygen -p -f <私钥文件>」改一次口令（新旧口令可以相同），它会转成 OpenSSH 格式。")
            {
                ProblemKind = SshKeyProblemKind.LegacyPemEncryption,
            };
        }

        bool needsPassphrase = format == SshPrivateKeyFormat.Pkcs8Encrypted
            || pem.Contains("DEK-Info", StringComparison.Ordinal);

        if (needsPassphrase && passphrase.IsEmpty)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyPassphraseRequired, $"这把私钥需要口令{where}。")
            {
                ProblemKind = SshKeyProblemKind.PassphraseRequired,
            };
        }

        // 加密 PKCS#8：先看一眼 KDF 的迭代数（来自文件），过大就不交给 BCL 去算（见 MaxPkcs8Iterations）。
        bool ecdsaFirst = false;
        if (format == SshPrivateKeyFormat.Pkcs8Encrypted)
        {
            byte[] encrypted = DecodePemBody(pem, "ENCRYPTED PRIVATE KEY", where);
            if (ReadPkcs8Encryption(encrypted) is { } encryption)
            {
                // if (encryption.Iterations < 1 || encryption.Iterations > MaxPkcs8Iterations)
                if (encryption.Iterations is var iterations && (iterations < 1 || iterations > MaxPkcs8Iterations))
                {
                    throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                        $"加密 PKCS#8 私钥的 KDF 迭代数不合理{where}（{encryption.Iterations} 次，上限 {MaxPkcs8Iterations} 次）。" +
                        "文件可能被改过；确实设过这么多次的话，请用更少的迭代数重新加密这把钥。")
                    {
                        ProblemKind = SshKeyProblemKind.KdfCostTooHigh,
                    };
                }
                ecdsaFirst = encryption.EncryptedBytes <= EcFirstEncryptedBytes;
            }

            // 〔AU-E8〕只解密一次：口令错就报口令错；解开之后看里面装的是什么钥，按算法分派。
            // 曾经逐个按 RSA、ECDSA 交给 BCL 去试（每试一次 KDF 整个跑一遍），都失败就报「口令多半不对」——
            // 装的是 Ed25519 / DSA 时口令明明是对的，界面一遍遍弹口令框。
            // 解密方案 BouncyCastle 不认的（null），照旧交给下面 BCL 那一路去试。
            cancellationToken.ThrowIfCancellationRequested();
            if (DecryptPkcs8(encrypted, passphrase, where) is { } plain)
            {
                try
                {
                    return LoadPkcs8(plain, where);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
        }
        else if (format == SshPrivateKeyFormat.Pkcs8)
        {
            // 明文 PKCS#8 同样按里面的算法分派：Ed25519、DSA 报「不支持」，而不是「格式不对或已损坏」。
            byte[] plain = DecodePemBody(pem, "PRIVATE KEY", where);
            try
            {
                return LoadPkcs8(plain, where);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        // 先按 RSA 试，再按 ECDSA 试 —— PEM 头部不总能区分
        // （PKCS#8 的 BEGIN PRIVATE KEY 对两者是一样的）。加密 PKCS#8 按密文大小排先后（见 EcFirstEncryptedBytes）。
        foreach (bool asEcdsa in ecdsaFirst ? (ReadOnlySpan<bool>)[true, false] : [false, true])
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return asEcdsa
                    ? LoadEcdsaFromPem(pem, passphrase, needsPassphrase)
                    : LoadRsaFromPem(pem, passphrase, needsPassphrase);
            }
            catch (CryptographicException)
            {
                // 换下一种试。真正的失败在循环外面报。
            }
            catch (ArgumentException)
            {
                // 同上。
            }
        }

        throw new SshPrivateKeyException(
            needsPassphrase ? SshFailureReason.KeyPassphraseIncorrect : SshFailureReason.KeyFormatInvalid,
            needsPassphrase
                ? $"私钥解不开{where} —— 口令多半不对。"
                : $"私钥读不出来{where}，文件可能已损坏。")
        {
            ProblemKind = needsPassphrase ? SshKeyProblemKind.PassphraseIncorrect : SshKeyProblemKind.Malformed,
        };
    }

    /// <summary>读加密 PKCS#8 的 KDF 迭代数与密文长度（RFC 5958 §3、RFC 8018 §6.1 / §6.2 / A.2）。</summary>
    /// <returns>认不出结构（或不是 PBKDF2 / PBES1 那一类）时为 <see langword="null"/>：交给 BCL 去报错。</returns>
    /// <remarks>
    /// <code>
    /// EncryptedPrivateKeyInfo ::= SEQUENCE { encryptionAlgorithm AlgorithmIdentifier, encryptedData OCTET STRING }
    /// PBES2:  parameters = SEQUENCE { keyDerivationFunc AlgorithmIdentifier(PBKDF2, SEQUENCE { salt, iterationCount, ... }),
    ///                                 encryptionScheme AlgorithmIdentifier }
    /// PBES1 / PKCS#12 PBE:  parameters = SEQUENCE { salt OCTET STRING, iterationCount INTEGER }
    /// </code>
    /// </remarks>
    internal static (BigInteger Iterations, int EncryptedBytes)? ReadPkcs8Encryption(byte[] der)
    {
        const string pbes2 = "1.2.840.113549.1.5.13";
        const string pbkdf2 = "1.2.840.113549.1.5.12";

        try
        {
            AsnReader info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            AsnReader algorithm = info.ReadSequence();
            string scheme = algorithm.ReadObjectIdentifier();
            AsnReader parameters = algorithm.ReadSequence();

            BigInteger iterations;
            if (scheme == pbes2)
            {
                AsnReader kdf = parameters.ReadSequence();
                if (kdf.ReadObjectIdentifier() != pbkdf2)
                {
                    return null;   // scrypt 之类：BCL 本来就不认，交给它去报
                }

                AsnReader kdfParameters = kdf.ReadSequence();
                if (kdfParameters.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString))
                {
                    kdfParameters.ReadOctetString();
                }
                else
                {
                    kdfParameters.ReadSequence();   // salt 的另一种写法：otherSource AlgorithmIdentifier
                }
                iterations = kdfParameters.ReadInteger();
            }
            else
            {
                parameters.ReadOctetString();
                iterations = parameters.ReadInteger();
            }

            return (iterations, info.ReadOctetString().Length);
        }
        catch (AsnContentException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>用口令解开加密 PKCS#8，交回里面的明文 PrivateKeyInfo（DER）。</summary>
    /// <returns>
    /// BouncyCastle 不认这种加密方案、或者解出来的不像 PrivateKeyInfo（口令不对而填充恰好对上，约 1/256）时为
    /// <see langword="null"/>：交给 BCL 那一路去试、去报。
    /// </returns>
    /// <exception cref="SshPrivateKeyException">填充校验失败 —— 口令不对（<see cref="SshFailureReason.KeyPassphraseIncorrect"/>）。</exception>
    private static byte[]? DecryptPkcs8(byte[] encrypted, ReadOnlySpan<char> passphrase, string where)
    {
        Org.BouncyCastle.Asn1.Pkcs.EncryptedPrivateKeyInfo info;
        try
        {
            info = Org.BouncyCastle.Asn1.Pkcs.EncryptedPrivateKeyInfo.GetInstance(encrypted);
        }
        catch (Exception)
        {
            return null;
        }

        char[] password = passphrase.ToArray();
        try
        {
            return Org.BouncyCastle.Pkcs.PrivateKeyInfoFactory.CreatePrivateKeyInfo(password, info).GetDerEncoded();
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyPassphraseIncorrect, $"私钥解不开{where} —— 口令多半不对。", ex)
            {
                ProblemKind = SshKeyProblemKind.PassphraseIncorrect,
            };
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>按 PrivateKeyInfo 里的算法标识分派（RFC 5958 §2）。</summary>
    /// <remarks>
    /// SSH 用得上的是 RSA、三条 NIST 曲线上的 ECDSA（RFC 5656 §10.1）与 Ed25519（RFC 8410）；
    /// Ed448 / DSA 与别的曲线报「不支持」，并说出是什么 —— 不让人去怀疑口令或文件。
    /// </remarks>
    private static InMemorySshSigner LoadPkcs8(byte[] der, string where)
    {
        const string rsaEncryption = "1.2.840.113549.1.1.1";
        const string ecPublicKey = "1.2.840.10045.2.1";
        const string ed25519 = "1.3.101.112";
        const string ed448 = "1.3.101.113";
        const string dsa = "1.2.840.10040.4.1";

        (string algorithm, string? curve) = ReadPkcs8Algorithm(der, where);
        switch (algorithm)
        {
            case rsaEncryption:
                {
                    var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(der, out _);
                    return InMemorySshSigner.FromRsa(rsa);
                }

            case ecPublicKey:
                {
                    // 先按曲线的 OID 判：BCL 不认的曲线（各平台不一）连导入都过不去，报出来的会是「格式不对」。
                    if (curve is not ("1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35"))
                    {
                        throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                            $"这把 ECDSA 私钥用的曲线是 {DescribeCurveOid(curve)}{where}，SSH 只支持 NIST P-256 / P-384 / P-521。")
                        {
                            ProblemKind = SshKeyProblemKind.UnsupportedCurve,
                            AlgorithmName = DescribeCurveOid(curve),
                        };
                    }
                    var ecdsa = ECDsa.Create();
                    ecdsa.ImportPkcs8PrivateKey(der, out _);
                    return InMemorySshSigner.FromEcdsa(ecdsa);
                }

            case ed25519:
                return LoadPkcs8Ed25519(der, where);

            case ed448:
                throw new SshPrivateKeyException(SshFailureReason.Unsupported, $"这是一把 Ed448 私钥{where}，SSH 不用 Ed448。")
                {
                    ProblemKind = SshKeyProblemKind.UnsupportedKeyType,
                    AlgorithmName = "Ed448",
                };

            case dsa:
                throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                    $"这是一把 DSA 私钥{where}。DSA（ssh-dss）已被 OpenSSH 废弃，本库不支持。")
                {
                    ProblemKind = SshKeyProblemKind.UnsupportedKeyType,
                    AlgorithmName = "DSA",
                };

            default:
                throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                    $"这把 PKCS#8 私钥的算法是 {algorithm}{where}，SSH 用不上（支持 RSA、NIST 曲线上的 ECDSA 与 Ed25519）。")
                {
                    ProblemKind = SshKeyProblemKind.UnsupportedKeyType,
                    AlgorithmName = algorithm,
                };
        }
    }

    /// <summary>PKCS#8 里的 Ed25519（RFC 8410 §7）：取出 32 字节种子；带着公钥（v2）时核对它是不是种子导出的那一把。</summary>
    /// <remarks>
    /// <code>
    /// OneAsymmetricKey ::= SEQUENCE { version, privateKeyAlgorithm, privateKey OCTET STRING,
    ///                                 attributes [0] IMPLICIT OPTIONAL, ..., [[2: publicKey [1] IMPLICIT BIT STRING OPTIONAL ]], ... }
    /// privateKey 里装的是 CurvePrivateKey ::= OCTET STRING（32 字节，RFC 8032 的私钥）
    /// </code>
    /// 按 BER 读（RFC 5958 要求），属性与以后扩展的字段跳过。公钥对不上与 <c>openssh-key-v1</c>、<c>.ppk</c> 同一口径，
    /// 报 <see cref="SshFailureReason.KeyFormatInvalid"/>。BCL 导入不了这种钥（.NET 11 仍没有独立的 Ed25519），曾经一律报「不支持」。
    /// </remarks>
    private static InMemorySshSigner LoadPkcs8Ed25519(byte[] der, string where)
    {
        Asn1Tag publicKeyTag = new(TagClass.ContextSpecific, 1);
        byte[]? privateKey = null;
        byte[]? seed = null;
        byte[]? publicKey = null;
        try
        {
            try
            {
                AsnReader info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
                _ = info.ReadInteger();   // version：v1（0）或 v2（1）
                _ = info.ReadSequence();  // AlgorithmIdentifier：id-Ed25519，参数缺省（RFC 8410 §3）
                privateKey = info.ReadOctetString();
                AsnReader curvePrivateKey = new(privateKey, AsnEncodingRules.BER);
                seed = curvePrivateKey.ReadOctetString();
                curvePrivateKey.ThrowIfNotEmpty();

                while (info.HasData)
                {
                    if (!info.PeekTag().HasSameClassAndValue(publicKeyTag))
                    {
                        _ = info.ReadEncodedValue();   // attributes [0] 与以后扩展的字段
                        continue;
                    }

                    publicKey = info.ReadBitString(out int unusedBits, publicKeyTag);
                    if (unusedBits != 0)
                    {
                        throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                            $"PKCS#8 里 Ed25519 的公钥不是整字节{where}，文件多半损坏了。")
                        {
                            ProblemKind = SshKeyProblemKind.Malformed,
                        };
                    }
                }
            }
            catch (AsnContentException ex)
            {
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"PKCS#8 里 Ed25519 私钥的结构不对{where}。", ex)
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }

            if (seed.Length != 32)
            {
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                    $"Ed25519 私钥应当是 32 字节，实际 {seed.Length} 字节{where}。")
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }

            // 签名器复制一份归自己所有；这一份由这里清。
            var signer = InMemorySshSigner.FromEd25519(seed);
            if (publicKey is not null && !signer.PublicKey.Blob.Span[^32..].SequenceEqual(publicKey))
            {
                signer.Dispose();
                throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid,
                    $"PKCS#8 里 Ed25519 的公钥与私钥导出的不一致{where}，文件多半损坏了。")
                {
                    ProblemKind = SshKeyProblemKind.Malformed,
                };
            }
            return signer;
        }
        finally
        {
            Clear(privateKey, seed);
        }
    }

    /// <summary>PrivateKeyInfo 的算法 OID；是 EC 时连同曲线的 OID（namedCurve 参数，没有就是 null）。</summary>
    private static (string Algorithm, string? Curve) ReadPkcs8Algorithm(byte[] der, string where)
    {
        try
        {
            AsnReader info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            _ = info.ReadInteger();   // version
            AsnReader algorithm = info.ReadSequence();
            string oid = algorithm.ReadObjectIdentifier();
            string? curve = algorithm.HasData && algorithm.PeekTag().HasSameClassAndValue(Asn1Tag.ObjectIdentifier)
                ? algorithm.ReadObjectIdentifier()
                : null;
            return (oid, curve);
        }
        catch (AsnContentException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"PKCS#8 私钥的结构不对{where}。", ex)
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }
    }

    /// <summary>给人看的曲线名：认识的写名字，不认识的写 OID。</summary>
    private static string DescribeCurveOid(string? oid) => oid switch
    {
        null => "（显式参数，没有曲线名）",
        "1.3.132.0.10" => "secp256k1",
        "1.3.36.3.3.2.8.1.1.7" => "brainpoolP256r1",
        "1.3.36.3.3.2.8.1.1.11" => "brainpoolP384r1",
        "1.3.36.3.3.2.8.1.1.13" => "brainpoolP512r1",
        _ => oid,
    };

    private static InMemorySshSigner LoadRsaFromPem(string pem, ReadOnlySpan<char> passphrase, bool encrypted)
    {
        var rsa = RSA.Create();
        if (encrypted)
        {
            rsa.ImportFromEncryptedPem(pem, passphrase);
        }
        else
        {
            rsa.ImportFromPem(pem);
        }
        return InMemorySshSigner.FromRsa(rsa);
    }

    private static InMemorySshSigner LoadEcdsaFromPem(string pem, ReadOnlySpan<char> passphrase, bool encrypted)
    {
        var ecdsa = ECDsa.Create();
        if (encrypted)
        {
            ecdsa.ImportFromEncryptedPem(pem, passphrase);
        }
        else
        {
            ecdsa.ImportFromPem(pem);
        }

        // PKCS#8 / SEC1 能装任意曲线：SSH 只定义了三条 NIST 曲线（RFC 5656 §10.1），别的报「不支持」而不是「格式不对」。
        ECCurve curve = ecdsa.ExportParameters(false).Curve;
        if (InMemorySshSigner.NistCurveOf(curve) is null)
        {
            string name = InMemorySshSigner.DescribeCurve(curve);
            ecdsa.Dispose();
            throw new SshPrivateKeyException(SshFailureReason.Unsupported,
                $"这把 ECDSA 私钥用的曲线是 {name}，SSH 只支持 NIST P-256 / P-384 / P-521。")
            {
                ProblemKind = SshKeyProblemKind.UnsupportedCurve,
                AlgorithmName = name,
            };
        }
        return InMemorySshSigner.FromEcdsa(ecdsa);
    }

    // ------------------------------------------------------------ 工具

    private static byte[] DecodePemBody(string pem, string label, string where)
    {
        string begin = $"-----BEGIN {label}-----";
        string end = $"-----END {label}-----";

        int start = pem.IndexOf(begin, StringComparison.Ordinal);
        int stop = pem.IndexOf(end, StringComparison.Ordinal);

        if (start < 0 || stop < 0 || stop <= start)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"PEM 的起止标记不完整{where}。")
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }

        string body = pem[(start + begin.Length)..stop];

        try
        {
            return Convert.FromBase64String(
                body.Replace("\r", "", StringComparison.Ordinal)
                    .Replace("\n", "", StringComparison.Ordinal)
                    .Replace(" ", "", StringComparison.Ordinal));
        }
        catch (FormatException ex)
        {
            throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, $"PEM 的 base64 正文解不开{where}。", ex)
            {
                ProblemKind = SshKeyProblemKind.Malformed,
            };
        }
    }

    /// <summary>把大端字节当成**非负**整数读。</summary>
    private static BigInteger ToPositive(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    /// <summary>把整数写成固定长度的大端字节（左侧补零）。</summary>
    private static byte[] ToFixedLength(BigInteger value, int length)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        if (bytes.Length == length)
        {
            return bytes;
        }

        if (bytes.Length > length)
        {
            // 只可能是前面多了零；真的超长说明参数不对。
            int extra = bytes.Length - length;
            for (int i = 0; i < extra; i++)
            {
                if (bytes[i] != 0)
                {
                    throw new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, "RSA/ECDSA 的参数比它该有的长度还长，文件多半损坏了。")
                    {
                        ProblemKind = SshKeyProblemKind.Malformed,
                    };
                }
            }
            return bytes.AsSpan(extra).ToArray();
        }

        byte[] padded = new byte[length];
        bytes.CopyTo(padded.AsSpan(length - bytes.Length));
        return padded;
    }

    /// <summary>去掉 mpint 为表示正数而加的那个前导零。</summary>
    private static byte[] TrimLeadingZero(byte[] value)
    {
        int index = 0;
        while (index < value.Length - 1 && value[index] == 0)
        {
            index++;
        }
        return index == 0 ? value : value.AsSpan(index).ToArray();
    }
}
