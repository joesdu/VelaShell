using System.Diagnostics;
using System.Security.Cryptography;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 本机加密密钥在内存里的那一份,以及主密码那一层:锁着(只有包裹)/ 解着(有能用的密钥)、解锁、锁定;
/// 开启 / 关闭 / 修改 / 忘记见同名分部文件。
/// </summary>
/// <remarks>
/// <para>
/// 主密码没开时它就是第一步的那串密钥,永远解着。开着时启动解析只拿到包裹(<see cref="SealedKeys" />),
/// <see cref="IsLocked" /> 为 true:加密抛 <see cref="SecretsLockedException" />、解密一律解不开 ——
/// 与第一步「密钥没取到」是同一条路,调用方本来就会处理。
/// </para>
/// <para>
/// 解锁:主密码经 Argon2id 派生 KEK,逐个解包;解开的密钥对着条目编号自证(编号由密钥派生),对不上的不用。
/// 解开之后顺手收尾:主密码开着却以明文存着的密钥(降级到旧版本时它另起的那把、开启中途断掉的)包裹起来写回去,
/// 开启 / 关闭做到一半的接着做完,修改主密码留下的旧记录清掉。任何一项收尾失败都不影响这次解锁。
/// </para>
/// <para>
/// 输错的退避:连续输错第 n 次之后要等 2^(n-1) 秒(1、2、4… 上限 30 秒)才能再试;不锁死、不删任何东西。
/// 计数只活在这个进程里 —— 离线猜测本来就不经过这里,能挡的只是有人坐在电脑前一个个试。
/// </para>
/// <para>
/// 锁定:清零内存里的密钥(<see cref="CryptographicOperations.ZeroMemory" />)回到锁着。加解密都在
/// <see cref="UseKeys{T}" /> 的锁里做,锁定不会与一次正在进行的加密交错(否则可能拿清零了一半的密钥加密)。
/// </para>
/// </remarks>
internal sealed partial class MasterKeyVault
{
    private const int MaxBackoffSeconds = 30;

    private readonly MasterKeyResolver _resolver;
    private readonly TimeProvider _time;

    /// <summary>加解密与换密钥串、换状态时持有(都很快)。</summary>
    private readonly Lock _keysGate = new();

    /// <summary>解锁、开启、关闭、修改、忘记这些写存储的操作串行做(可能很慢)。</summary>
    private readonly Lock _operationGate = new();

    private MasterKeyRing? _ring;
    private SealedKeys? _sealed;
    private MasterKeyStatus _status;
    private int _failures;
    private DateTimeOffset _notBefore;
    private bool _unlockedWithRecoveryCode;

    /// <summary>上一次解锁时每个解开的位置装的是哪一把(编号,不含密钥材料):用恢复码解开后设新主密码时要知道本地密钥文件里是哪一把。</summary>
    private readonly Dictionary<KeySlot, string> _openedSlotIds = [];

    /// <summary>解析一次本机加密密钥并构造(可能阻塞:密钥链解锁要等用户)。</summary>
    public MasterKeyVault(MasterKeyResolver resolver, TimeProvider? time = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _time = time ?? TimeProvider.System;
        MasterKeyResolution resolution = resolver.Resolve();
        _ring = resolution.Ring;
        _sealed = resolution.Sealed;
        _status = resolution.Status;
    }

    /// <summary>解析结果(含主密码开没开)。</summary>
    public MasterKeyStatus Status
    {
        get
        {
            lock (_keysGate)
            {
                return _status;
            }
        }
    }

    /// <summary>主密码开着(事实来源是包裹格式)。</summary>
    public bool IsProtected
    {
        get
        {
            lock (_keysGate)
            {
                return _status.MasterPasswordEnabled;
            }
        }
    }

    /// <summary>主密码开着且此刻锁着。</summary>
    public bool IsLocked
    {
        get
        {
            lock (_keysGate)
            {
                return _status.MasterPasswordEnabled && _ring is null;
            }
        }
    }

    /// <summary>这一次是用恢复码解开的(界面据此要求设一个新的主密码)。</summary>
    public bool UnlockedWithRecoveryCode
    {
        get
        {
            lock (_keysGate)
            {
                return _unlockedWithRecoveryCode && _ring is not null;
            }
        }
    }

    /// <summary>主密码开着且生成过恢复码。</summary>
    public bool HasRecoveryCode
    {
        get
        {
            lock (_keysGate)
            {
                return _sealed?.Header?.RecoverySalt is not null;
            }
        }
    }

