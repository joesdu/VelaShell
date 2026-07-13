using System.Text;

namespace VelaShell.Core.Ssh;

public class Utf8StreamDecoder
{
    private readonly List<byte> _buffer = [];
    private readonly Decoder _decoder;

    public Utf8StreamDecoder()
    {
        // Use replacement fallback to emit U+FFFD for invalid byte sequences
        var encoding = new UTF8Encoding(false, false);
        _decoder = encoding.GetDecoder();
        _decoder.Fallback = new DecoderReplacementFallback("\uFFFD");
    }

    public string DecodeBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }
        _buffer.AddRange(bytes);
        char[] charBuffer = new char[_buffer.Count * 2];
        _decoder.Convert([.. _buffer],
            0,
            _buffer.Count,
            charBuffer,
            0,
            charBuffer.Length,
            false,
            out int bytesUsed,
            out int charsUsed,
            out bool _);
        _buffer.RemoveRange(0, bytesUsed);
        return new(charBuffer, 0, charsUsed);
    }

    /// <summary>
    /// Flush any remaining incomplete bytes as replacement characters (U+FFFD)
    /// </summary>
    public string Flush()
    {
        byte[] allBytes = [.. _buffer];
        char[] charBuffer = new char[Math.Max(allBytes.Length, 1) * 2];
        _decoder.Convert(allBytes,
            0,
            allBytes.Length,
            charBuffer,
            0,
            charBuffer.Length,
            true,
            out int _,
            out int charsUsed,
            out _);
        _buffer.Clear();
        return new(charBuffer, 0, charsUsed);
    }

    public void Reset()
    {
        _buffer.Clear();
        _decoder.Reset();
    }
}
