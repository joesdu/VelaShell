// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 密钥交换的**服务端一侧**。本库的 ISshKeyExchange 是客户端视角，
// 这里实现与之相对的那一半，好让整条握手能在内存里跑完。
//
// ⚠️ 只为测试存在。

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.NtruPrime;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Protocol;
using BcBigInteger = Org.BouncyCastle.Math.BigInteger;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>服务端对客户端公开值的应答。</summary>
/// <param name="ServerPublicValue">要放进编号 31 报文的服务端公开值。</param>
/// <param name="SharedSecret">共享密钥。</param>
public readonly record struct TestKexResponse(byte[] ServerPublicValue, byte[] SharedSecret);

/// <summary>密钥交换的服务端一侧。</summary>
public static class TestKexResponder
{
    /// <summary>该算法名测试桩支不支持。</summary>
    public static bool IsSupported(string algorithm) => algorithm switch
    {
        SshAlgorithmNames.Curve25519Sha256 or SshAlgorithmNames.Curve25519Sha256LibSsh => true,
        SshAlgorithmNames.EcdhSha2Nistp256 or SshAlgorithmNames.EcdhSha2Nistp384 or SshAlgorithmNames.EcdhSha2Nistp521 => true,
        SshAlgorithmNames.DiffieHellmanGroup14Sha256 or SshAlgorithmNames.DiffieHellmanGroup16Sha512
            or SshAlgorithmNames.DiffieHellmanGroup14Sha1 => true,
        SshAlgorithmNames.MlKem768X25519Sha256 => true,
        SshAlgorithmNames.SNtruP761X25519Sha512 or SshAlgorithmNames.SNtruP761X25519Sha512OpenSsh => true,
        _ => false,
    };

    /// <summary>按客户端公开值算出服务端公开值与共享密钥。</summary>
    public static TestKexResponse Respond(string algorithm, ReadOnlySpan<byte> clientPublicValue) => algorithm switch
    {
        SshAlgorithmNames.Curve25519Sha256 or SshAlgorithmNames.Curve25519Sha256LibSsh =>
            RespondCurve25519(clientPublicValue),

        SshAlgorithmNames.EcdhSha2Nistp256 => RespondEcdh(clientPublicValue, ECCurve.NamedCurves.nistP256, 32),
        SshAlgorithmNames.EcdhSha2Nistp384 => RespondEcdh(clientPublicValue, ECCurve.NamedCurves.nistP384, 48),
        SshAlgorithmNames.EcdhSha2Nistp521 => RespondEcdh(clientPublicValue, ECCurve.NamedCurves.nistP521, 66),

        SshAlgorithmNames.DiffieHellmanGroup14Sha256 or SshAlgorithmNames.DiffieHellmanGroup14Sha1 =>
            RespondDiffieHellman(clientPublicValue, DHStandardGroups.rfc3526_2048),
        SshAlgorithmNames.DiffieHellmanGroup16Sha512 =>
            RespondDiffieHellman(clientPublicValue, DHStandardGroups.rfc3526_4096),

        SshAlgorithmNames.MlKem768X25519Sha256 =>
            RespondHybrid(clientPublicValue, kemPublicBytes: 1184, HashAlgorithmName.SHA256, MlKemEncapsulate),
        SshAlgorithmNames.SNtruP761X25519Sha512 or SshAlgorithmNames.SNtruP761X25519Sha512OpenSsh =>
            RespondHybrid(clientPublicValue, kemPublicBytes: 1158, HashAlgorithmName.SHA512, SNtruEncapsulate),

        _ => throw new NotSupportedException($"测试桩不支持的 KEX：{algorithm}"),
    };

