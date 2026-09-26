// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

namespace VelaShell.Ssh.HostKeys;

/// <summary>
/// 主机密钥策略的可选能力：说出一台主机已经记着哪些类型的主机密钥。
/// </summary>
/// <remarks>
/// <para>
/// 连接时据此把这些类型的主机密钥算法排到前面。协商以客户端的顺序为准（RFC 4253 §7.1），
/// 所以正常的服务端会谈成已经记下的那一种 —— 与记录比对得上，才谈得上「没变」。
/// </para>
/// <para>
/// 不这么排的话，本端偏好的类型（比如 ed25519）不在记录里时，就会被当成一台「没见过」的主机：
/// 要么误报，要么（更糟）让中间人靠出示一种没记过的类型绕过「密钥变了」的检查。
/// </para>
/// </remarks>
public interface IHostKeyTypePreference
{
    /// <summary>这台主机已经记着哪些类型的主机密钥（<c>ssh-ed25519</c>、<c>ssh-rsa</c>…）。没有记录时为空。</summary>
    /// <param name="host">被连的逻辑主机名。</param>
    /// <param name="port">端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<string>> GetKnownKeyTypesAsync(
        string host, int port, CancellationToken cancellationToken = default);
}
