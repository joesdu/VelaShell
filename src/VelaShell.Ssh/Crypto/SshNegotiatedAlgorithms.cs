// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.1  协商规则
//   RFC 5647       AEAD 加密下 MAC 列表的协商结果被忽略
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §2.2

namespace VelaShell.Ssh.Crypto;

/// <summary>一次协商的结果。</summary>
/// <param name="KeyExchange">密钥交换算法。</param>
/// <param name="HostKey">主机密钥算法（同时也是签名算法名）。</param>
/// <param name="EncryptionClientToServer">加密算法（客户端 → 服务端）。</param>
/// <param name="EncryptionServerToClient">加密算法（服务端 → 客户端）。</param>
/// <param name="MacClientToServer">完整性算法（客户端 → 服务端）；AEAD 下为 <see langword="null"/>。</param>
/// <param name="MacServerToClient">完整性算法（服务端 → 客户端）；AEAD 下为 <see langword="null"/>。</param>
/// <param name="CompressionClientToServer">压缩算法（客户端 → 服务端）。</param>
/// <param name="CompressionServerToClient">压缩算法（服务端 → 客户端）。</param>
/// <param name="StrictKeyExchange">双方是否都宣告支持严格 KEX（Terrapin 缓解）。</param>
/// <param name="PeerSupportsExtensionInfo">对端是否支持 RFC 8308 扩展协商。</param>
public readonly record struct SshNegotiatedAlgorithms(
    string KeyExchange,
    string HostKey,
    string EncryptionClientToServer,
    string EncryptionServerToClient,
    string? MacClientToServer,
    string? MacServerToClient,
    string CompressionClientToServer,
    string CompressionServerToClient,
    bool StrictKeyExchange,
    bool PeerSupportsExtensionInfo);
