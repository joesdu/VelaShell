using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace VelaShell.Infrastructure.Persistence;

/// <summary><c>secret.key.mp</c> 里记着的这一轮开启 / 修改的进度。</summary>
internal enum MasterPasswordHeaderState
{
    /// <summary>主密码开着。</summary>
    Active,

    /// <summary>开启做到一半:头已落盘,本机加密密钥可能还有没包裹的。</summary>
    Enabling,

    /// <summary>关闭做到一半:本机加密密钥可能已经有写回明文的。</summary>
    Disabling
}

/// <summary>
/// 主密码的 KDF 头(数据目录里的 <c>secret.key.mp</c>):版本、Argon2id 参数、盐,以及恢复码的盐。
/// 都不是机密 —— 只靠它什么也解不开,它也不含任何能拿来离线核对口令的东西(核对靠包裹本身的 GCM 标签)。
/// </summary>
/// <remarks>
/// 参数随头存:以后调参只需在下一次解锁成功时用新参数、新盐重新包裹(rehash),不必一次性迁移。
/// 读到超出合理范围的参数(被改坏、被人改大想拖死解锁)一律当成读不出来。
/// </remarks>
internal sealed class MasterPasswordHeader
{
    /// <summary>格式版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>出厂内存参数:64 MiB(维护者 2026-10-31 定,不为低端机降参)。</summary>
    public const int DefaultMemoryKiB = 64 * 1024;

    /// <summary>出厂迭代次数。</summary>
    public const int DefaultIterations = 3;

    /// <summary>出厂并行度。</summary>
    public const int DefaultParallelism = 1;

    /// <summary>盐长(字节)。</summary>
    public const int SaltSize = 16;

    private const string Kdf = "argon2id";
    private const int MaxMemoryKiB = 1024 * 1024;
    private const int MaxIterations = 64;
    private const int MaxParallelism = 16;

    /// <summary>Argon2id 的内存参数(KiB)。</summary>
    public required int MemoryKiB { get; init; }

    /// <summary>Argon2id 的迭代次数。</summary>
    public required int Iterations { get; init; }

    /// <summary>Argon2id 的并行度。</summary>
    public required int Parallelism { get; init; }

    /// <summary>派生主密码 KEK 的盐。</summary>
    public required byte[] Salt { get; init; }

    /// <summary>派生恢复码密钥的盐;没生成恢复码时为 null。</summary>
    public byte[]? RecoverySalt { get; init; }

    /// <summary>进度。</summary>
    public MasterPasswordHeaderState State { get; init; } = MasterPasswordHeaderState.Active;

    /// <summary>这份头里主密码那一份包裹的编号(由盐派生)。</summary>
    public byte[] PasswordWrapId => MasterKeyEnvelope.WrapIdOf(WrapKind.Password, Salt);

    /// <summary>恢复码那一份包裹的编号;没有恢复码时为 null。</summary>
    public byte[]? RecoveryWrapId => RecoverySalt is { } salt ? MasterKeyEnvelope.WrapIdOf(WrapKind.Recovery, salt) : null;

    /// <summary>参数就是出厂参数(不是的话下次解锁成功时 rehash)。</summary>
    public bool UsesCurrentParameters =>
        MemoryKiB == DefaultMemoryKiB && Iterations == DefaultIterations && Parallelism == DefaultParallelism;

    /// <summary>新开一份头:出厂参数、新盐。</summary>
    /// <param name="recoverySalt">恢复码的盐:沿用已有的那份(修改主密码时恢复码不变)、新生成,或 null(不要恢复码)。</param>
    /// <param name="state">进度。</param>
    public static MasterPasswordHeader CreateNew(byte[]? recoverySalt, MasterPasswordHeaderState state) => new()
    {
        MemoryKiB = DefaultMemoryKiB,
        Iterations = DefaultIterations,
        Parallelism = DefaultParallelism,
        Salt = RandomNumberGenerator.GetBytes(SaltSize),
        RecoverySalt = recoverySalt,
        State = state
    };

    /// <summary>同一份头换个进度。</summary>
    public MasterPasswordHeader WithState(MasterPasswordHeaderState state) => new()
    {
        MemoryKiB = MemoryKiB,
        Iterations = Iterations,
        Parallelism = Parallelism,
        Salt = Salt,
        RecoverySalt = RecoverySalt,
        State = state
    };

