using SonnetDB.Documents;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>shared_credentials</c> 的共享凭据仓储(#550,文档 Id 为 Guid 字符串)。
/// 密码与私钥口令经 <see cref="ISecretProtector" /> 加密后落盘,与连接配置里的密码同一强度。
/// </summary>
public sealed class SonnetDbSharedCredentialRepository(SonnetDbEngine engine, ISecretProtector protector)
    : ISharedCredentialRepository
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<List<SharedCredential>> GetAllAsync()
    {
        List<SharedCredential?> credentials = await _engine.WithCollectionAsync(SonnetDbEngine.SharedCredentialsCollection, store =>
            store.Scan().Select(row => SonnetDbJson.Deserialize<SharedCredential>(row.Json)).ToList()).ConfigureAwait(false);
        return [.. credentials.OfType<SharedCredential>()
                              .Select(Unprotect)
                              .OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public async Task<List<SharedCredentialMetadata>> ListMetadataAsync() =>
        await _engine.WithCollectionAsync(SonnetDbEngine.SharedCredentialsCollection, store =>
            store.Scan().Select(row => SonnetDbJson.Deserialize<SharedCredential>(row.Json))
                .OfType<SharedCredential>()
                .Select(c => new SharedCredentialMetadata(c.Id, c.Name, c.Username, c.AuthMethod, !string.IsNullOrEmpty(c.Password)))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string?> GetPasswordAsync(Guid id)
    {
        SharedCredential? credential = await _engine.WithCollectionAsync(SonnetDbEngine.SharedCredentialsCollection, store =>
        {
            DocumentRow? row = store.Get(id.ToString("D"));
            return row is null ? null : SonnetDbJson.Deserialize<SharedCredential>(row.Json);
        }).ConfigureAwait(false);
        if (credential is not { AuthMethod: Core.Models.AuthMethod.Password, Password.Length: > 0 })
        {
            return null;
        }
        string? password = _protector.Unprotect(credential.Password);
        // 保护器的历史契约在失败时保留密文;终端填入不能把这种失败当成可发送的密码。
        if (credential.Password.StartsWith("enc1:", StringComparison.Ordinal)
            && string.Equals(password, credential.Password, StringComparison.Ordinal))
        {
            throw new System.Security.Cryptography.CryptographicException();
        }
        return password;
    }

    /// <inheritdoc />
    public async Task<SharedCredential?> GetAsync(Guid id)
    {
        SharedCredential? credential = await _engine.WithCollectionAsync(SonnetDbEngine.SharedCredentialsCollection, store =>
        {
            DocumentRow? row = store.Get(id.ToString("D"));
            return row is null ? null : SonnetDbJson.Deserialize<SharedCredential>(row.Json);
        }).ConfigureAwait(false);
        return credential is null ? null : Unprotect(credential);
    }

    /// <inheritdoc />
    public async Task SaveAsync(SharedCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        // 主密码锁着:密码与私钥口令冻在库里那一份上(新的加不了密,旧的也不被空值顶掉),其余字段照常存。
        bool locked = _protector.IsLocked;
        SharedCredential copy = locked ? credential.Clone() : Protect(credential);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.SharedCredentialsCollection, store =>
        {
            string key = credential.Id.ToString("D");
            if (locked)
            {
                SharedCredential? stored = store.Get(key) is { } row ? SonnetDbJson.Deserialize<SharedCredential>(row.Json) : null;
                copy.Password = StoredSecret.IsUnreadable(copy.Password) ? copy.Password : stored?.Password;
                copy.PrivateKeyPassphrase = StoredSecret.IsUnreadable(copy.PrivateKeyPassphrase) ? copy.PrivateKeyPassphrase : stored?.PrivateKeyPassphrase;
            }
            store.Upsert(key, SonnetDbJson.Serialize(copy));
            return null;
        }).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id)
    {
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.SharedCredentialsCollection, store =>
        {
            store.Delete(id.ToString("D"));
            return null;
        }).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>返回加密副本 —— 不得修改调用方持有的实例(界面还拿着它的明文)。</summary>
    private SharedCredential Protect(SharedCredential credential)
    {
        SharedCredential copy = credential.Clone();
        copy.Password = _protector.Protect(credential.Password);
        copy.PrivateKeyPassphrase = _protector.Protect(credential.PrivateKeyPassphrase);
        return copy;
    }

    private SharedCredential Unprotect(SharedCredential credential)
    {
        credential.Password = _protector.Unprotect(credential.Password);
        credential.PrivateKeyPassphrase = _protector.Unprotect(credential.PrivateKeyPassphrase);
        return credential;
    }
}
