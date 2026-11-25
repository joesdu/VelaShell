// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Buffers;

namespace VelaShell.Ssh;

internal static class BoundedFileReader
{
    internal const int MaxBytes = 1024 * 1024;

    public static async ValueTask<T> ReadAsync<T>(string path, Func<byte[], int, T> decode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await ReadAsync(stream, decode, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<T> ReadAsync<T>(Stream stream, Func<byte[], int, T> decode, CancellationToken cancellationToken)
    {
        long length = stream.CanSeek ? stream.Length : 0;
        if (length > MaxBytes)
        {
            throw new InvalidDataException($"文件超过 {MaxBytes} 字节。");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, (int)length + 1));
        int used = 0;
        try
        {
            while (true)
            {
                int capacity = Math.Min(buffer.Length, MaxBytes + 1);
                int count = await stream.ReadAsync(buffer.AsMemory(used, capacity - used), cancellationToken).ConfigureAwait(false);
                used += count;
                if (count == 0)
                {
                    return decode(buffer, used);
                }
                if (used > MaxBytes)
                {
                    throw new InvalidDataException($"文件超过 {MaxBytes} 字节。");
                }
                if (used == capacity)
                {
                    byte[] next = ArrayPool<byte>.Shared.Rent(Math.Min(capacity * 2, MaxBytes + 1));
                    buffer.AsSpan(0, used).CopyTo(next);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = next;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