    /// <summary>写成文本(每行 <c>键=值</c>,前面几行注释说明这是什么)。</summary>
    public string Serialize()
    {
        StringBuilder text = new();
        text.Append("# VelaShell master password: key-derivation parameters only (no key material, nothing to check a password against).\n");
        text.Append("# The local encryption keys are wrapped with a key derived from the master password; without this file they cannot be unwrapped.\n");
        text.Append("version=").Append(CurrentVersion).Append('\n');
        text.Append("kdf=").Append(Kdf).Append('\n');
        text.Append("m=").Append(MemoryKiB.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("t=").Append(Iterations.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("p=").Append(Parallelism.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("salt=").Append(Convert.ToBase64String(Salt)).Append('\n');
        if (RecoverySalt is { } recovery)
        {
            text.Append("recovery-salt=").Append(Convert.ToBase64String(recovery)).Append('\n');
        }
        text.Append("state=").Append(State switch
        {
            MasterPasswordHeaderState.Enabling => "enabling",
            MasterPasswordHeaderState.Disabling => "disabling",
            _ => "active"
        }).Append('\n');
        return text.ToString();
    }

    /// <summary>解析;任何一项缺失、重复、超出范围都返回 null(当成读不出来)。</summary>
    public static MasterPasswordHeader? Parse(string text)
    {
        Dictionary<string, string> values = [with(StringComparer.Ordinal)];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !values.TryAdd(line[..eq], line[(eq + 1)..]))
            {
                return null;
            }
        }
        if (!values.TryGetValue("version", out string? version) || version != "1"
            || !values.TryGetValue("kdf", out string? kdf) || kdf != Kdf
            || !TryInt(values, "m", 8, MaxMemoryKiB, out int m)
            || !TryInt(values, "t", 1, MaxIterations, out int t)
            || !TryInt(values, "p", 1, MaxParallelism, out int p)
            || m < 8 * p
            || !TrySalt(values, "salt", out byte[]? salt))
        {
            return null;
        }
        byte[]? recoverySalt = null;
        if (values.ContainsKey("recovery-salt") && !TrySalt(values, "recovery-salt", out recoverySalt))
        {
            return null;
        }
        MasterPasswordHeaderState state;
        switch (values.GetValueOrDefault("state", "active"))
        {
            case "active":
                state = MasterPasswordHeaderState.Active;
                break;
            case "enabling":
                state = MasterPasswordHeaderState.Enabling;
                break;
            case "disabling":
                state = MasterPasswordHeaderState.Disabling;
                break;
            default:
                return null;
        }
        return new()
        {
            MemoryKiB = m,
            Iterations = t,
            Parallelism = p,
            Salt = salt!,
            RecoverySalt = recoverySalt,
            State = state
        };

        static bool TryInt(Dictionary<string, string> values, string key, int min, int max, out int value)
        {
            value = 0;
            return values.TryGetValue(key, out string? s)
                   && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                   && value >= min && value <= max;
        }

        static bool TrySalt(Dictionary<string, string> values, string key, out byte[]? salt)
        {
            salt = null;
            if (!values.TryGetValue(key, out string? s))
            {
                return false;
            }
            try
            {
                salt = Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return false;
            }
            return salt.Length == SaltSize;
        }
    }
}

/// <summary>一份包裹是用什么解的。</summary>
internal enum WrapKind : byte
{
    /// <summary>主密码(Argon2id 派生的 KEK)。</summary>
    Password = 0x50,

