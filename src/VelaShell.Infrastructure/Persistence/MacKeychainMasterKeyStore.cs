using System.Runtime.InteropServices;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// macOS 钥匙串:本机加密密钥存成登录钥匙串里的一条「通用密码」(Security.framework 的 <c>SecItem*</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 条目:服务 <see cref="Service" />、账户 = 条目编号、标签「VelaShell local encryption key」,在「钥匙串访问」里按 VelaShell 搜得到;
/// 删掉它,用它加密的密码就解不开了(数据目录里的清单还在,删掉的那一把会如实报「找不到」)。
/// </para>
/// <para>
/// <b>访问控制放开到「任何应用」</b>(等价于 <c>security add-generic-password -A</c>)。默认的访问控制只认创建它的那个程序的
/// 代码签名,而发行包是 ad-hoc 签名(<c>release.yml</c> 的 <c>codesign --sign -</c>),每发一版签名就变 ——
/// 每次更新后第一次启动都会弹「VelaShell 想要使用钥匙串中的机密信息」并要求输入登录密码。放开之后与原先 0600 的密钥文件一样,
/// 本用户的进程都读得到,但密钥不再躺在数据目录里:拷走、备份、同步数据目录都带不走它 —— 这一步要解决的正是这个。
/// 有了稳定的 Developer ID 签名之后可以改回默认(只认 VelaShell),见 <see cref="CreateOpenAccess" />。
/// 放开失败(弃用的 SecAccess 接口哪天没了)时退回默认访问控制,只是多弹授权框,不影响存取。
/// </para>
/// <para>
/// 用户在授权框里点了拒绝、钥匙串锁着又不许交互时,一律以「用不了」交回,上层回落到本地密钥文件、不丢数据。
/// 这些错误码只能在真机上验证(CI 只编译到),见 velashell-docs 的密钥链调研 §2。
/// </para>
/// </remarks>
internal sealed unsafe partial class MacKeychainMasterKeyStore : IMasterKeyStore
{
    /// <summary>钥匙串条目的服务名。</summary>
    internal const string Service = "com.velashell.master-key";

