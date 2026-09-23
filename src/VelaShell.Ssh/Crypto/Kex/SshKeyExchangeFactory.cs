// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §3;velashell-docs/zh/ssh/design/architecture.md §8 第 3 项

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>按协商出的算法名造一个密钥交换实例。</summary>
/// <remarks>
/// <para>
/// 这是架构 §8 第 3 项的扩展点：用 <see cref="Register"/> 加新算法，
/// 不需要改本库的任何代码。后量子的下一代方案照此接入。
/// </para>
/// <para>
/// 注册表是进程级的，且**只在启动时写**。运行期改它不是受支持的用法。
/// </para>
/// </remarks>
public static class SshKeyExchangeFactory
{
    private static readonly Dictionary<string, Func<string, ISshKeyExchange>> Registry =
        new(StringComparer.Ordinal)
        {
            [SshAlgorithmNames.MlKem768X25519Sha256] = static n => new HybridKeyExchange(n),
            [SshAlgorithmNames.SNtruP761X25519Sha512] = static n => new HybridKeyExchange(n),
            [SshAlgorithmNames.SNtruP761X25519Sha512OpenSsh] = static n => new HybridKeyExchange(n),
            [SshAlgorithmNames.Curve25519Sha256] = static n => new Curve25519KeyExchange(n),
            [SshAlgorithmNames.Curve25519Sha256LibSsh] = static n => new Curve25519KeyExchange(n),
            [SshAlgorithmNames.EcdhSha2Nistp256] = static n => new EcdhKeyExchange(n),
            [SshAlgorithmNames.EcdhSha2Nistp384] = static n => new EcdhKeyExchange(n),
            [SshAlgorithmNames.EcdhSha2Nistp521] = static n => new EcdhKeyExchange(n),
            [SshAlgorithmNames.DiffieHellmanGroup14Sha256] = static n => new DiffieHellmanGroupKeyExchange(n),
            [SshAlgorithmNames.DiffieHellmanGroup16Sha512] = static n => new DiffieHellmanGroupKeyExchange(n),
            [SshAlgorithmNames.DiffieHellmanGroup14Sha1] = static n => new DiffieHellmanGroupKeyExchange(n),
        };

    /// <summary>注册一个自定义的密钥交换算法。</summary>
    /// <param name="name">算法的注册名。含 <c>@</c> 的是厂商扩展，不含的必须是 IANA 注册过的。</param>
    /// <param name="factory">工厂。</param>
    public static void Register(string name, Func<string, ISshKeyExchange> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        Registry[name] = factory;
    }

    /// <summary>该算法名是否已注册。</summary>
    public static bool IsSupported(string name) => Registry.ContainsKey(name);

    /// <summary>按算法名创建实例。</summary>
    /// <exception cref="SshKeyExchangeException">算法名未注册。</exception>
    /// <remarks>
    /// 正常情况下不会失败：协商只会选中我们自己列表里的名字。
    /// 走到这里说明算法清单与注册表不一致 —— 那是一个编程错误，不是对端的问题。
    /// </remarks>
    public static ISshKeyExchange Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Registry.TryGetValue(name, out Func<string, ISshKeyExchange>? factory)
            ? factory(name)
            : throw new SshKeyExchangeException(
                $"未注册的密钥交换算法：{name}。算法清单与工厂注册表不一致。");
    }
}
