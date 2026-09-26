// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH sshd(8) 的 SSH_KNOWN_HOSTS 章节（含 @cert-authority / @revoked）
//   行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4、§5.5

namespace VelaShell.Ssh.HostKeys;

/// <summary><c>known_hosts</c> 里的一条。</summary>
/// <param name="Patterns">主机模式（逗号分隔的原文已经拆开）。</param>
/// <param name="IsHashed">主机名是不是 <c>|1|salt|hash</c> 形式。</param>
/// <param name="Marker"><c>@cert-authority</c> 或 <c>@revoked</c>；没有则为空。</param>
/// <param name="KeyType">密钥类型。</param>
/// <param name="KeyBlob">公钥 blob。</param>
/// <param name="LineNumber">在文件里的行号（1 起）。</param>
public sealed record KnownHostEntry(
    IReadOnlyList<string> Patterns,
    bool IsHashed,
    string Marker,
    string KeyType,
    ReadOnlyMemory<byte> KeyBlob,
    int LineNumber)
{
    /// <summary>这一条是不是「此密钥已吊销」。</summary>
    public bool IsRevoked => Marker == "@revoked";

    /// <summary>这一条是不是证书颁发者。</summary>
    public bool IsCertificateAuthority => Marker == "@cert-authority";
}
