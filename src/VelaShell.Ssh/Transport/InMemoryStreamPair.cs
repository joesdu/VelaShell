// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §10.2

namespace VelaShell.Ssh.Transport;

/// <summary><see cref="InMemoryTransport.CreatePair"/> 造出的一对互联的双工流。</summary>
/// <param name="First">一端。它写出的字节，<paramref name="Second"/> 读得到。</param>
/// <param name="Second">另一端。它写出的字节，<paramref name="First"/> 读得到。</param>
/// <remarks>两端对称，哪端当客户端都行；可以直接解构：<c>(var client, var server) = InMemoryTransport.CreatePair();</c></remarks>
public readonly record struct InMemoryStreamPair(InMemoryDuplexStream First, InMemoryDuplexStream Second);