    /// <summary>恢复码(HKDF 派生的密钥 —— 恢复码本身是 128 位随机数,不需要慢哈希)。</summary>
    Recovery = 0x52
}

/// <summary>包裹里的一条:种类 ‖ 包裹编号(8 字节,由对应的盐派生)‖ nonce(12)‖ tag(16)‖ 密文(32)。</summary>
internal sealed record WrapRecord(WrapKind Kind, byte[] WrapId, byte[] Body);

/// <summary>
/// 包裹过的本机加密密钥(存进密钥链条目 / 密钥文件的那一份):<c>VSMP1</c> 魔数 ‖ 一条或几条 <see cref="WrapRecord" />。
/// </summary>
/// <remarks>
/// <para>
/// 一般是两条:主密码一条、恢复码一条(跳过了恢复码就只有一条)。修改主密码时先加上新主密码那一条、换头、
/// 再删旧的那一条 —— 中途断在哪儿,磁盘上那份头对应的那一条都在,不会出现「头换了、包裹还没换」的空档。
/// 编号对不上当前头的那几条是这种过渡的残留,解锁时不用、成功解锁后顺手清掉。
/// </para>
/// <para>
/// 附加认证数据是魔数 ‖ 种类 ‖ 编号:一条记录挪不到别的种类或别的头下去用。条目编号仍按 DEK 派生不变,
/// 不认 <c>VSMP1</c> 的旧版本读到它只会当成「内容对不上编号」—— 不用,也不动(降级不丢数据)。
/// </para>
/// </remarks>
internal sealed class MasterKeyEnvelope
{
    /// <summary>每条记录的长度。</summary>
    public const int RecordSize = 1 + WrapIdSize + NonceSize + TagSize + MasterKeyResolver.KeySize;

    private const int WrapIdSize = 8;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxRecords = 8;

    private static ReadOnlySpan<byte> WrapIdDomain => "VelaShell master password wrap id v1\0"u8;

    /// <summary>构造一份包裹。</summary>
    /// <param name="records">记录(至少一条)。</param>
    public MasterKeyEnvelope(IReadOnlyList<WrapRecord> records)
    {
        if (records.Count is 0 or > MaxRecords)
        {
            throw new ArgumentException(@"An envelope holds one to eight records.", nameof(records));
        }
        Records = records;
    }

    /// <summary>魔数。</summary>
    public static ReadOnlySpan<byte> Magic => "VSMP1"u8;

    /// <summary>全部记录。</summary>
    public IReadOnlyList<WrapRecord> Records { get; }

    /// <summary>一份存着的值是不是包裹(看魔数)。</summary>
    public static bool IsEnvelope(ReadOnlySpan<byte> value) => value.StartsWith(Magic);

    /// <summary>解析;不是包裹或长度不对时为 false。</summary>
    public static bool TryParse(ReadOnlySpan<byte> value, out MasterKeyEnvelope? envelope)
    {
        envelope = null;
        if (!IsEnvelope(value) || value.Length == Magic.Length || (value.Length - Magic.Length) % RecordSize != 0
            || (value.Length - Magic.Length) / RecordSize > MaxRecords)
        {
            return false;
        }
        List<WrapRecord> records = [];
        for (int offset = Magic.Length; offset < value.Length; offset += RecordSize)
        {
            ReadOnlySpan<byte> record = value.Slice(offset, RecordSize);
            if (record[0] is not ((byte)WrapKind.Password or (byte)WrapKind.Recovery))
            {
                return false;
            }
            records.Add(new((WrapKind)record[0], record.Slice(1, WrapIdSize).ToArray(), record[(1 + WrapIdSize)..].ToArray()));
        }
        envelope = new(records);
        return true;
    }

    /// <summary>写成存进去的字节。</summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[Magic.Length + (Records.Count * RecordSize)];
        Magic.CopyTo(bytes);
        int offset = Magic.Length;
        foreach (WrapRecord record in Records)
        {
            bytes[offset] = (byte)record.Kind;
            record.WrapId.CopyTo(bytes, offset + 1);
            record.Body.CopyTo(bytes, offset + 1 + WrapIdSize);
            offset += RecordSize;
        }
        return bytes;
    }

    /// <summary>由盐派生一份包裹的编号(单向,不泄露盐以外的任何东西)。</summary>
    public static byte[] WrapIdOf(WrapKind kind, ReadOnlySpan<byte> salt)
    {
        Span<byte> input = stackalloc byte[WrapIdDomain.Length + 1 + salt.Length];
        WrapIdDomain.CopyTo(input);
        input[WrapIdDomain.Length] = (byte)kind;
        salt.CopyTo(input[(WrapIdDomain.Length + 1)..]);
        return SHA256.HashData(input)[..WrapIdSize];
    }

