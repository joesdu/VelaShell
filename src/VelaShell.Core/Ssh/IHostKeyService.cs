using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>主机密钥服务:管理 known_hosts,校验、信任与移除服务器主机密钥指纹。</summary>
public interface IHostKeyService
{
    /// <summary>校验目标主机密钥指纹是否可信,返回首次遇见/匹配/不匹配等验证结果。</summary>
    Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>将指定主机密钥指纹标记为受信任并写入已知主机记录。</summary>
    Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>获取全部已知(受信任)主机记录。</summary>
    Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default);

    /// <summary>移除指定主机与端口对应的已知主机记录。</summary>
    Task RemoveKnownHostAsync(string host, int port, CancellationToken cancellationToken = default);
}
