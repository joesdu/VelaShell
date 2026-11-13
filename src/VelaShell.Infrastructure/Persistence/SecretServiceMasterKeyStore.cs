using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// Linux 的 Secret Service(<c>org.freedesktop.secrets</c>,由 gnome-keyring / KWallet 等提供),经 libsecret 存取。
/// </summary>
/// <remarks>
/// <para>
/// 条目:schema <see cref="SchemaName" />、属性 <c>key-id</c> = 条目编号,存在默认集合(通常是「登录」钥匙环)里,
/// 值是密钥的 base64。Seahorse(「密码和密钥」)里看得到;命令行可用
/// <c>secret-tool search --all xdg:schema com.velashell.MasterKey</c> 查看。
/// </para>
/// <para>
/// <b>不一定存在</b>:纯 SSH 登录、无桌面会话、精简发行版上可能没装 libsecret、没有会话总线、没有 keyring 守护进程,
/// keyring 锁着时用户也可能取消解锁框。所有这些一律以「用不了」交回,上层回落到本地密钥文件并在日志里写明原因。
/// </para>
/// <para>
/// <b>两种看上去「好好的」情形也算用不了</b>(#750):一是钥匙环锁着、解锁框被取消或没弹出来 —— libsecret 的 lookup
/// 这时交回 NULL 而不带错误,与「条目不在」分不开,所以 NULL 之后不弹框地再搜一遍,条目在而锁着就报用不了;
/// 二是默认集合指向 session 集合(只在内存里,重启即清空)—— 写进去、读回核对都过得去,重启后密钥就没了,
/// 所以写之前先看默认别名指向哪个集合。
/// </para>
/// <para>
/// 只用 libsecret 里不带变参的 <c>*v_sync</c> 版本:变参函数经 P/Invoke 调用在 x86-64 上要额外设 <c>%al</c>,
/// 照普通函数声明属于未定义行为。属性表用 GLib 的 <c>GHashTable</c> 传。sync 版本自带 main context,任何线程上都能调,
/// 但 keyring 锁着时会等用户在系统解锁框里作答 —— 所以只在启动时的后台线程上解析一次。
/// </para>
/// </remarks>
internal sealed unsafe partial class SecretServiceMasterKeyStore : IMasterKeyStore
{
    /// <summary>libsecret 的库名。</summary>
    internal const string LibSecret = "libsecret-1.so.0";

    /// <summary>条目的 schema 名(Seahorse 里显示的类型、<c>secret-tool</c> 按它搜)。</summary>
    internal const string SchemaName = "com.velashell.MasterKey";

    private const string LibGlib = "libglib-2.0.so.0";
    private const string KeyIdAttribute = "key-id";
    private const int SchemaAttributeString = 0; // SECRET_SCHEMA_ATTRIBUTE_STRING
    private const int MaxBase64ValueLength = 4096; // 32 字节的密钥是 44 个字符,包裹过的那份也只有几百个
    private const int SearchAll = 1 << 1; // SECRET_SEARCH_ALL:锁着的条目也列出来;不带 UNLOCK / LOAD_SECRETS,不弹框
    private const string DefaultAlias = "default";
    private const string SessionCollectionSuffix = "/collection/session";
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibGobject = "libgobject-2.0.so.0";

    /// <summary>条目在、钥匙环锁着(解锁框被取消或没弹出来)时交回的原因。</summary>
    internal const string LockedReason = "the keyring holding the key is locked (the unlock prompt was dismissed or could not be shown)";

    /// <summary>默认集合是 session 集合时交回的原因。</summary>
    internal const string SessionCollectionReason =
        "the default keyring is the session keyring, which is kept only in memory and cleared on restart";