    /// <summary>输错之后还要等多久才能再试。</summary>
    public TimeSpan RetryAfter
    {
        get
        {
            lock (_keysGate)
            {
                TimeSpan left = _notBefore - _time.GetUtcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>在锁里用此刻的密钥串做一次加解密;锁着时 <paramref name="action" /> 拿到 null。</summary>
    public T UseKeys<T>(Func<MasterKeyRing?, T> action)
    {
        lock (_keysGate)
        {
            return action(_ring);
        }
    }

    /// <summary>锁定:清零内存里的密钥。主密码没开或本来就锁着时什么都不做,返回 false。</summary>
    public bool Lock()
    {
        lock (_keysGate)
        {
            if (!_status.MasterPasswordEnabled || _ring is null)
            {
                return false;
            }
            _ring.Zero();
            _ring = null;
            _unlockedWithRecoveryCode = false;
            return true;
        }
    }

    /// <summary>用主密码解锁。派生 KEK 要 64 MiB 内存与几百毫秒:只在后台线程上调。</summary>
    /// <param name="passwordUtf8">主密码的 UTF-8 字节(调用方负责用完清零)。</param>
    public MasterPasswordUnlockResult Unlock(byte[] passwordUtf8)
    {
        ArgumentNullException.ThrowIfNull(passwordUtf8);
        lock (_operationGate)
        {
            if (Precheck() is { } early)
            {
                return early;
            }
            SealedKeys sealedKeys = _sealed!;
            MasterPasswordHeader header = sealedKeys.Header!;
            byte[] kek = MasterPasswordKdf.DerivePasswordKey(passwordUtf8, header);
            try
            {
                MasterPasswordUnlockResult result = Open(sealedKeys, WrapKind.Password, header.PasswordWrapId, kek, passwordKek: kek);
                if (result.Status == MasterPasswordUnlockStatus.Unlocked)
                {
                    TryRehash(passwordUtf8, header, kek);
                }
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
    }

    /// <summary>用恢复码解锁(之后界面要求设一个新的主密码)。</summary>
    /// <param name="recoveryCode">用户输入的恢复码。</param>
    public MasterPasswordUnlockResult UnlockWithRecoveryCode(string recoveryCode)
    {
        lock (_operationGate)
        {
            if (Precheck() is { } early)
            {
                return early;
            }
            SealedKeys sealedKeys = _sealed!;
            MasterPasswordHeader header = sealedKeys.Header!;
            if (header.RecoverySalt is not { } recoverySalt)
            {
                return new(MasterPasswordUnlockStatus.NoRecoveryCode);
            }
            if (!RecoveryCode.TryParse(recoveryCode, out byte[] entropy))
            {
                return new(MasterPasswordUnlockStatus.MalformedRecoveryCode);
            }
            byte[] key = MasterPasswordKdf.DeriveRecoveryKey(entropy, recoverySalt);
            CryptographicOperations.ZeroMemory(entropy);
            try
            {
                return Open(sealedKeys, WrapKind.Recovery, header.RecoveryWrapId, key, passwordKek: null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>解锁前的几种早退:没开、已解、要等、没东西可解。</summary>
    private MasterPasswordUnlockResult? Precheck()
    {
        lock (_keysGate)
        {
            if (!_status.MasterPasswordEnabled)
            {
                return new(MasterPasswordUnlockStatus.NotEnabled);
            }
            if (_ring is not null)
            {
                return new(MasterPasswordUnlockStatus.AlreadyUnlocked);
            }
            TimeSpan left = _notBefore - _time.GetUtcNow();
            if (left > TimeSpan.Zero)
            {
                return new(MasterPasswordUnlockStatus.Throttled, left);
            }
            if (_sealed is not { Header: not null, Wrapped.Count: > 0 })
            {
                return new(MasterPasswordUnlockStatus.NothingToUnlock);
            }
            return null;
        }
    }

    /// <summary>输错一次:计数加一,算出这次要等多久。</summary>
    private MasterPasswordUnlockResult RecordFailure()
    {
        lock (_keysGate)
        {
            _failures++;
            int seconds = Math.Min(MaxBackoffSeconds, 1 << Math.Min(_failures - 1, 5));
            _notBefore = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
            return new(MasterPasswordUnlockStatus.WrongSecret, TimeSpan.FromSeconds(seconds));
        }
    }

    /// <summary>
    /// 用 <paramref name="key" /> 解开每一份包裹里种类与编号对得上的那条记录。解开的密钥对着条目编号自证。
    /// </summary>
    /// <param name="sealedKeys">包裹。</param>
    /// <param name="kind">用哪一种记录(主密码 / 恢复码)。</param>
    /// <param name="wrapId">当前头里那一种记录的编号。</param>
    /// <param name="key">派生出的密钥。</param>
    /// <param name="anyCandidate">至少有一份包裹里有对得上的记录(否则不算输错,是没东西可解)。</param>
    /// <returns>每个解开了的位置及其密钥(同一把在密钥链与本地文件里各一份时两处都在)。</returns>
    private static List<OpenedKey> UnwrapAll(SealedKeys sealedKeys, WrapKind kind, byte[]? wrapId, byte[] key, out bool anyCandidate)
    {
        List<OpenedKey> opened = [];
        anyCandidate = false;
        foreach (WrappedKey wrapped in sealedKeys.Wrapped)
        {
            if (wrapped.Envelope.Find(kind, wrapId) is not { } record)
            {
                continue;
            }
            anyCandidate = true;
            if (!MasterKeyEnvelope.TryUnwrap(record, key, out byte[] dek))
            {
                continue;
            }
            string id = MasterKeyResolver.KeyIdOf(dek);
            if (wrapped.Slot.KeychainId is { } entryId && entryId != id)
            {
                // 解得开,却不是这个条目名下的那把(条目被人换过):不用它,也不动它。
                Trace.WriteLine($"[Secrets] Keychain entry {entryId} unwraps to a different key; ignoring it");
                CryptographicOperations.ZeroMemory(dek);
                continue;
            }
            opened.Add(new(wrapped, id, dek));
        }
        return opened;
    }

    /// <summary>解开的一个位置。</summary>
    private sealed record OpenedKey(WrappedKey Wrapped, string Id, byte[] Key);

    /// <summary>逐个解包;解开至少一把就算解锁成功,接着做收尾。</summary>
    private MasterPasswordUnlockResult Open(SealedKeys sealedKeys, WrapKind kind, byte[]? wrapId, byte[] key, byte[]? passwordKek)
    {
        List<OpenedKey> opened = UnwrapAll(sealedKeys, kind, wrapId, key, out bool anyCandidate);
        if (opened.Count == 0)
        {
            return anyCandidate ? RecordFailure() : new(MasterPasswordUnlockStatus.NothingToUnlock);
        }
        // 密钥串:解开的(第一把用来加密)+ 明文存着的那几把;同一把只留一份。
        List<byte[]> keys = [];
        HashSet<string> ids = [];
        foreach (OpenedKey o in opened)
        {
            if (ids.Add(o.Id))
            {
                keys.Add([.. o.Key]);
            }
        }
        foreach (PlainKey plain in sealedKeys.Plain)
        {
            if (ids.Add(plain.Id))
            {
                keys.Add([.. plain.Key]);
            }
        }
        lock (_keysGate)
        {
            _failures = 0;
            _notBefore = default;
            _ring = new(keys, _status);
            _unlockedWithRecoveryCode = passwordKek is null;
            _openedSlotIds.Clear();
            foreach (OpenedKey o in opened)
            {
                _openedSlotIds[o.Wrapped.Slot] = o.Id;
            }
        }
        Trace.WriteLine($"[Secrets] Unlocked {ids.Count} key(s) with the {(passwordKek is null ? "recovery code" : "master password")}");
        if (passwordKek is not null)
        {
            // 用恢复码解开的不收尾:明文存着的那几把留到设了新主密码时再包(这会儿没有主密码那一份 KEK)。
            AfterPasswordUnlock(sealedKeys, opened, passwordKek);
        }
        foreach (OpenedKey o in opened)
        {
            CryptographicOperations.ZeroMemory(o.Key);
        }
        return new(MasterPasswordUnlockStatus.Unlocked);
    }

    /// <summary>
    /// 用主密码解开之后的收尾(都是尽力而为,失败只记日志、下次解锁再试):明文存着的密钥包起来写回去、
    /// 开启做到一半的标记为完成、修改主密码留下的旧记录清掉;关闭做到一半的接着关完。
    /// </summary>
    private void AfterPasswordUnlock(SealedKeys sealedKeys, List<OpenedKey> opened, byte[] kek)
    {
        MasterPasswordHeader header = sealedKeys.Header!;
        if (header.State == MasterPasswordHeaderState.Disabling)
        {
            FinishInterruptedDisable(sealedKeys, opened);
            return;
        }
        List<WrappedKey> wrapped = [.. sealedKeys.Wrapped];
        List<PlainKey> plainLeft = [];
        // 明文存着的那几把:包起来(只有主密码那一条 —— 恢复码那一份密钥这会儿没有)写回原处。
        foreach (PlainKey plain in sealedKeys.Plain)
        {
            MasterKeyEnvelope envelope = new([MasterKeyEnvelope.Wrap(WrapKind.Password, header.PasswordWrapId, kek, plain.Key)]);
            byte[] bytes = envelope.ToBytes();
            if (_resolver.TryWriteSlot(plain.Slot, bytes))
            {
                wrapped.Add(new(plain.Slot, envelope, bytes));
                Trace.WriteLine($"[Secrets] Wrapped the unprotected key in {plain.Slot} with the master password");
            }
            else
            {
                plainLeft.Add(plain);
                Trace.WriteLine($"[Secrets] {plain.Slot} still holds an unwrapped key; will retry at the next unlock");
            }
        }
        // 修改主密码中途留下的、编号对不上当前头的旧记录:清掉。
        for (int i = 0; i < wrapped.Count; i++)
        {
            WrappedKey w = wrapped[i];
            WrapRecord[] current = [.. w.Envelope.Records.Where(r => IsCurrent(r, header))];
            if (current.Length == w.Envelope.Records.Count || !current.Any(static r => r.Kind == WrapKind.Password)
                || !opened.Exists(o => o.Wrapped.Slot == w.Slot))
            {
                continue;
            }
            MasterKeyEnvelope cleaned = new(current);
            byte[] bytes = cleaned.ToBytes();
            if (_resolver.TryWriteSlot(w.Slot, bytes))
            {
                wrapped[i] = new(w.Slot, cleaned, bytes);
            }
        }
        MasterPasswordHeader finalHeader = header;
        if (header.State == MasterPasswordHeaderState.Enabling)
        {
            MasterPasswordHeader active = header.WithState(MasterPasswordHeaderState.Active);
            if (_resolver.TryWriteHeader(active))
            {
                finalHeader = active;
            }
        }
        lock (_keysGate)
        {
            _sealed = new() { Header = finalHeader, Problem = MasterPasswordProblem.None, Wrapped = wrapped, Plain = plainLeft };
        }
    }

    private static bool IsCurrent(WrapRecord record, MasterPasswordHeader header) => record.Kind switch
    {
        WrapKind.Password => record.WrapId.AsSpan().SequenceEqual(header.PasswordWrapId),
        WrapKind.Recovery => header.RecoveryWrapId is { } id && record.WrapId.AsSpan().SequenceEqual(id),
        _ => false
    };

    /// <summary>关闭主密码中途断掉:解开之后把剩下的也写回明文、删头,主密码就此关掉。</summary>
    private void FinishInterruptedDisable(SealedKeys sealedKeys, List<OpenedKey> opened)
    {
        bool allPlain = sealedKeys.Wrapped.All(w => opened.Exists(o => o.Wrapped.Slot == w.Slot));
        foreach (OpenedKey o in opened)
        {
            allPlain &= _resolver.TryWriteSlot(o.Wrapped.Slot, [.. o.Key]);
        }
        if (!allPlain || !_resolver.TryDeleteHeader())
        {
            Trace.WriteLine("[Secrets] Could not finish turning the master password off; will retry at the next unlock");
            return;
        }
        lock (_keysGate)
        {
            _sealed = null;
            _status = WithMasterPassword(_status, enabled: false, MasterPasswordProblem.None);
        }
        Trace.WriteLine("[Secrets] Finished turning the master password off");
    }

    /// <summary>状态的一份拷贝,换掉主密码那两项。</summary>
    private static MasterKeyStatus WithMasterPassword(MasterKeyStatus status, bool enabled, MasterPasswordProblem problem) => new()
    {
        Protection = status.Protection,
        KeychainName = status.KeychainName,
        KeychainError = status.KeychainError,
        UnavailableKeyIds = status.UnavailableKeyIds,
        MissingKeyIds = status.MissingKeyIds,
        ReplacedKeyFile = status.ReplacedKeyFile,
        Migrated = status.Migrated,
        MasterPasswordEnabled = enabled,
        MasterPasswordProblem = problem,
        RetiredMasterPasswordHeader = status.RetiredMasterPasswordHeader
    };
}
