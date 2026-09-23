// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 一个在内存里说 agent 协议的服务端,以及一个「假装是远端」的 agent 客户端。
//
// ⚠️ **只为测试存在,绝不发布。**

using System.Buffers;
using System.Buffers.Binary;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>在一条流上说 agent 协议的测试 agent。</summary>
public sealed class TestAgent
{
    private const int MaxMessage = 256 * 1024;

    private readonly List<(InMemorySshSigner Signer, string Comment)> _keys = [];

    /// <summary>收到的签名请求次数。</summary>
    public int SignRequests { get; private set; }

    /// <summary>收到的列身份请求次数。</summary>
    public int ListRequests { get; private set; }

    /// <summary>加一把密钥。</summary>
    public SshPublicKey Add(InMemorySshSigner signer, string comment)
    {
        _keys.Add((signer, comment));
        return signer.PublicKey;
    }

    /// <summary>在一条流上服务，直到对端关闭。</summary>
    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, cancellationToken);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

                if (length is 0 or > MaxMessage)
                {
                    return;
                }

                byte[] request = new byte[length];
                await stream.ReadExactlyAsync(request, cancellationToken);

                byte[] response = await HandleAsync(request, cancellationToken);

                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)response.Length);
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(response, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (EndOfStreamException)
        {
            // 对端走了。
        }
        catch (OperationCanceledException)
        {
            // 测试收尾。
        }
        catch (IOException)
        {
            // 同上。
        }
    }

    private async Task<byte[]> HandleAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (request.Length == 0)
        {
            return [5];   // FAILURE
        }

        if (request[0] == 11)   // REQUEST_IDENTITIES
        {
            ListRequests++;

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteByte(12);   // IDENTITIES_ANSWER
            writer.WriteUInt32((uint)_keys.Count);

            foreach ((InMemorySshSigner signer, string comment) in _keys)
            {
                writer.WriteString(signer.PublicKey.Blob.Span);
                writer.WriteUtf8String(comment);
            }

            return buffer.WrittenSpan.ToArray();
        }

        if (request[0] == 13)   // SIGN_REQUEST
        {
            SignRequests++;

            SshDataReader reader = new(new ReadOnlySequence<byte>(request));
            reader.ReadByte();
            byte[] keyBlob = reader.ReadStringAsArray(MaxMessage);
            byte[] data = reader.ReadStringAsArray(MaxMessage);
            uint flags = reader.ReadUInt32();

            foreach ((InMemorySshSigner signer, _) in _keys)
            {
                if (!signer.PublicKey.Blob.Span.SequenceEqual(keyBlob))
                {
                    continue;
                }

                string algorithm = signer.PublicKey.KeyType == SshAlgorithmNames.SshRsa
                    ? (flags & 0x04) != 0
                        ? SshAlgorithmNames.RsaSha512
                        : (flags & 0x02) != 0
                            ? SshAlgorithmNames.RsaSha256
                            : SshAlgorithmNames.SshRsa
                    : signer.PublicKey.KeyType;

                byte[] signature = await signer.SignAsync(data, algorithm, cancellationToken);

                ArrayBufferWriter<byte> buffer = new();
                SshDataWriter writer = new(buffer);
                writer.WriteByte(14);   // SIGN_RESPONSE
                writer.WriteString(signature);
                return buffer.WrittenSpan.ToArray();
            }

            return [5];   // 没有这把钥
        }

        // 增删密钥之类的一律拒绝。
        return [5];
    }
}

/// <summary>假装自己是远端主机上的 <c>ssh-add -l</c> / 签名调用方。</summary>
/// <remarks>
/// 它说的是 agent 协议，但字节从一条 SSH 通道上走 ——
/// 也就是 <c>auth-agent@openssh.com</c> 那条通道的服务端一侧。
/// </remarks>
public static class TestRemoteAgentClient
{
    private const int MaxMessage = 256 * 1024;

    /// <summary>发一条 agent 请求并读回应答。</summary>
    public static async Task<byte[]> ExchangeAsync(
        Stream stream, byte[] request, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)request.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        await stream.ReadExactlyAsync(header, cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

        byte[] response = new byte[length];
        await stream.ReadExactlyAsync(response, cancellationToken);
        return response;
    }

    /// <summary>列出远端能看到的密钥。</summary>
    public static async Task<IReadOnlyList<(SshPublicKey Key, string Comment)>> ListAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        byte[] response = await ExchangeAsync(stream, [11], cancellationToken);

        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        byte type = reader.ReadByte();

        if (type != 12)
        {
            return [];
        }

        uint count = reader.ReadUInt32();
        List<(SshPublicKey, string)> keys = [];

        for (uint i = 0; i < count; i++)
        {
            byte[] blob = reader.ReadStringAsArray(MaxMessage);
            string comment = reader.ReadUtf8String(MaxMessage);
            keys.Add((SshPublicKey.Parse(blob), comment));
        }

        return keys;
    }

    /// <summary>请远端的 agent 签一段数据。</summary>
    /// <returns>签名 blob；被拒时为 <see langword="null"/>。</returns>
    public static async Task<byte[]?> SignAsync(
        Stream stream, SshPublicKey key, byte[] data, uint flags, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteByte(13);
        writer.WriteString(key.Blob.Span);
        writer.WriteString(data);
        writer.WriteUInt32(flags);

        byte[] response = await ExchangeAsync(stream, buffer.WrittenSpan.ToArray(), cancellationToken);

        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        return reader.ReadByte() == 14 ? reader.ReadStringAsArray(MaxMessage) : null;
    }
}
