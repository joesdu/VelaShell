using System.Text;

namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// Incremental byte-stream decoder that buffers partial multibyte sequences across feeds and
/// substitutes U+FFFD for malformed input. Defaults to UTF-8 but accepts any <see cref="Encoding"/>
/// (e.g. GBK, Big5) so the terminal's charset is configurable while UTF-8 remains the default.
/// </summary>
public sealed class Utf8Sink
{
    private Decoder _decoder;
    private char[] _chars = new char[1024];

    public Utf8Sink(Encoding? encoding = null)
    {
        _decoder = CreateDecoder(encoding);
    }

    public void SetEncoding(Encoding encoding)
    {
        _decoder = CreateDecoder(encoding);
    }

    private static Decoder CreateDecoder(Encoding? encoding)
    {
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var decoder = encoding.GetDecoder();
        decoder.Fallback = new DecoderReplacementFallback("�");
        return decoder;
    }

    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        int max = _decoder.GetCharCount(bytes, flush: false);
        if (max == 0)
            return string.Empty;
        if (_chars.Length < max)
            _chars = new char[max];

        int written = _decoder.GetChars(bytes, _chars, flush: false);
        return new string(_chars, 0, written);
    }

    public void Reset() => _decoder.Reset();
}
