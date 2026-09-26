// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.key  openssh-key-v1 容器格式
//   RFC 8017 §A.1.2       PKCS#1 RSAPrivateKey
//   RFC 5958              PKCS#8
//   RFC 5915              SEC1 EC 私钥
//   RFC 8410              PKCS#8 里的 Ed25519
//   行为规格:             velashell-docs/zh/ssh/design/architecture.md §8 第 5 项

namespace VelaShell.Ssh.Keys;

/// <summary>私钥文件的格式。</summary>
public enum SshPrivateKeyFormat
{
    /// <summary>认不出来。</summary>
    Unknown,

    /// <summary><c>-----BEGIN OPENSSH PRIVATE KEY-----</c>（OpenSSH 7.8 起的默认格式）。</summary>
    OpenSsh,

    /// <summary><c>-----BEGIN RSA PRIVATE KEY-----</c>（PKCS#1，老 OpenSSH 的默认）。</summary>
    Pkcs1Rsa,

    /// <summary><c>-----BEGIN EC PRIVATE KEY-----</c>（SEC1）。</summary>
    Sec1Ec,

    /// <summary><c>-----BEGIN PRIVATE KEY-----</c>（PKCS#8，未加密）。</summary>
    Pkcs8,

    /// <summary><c>-----BEGIN ENCRYPTED PRIVATE KEY-----</c>（PKCS#8，已加密）。</summary>
    Pkcs8Encrypted,

    /// <summary>PuTTY 的 <c>.ppk</c>（v2 或 v3）。</summary>
    Putty,
}