    /// <summary>用 <paramref name="kek" /> 包一把本机加密密钥。</summary>
    public static WrapRecord Wrap(WrapKind kind, byte[] wrapId, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> dek)
    {
        if (dek.Length != MasterKeyResolver.KeySize || wrapId.Length != WrapIdSize)
        {
            throw new ArgumentException(@"A wrap takes a 32-byte key and an 8-byte wrap id.");
        }
        byte[] body = new byte[NonceSize + TagSize + MasterKeyResolver.KeySize];
        RandomNumberGenerator.Fill(body.AsSpan(0, NonceSize));
        using AesGcm aes = new(kek, TagSize);
        aes.Encrypt(body.AsSpan(0, NonceSize), dek, body.AsSpan(NonceSize + TagSize), body.AsSpan(NonceSize, TagSize),
            AssociatedData(kind, wrapId));
        return new(kind, wrapId, body);
    }

    /// <summary>用 <paramref name="kek" /> 解一条记录;认证标签对不上(口令不对、记录被改)为 false。</summary>
    public static bool TryUnwrap(WrapRecord record, ReadOnlySpan<byte> kek, out byte[] dek)
    {
        dek = new byte[MasterKeyResolver.KeySize];
        try
        {
            using AesGcm aes = new(kek, TagSize);
            aes.Decrypt(record.Body.AsSpan(0, NonceSize), record.Body.AsSpan(NonceSize + TagSize),
                record.Body.AsSpan(NonceSize, TagSize), dek, AssociatedData(record.Kind, record.WrapId));
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(dek);
            dek = [];
            return false;
        }
    }

    /// <summary>这份包裹里编号对得上 <paramref name="wrapId" /> 的那条(同种类);没有为 null。</summary>
    public WrapRecord? Find(WrapKind kind, byte[]? wrapId) =>
        wrapId is null ? null : Records.FirstOrDefault(r => r.Kind == kind && r.WrapId.AsSpan().SequenceEqual(wrapId));

    private static byte[] AssociatedData(WrapKind kind, byte[] wrapId)
    {
        byte[] aad = new byte[Magic.Length + 1 + wrapId.Length];
        Magic.CopyTo(aad);
        aad[Magic.Length] = (byte)kind;
        wrapId.CopyTo(aad, Magic.Length + 1);
        return aad;
    }
}

/// <summary>主密码与恢复码的密钥派生。</summary>
internal static class MasterPasswordKdf
{
    /// <summary>派生出的密钥长度(AES-256)。</summary>
    public const int KeySize = 32;

    private static readonly byte[] RecoveryInfo = "VelaShell master password recovery key v1"u8.ToArray();

    /// <summary>
    /// 主密码 → KEK(Argon2id v1.3,参数取头里的)。64 MiB 内存、几百毫秒 CPU:只在后台线程上调。
    /// </summary>
    /// <param name="passwordUtf8">主密码的 UTF-8 字节(调用方负责用完清零)。</param>
    /// <param name="header">KDF 头。</param>
    public static byte[] DerivePasswordKey(byte[] passwordUtf8, MasterPasswordHeader header)
    {
        ArgumentNullException.ThrowIfNull(passwordUtf8);
        ArgumentNullException.ThrowIfNull(header);
        Argon2Parameters parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(header.Salt)
            .WithMemoryAsKB(header.MemoryKiB)
            .WithIterations(header.Iterations)
            .WithParallelism(header.Parallelism)
            .Build();
        Argon2BytesGenerator generator = new();
        generator.Init(parameters);
        byte[] key = new byte[KeySize];
        generator.GenerateBytes(passwordUtf8, key);
        return key;
    }

    /// <summary>恢复码(128 位随机数)→ 解恢复码那一份包裹的密钥(HKDF-SHA256)。</summary>
    /// <param name="entropy">恢复码解码出的 16 字节。</param>
    /// <param name="recoverySalt">头里的恢复码盐。</param>
    public static byte[] DeriveRecoveryKey(ReadOnlySpan<byte> entropy, ReadOnlySpan<byte> recoverySalt)
    {
        byte[] key = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, entropy, key, recoverySalt, RecoveryInfo);
        return key;
    }
}
