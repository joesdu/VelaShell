using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>一次解析的结果:解密时依次尝试的密钥(第一把用来加密)与给界面看的状态。</summary>
internal sealed class MasterKeyRing(IReadOnlyList<byte[]> keys, MasterKeyStatus status)
{
    /// <summary>加密用的那把。</summary>
    public byte[] Current => Keys[0];

    /// <summary>解密时依次尝试的全部密钥(至少一把,第一把即 <see cref="Current" />)。</summary>
    public IReadOnlyList<byte[]> Keys { get; } = keys;

    /// <summary>给界面看的解析结果。</summary>
    public MasterKeyStatus Status { get; } = status;

    /// <summary>就地清零全部密钥(锁定时用;之后这个对象不能再用)。</summary>
    public void Zero()
    {
        foreach (byte[] key in Keys)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>本机加密密钥的一个存放位置:系统密钥链里的一个条目(按编号),或者数据目录里的本地密钥文件。</summary>
/// <param name="KeychainId">密钥链条目编号;null = 本地密钥文件 <c>secret.key</c>。</param>
internal readonly record struct KeySlot(string? KeychainId)
{
    /// <summary>本地密钥文件。</summary>
    public static KeySlot KeyFile => default;

    /// <summary>是本地密钥文件。</summary>
    public bool IsKeyFile => KeychainId is null;

    /// <inheritdoc />
    public override string ToString() => KeychainId ?? "key file";
}

/// <summary>一个位置上存着的原样字节(本地密钥文件在 Windows 上是 DPAPI 解开之后的那份)。</summary>
internal sealed record StoredKeySlot(KeySlot Slot, byte[] Value);

/// <summary>以明文形式存着的一把本机加密密钥。</summary>
internal sealed record PlainKey(KeySlot Slot, string Id, byte[] Key);

/// <summary>以主密码包裹过的形式存着的一把本机加密密钥(解锁前不知道它是哪一把)。</summary>
internal sealed record WrappedKey(KeySlot Slot, MasterKeyEnvelope Envelope, byte[] Stored);

/// <summary>主密码开着时的解析结果:解锁要用的全部材料。</summary>
internal sealed class SealedKeys
{
    /// <summary>KDF 头;不在或读不出来时为 null(见 <see cref="Problem" />)。</summary>
    public MasterPasswordHeader? Header { get; init; }

    /// <summary>解不了锁的原因。</summary>
    public MasterPasswordProblem Problem { get; init; }

    /// <summary>包裹过的密钥。</summary>
    public IReadOnlyList<WrappedKey> Wrapped { get; init; } = [];

    /// <summary>
    /// 主密码开着却以明文存着的密钥:降级到不认主密码的旧版本时它另起的那把、开启 / 关闭中途被打断的那几把。
    /// 锁着时不用;解锁成功后包裹起来写回去。
    /// </summary>
    public IReadOnlyList<PlainKey> Plain { get; init; } = [];
}

/// <summary><see cref="MasterKeyResolver.Resolve" /> 的结果:要么是一串能直接用的密钥,要么是等着主密码解开的一组包裹。</summary>
internal sealed class MasterKeyResolution
{
    /// <summary>主密码没开:能直接用的密钥。</summary>
    public MasterKeyRing? Ring { get; init; }

    /// <summary>主密码开着:解锁要用的材料。</summary>
    public SealedKeys? Sealed { get; init; }

    /// <summary>给界面看的状态。</summary>
    public required MasterKeyStatus Status { get; init; }
}

/// <summary>
/// 决定本机加密密钥从哪来:系统密钥链优先,用不了回落到数据目录里的密钥文件;
/// 顺带把存量的明文密钥文件迁进密钥链;主密码开着时只认出包裹过的密钥,交给 <see cref="MasterKeyVault" /> 去解。
/// </summary>
/// <remarks>
/// <para><b>数据目录里有三样东西</b>:</para>
/// <list type="bullet">
/// <item><c>secret.key</c> —— 本地密钥文件:Windows 上是 DPAPI(当前用户)包裹的密文(Windows 的保管方式就是它),
/// 其余平台是 32 字节明文 + 0600,只作存量与回落。主密码开着时里面是包裹过的那份(<c>VSMP1</c> ‖ 记录)。</item>
/// <item><c>secret.key.ref</c> —— 密钥链条目清单:每行一个条目编号,不含密钥材料。拷走数据目录只带走编号,
/// 在别的电脑 / 账户上取不回密钥 —— 这正是把密钥交给密钥链的目的。</item>
/// <item><c>secret.key.mp</c> —— 主密码的 KDF 头(盐与参数,不是机密),只在开了主密码时存在。</item>
/// </list>
/// <para>
/// <b>条目编号由密钥本身派生</b>(<see cref="KeyIdOf" />):同一把密钥无论迁移被打断几次都落在同一个条目上,
/// 迁移天然幂等、不留孤儿条目;读回来的密钥还能对着编号自证没被换过。包裹过的条目沿用同一个编号,解开之后同样自证。
/// </para>
/// <para>
/// <b>多把密钥并存,而且从不自动丢弃任何一把</b>。密钥链偶尔用不了(Linux 上没起 keyring、用户点了拒绝),
/// 这一轮就另起一把放在本地文件里给新密码用,清单里的旧编号原样留着;密钥链回来的那次启动,
/// 把本地那把也并进密钥链。于是「密钥链暂时不可用」永远不会让已存的密码变成永远解不开的密文。
/// 回滚到不认密钥链的旧版本、旧版本另生成了一把 <c>secret.key</c>,升回来时也走同一条路并进来。
/// </para>
/// <para>
/// <b>主密码开着时不生成、不迁移任何密钥</b>:另起一把主密码管不到的新密钥,等于悄悄把保护关掉。
/// 这时解析只把能找到的包裹与明文密钥交出去,加密要等解锁(<see cref="MasterKeyVault" />)。
/// </para>
/// <para>
/// <b>任何一步失败都停在「照旧能用」的状态</b>:写进密钥链要读回核对,核对过、清单落盘了才删本地文件;
/// 中途断电、进程被杀,下次启动从头再走一遍就收敛。
/// </para>
/// </remarks>
public sealed class MasterKeyResolver
{
    /// <summary>密钥长度(AES-256)。</summary>
    public const int KeySize = 32;

    /// <summary>清单文件相对密钥文件的后缀(<c>secret.key</c> → <c>secret.key.ref</c>)。</summary>
    public const string ReferenceSuffix = ".ref";

    /// <summary>主密码 KDF 头相对密钥文件的后缀(<c>secret.key</c> → <c>secret.key.mp</c>)。</summary>
    public const string HeaderSuffix = ".mp";

    /// <summary>迁移后删除本地密钥文件时的中转名:先改名再清零删除,中途断掉也不会把清零的文件当成密钥读回来。</summary>
    private const string DeletingSuffix = ".deleting";

    private const string KeyLinePrefix = "key=";
    private const int KeyIdLength = 32;
    private static ReadOnlySpan<byte> KeyIdDomain => "VelaShell master key id v1\0"u8;

    /// <summary>构造解析器。</summary>
    /// <param name="keyFilePath">本地密钥文件路径(清单文件与它同目录,名字加 <see cref="ReferenceSuffix" />)。</param>
    /// <param name="store">这个平台的系统密钥链;null = 只用本地密钥文件(Windows,或测试)。</param>
    public MasterKeyResolver(string keyFilePath, IMasterKeyStore? store)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
        {
            throw new ArgumentException(@"Key file path is required.", nameof(keyFilePath));
        }
        KeyFilePath = keyFilePath;
        ReferenceFilePath = keyFilePath + ReferenceSuffix;
        HeaderFilePath = keyFilePath + HeaderSuffix;
        Store = store;
    }

    /// <summary>密钥链条目清单的路径。</summary>
    public string ReferenceFilePath { get; }

    /// <summary>主密码 KDF 头的路径。</summary>
    public string HeaderFilePath { get; }

    /// <summary>本地密钥文件的路径。</summary>
    internal string KeyFilePath { get; }

    /// <summary>这个平台的系统密钥链;没有时为 null。</summary>
    internal IMasterKeyStore? Store { get; }

    /// <summary>
    /// 由密钥派生它在密钥链里的条目编号:加了域分隔的 SHA-256 取前 128 位,小写十六进制。
    /// 单向,不泄露密钥;同一把密钥永远得到同一个编号。
    /// </summary>
    public static string KeyIdOf(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException(@"A master key is 32 bytes.", nameof(key));
        }
        Span<byte> input = stackalloc byte[KeyIdDomain.Length + key.Length];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            KeyIdDomain.CopyTo(input);
            key.CopyTo(input[KeyIdDomain.Length..]);
            SHA256.HashData(input, hash);
            return Convert.ToHexStringLower(hash[..(KeyIdLength / 2)]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>解析一次本机加密密钥(可能阻塞:密钥链解锁要等用户)。除了本地文件也写不进去,不抛异常。</summary>
    internal MasterKeyResolution Resolve()
    {
        Session session = new(this);
        session.Run();
        return session.ToResolution();
    }

    /// <summary>一次解析的过程状态(只活在 <see cref="Resolve" /> 里)。</summary>
    private sealed class Session(MasterKeyResolver owner)
    {
        private readonly List<(string Id, byte[] Key, bool InKeychain)> _keys = [];
        private readonly List<WrappedKey> _wrapped = [];
        private readonly List<string> _unavailable = [];
        private readonly List<string> _missing = [];
        private List<string> _referenced = [];

        /// <summary>清单读不出来(权限、IO)时这一轮一律不写清单 —— 否则一份新的会把读不出的那份盖掉。</summary>
        private bool _referenceWritable = true;

        /// <summary>
        /// 密钥链此刻能不能用。第一次撞上 <see cref="MasterKeyStoreStatus.Unavailable" /> 之后就不再去问:
        /// 每问一次都可能再弹一次解锁框,而答案不会变。
        /// </summary>
        private bool _storeUsable = owner.Store is not null;

        private string? _storeError;
        private string? _replacedKeyFile;
        private bool _migrated;

        private byte[]? _fileKey;
        private HeaderRead _headerRead;
        private MasterPasswordHeader? _header;
        private bool _protected;
        private MasterPasswordProblem _problem;
        private string? _retiredHeader;

        private enum HeaderRead { Absent, Ok, Unreadable }

        public void Run()
        {
            owner.FinishPendingDelete();
            LoadHeader();
            LoadReferencedKeys();
            LoadLocalKeyFile();
            DecideProtection();
            if (!_protected)
            {
                AdoptKeyFile();
                if (_keys.Count == 0)
                {
                    CreateKey();
                }
            }
            Trace.WriteLine(_protected
                ? $"[Secrets] Master key: protected by the master password, {_wrapped.Count} wrapped key(s), {_keys.Count} plain"
                  + (_problem == MasterPasswordProblem.None ? "" : $", problem: {_problem}")
                  + (_unavailable.Count > 0 ? $", unavailable: {string.Join(',', _unavailable)}" : "")
                  + (_missing.Count > 0 ? $", missing: {string.Join(',', _missing)}" : "")
                  + (_storeError is null ? "" : $", keychain error: {_storeError}")
                : $"[Secrets] Master key: {(_keys[0].InKeychain ? owner.Store!.Name : OperatingSystem.IsWindows() ? "DPAPI-wrapped key file" : "plain key file")}"
                  + $", {_keys.Count} key(s)"
                  + (_migrated ? ", migrated the key file into the keychain" : "")
                  + (_unavailable.Count > 0 ? $", unavailable: {string.Join(',', _unavailable)}" : "")
                  + (_missing.Count > 0 ? $", missing: {string.Join(',', _missing)}" : "")
                  + (_storeError is null ? "" : $", keychain error: {_storeError}")
                  + (_retiredHeader is null ? "" : $", retired master password header as '{_retiredHeader}'"));
        }

        public MasterKeyResolution ToResolution()
        {
            MasterKeyStatus status = new()
            {
                Protection = ProtectionOfCurrentKey(),
                KeychainName = owner.Store?.Name,
                KeychainError = _storeError,
                UnavailableKeyIds = _unavailable,
                MissingKeyIds = _missing,
                ReplacedKeyFile = _replacedKeyFile,
                Migrated = _migrated,
                MasterPasswordEnabled = _protected,
                MasterPasswordProblem = _problem,
                RetiredMasterPasswordHeader = _retiredHeader
            };
            if (!_protected)
            {
                return new() { Ring = new([.. _keys.Select(static k => k.Key)], status), Status = status };
            }
            return new()
            {
                Sealed = new()
                {
                    Header = _header,
                    Problem = _problem,
                    Wrapped = _wrapped,
                    Plain = [.. _keys.Select(static k => new PlainKey(k.InKeychain ? new KeySlot(k.Id) : KeySlot.KeyFile, k.Id, k.Key))]
                },
                Status = status
            };
        }

        private MasterKeyProtection ProtectionOfCurrentKey()
        {
            bool inKeychain = _protected
                ? _wrapped.Count > 0 ? !_wrapped[0].Slot.IsKeyFile : _keys.Count > 0 && _keys[0].InKeychain
                : _keys[0].InKeychain;
            return inKeychain ? MasterKeyProtection.SystemKeychain
                : OperatingSystem.IsWindows() ? MasterKeyProtection.UserBoundFile
                : MasterKeyProtection.PlainFile;
        }

        /// <summary>主密码的 KDF 头(没开主密码时不存在)。</summary>
        private void LoadHeader()
        {
            string text;
            try
            {
                if (!File.Exists(owner.HeaderFilePath))
                {
                    _headerRead = HeaderRead.Absent;
                    return;
                }
                text = File.ReadAllText(owner.HeaderFilePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[Secrets] {owner.HeaderFilePath} could not be read ({ex.Message})");
                _headerRead = HeaderRead.Unreadable;
                return;
            }
            _header = MasterPasswordHeader.Parse(text);
            _headerRead = _header is null ? HeaderRead.Unreadable : HeaderRead.Ok;
        }

        /// <summary>第一步:清单里记着的密钥逐个去密钥链里取。</summary>
        private void LoadReferencedKeys()
        {
            _referenced = owner.ReadReference(out _referenceWritable);
            foreach (string id in _referenced)
            {
                if (owner.Store is null)
                {
                    // 这台机器上没有密钥链后端(数据目录从 macOS / Linux 拷到了 Windows)。
                    _missing.Add(id);
                    continue;
                }
                if (!_storeUsable)
                {
                    _unavailable.Add(id);
                    continue;
                }
                MasterKeyStoreResult result = owner.Store.Load(id);
                switch (result.Status)
                {
                    case MasterKeyStoreStatus.Ok when result.Key is { Length: KeySize } key && KeyIdOf(key) == id:
                        _keys.Add((id, key, true));
                        break;
                    case MasterKeyStoreStatus.Ok when result.Key is { } stored && MasterKeyEnvelope.TryParse(stored, out MasterKeyEnvelope? envelope):
                        // 主密码包裹过的那一份:解开之前不知道是哪一把,解开之后对着编号自证(MasterKeyVault)。
                        _wrapped.Add(new(new KeySlot(id), envelope!, stored));
                        break;
                    case MasterKeyStoreStatus.Ok:
                        // 条目在,内容却对不上编号:不用它,也不动它。
                        Trace.WriteLine($"[Secrets] Keychain entry {id} does not hold the key it is named after; ignoring it");
                        _missing.Add(id);
                        break;
                    case MasterKeyStoreStatus.NotFound:
                        _missing.Add(id);
                        break;
                    default:
                        MarkUnavailable(result.Error);
                        _unavailable.Add(id);
                        break;
                }
            }
        }

        /// <summary>第二步:读数据目录里的本地密钥文件(明文的一把、包裹过的一份,或者没有)。</summary>
        private void LoadLocalKeyFile()
        {
            switch (LoadKeyFile(owner.KeyFilePath, out _replacedKeyFile))
            {
                case { Length: KeySize } key:
                    _fileKey = key;
                    break;
                case { } stored when MasterKeyEnvelope.TryParse(stored, out MasterKeyEnvelope? envelope):
                    _wrapped.Add(new(KeySlot.KeyFile, envelope!, stored));
                    break;
            }
        }

        /// <summary>
        /// 主密码开没开:看包裹本身,不看设置。头与包裹对不上时,只在确定没有任何一把密钥依赖它的情况下才把头挪开。
        /// </summary>
        private void DecideProtection()
        {
            bool wrappedFound = _wrapped.Count > 0;
            // 有条目这次没问成(密钥链用不了):它是不是包裹过的说不准,宁可当成开着。
            bool undetermined = _unavailable.Count > 0;
            switch (_headerRead)
            {
                case HeaderRead.Absent:
                    _protected = wrappedFound;
                    _problem = wrappedFound ? MasterPasswordProblem.HeaderMissing : MasterPasswordProblem.None;
                    break;
                case HeaderRead.Unreadable when wrappedFound || undetermined:
                    // 读不出来的头原样留着:那可能是解开这些包裹的唯一一份盐。
                    _protected = true;
                    _problem = MasterPasswordProblem.HeaderUnreadable;
                    break;
                case HeaderRead.Unreadable:
                    RetireHeader("unreadable");
                    break;
                case HeaderRead.Ok when wrappedFound:
                    _protected = true;
                    break;
                case HeaderRead.Ok when undetermined:
                    _protected = true;
                    _problem = MasterPasswordProblem.KeysUnavailable;
                    break;
                default:
                    // 头好好的,却一把包裹过的密钥都没有,而且每一把都问到了:没有任何东西依赖这份头。
                    if (_header!.State is MasterPasswordHeaderState.Enabling or MasterPasswordHeaderState.Disabling)
                    {
                        // 开启还没包裹任何一把就断了 / 关闭已经全部写回明文:这份头没用了,删掉它就是收尾。
                        owner.TryDeleteHeader();
                    }
                    else
                    {
                        // 开着的头找不到它包裹的密钥(旧版本把包裹过的密钥文件挪开了、条目被删了):改名保留,照第一步的规矩继续。
                        RetireHeader("orphaned");
                    }
                    break;
            }
            if (_protected && _fileKey is { } fileKey)
            {
                // 主密码开着时本地文件里的明文密钥(降级到旧版本时它另起的):解锁后包裹,锁着时不用。
                string id = KeyIdOf(fileKey);
                if (!_keys.Exists(k => k.Id == id))
                {
                    _keys.Add((id, fileKey, false));
                }
            }
        }

        private void RetireHeader(string reason)
        {
            try
            {
                _retiredHeader = MoveAside(owner.HeaderFilePath, reason);
                Trace.WriteLine($"[Secrets] {owner.HeaderFilePath} has nothing to unwrap ({reason}); kept it as '{_retiredHeader}'");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[Secrets] {owner.HeaderFilePath} has nothing to unwrap ({reason}) and could not be moved aside ({ex.Message})");
            }
        }

        /// <summary>
        /// 第三步(主密码没开时):数据目录里的本地密钥文件(存量的、上一轮回落时生成的、旧版本生成的)。
        /// 密钥链能用就迁进去、核对过再删文件;用不了就照旧从文件里用。
        /// </summary>
        private void AdoptKeyFile()
        {
            if (_fileKey is not { } fileKey)
            {
                return;
            }
            string id = KeyIdOf(fileKey);
            bool alreadyLoaded = _keys.Exists(k => k.Id == id);
            bool inKeychain = alreadyLoaded || (_storeUsable && Put(id, fileKey));
            if (inKeychain)
            {
                bool listed = _referenced.Contains(id);
                if (!listed)
                {
                    _referenced.Add(id);
                    listed = owner.TryWriteReference(_referenced, _referenceWritable);
                    if (!listed)
                    {
                        _referenced.Remove(id);
                    }
                }
                // 清单里记上了,本地这份才是多余的;删不掉(被占用、权限)就留到下次再删。
                if (listed)
                {
                    _missing.Remove(id);
                    _unavailable.Remove(id);
                    _migrated = TryDeleteKeyFile(owner.KeyFilePath) || _migrated;
                }
                else
                {
                    inKeychain = false;
                }
            }
            if (!alreadyLoaded)
            {
                _keys.Add((id, fileKey, inKeychain));
            }
        }

        /// <summary>第四步:一把能用的都没有(全新安装,或旧密钥这次都取不到)——生成一把新的。</summary>
        private void CreateKey()
        {
            byte[] key = RandomNumberGenerator.GetBytes(KeySize);
            string id = KeyIdOf(key);
            bool inKeychain = false;
            if (_storeUsable && Put(id, key))
            {
                _referenced.Add(id);
                inKeychain = owner.TryWriteReference(_referenced, _referenceWritable);
                if (!inKeychain)
                {
                    _referenced.Remove(id);
                }
            }
            if (!inKeychain)
            {
                // 回落:与引入密钥链之前一样落到本地文件(Windows 上 DPAPI 包裹)。
                // 这里再失败就是数据目录根本写不进去,照旧抛出 —— 数据库那边也一样起不来。
                WriteKeyFile(owner.KeyFilePath, key);
            }
            _keys.Add((id, key, inKeychain));
        }

        /// <summary>把一把密钥存进密钥链并读回核对;已在且一致时什么都不写。</summary>
        private bool Put(string id, byte[] key)
        {
            IMasterKeyStore store = owner.Store!;
            MasterKeyStoreResult existing = store.Load(id);
            switch (existing.Status)
            {
                case MasterKeyStoreStatus.Unavailable:
                    MarkUnavailable(existing.Error);
                    return false;
                case MasterKeyStoreStatus.Ok:
                    if (existing.Key is { } stored && CryptographicOperations.FixedTimeEquals(stored, key))
                    {
                        return true; // 上一次迁移写进去了、没来得及记清单
                    }
                    // 同编号的条目装着别的东西:不覆盖(从不毁掉任何可能有用的密钥材料),本地文件照旧留着。
                    Trace.WriteLine($"[Secrets] Keychain entry {id} already holds different bytes; leaving it and keeping the key file");
                    return false;
            }
            MasterKeyStoreResult saved = store.Save(id, key);
            if (saved.Status != MasterKeyStoreStatus.Ok)
            {
                MarkUnavailable(saved.Error);
                return false;
            }
            // 存进去不等于取得回来(存到了一个锁着的集合、条目的访问控制不认我们):读回来核对过才算数。
            MasterKeyStoreResult check = store.Load(id);
            if (check.Status == MasterKeyStoreStatus.Ok && check.Key is { } readBack
                && CryptographicOperations.FixedTimeEquals(readBack, key))
            {
                return true;
            }
            if (check.Status == MasterKeyStoreStatus.Unavailable)
            {
                MarkUnavailable(check.Error);
            }
            Trace.WriteLine($"[Secrets] Keychain entry {id} could not be read back after saving ({check.Status}); keeping the key file");
            return false;
        }

        private void MarkUnavailable(string? error)
        {
            _storeUsable = false;
            _storeError ??= error ?? "unavailable";
        }
    }

    // ———— 给主密码那一层用的存取(开启 / 关闭 / 修改 / 解锁后的收尾) ————

    /// <summary>
    /// 现读一遍每个位置上存着的东西(清单里的每个条目 + 本地密钥文件),不做任何迁移、不挪任何文件。
    /// </summary>
    /// <param name="complete">每个位置都问到了为 true;有条目这次没问成(密钥链用不了)、密钥文件读不出来为 false。</param>
    internal List<StoredKeySlot> ScanSlots(out bool complete)
    {
        complete = true;
        List<StoredKeySlot> slots = [];
        List<string> ids;
        try
        {
            ids = File.Exists(ReferenceFilePath) ? ParseReference(File.ReadAllLines(ReferenceFilePath)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {ReferenceFilePath} could not be read ({ex.Message})");
            complete = false;
            ids = [];
        }
        foreach (string id in ids)
        {
            if (Store is null)
            {
                continue; // 这台机器上没有密钥链:清单里的条目本来就取不到,不算没问成
            }
            MasterKeyStoreResult result = Store.Load(id);
            switch (result.Status)
            {
                case MasterKeyStoreStatus.Ok when result.Key is { Length: > 0 } value:
                    slots.Add(new(new KeySlot(id), value));
                    break;
                case MasterKeyStoreStatus.Unavailable:
                    complete = false;
                    break;
            }
        }
        if (File.Exists(KeyFilePath))
        {
            if (TryReadKeyFile(out byte[]? value))
            {
                slots.Add(new(KeySlot.KeyFile, value!));
            }
            else
            {
                complete = false;
            }
        }
        return slots;
    }

    /// <summary>读一个位置上存着的东西;没有或读不出来为 null。</summary>
    internal byte[]? ReadSlot(KeySlot slot)
    {
        if (slot.IsKeyFile)
        {
            return File.Exists(KeyFilePath) && TryReadKeyFile(out byte[]? value) ? value : null;
        }
        if (Store is null)
        {
            return null;
        }
        MasterKeyStoreResult result = Store.Load(slot.KeychainId!);
        return result.Status == MasterKeyStoreStatus.Ok ? result.Key : null;
    }

    /// <summary>
    /// 把一个位置上的内容换成 <paramref name="value" />,读回逐字节核对过才算数。密钥文件先写临时文件、落盘、再改名覆盖,
    /// 中途断掉不会留下半份。
    /// </summary>
    /// <returns>写进去并核对过为 true;写不进、读不回、读回来不一样为 false(原内容在写不进时保持不变)。</returns>
    internal bool TryWriteSlot(KeySlot slot, byte[] value)
    {
        try
        {
            if (slot.IsKeyFile)
            {
                WriteKeyFileAtomically(KeyFilePath, value);
            }
            else if (Store is null || Store.Save(slot.KeychainId!, value).Status != MasterKeyStoreStatus.Ok)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Trace.WriteLine($"[Secrets] {slot} could not be written ({ex.Message})");
            return false;
        }
        byte[]? readBack = ReadSlot(slot);
        bool same = readBack is not null && CryptographicOperations.FixedTimeEquals(readBack, value);
        if (!same)
        {
            Trace.WriteLine($"[Secrets] {slot} did not read back what was written");
        }
        return same;
    }

    /// <summary>原子地写 KDF 头(临时文件 + 落盘 + 改名)。写不进返回 false,原来那份不动。</summary>
    internal bool TryWriteHeader(MasterPasswordHeader header)
    {
        string temp = HeaderFilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeaderFilePath)!);
            WriteDurably(temp, Encoding.UTF8.GetBytes(header.Serialize()));
            File.Move(temp, HeaderFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {HeaderFilePath} could not be written ({ex.Message})");
            return false;
        }
    }

    /// <summary>读 KDF 头;不在或读不出来为 null。</summary>
    internal MasterPasswordHeader? ReadHeader()
    {
        try
        {
            return File.Exists(HeaderFilePath) ? MasterPasswordHeader.Parse(File.ReadAllText(HeaderFilePath, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>删掉 KDF 头(关闭主密码的最后一步);不在也算成功。</summary>
    internal bool TryDeleteHeader()
    {
        try
        {
            File.Delete(HeaderFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {HeaderFilePath} could not be removed ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// 「忘记主密码」:头、清单、本地密钥文件都改名保留(<c>名字.forgotten-时间戳</c>),密钥链里的条目原样不动。
    /// 之后再解析就是一个全新的开始(另起新密钥)。想起主密码时把这几个文件改回原名即可恢复。
    /// </summary>
    /// <returns>改名保留成的文件名;任何一个改不了名时抛出(已改的不回滚 —— 再做一次会接着改剩下的)。</returns>
    internal IReadOnlyList<string> SetAsideForForgottenPassword()
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        List<string> kept = [];
        foreach (string path in new[] { HeaderFilePath, ReferenceFilePath, KeyFilePath })
        {
            if (File.Exists(path))
            {
                string aside = $"{path}.forgotten-{stamp}";
                File.Move(path, aside, overwrite: false);
                kept.Add(aside);
            }
        }
        return kept;
    }

    // ———— 清单文件 ————

    /// <summary>读清单;不存在为空。读不出来时 <paramref name="writable" /> 为 false(这一轮不许覆盖它)。</summary>
    private List<string> ReadReference(out bool writable)
    {
        writable = true;
        string[] lines;
        try
        {
            if (!File.Exists(ReferenceFilePath))
            {
                return [];
            }
            lines = File.ReadAllLines(ReferenceFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {ReferenceFilePath} could not be read ({ex.Message}); it will not be rewritten this run");
            writable = false;
            return [];
        }
        List<string> ids = ParseReference(lines);
        if (ids.Count == 0)
        {
            // 文件在、却一个编号也认不出(被截断、被改坏):原样挪开保留,别让之后写的新清单把它盖掉。
            // 挪不开就这一轮不写清单,新密钥落到本地文件。
            try
            {
                Trace.WriteLine($"[Secrets] {ReferenceFilePath} lists no usable entry; kept it as '{MoveAside(ReferenceFilePath)}'");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[Secrets] {ReferenceFilePath} lists no usable entry and could not be moved aside ({ex.Message})");
                writable = false;
            }
        }
        return ids;
    }

    private static List<string> ParseReference(string[] lines)
    {
        List<string> ids = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith(KeyLinePrefix, StringComparison.Ordinal)
                && line[KeyLinePrefix.Length..] is { Length: KeyIdLength } id
                && id.All(static c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c))
                && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>原子地写清单:先写同目录的临时文件并落盘,再改名覆盖。写不进去返回 false,原清单不动。</summary>
    private bool TryWriteReference(List<string> ids, bool writable)
    {
        if (!writable)
        {
            return false;
        }
        StringBuilder text = new();
        text.Append("# VelaShell: the local encryption key is kept in the system keychain (").Append(Store?.Name).Append(").\n");
        text.Append("# This file only lists keychain entry ids, no key material. Without those entries saved passwords cannot be\n");
        text.Append("# decrypted - copying the data directory to another computer or account does not bring the key along.\n");
        foreach (string id in ids)
        {
            text.Append(KeyLinePrefix).Append(id).Append('\n');
        }
        string temp = ReferenceFilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReferenceFilePath)!);
            WriteDurably(temp, Encoding.UTF8.GetBytes(text.ToString()));
            File.Move(temp, ReferenceFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {ReferenceFilePath} could not be written ({ex.Message})");
            return false;
        }
    }

    // ———— 本地密钥文件 ————

    /// <summary>
    /// 读本地密钥文件;不存在为 null。返回 32 字节的明文密钥,或者主密码包裹过的那一份(<c>VSMP1</c> ‖ 记录)。
    /// 文件在、却用不了(换了 Windows 账户 / 重装系统后 DPAPI 解不开,或者被截断)时
    /// 先原样挪开再返回 null,<paramref name="replacedWith" /> 给出挪去的文件名。
    /// </summary>
    private static byte[]? LoadKeyFile(string keyFilePath, out string? replacedWith)
    {
        replacedWith = null;
        if (!File.Exists(keyFilePath))
        {
            return null;
        }
        byte[] stored = File.ReadAllBytes(keyFilePath);

        // Windows:密钥以 DPAPI(CurrentUser)密文落盘。历史版本可能是 32 字节明文,
        // 解包失败时回退按明文处理,并顺带升级为 DPAPI 密文。
        if (OperatingSystem.IsWindows())
        {
            if (TryDpapiUnprotect(stored, out byte[] unwrapped)
                && (unwrapped.Length == KeySize || MasterKeyEnvelope.TryParse(unwrapped, out _)))
            {
                return unwrapped;
            }
            if (stored.Length == KeySize)
            {
                WriteKeyFile(keyFilePath, stored); // 明文 → DPAPI 密文迁移
                return stored;
            }
        }
        else if (stored.Length == KeySize || MasterKeyEnvelope.TryParse(stored, out _))
        {
            return stored;
        }

        // 直接覆盖的话,换回原账户、找回原密钥的那条路就被这里亲手堵死了,
        // 连"密钥被换过"这件事本身都查无实据 —— 用它加密的连接口令与插件信任库会一并变成永远解不开的密文。
        replacedWith = MoveAside(keyFilePath);
        Trace.WriteLine($"[Secrets] {keyFilePath} could not be used; kept it as '{replacedWith}'");
        return null;
    }

    /// <summary>读本地密钥文件的内容(Windows 上解开 DPAPI);读不出来为 false。不挪任何文件。</summary>
    private bool TryReadKeyFile(out byte[]? value)
    {
        value = null;
        try
        {
            byte[] stored = File.ReadAllBytes(KeyFilePath);
            if (!OperatingSystem.IsWindows())
            {
                value = stored;
                return true;
            }
            if (TryDpapiUnprotect(stored, out byte[] unwrapped))
            {
                value = unwrapped;
                return true;
            }
            if (stored.Length == KeySize)
            {
                value = stored; // 历史版本的明文,下次解析时改包 DPAPI
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>写入密钥:Windows 用 DPAPI(CurrentUser)包裹,其余平台明文 + 0600 权限(创建时即 0600,不留窗口)。</summary>
    private static void WriteKeyFile(string keyFilePath, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(keyFilePath, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
            return;
        }
        WriteDurably(keyFilePath, key);
        File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>同 <see cref="WriteKeyFile" />,但先写临时文件、落盘、再改名覆盖:替换一份已有的密钥文件时不留下半份。</summary>
    private static void WriteKeyFileAtomically(string keyFilePath, byte[] value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        string temp = keyFilePath + ".tmp";
        byte[] content = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser)
            : value;
        WriteDurably(temp, content);
        File.Move(temp, keyFilePath, overwrite: true);
    }

    /// <summary>
    /// 迁移完成后删掉本地密钥文件:先改名成中转名,再就地写零、落盘、删除。
    /// </summary>
    /// <remarks>
    /// 写零是因为删除只摘掉目录项,数据块在被复用前一直留在磁盘上;SSD 的磨损均衡与写时复制文件系统
    /// (APFS / btrfs)下这只是尽力而为。先改名是为了中途断掉时不留下一个「全零的 secret.key」——
    /// 那会在下次启动被当成一把明文密钥读回来。
    /// </remarks>
    private static bool TryDeleteKeyFile(string keyFilePath)
    {
        string deleting = keyFilePath + DeletingSuffix;
        try
        {
            File.Move(keyFilePath, deleting, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {keyFilePath} is already in the keychain but could not be removed ({ex.Message}); will retry next start");
            return false;
        }
        ScrubAndDelete(deleting);
        return true;
    }

    /// <summary>收尾上一次没删完的中转文件(改名之后、删除之前被打断)。</summary>
    private void FinishPendingDelete()
    {
        string deleting = KeyFilePath + DeletingSuffix;
        if (File.Exists(deleting))
        {
            ScrubAndDelete(deleting);
        }
    }

    private static void ScrubAndDelete(string path)
    {
        try
        {
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[stream.Length]);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] {path} could not be scrubbed ({ex.Message}); will retry next start");
        }
    }

    /// <summary>写文件并落盘;非 Windows 上创建时即 0600。</summary>
    private static void WriteDurably(string path, byte[] content)
    {
        FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using FileStream stream = new(path, options);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>把一个用不了的文件原样改名保留(<c>名字.原因-时间戳</c>),返回新文件名。</summary>
    private static string MoveAside(string path, string reason = "unreadable")
    {
        string aside = $"{path}.{reason}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, aside, overwrite: true);
        return aside;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDpapiUnprotect(byte[] wrapped, out byte[] key)
    {
        try
        {
            key = ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser);
            return true;
        }
        catch (CryptographicException)
        {
            key = [];
            return false;
        }
    }
}