    private const string Label = "VelaShell local encryption key";
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    private static readonly Lazy<Symbols> Sym = new(static () => new Symbols(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc />
    public string Name => "macOS Keychain";

    /// <inheritdoc />
    public MasterKeyStoreResult Load(string keyId)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return MasterKeyStoreResult.Unavailable("not running on macOS");
        }
        try
        {
            return LoadCore(keyId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return MasterKeyStoreResult.Unavailable(ex.Message);
        }
    }

    /// <inheritdoc />
    public MasterKeyStoreResult Save(string keyId, ReadOnlySpan<byte> key)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return MasterKeyStoreResult.Unavailable("not running on macOS");
        }
        try
        {
            return SaveCore(keyId, key);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return MasterKeyStoreResult.Unavailable(ex.Message);
        }
    }

    /// <summary>
    /// 删掉一条(只给真钥匙串的手动用例收尾:应用自己从不删密钥 —— 删了就再也解不开用它加密的密码)。
    /// </summary>
    internal static bool Delete(string keyId)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        Symbols s = Sym.Value;
        nint service = CfString(Service);
        nint account = CfString(keyId);
        nint query = 0;
        try
        {
            query = CfDictionary([s.Class, s.AttrService, s.AttrAccount], [s.ClassGenericPassword, service, account]);
            return SecItemDelete(query) == ErrSecSuccess;
        }
        finally
        {
            Release(query);
            Release(account);
            Release(service);
        }
    }

    private static MasterKeyStoreResult LoadCore(string keyId)
    {
        Symbols s = Sym.Value;
        nint service = CfString(Service);
        nint account = CfString(keyId);
        nint query = 0;
        try
        {
            query = CfDictionary(
                [s.Class, s.AttrService, s.AttrAccount, s.ReturnData, s.MatchLimit],
                [s.ClassGenericPassword, service, account, s.BooleanTrue, s.MatchLimitOne]);
            int status = SecItemCopyMatching(query, out nint result);
            if (status == ErrSecItemNotFound)
            {
                return MasterKeyStoreResult.Missing;
            }
            if (status != ErrSecSuccess)
            {
                return MasterKeyStoreResult.Unavailable(Describe(status));
            }
            try
            {
                if (result == 0 || CFGetTypeID(result) != CFDataGetTypeID())
                {
                    return MasterKeyStoreResult.Unavailable("keychain returned no data");
                }
                nint length = CFDataGetLength(result);
                return MasterKeyStoreResult.Found(new ReadOnlySpan<byte>(CFDataGetBytePtr(result), checked((int)length)).ToArray());
            }
            finally
            {
                Release(result);
            }
        }
        finally
        {
            Release(query);
            Release(service);
            Release(account);
        }
    }

    private static MasterKeyStoreResult SaveCore(string keyId, ReadOnlySpan<byte> key)
    {
        Symbols s = Sym.Value;
        nint service = CfString(Service);
        nint account = CfString(keyId);
        nint label = CfString(Label);
        nint data;
        fixed (byte* bytes = key)
        {
            data = CFDataCreate(0, bytes, key.Length);
        }
        nint access = CreateOpenAccess(s);
        nint attributes = 0;
        nint query = 0;
        nint update = 0;
        try
        {
            int status = ErrSecItemNotFound;
            if (access != 0)
            {
                attributes = CfDictionary([s.Class, s.AttrService, s.AttrAccount, s.AttrLabel, s.ValueData, s.AttrAccess],
                    [s.ClassGenericPassword, service, account, label, data, access]);
                status = SecItemAdd(attributes, 0);
            }
            if (status is not ErrSecSuccess and not ErrSecDuplicateItem and not (-128 or -25293 or -25308 or -25291 or -25294 or -25295))
            {
                // 没造出放开的访问控制,或者钥匙串不收它:退回默认访问控制(只多弹授权框,存取照常)。
                // 用户点了拒绝、钥匙串锁着 / 不存在这几种与访问控制无关,不再试第二次(免得再弹一次框)。
                Release(attributes);
                attributes = CfDictionary([s.Class, s.AttrService, s.AttrAccount, s.AttrLabel, s.ValueData],
                    [s.ClassGenericPassword, service, account, label, data]);
                status = SecItemAdd(attributes, 0);
            }
            if (status == ErrSecDuplicateItem)
            {
                // 同编号的条目已在(编号由密钥派生;开启 / 关闭主密码时就是要把明文换成包裹、或者换回来):只换数据,访问控制保持原样。
                query = CfDictionary([s.Class, s.AttrService, s.AttrAccount], [s.ClassGenericPassword, service, account]);
                update = CfDictionary([s.ValueData], [data]);
                status = SecItemUpdate(query, update);
            }
            return status == ErrSecSuccess ? MasterKeyStoreResult.Saved : MasterKeyStoreResult.Unavailable(Describe(status));
        }
        finally
        {
            Release(update);
            Release(query);
            Release(attributes);
            Release(access);
            Release(data);
            Release(label);
            Release(account);
            Release(service);
        }
    }

    /// <summary>
    /// 造一份「任何应用都可读」的访问控制(见类型注释);任何一步失败返回 0,调用方退回默认访问控制。
    /// </summary>
    /// <remarks>
    /// <c>SecAccessCreate</c> 传 null 的受信应用表 = 只信任调用方;再把「解密」那几条 ACL 的应用表改成 null = 任何应用。
    /// 这几个接口自 macOS 10.10 起标为弃用但仍然导出;文件型(登录)钥匙串只认这套写法。
    /// </remarks>
    private static nint CreateOpenAccess(Symbols s)
    {
        nint descriptor = CfString(Label);
        nint access = 0;
        nint acls = 0;
        try
        {
            if (SecAccessCreate(descriptor, 0, out access) != ErrSecSuccess || access == 0)
            {
                return 0;
            }
            acls = SecAccessCopyMatchingACLList(access, s.AclAuthorizationDecrypt);
            if (acls == 0)
            {
                return Discard(ref access);
            }
            nint count = CFArrayGetCount(acls);
            for (nint i = 0; i < count; i++)
            {
                nint acl = CFArrayGetValueAtIndex(acls, i);
                if (SecACLCopyContents(acl, out nint applications, out nint description, out ushort prompt) != ErrSecSuccess)
                {
                    return Discard(ref access);
                }
                try
                {
                    if (SecACLSetContents(acl, 0, description, prompt) != ErrSecSuccess)
                    {
                        return Discard(ref access);
                    }
                }
                finally
                {
                    Release(applications);
                    Release(description);
                }
            }
            nint result = access;
            access = 0;
            return result;
        }
        catch (EntryPointNotFoundException)
        {
            return Discard(ref access);
        }
        finally
        {
            Release(acls);
            Release(access);
            Release(descriptor);
        }

        static nint Discard(ref nint handle)
        {
            Release(handle);
            handle = 0;
            return 0;
        }
    }

    /// <summary>OSStatus 转成给日志看的一句话(常见的几种带名字)。</summary>
    internal static string Describe(int status) => status switch
    {
        -128 => "OSStatus -128 (errSecUserCanceled: access was denied in the prompt)",
        -25293 => "OSStatus -25293 (errSecAuthFailed)",
        -25308 => "OSStatus -25308 (errSecInteractionNotAllowed: the keychain is locked and no prompt can be shown)",
        -25291 => "OSStatus -25291 (errSecNotAvailable: no keychain is available)",
        -25294 => "OSStatus -25294 (errSecNoSuchKeychain)",
        -25295 => "OSStatus -25295 (errSecInvalidKeychain)",
        -34018 => "OSStatus -34018 (errSecMissingEntitlement)",
        -61 => "OSStatus -61 (errSecWrPerm)",
        -50 => "OSStatus -50 (errSecParam)",
        _ => $"OSStatus {status}"
    };

    private static nint CfString(string value)
    {
        fixed (char* chars = value)
        {
            return CFStringCreateWithCharacters(0, chars, value.Length);
        }
    }

    private static nint CfDictionary(ReadOnlySpan<nint> keys, ReadOnlySpan<nint> values)
    {
        Symbols s = Sym.Value;
        fixed (nint* k = keys)
        fixed (nint* v = values)
        {
            // kCFType 回调会 retain 键与值:造完字典后调用方照样释放自己手里那份。
            return CFDictionaryCreate(0, k, v, keys.Length, s.KeyCallBacks, s.ValueCallBacks);
        }
    }

    private static void Release(nint handle)
    {
        if (handle != 0)
        {
            CFRelease(handle);
        }
    }

    /// <summary>Security / CoreFoundation 导出的常量(全局 CFStringRef 变量:取变量地址再读出值)。</summary>
    private sealed class Symbols
    {
        public readonly nint Class, ClassGenericPassword, AttrService, AttrAccount, AttrLabel, AttrAccess;
        public readonly nint ValueData, ReturnData, MatchLimit, MatchLimitOne, AclAuthorizationDecrypt;
        public readonly nint BooleanTrue, KeyCallBacks, ValueCallBacks;

        public Symbols()
        {
            nint security = NativeLibrary.Load(SecurityFramework);
            nint cf = NativeLibrary.Load(CoreFoundation);
            Class = Read(security, "kSecClass");
            ClassGenericPassword = Read(security, "kSecClassGenericPassword");
            AttrService = Read(security, "kSecAttrService");
            AttrAccount = Read(security, "kSecAttrAccount");
            AttrLabel = Read(security, "kSecAttrLabel");
            AttrAccess = Read(security, "kSecAttrAccess");
            ValueData = Read(security, "kSecValueData");
            ReturnData = Read(security, "kSecReturnData");
            MatchLimit = Read(security, "kSecMatchLimit");
            MatchLimitOne = Read(security, "kSecMatchLimitOne");
            AclAuthorizationDecrypt = Read(security, "kSecACLAuthorizationDecrypt");
            BooleanTrue = Read(cf, "kCFBooleanTrue");
            // 这两个是结构体本身(不是指针变量),要的就是它们的地址。
            KeyCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
            ValueCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        }

        private static nint Read(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemCopyMatching(nint query, out nint result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemAdd(nint attributes, nint result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemUpdate(nint query, nint attributesToUpdate);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemDelete(nint query);

    [LibraryImport(SecurityFramework)]
    private static partial int SecAccessCreate(nint descriptor, nint trustedList, out nint access);

    [LibraryImport(SecurityFramework)]
    private static partial nint SecAccessCopyMatchingACLList(nint access, nint authorizationTag);

    [LibraryImport(SecurityFramework)]
    private static partial int SecACLCopyContents(nint acl, out nint applicationList, out nint description, out ushort promptSelector);

    [LibraryImport(SecurityFramework)]
    private static partial int SecACLSetContents(nint acl, nint applicationList, nint description, ushort promptSelector);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFDataGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