    private static readonly Lazy<nint> Schema = new(BuildSchema, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<(nint Hash, nint Equal)> StringFunctions = new(LoadStringFunctions, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _label;
    private readonly string _libraryProbe;

    /// <summary>构造。</summary>
    /// <param name="dataRoot">数据根(写进条目标签:同一用户开了几个 <c>--data-root</c> 时在 Seahorse 里分得清)。</param>
    /// <param name="libraryProbe">先探测的库名(测试里换成一个不存在的库,验证「没装 libsecret」时的回落)。</param>
    public SecretServiceMasterKeyStore(string dataRoot, string libraryProbe = LibSecret)
    {
        _label = $"VelaShell local encryption key ({dataRoot})";
        _libraryProbe = libraryProbe;
    }

    /// <inheritdoc />
    public string Name => "Secret Service";

    /// <inheritdoc />
    public MasterKeyStoreResult Load(string keyId)
    {
        if (Unusable() is { } reason)
        {
            return MasterKeyStoreResult.Unavailable(reason);
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
        if (Unusable() is { } reason)
        {
            return MasterKeyStoreResult.Unavailable(reason);
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

    /// <summary>不是 Linux、或者 libsecret 没装时给出原因;能试时为 null。</summary>
    private string? Unusable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "not running on Linux";
        }
        if (!NativeLibrary.TryLoad(_libraryProbe, out nint handle))
        {
            return $"{_libraryProbe} is not installed";
        }
        NativeLibrary.Free(handle);
        return null;
    }

    /// <summary>
    /// 删掉一条(只给真 Secret Service 的手动用例收尾:应用自己从不删密钥 —— 删了就再也解不开用它加密的密码)。
    /// </summary>
    internal bool Delete(string keyId)
    {
        if (Unusable() is not null)
        {
            return false;
        }
        nint attributes = NewAttributes(keyId, out nint name, out nint value);
        nint error = 0;
        try
        {
            return secret_password_clearv_sync(Schema.Value, attributes, 0, &error) != 0 && error == 0;
        }
        finally
        {
            if (error != 0)
            {
                g_error_free(error);
            }
            g_hash_table_unref(attributes);
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static MasterKeyStoreResult LoadCore(string keyId)
    {
        nint attributes = NewAttributes(keyId, out nint name, out nint value);
        nint error = 0;
        try
        {
            nint password = secret_password_lookupv_sync(Schema.Value, attributes, 0, &error);
            if (error != 0)
            {
                return MasterKeyStoreResult.Unavailable(ErrorText(error));
            }
            if (password == 0)
            {
                // 钥匙环锁着、解锁框被取消(或根本没弹出来)时 libsecret 同样交回 NULL 而不带错误(#750:自动登录的机器
                // 重启后登录钥匙环不会自动解锁)。不弹框地再查一遍:条目在、只是锁着,就是「这次用不了」而不是「没有」——
                // 报成「没有」的话上层会另起一把新密钥,已存的密码在这一轮全都解不开。
                return HasLockedEntry(attributes)
                    ? MasterKeyStoreResult.Unavailable(LockedReason)
                    : MasterKeyStoreResult.Missing;
            }
            try
            {
                // 直接在原生的 UTF-8 缓冲上解 base64:密钥不经过托管字符串(那份没法清零)。
                // 值可能是 32 字节的密钥,也可能是主密码包裹过的那一份(VSMP1 ‖ 记录,几百字节以内):长度交给上层判断。
                ReadOnlySpan<byte> text = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)password);
                if (text.Length > MaxBase64ValueLength)
                {
                    return MasterKeyStoreResult.Found([]); // 不是我们写的东西:交给上层按「对不上编号」处理
                }
                byte[] decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
                if (Base64.DecodeFromUtf8(text, decoded, out _, out int written) != OperationStatus.Done)
                {
                    CryptographicOperations.ZeroMemory(decoded);
                    return MasterKeyStoreResult.Found([]); // 内容不是一把密钥:交给上层按「对不上编号」处理
                }
                byte[] key = decoded[..written];
                CryptographicOperations.ZeroMemory(decoded);
                return MasterKeyStoreResult.Found(key);
            }
            finally
            {
                secret_password_free(password); // 先清零再释放
            }
        }
        finally
        {
            if (error != 0)
            {
                g_error_free(error);
            }
            g_hash_table_unref(attributes);
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private MasterKeyStoreResult SaveCore(string keyId, ReadOnlySpan<byte> key)
    {
        if (DefaultCollectionIsSession())
        {
            // 默认集合指向 gnome-keyring 的 session 集合(没有「登录」钥匙环、或默认钥匙环被设成了它):
            // 那里的条目只在内存里,写进去、读回核对都没问题,重启之后就没了(#750)。当成用不了,上层回落到本地密钥文件。
            return MasterKeyStoreResult.Unavailable(SessionCollectionReason);
        }
        nint attributes = NewAttributes(keyId, out nint name, out nint value);
        nint label = Marshal.StringToCoTaskMemUTF8(_label);
        int encodedLength = Base64.GetMaxEncodedToUtf8Length(key.Length);
        byte* secret = (byte*)NativeMemory.AllocZeroed((nuint)encodedLength + 1);
        nint error = 0;
        try
        {
            if (Base64.EncodeToUtf8(key, new Span<byte>(secret, encodedLength), out _, out int written) != OperationStatus.Done)
            {
                return MasterKeyStoreResult.Unavailable("key could not be encoded");
            }
            secret[written] = 0;
            // collection 传 null = 默认集合(SECRET_COLLECTION_DEFAULT);同属性的条目已在时 libsecret 就地替换。
            int stored = secret_password_storev_sync(Schema.Value, attributes, 0, label, secret, 0, &error);
            if (error != 0)
            {
                return MasterKeyStoreResult.Unavailable(ErrorText(error));
            }
            return stored != 0 ? MasterKeyStoreResult.Saved : MasterKeyStoreResult.Unavailable("secret_password_storev_sync failed");
        }
        finally
        {
            NativeMemory.Clear(secret, (nuint)encodedLength + 1);
            NativeMemory.Free(secret);
            if (error != 0)
            {
                g_error_free(error);
            }
            g_hash_table_unref(attributes);
            Marshal.FreeCoTaskMem(label);
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>
    /// 不解锁、不弹框地按同样的属性搜一遍(<c>SECRET_SEARCH_ALL</c>,锁着的条目也列出来),有锁着的就为 true。
    /// 搜不成(出错、旧版 libsecret 没有这几个入口)时为 false —— 说不准就照旧按「没有」处理。
    /// </summary>
    private static bool HasLockedEntry(nint attributes)
    {
        nint error = 0;
        nint list = 0;
        try
        {
            list = secret_service_search_sync(0, Schema.Value, attributes, SearchAll, 0, &error);
            if (error != 0)
            {
                return false;
            }
            for (var node = (GList*)list; node is not null; node = node->Next)
            {
                if (node->Data != 0 && secret_item_get_locked(node->Data) != 0)
                {
                    return true;
                }
            }
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            for (var node = (GList*)list; node is not null; node = node->Next)
            {
                if (node->Data != 0)
                {
                    g_object_unref(node->Data);
                }
            }
            if (list != 0)
            {
                g_list_free(list);
            }
            if (error != 0)
            {
                g_error_free(error);
            }
        }
    }

    /// <summary>
    /// 默认集合(别名 <c>default</c>)是不是 session 集合 —— gnome-keyring 只放在内存里、重启就清空的那个。
    /// 别名不存在、查不成、旧版 libsecret 没有这个入口时为 false(交给存的那一步自己成败)。
    /// </summary>
    private static bool DefaultCollectionIsSession()
    {
        nint alias = Marshal.StringToCoTaskMemUTF8(DefaultAlias);
        nint error = 0;
        nint collection = 0;
        try
        {
            collection = secret_collection_for_alias_sync(0, alias, 0, 0, &error);
            if (error != 0 || collection == 0)
            {
                return false;
            }
            return IsSessionCollectionPath(Marshal.PtrToStringUTF8(g_dbus_proxy_get_object_path(collection)));
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (collection != 0)
            {
                g_object_unref(collection);
            }
            if (error != 0)
            {
                g_error_free(error);
            }
            Marshal.FreeCoTaskMem(alias);
        }
    }

    /// <summary>D-Bus 对象路径指的是 session 集合(<c>/org/freedesktop/secrets/collection/session</c>)。</summary>
    internal static bool IsSessionCollectionPath(string? objectPath) =>
        objectPath is not null && objectPath.EndsWith(SessionCollectionSuffix, StringComparison.Ordinal);

    /// <summary>
    /// 属性表 <c>{ "key-id": 编号 }</c>。表不带销毁回调,键与值由调用方在 unref 之后自己释放。
    /// </summary>
    private static nint NewAttributes(string keyId, out nint name, out nint value)
    {
        (nint hash, nint equal) = StringFunctions.Value;
        nint table = g_hash_table_new(hash, equal);
        name = Marshal.StringToCoTaskMemUTF8(KeyIdAttribute);
        value = Marshal.StringToCoTaskMemUTF8(keyId);
        g_hash_table_insert(table, name, value);
        return table;
    }

    private static string ErrorText(nint error)
    {
        var e = (GError*)error;
        return $"{Marshal.PtrToStringUTF8((nint)e->Message) ?? "unknown error"} (GError {e->Domain}/{e->Code})";
    }

    /// <summary>
    /// 结构体布局自检(测试用)。64 位上 C 侧是:<c>SecretSchema</c> 592 字节、属性槽从偏移 16 开始、
    /// 保留的 gint 在 528;<c>SecretSchemaAttribute</c> 16 字节;<c>GError.message</c> 在偏移 8。
    /// 这几个数在 Windows 上也算得出来,布局写错了不必等到 Linux 真机才发现。
    /// </summary>
    internal static (int SchemaSize, int AttributesOffset, int ReservedOffset, int AttributeSize, int ErrorMessageOffset) Layout()
    {
        SecretSchema schema = default;
        GError error = default;
        byte* schemaBase = (byte*)&schema;
        byte* errorBase = (byte*)&error;
        return (sizeof(SecretSchema), (int)((byte*)&schema.Attributes - schemaBase), (int)((byte*)&schema.Reserved - schemaBase),
            sizeof(SecretSchemaAttribute), (int)((byte*)&error.Message - errorBase));
    }

    /// <summary><c>GList</c> 的布局自检(测试用):64 位上 24 字节,<c>next</c> 在偏移 8。</summary>
    internal static (int Size, int NextOffset) ListLayout()
    {
        GList node = default;
        return (sizeof(GList), (int)((byte*)&node.Next - (byte*)&node));
    }

    private static (nint, nint) LoadStringFunctions()
    {
        nint glib = NativeLibrary.Load(LibGlib);
        return (NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
    }

    /// <summary>
    /// 造一份常驻的 <c>SecretSchema</c>(进程内只造一次,不释放:libsecret 在调用期间引用它,而它本来就该活到进程结束)。
    /// </summary>
    private static nint BuildSchema()
    {
        var schema = (SecretSchema*)NativeMemory.AllocZeroed((nuint)sizeof(SecretSchema));
        schema->Name = Marshal.StringToCoTaskMemUTF8(SchemaName);
        schema->Flags = 0; // SECRET_SCHEMA_NONE:按 schema 名匹配(xdg:schema)
        schema->Attributes[0] = new() { Name = Marshal.StringToCoTaskMemUTF8(KeyIdAttribute), Type = SchemaAttributeString };
        // 其余属性槽保持全零:name 为 NULL 的那一项即表尾。
        return (nint)schema;
    }

    /// <summary><c>GError</c>:<c>{ GQuark domain; gint code; gchar *message; }</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public byte* Message;
    }

    /// <summary><c>GList</c>:<c>{ gpointer data; GList *next; GList *prev; }</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GList
    {
        public nint Data;
        public GList* Next;
        public GList* Prev;
    }

    /// <summary><c>SecretSchemaAttribute</c>:<c>{ const gchar *name; SecretSchemaAttributeType type; }</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        public nint Name;
        public int Type;
    }

    [InlineArray(32)]
    private struct SecretSchemaAttributes
    {
        private SecretSchemaAttribute _element;
    }

    /// <summary>
    /// <c>SecretSchema</c>(libsecret 的 secret-schema.h):名字、标志、32 个属性槽,以及保留字段(一个 gint、七个指针)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        public nint Name;
        public int Flags;
        public SecretSchemaAttributes Attributes;
        public int Reserved;
        public nint Reserved1;
        public nint Reserved2;
        public nint Reserved3;
        public nint Reserved4;
        public nint Reserved5;
        public nint Reserved6;
        public nint Reserved7;
    }

    [LibraryImport(LibSecret)]
    private static partial nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial int secret_password_storev_sync(nint schema, nint attributes, nint collection, nint label,
        byte* password, nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial int secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial void secret_password_free(nint password);

    /// <remarks><paramref name="service" /> 传 0:libsecret 自己取默认的 <c>SecretService</c>(同 lookup / store 的做法)。</remarks>
    [LibraryImport(LibSecret)]
    private static partial nint secret_service_search_sync(nint service, nint schema, nint attributes, int flags,
        nint cancellable, nint* error);

    [LibraryImport(LibSecret)]
    private static partial int secret_item_get_locked(nint item);

    /// <remarks>别名不存在时交回 NULL 且不带错误。<paramref name="service" /> 传 0 同上。</remarks>
    [LibraryImport(LibSecret)]
    private static partial nint secret_collection_for_alias_sync(nint service, nint alias, int flags, nint cancellable,
        nint* error);

    [LibraryImport(LibGio)]
    private static partial nint g_dbus_proxy_get_object_path(nint proxy);

    [LibraryImport(LibGobject)]
    private static partial void g_object_unref(nint instance);

    [LibraryImport(LibGlib)]
    private static partial void g_list_free(nint list);

    [LibraryImport(LibGlib)]
    private static partial nint g_hash_table_new(nint hashFunc, nint keyEqualFunc);

    [LibraryImport(LibGlib)]
    private static partial void g_hash_table_insert(nint table, nint key, nint value);

    [LibraryImport(LibGlib)]
    private static partial void g_hash_table_unref(nint table);

    [LibraryImport(LibGlib)]
    private static partial void g_error_free(nint error);
}
