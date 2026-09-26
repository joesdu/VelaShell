// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3

using System.Collections.Frozen;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>按协商出的算法名造一个密钥交换实例。</summary>
/// <remarks>
/// 表是固定的：加新算法（后量子的下一代方案之类）就是在这里加一行，外加 <see cref="SshAlgorithmNames"/> 的常量。
/// 曾经有过进程级的 <c>Register</c>，但这个类型是 <c>internal</c>，那个「扩展点」除了一条测试没人能调，
/// 却让一张无锁的字典在运行期可写。
/// </remarks>
internal static class SshKeyExchangeFactory
{
    private static readonly FrozenDictionary<string, Func<string, ISshKeyExchange>> Registry =
        new Dictionary<string, Func<string, ISshKeyExchange>>(StringComparer.Ordinal)
        {
            [SshAlgorithmNames.MlKem768X25519Sha256] = static n => new HybridKeyExchange(n),
            [SshAlgorithmNames.Sntrup761X25519Sha512] = static n => new HybridKeyExchange(n),
            [SshAlgorithmNames.Sntrup761X25519Sha512OpenSsh] = static n => new HybridKeyExchange(n),
            [SshAlgorithmNames.Curve25519Sha256] = static n => new Curve25519KeyExchange(n),
            [SshAlgorithmNames.Curve25519Sha256LibSsh] = static n => new Curve25519KeyExchange(n),
            [SshAlgorithmNames.EcdhSha2Nistp256] = static n => new EcdhKeyExchange(n),
            [SshAlgorithmNames.EcdhSha2Nistp384] = static n => new EcdhKeyExchange(n),
            [SshAlgorithmNames.EcdhSha2Nistp521] = static n => new EcdhKeyExchange(n),
            [SshAlgorithmNames.DiffieHellmanGroup14Sha256] = static n => new DiffieHellmanGroupKeyExchange(n),
            [SshAlgorithmNames.DiffieHellmanGroup16Sha512] = static n => new DiffieHellmanGroupKeyExchange(n),
            [SshAlgorithmNames.DiffieHellmanGroup14Sha1] = static n => new DiffieHellmanGroupKeyExchange(n),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>该算法名是否已实现。</summary>
    public static bool IsSupported(string name) => Registry.ContainsKey(name);

    /// <summary>按算法名创建实例。</summary>
    /// <exception cref="InvalidOperationException">算法名没有实现。</exception>
    /// <remarks>
    /// 正常情况下不会失败：协商只会选中我们自己清单里的名字，而清单在连接前已由
    /// <c>SshAlgorithmSet.Validate()</c> 对照 <see cref="IsSupported"/> 查过。
    /// 走到这里说明两者不一致 —— 那是一个编程错误，不是对端的问题，所以不是 <see cref="SshKeyExchangeException"/>。
    /// </remarks>
    public static ISshKeyExchange Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Registry.TryGetValue(name, out Func<string, ISshKeyExchange>? factory)
            ? factory(name)
            : throw new InvalidOperationException(
                $"未实现的密钥交换算法：{name}。算法清单与工厂表不一致。");
    }
}
