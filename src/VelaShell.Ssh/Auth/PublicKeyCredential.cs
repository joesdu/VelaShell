// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5.2  none
//   RFC 4252 §7    publickey
//   RFC 4252 §8    password
//   RFC 4256       keyboard-interactive
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md §2、§4、§5、§6

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Auth;

/// <summary>公钥认证。</summary>
/// <remarks>
/// 私钥从哪来由 <see cref="ISshSigner"/> 决定：文件、ssh-agent、PKCS#11、HSM、
/// 云密钥服务 —— <b>私钥可以从不进程内</b>。
/// </remarks>
public sealed class PublicKeyCredential : SshCredential
{
    /// <summary>用一个签名器构造。</summary>
    /// <param name="signer">签名器。</param>
    /// <param name="label">给使用者看的标签（如私钥文件路径）。</param>
    public PublicKeyCredential(ISshSigner signer, string? label = null)
    {
        Signer = signer ?? throw new ArgumentNullException(nameof(signer));
        Label = label ?? $"publickey ({signer.PublicKey.KeyType})";
    }

    /// <inheritdoc />
    public override string MethodName => SshProtocolNames.AuthPublicKey;

    /// <inheritdoc />
    public override string Label { get; }

    /// <summary>签名器。</summary>
    public ISshSigner Signer { get; }
}