    private static TestKexResponse RespondCurve25519(ReadOnlySpan<byte> clientPublic)
    {
        X25519PrivateKeyParameters serverPrivate = new(new SecureRandom());
        byte[] serverPublic = serverPrivate.GeneratePublicKey().GetEncoded();

        byte[] secret = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverPrivate);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(clientPublic.ToArray()), secret, 0);

        return new TestKexResponse(serverPublic, secret);
    }

    private static TestKexResponse RespondEcdh(ReadOnlySpan<byte> clientPublic, ECCurve curve, int coordinate)
    {
        using var server = ECDiffieHellman.Create(curve);
        ECParameters p = server.ExportParameters(false);

        byte[] serverPublic = new byte[1 + (coordinate * 2)];
        serverPublic[0] = 0x04;
        p.Q.X!.CopyTo(serverPublic.AsSpan(1 + coordinate - p.Q.X!.Length));
        p.Q.Y!.CopyTo(serverPublic.AsSpan(1 + (coordinate * 2) - p.Q.Y!.Length));

        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = curve,
            Q = new ECPoint
            {
                X = clientPublic[1..(1 + coordinate)].ToArray(),
                Y = clientPublic[(1 + coordinate)..].ToArray(),
            },
        });

        return new TestKexResponse(serverPublic, server.DeriveRawSecretAgreement(peer.PublicKey));
    }

    private static TestKexResponse RespondDiffieHellman(ReadOnlySpan<byte> clientPublic, DHParameters group)
    {
        DHParameters parameters = new(group.P, group.G, null, 512);

        DHKeyPairGenerator generator = new();
        generator.Init(new DHKeyGenerationParameters(new SecureRandom(), parameters));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        DHBasicAgreement agreement = new();
        agreement.Init(pair.Private);
        BcBigInteger secret = agreement.CalculateAgreement(
            new DHPublicKeyParameters(new BcBigInteger(1, clientPublic.ToArray()), parameters));

        return new TestKexResponse(
            ((DHPublicKeyParameters)pair.Public).Y.ToByteArrayUnsigned(),
            secret.ToByteArrayUnsigned());
    }

    private static TestKexResponse RespondHybrid(
        ReadOnlySpan<byte> clientPublic,
        int kemPublicBytes,
        HashAlgorithmName hash,
        Func<byte[], (byte[] Ciphertext, byte[] Secret)> encapsulate)
    {
        (byte[] ciphertext, byte[] kemSecret) = encapsulate(clientPublic[..kemPublicBytes].ToArray());

        X25519PrivateKeyParameters serverX25519 = new(new SecureRandom());
        byte[] serverX25519Public = serverX25519.GeneratePublicKey().GetEncoded();

        byte[] classical = new byte[32];
        X25519Agreement agreement = new();
        agreement.Init(serverX25519);
        agreement.CalculateAgreement(
            new X25519PublicKeyParameters(clientPublic[kemPublicBytes..].ToArray()), classical, 0);

        // K = HASH(K_kem ‖ K_x25519)
        byte[] combined = [.. kemSecret, .. classical];
        byte[] secret = hash == HashAlgorithmName.SHA256
            ? SHA256.HashData(combined)
            : SHA512.HashData(combined);

        return new TestKexResponse([.. ciphertext, .. serverX25519Public], secret);
    }

    private static (byte[] Ciphertext, byte[] Secret) MlKemEncapsulate(byte[] publicKey)
    {
        MLKemEncapsulator encapsulator = new(MLKemParameters.ml_kem_768);
        encapsulator.Init(MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, publicKey));

        byte[] ciphertext = new byte[encapsulator.EncapsulationLength];
        byte[] secret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, 0, ciphertext.Length, secret, 0, secret.Length);
        return (ciphertext, secret);
    }

    private static (byte[] Ciphertext, byte[] Secret) SNtruEncapsulate(byte[] publicKey)
    {
        SNtruPrimeKemGenerator generator = new(new SecureRandom());
        ISecretWithEncapsulation encapsulated = generator.GenerateEncapsulated(
            new SNtruPrimePublicKeyParameters(SNtruPrimeParameters.sntrup761, publicKey));
        return (encapsulated.GetEncapsulation(), encapsulated.GetSecret());
    }
}
