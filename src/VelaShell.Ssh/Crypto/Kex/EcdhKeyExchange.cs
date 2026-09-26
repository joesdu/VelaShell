// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 5656 §3.1  公钥按 RFC 5480 的未压缩点编码 0x04 ‖ X ‖ Y
//   RFC 5656 §4    ecdh-sha2-*:公钥按 string,共享密钥(X 坐标)按 mpint
//   RFC 5656 §6.2.1 曲线与哈希的配对
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §3.3

using System.Security.Cryptography;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary><c>ecdh-sha2-nistp256</c> / <c>nistp384</c> / <c>nistp521</c>。</summary>
/// <remarks>
/// 曲线与哈希是**配对**的（RFC 5656 §6.2.1）：P-256↔SHA-256、P-384↔SHA-384、P-521↔SHA-512。
/// <para>
/// ⚠️ <b>nistp521 的坐标是 66 字节</b>（521 位向上取整），未压缩点共 133 字节。
/// 按 64 或 65 字节假设写死的实现会在这条曲线上崩掉 —— 而它恰好是最少被测到的那条。
/// </para>
/// </remarks>
internal sealed class EcdhKeyExchange : ISshKeyExchange
{
    private const byte UncompressedPointTag = 0x04;

    private readonly ECCurve _curve;
    private readonly int _coordinateBytes;
    private readonly ECDiffieHellman _ecdh;
    private bool _disposed;

    /// <summary>按算法名创建一次 ECDH 交换。</summary>
    /// <param name="name">
    /// <see cref="SshAlgorithmNames.EcdhSha2Nistp256"/> / <c>384</c> / <c>521</c> 之一。
    /// </param>
    public EcdhKeyExchange(string name)
    {
        (_curve, HashAlgorithm, _coordinateBytes) = name switch
        {
            SshAlgorithmNames.EcdhSha2Nistp256 => (ECCurve.NamedCurves.nistP256, HashAlgorithmName.SHA256, 32),
            SshAlgorithmNames.EcdhSha2Nistp384 => (ECCurve.NamedCurves.nistP384, HashAlgorithmName.SHA384, 48),
            // 521 位 → 66 字节，不是 64。
            SshAlgorithmNames.EcdhSha2Nistp521 => (ECCurve.NamedCurves.nistP521, HashAlgorithmName.SHA512, 66),
            _ => throw new ArgumentException($"不是已知的 ECDH 算法名：{name}", nameof(name)),
        };

        Name = name;
        _ecdh = ECDiffieHellman.Create(_curve);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public HashAlgorithmName HashAlgorithm { get; }

    /// <inheritdoc />
    public SshKexValueEncoding PublicValueEncoding => SshKexValueEncoding.ByteString;

    /// <inheritdoc />
    public SshKexValueEncoding SharedSecretEncoding => SshKexValueEncoding.Mpint;

    /// <inheritdoc />
    public byte[] CreateClientPublicValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ECParameters parameters = _ecdh.ExportParameters(includePrivateParameters: false);
        byte[] point = new byte[1 + (_coordinateBytes * 2)];
        point[0] = UncompressedPointTag;

        // X/Y 可能短于坐标长度（前导零被省略），必须**右对齐**补零。
        // 左对齐会产出一个完全不同的点，而对端只会报一句「协商失败」。
        CopyRightAligned(parameters.Q.X!, point.AsSpan(1, _coordinateBytes));
        CopyRightAligned(parameters.Q.Y!, point.AsSpan(1 + _coordinateBytes, _coordinateBytes));
        return point;
    }

    /// <inheritdoc />
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int expected = 1 + (_coordinateBytes * 2);
        if (serverPublicValue.Length != expected)
        {
            throw new SshKeyExchangeException(
                $"{Name} 的服务端公钥必须是 {expected} 字节（未压缩点），收到 {serverPublicValue.Length} 字节。");
        }
        if (serverPublicValue[0] != UncompressedPointTag)
        {
            throw new SshKeyExchangeException(
                $"{Name} 的服务端公钥不是未压缩点编码（首字节 0x{serverPublicValue[0]:X2}，应为 0x04）。");
        }

        ECParameters peer = new()
        {
            Curve = _curve,
            Q = new ECPoint
            {
                X = serverPublicValue.Slice(1, _coordinateBytes).ToArray(),
                Y = serverPublicValue.Slice(1 + _coordinateBytes, _coordinateBytes).ToArray(),
            },
        };

        try
        {
            // ECDiffieHellman.Create(ECParameters) 会校验点确实在曲线上并且不是无穷远点。
            // 不校验就等于接受任意点，那是一条可以泄漏私钥的路（无效曲线攻击）。
            using var peerKey = ECDiffieHellman.Create(peer);

            // DeriveRawSecretAgreement 给的是共享点的 **X 坐标**，正是 SSH 要的 K。
            // 不要用 DeriveKeyMaterial —— 那会再套一层 KDF，与协议不符。
            return _ecdh.DeriveRawSecretAgreement(peerKey.PublicKey);
        }
        catch (Exception ex) when (ex is not SshKeyExchangeException)
        {
            // 刻意捕获得宽。**各平台在这里抛的类型不一样**：
            // Linux/macOS 的 OpenSSL 后端直接抛 CryptographicException，
            // 而 Windows 的 CNG 后端把它包进另一层（ECCng.ImportKeyBlob → CngKey.Import）。
            // 只接 CryptographicException 会在 Windows 上漏掉这条路 ——
            // 而「漏掉」在这里意味着一个无效曲线点被当成了合法输入。
            //
            // 无论什么异常，含义都是同一个：对端给的点我们用不了。
            throw new SshKeyExchangeException($"{Name} 协商失败：服务端公钥不在曲线上或非法。", ex);
        }
    }

    private static void CopyRightAligned(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length > destination.Length)
        {
            // 去掉前导零之后仍然过长才是真的错。
            ReadOnlySpan<byte> trimmed = source;
            while (trimmed.Length > destination.Length && trimmed[0] == 0)
            {
                trimmed = trimmed[1..];
            }
            if (trimmed.Length > destination.Length)
            {
                throw new SshKeyExchangeException("EC 坐标长度超出曲线定义。");
            }
            source = trimmed;
        }

        destination.Clear();
        source.CopyTo(destination[^source.Length..]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _ecdh.Dispose();
    }
}
