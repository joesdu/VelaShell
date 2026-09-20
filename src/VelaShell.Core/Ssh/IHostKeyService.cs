using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>主机密钥服务:管理 known_hosts,校验、信任与移除服务器主机密钥指纹。</summary>
public interface IHostKeyService
{
    /// <summary>校验目标主机密钥指纹是否可信,返回首次遇见/匹配/不匹配等验证结果。</summary>
    Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取指定主机与端口已记录的已知主机条目;从未记录过时返回 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 指纹变更弹窗要把"原来记的是哪一把"摆给用户看 —— 只说"变了"而不给旧指纹,
    /// 用户无从判断这是自己刚重装的那台,还是一次劫持。
    /// </remarks>
    Task<KnownHost?> FindKnownHostAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>将指定主机密钥指纹标记为受信任并写入已知主机记录。</summary>
    Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>获取全部已知(受信任)主机记录。</summary>
    Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default);

    /// <summary>移除指定主机与端口对应的已知主机记录。</summary>
    Task RemoveKnownHostAsync(string host, int port, CancellationToken cancellationToken = default);
}
