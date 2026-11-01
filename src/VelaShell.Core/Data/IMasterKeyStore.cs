namespace VelaShell.Core.Data;

/// <summary>
/// 系统密钥链里的一个存放位置:按条目编号存取 <see cref="ISecretProtector" /> 用的本机加密密钥 ——
/// 32 字节的密钥本身,或者开了主密码时包裹过的那一份(<c>VSMP1</c> ‖ 记录,几百字节以内)。实现不解释内容,原样存取。
/// </summary>
/// <remarks>
/// <para>
/// 平台差异只存在于这一层(velashell-docs《系统密钥链与 sudo 凭据填充可行性调研》§3):macOS 钥匙串、
/// Linux 的 Secret Service 各一个实现。Windows 没有实现 —— 那里的密钥文件本来就以 DPAPI(当前用户)包裹落盘,
/// 已经是密钥链级别的保护,改存别处只多一次迁移、不多一分安全。
/// </para>
/// <para>
/// 只管「存在哪」,不管「先查哪个、查不到怎么办」:回落到文件密钥、迁移存量密钥文件、多把密钥并存,
/// 都是上层解析器(<c>MasterKeyResolver</c>)的事,各平台实现互相不知道对方存在。
/// </para>
/// <para>
/// <b>同步接口是刻意的</b>:密钥只在进程里解析一次(启动时在后台线程上做完),之后
/// <see cref="ISecretProtector" /> 的同步调用方一律用内存里那份,不会把 async 传染到全仓。
/// 但实现可能阻塞数秒(钥匙串 / keyring 解锁要弹系统对话框,Secret Service 是 D-Bus 往返),不要在 UI 线程上调。
/// </para>
/// <para>
/// <b>任何失败都不抛异常</b>:库不存在、没有 D-Bus、用户点了拒绝,一律以
/// <see cref="MasterKeyStoreStatus.Unavailable" /> 交回,由上层回落。
/// </para>
/// </remarks>
public interface IMasterKeyStore
{
    /// <summary>给日志与提示看的后端名(如 <c>macOS Keychain</c>、<c>Secret Service</c>)。</summary>
    string Name { get; }

    /// <summary>按条目编号取回密钥。</summary>
    /// <param name="keyId">条目编号(由密钥本身派生,见 <c>MasterKeyResolver.KeyIdOf</c>)。</param>
    /// <returns>
    /// 找到时 <see cref="MasterKeyStoreStatus.Ok" /> 并带密钥;密钥链能用但没有这一条时 <see cref="MasterKeyStoreStatus.NotFound" />;
    /// 密钥链此刻用不了时 <see cref="MasterKeyStoreStatus.Unavailable" /> 并带原因。
    /// </returns>
    MasterKeyStoreResult Load(string keyId);

    /// <summary>以条目编号存入(已有同编号条目时覆盖)。</summary>
    /// <param name="keyId">条目编号。</param>
    /// <param name="key">要存的值(32 字节密钥,或包裹过的那一份)。</param>
    /// <returns>存好时 <see cref="MasterKeyStoreStatus.Ok" />,否则 <see cref="MasterKeyStoreStatus.Unavailable" /> 并带原因。</returns>
    MasterKeyStoreResult Save(string keyId, ReadOnlySpan<byte> key);
}

/// <summary>一次密钥链存取的结果分类。</summary>
public enum MasterKeyStoreStatus
{
    /// <summary>成功(读到了 / 存好了)。</summary>
    Ok,

    /// <summary>密钥链能用,但没有这个编号的条目。</summary>
    NotFound,

    /// <summary>密钥链此刻用不了:库不存在、没有会话总线、keyring 锁着且用户取消了解锁、访问被拒……</summary>
    Unavailable
}

/// <summary>一次密钥链存取的结果。</summary>
/// <param name="Status">结果分类。</param>
/// <param name="Key">读到的密钥(仅 <see cref="IMasterKeyStore.Load" /> 成功时有)。</param>
/// <param name="Error">用不了的原因(给日志与提示看,不含密钥材料)。</param>
public readonly record struct MasterKeyStoreResult(MasterKeyStoreStatus Status, byte[]? Key = null, string? Error = null)
{
    /// <summary>读到了密钥。</summary>
    public static MasterKeyStoreResult Found(byte[] key) => new(MasterKeyStoreStatus.Ok, key);

    /// <summary>存好了。</summary>
    public static MasterKeyStoreResult Saved => new(MasterKeyStoreStatus.Ok);

    /// <summary>没有这一条。</summary>
    public static MasterKeyStoreResult Missing => new(MasterKeyStoreStatus.NotFound);

    /// <summary>密钥链此刻用不了。</summary>
    public static MasterKeyStoreResult Unavailable(string error) => new(MasterKeyStoreStatus.Unavailable, Error: error);
}
