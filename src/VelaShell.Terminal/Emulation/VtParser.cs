using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// A DEC-compatible escape-sequence parser implementing the state machine described by
/// Paul Williams (https://vt100.net/emu/dec_ansi_parser). It consumes a stream of Unicode
/// scalar values (UTF-8 is decoded upstream so that multibyte text never collides with the
/// 7-bit control set) and dispatches semantic events to an <see cref="IVtActions" /> sink.
/// A dedicated VT52-compatibility path is provided because VT52 uses a distinct,
/// non-CSI escape grammar.
/// </summary>
public sealed class VtParser(IVtActions actions)
{
    private const int MaxParams = 32;

    private readonly StringBuilder _intermediates = new(4);
    private readonly StringBuilder _oscOrDcs = new(64);

    private readonly List<int> _params = new(MaxParams);
    private int _currentParam;

    private char _dcsFinal;
    private bool _hasCurrentParam;
    private char _prefix;
    private State _state = State.Ground;
    private int _vt52Row;

    /// <summary>When true, the parser interprets input using the VT52 escape grammar.</summary>
    public bool Vt52Mode { get; set; }

    public void Reset()
    {
        _state = State.Ground;
        ClearParams();
        _intermediates.Clear();
        _oscOrDcs.Clear();
        _prefix = '\0';
    }

    private void Parse(ReadOnlySpan<char> text)
    {
        // Iterate Unicode scalar values so surrogate pairs are delivered as one rune.
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                Consume(char.ConvertToUtf32(c, text[i + 1]));
                i++;
            }
            else
            {
                Consume(c);
            }
        }
    }

    public void Parse(string text) => Parse(text.AsSpan());

    private void Consume(int rune)
    {
        if (Vt52Mode && _state is State.Ground or State.Vt52Escape or State.Vt52CursorRow or State.Vt52CursorCol)
        {
            ConsumeVt52(rune);
            return;
        }
        switch (rune)
        {
            // CAN and SUB abort any sequence in progress.
            case 0x18 or 0x1A:
                _state = State.Ground;
                ClearParams();
                _intermediates.Clear();
                return;
            // ESC restarts a sequence from most states. OSC/DCS are the exception: their
            // terminator ST is ESC \, so the collected payload must be dispatched here —
            // otherwise a string terminated by ST (instead of BEL) is silently discarded.
            case 0x1B:
                {
                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (_state == State.OscString)
                    {
                        DispatchOsc();
                    }
                    else if (_state == State.DcsPassthrough)
                    {
                        DispatchDcs();
                    }
                    EnterEscape();
                    return;
                }
            default:
                switch (_state)
                {
                    case State.Ground:
                        Ground(rune);
                        break;
                    case State.Escape:
                        Escape(rune);
                        break;
                    case State.EscapeIntermediate:
                        EscapeIntermediate(rune);
                        break;
                    case State.CsiEntry:
                        CsiEntry(rune);
                        break;
                    case State.CsiParam:
                        CsiParam(rune);
                        break;
                    case State.CsiIntermediate:
                        CsiIntermediate(rune);
                        break;
                    case State.CsiIgnore:
                        CsiIgnore(rune);
                        break;
                    case State.OscString:
                        OscString(rune);
                        break;
                    case State.DcsEntry:
                        DcsEntry(rune);
                        break;
                    case State.DcsParam:
                        DcsParam(rune);
                        break;
                    case State.DcsIntermediate:
                        DcsIntermediate(rune);
                        break;
                    case State.DcsPassthrough:
                        DcsPassthrough(rune);
                        break;
                    case State.DcsIgnore:
                        DcsIgnore(rune);
                        break;
                    case State.SosPmApcString:
                        SosPmApcString(rune);
                        break;
                    case State.Vt52Escape:
                    case State.Vt52CursorRow:
                    case State.Vt52CursorCol:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                break;
        }
    }

    // ---- Ground -------------------------------------------------------------

    private void Ground(int rune)
    {
        if (IsC0(rune) || rune == 0x7F)
        {
            actions.Execute((char)rune);
            return;
        }
        actions.Print(rune);
    }

    private void EnterEscape()
    {
        _state = State.Escape;
        ClearParams();
        _intermediates.Clear();
        _prefix = '\0';
        _oscOrDcs.Clear();
    }

    private void Escape(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        if (rune is >= 0x20 and <= 0x2F) // intermediate
        {
            _intermediates.Append((char)rune);
            _state = State.EscapeIntermediate;
            return;
        }
        switch (rune)
        {
            case '[':
                _state = State.CsiEntry;
                return;
            case ']':
                _state = State.OscString;
                _oscOrDcs.Clear();
                return;
            case 'P':
                _state = State.DcsEntry;
                return;
            case 'X' or '^' or '_': // SOS / PM / APC
                _state = State.SosPmApcString;
                return;
        }
        if (rune is >= 0x30 and <= 0x7E)
        {
            actions.EscDispatch(_intermediates.ToString(), (char)rune);
        }
        _state = State.Ground;
    }

    private void EscapeIntermediate(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                return;
            case >= 0x30 and <= 0x7E:
                actions.EscDispatch(_intermediates.ToString(), (char)rune);
                break;
        }
        _state = State.Ground;
    }

    // ---- CSI ----------------------------------------------------------------

    private void CsiEntry(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            // < = > ?
            case >= 0x3C and <= 0x3F:
                _prefix = (char)rune;
                _state = State.CsiParam;
                return;
            case >= 0x30 and <= 0x39:
            case ';':
            case ':':
                HandleParamDigit(rune);
                _state = State.CsiParam;
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.CsiIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                actions.CsiDispatch(_prefix, _params, _intermediates.ToString(), (char)rune);
                _state = State.Ground;
                return;
            default:
                _state = State.CsiIgnore;
                break;
        }
    }

    private void CsiParam(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            case >= 0x30 and <= 0x39 or ';' or ':':
                HandleParamDigit(rune);
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.CsiIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                actions.CsiDispatch(_prefix, _params, _intermediates.ToString(), (char)rune);
                _state = State.Ground;
                return;
            default:
                _state = State.CsiIgnore;
                break;
        }
    }

    private void CsiIntermediate(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        switch (rune)
        {
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                actions.CsiDispatch(_prefix, _params, _intermediates.ToString(), (char)rune);
                _state = State.Ground;
                return;
            default:
                _state = State.CsiIgnore;
                break;
        }
    }

    private void CsiIgnore(int rune)
    {
        if (IsC0(rune))
        {
            actions.Execute((char)rune);
            return;
        }
        if (rune is >= 0x40 and <= 0x7E)
        {
            _state = State.Ground;
        }
    }

    // ---- OSC ----------------------------------------------------------------

    private void OscString(int rune)
    {
        switch (rune)
        {
            // Terminated by BEL (0x07) here, or by ST (ESC \) via the global ESC branch in
            // Consume — ESC never reaches this handler.
            case 0x07:
                DispatchOsc();
                _state = State.Ground;
                return;
            case >= 0x20:
                _oscOrDcs.Append(char.ConvertFromUtf32(rune));
                break;
        }
    }

    private void DispatchOsc()
    {
        string[] parts = _oscOrDcs.ToString().Split(';');
        actions.OscDispatch(parts);
        _oscOrDcs.Clear();
    }

    // ---- DCS ----------------------------------------------------------------

    private void DcsEntry(int rune)
    {
        switch (rune)
        {
            case >= 0x3C and <= 0x3F:
                _prefix = (char)rune;
                _state = State.DcsParam;
                return;
            case >= 0x30 and <= 0x39:
            case ';':
            case ':':
                HandleParamDigit(rune);
                _state = State.DcsParam;
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.DcsIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                _oscOrDcs.Clear();
                _dcsFinal = (char)rune;
                _state = State.DcsPassthrough;
                return;
            default:
                _state = State.DcsIgnore;
                break;
        }
    }

    private void DcsParam(int rune)
    {
        switch (rune)
        {
            case >= 0x30 and <= 0x39 or ';' or ':':
                HandleParamDigit(rune);
                return;
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                _state = State.DcsIntermediate;
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                _oscOrDcs.Clear();
                _dcsFinal = (char)rune;
                _state = State.DcsPassthrough;
                return;
            default:
                _state = State.DcsIgnore;
                break;
        }
    }

    private void DcsIntermediate(int rune)
    {
        switch (rune)
        {
            case >= 0x20 and <= 0x2F:
                _intermediates.Append((char)rune);
                return;
            case >= 0x40 and <= 0x7E:
                FinishParam();
                _oscOrDcs.Clear();
                _dcsFinal = (char)rune;
                _state = State.DcsPassthrough;
                return;
            default:
                _state = State.DcsIgnore;
                break;
        }
    }

    private void DcsPassthrough(int rune)
    {
        switch (rune)
        {
            // ST (ESC \) is handled by the global ESC branch in Consume — ESC never reaches here.
            case 0x07:
                DispatchDcs();
                _state = State.Ground;
                return;
            case >= 0x20:
            case 0x09:
            case 0x0A:
            case 0x0D:
                _oscOrDcs.Append(char.ConvertFromUtf32(rune));
                break;
        }
    }

    private void DcsIgnore(int rune)
    {
        if (rune == 0x1B)
        {
            EnterEscape();
        }
    }

    private void DispatchDcs()
    {
        actions.DcsDispatch(_prefix, _params, _intermediates.ToString(), _dcsFinal, _oscOrDcs.ToString());
        _oscOrDcs.Clear();
    }

    private void SosPmApcString(int rune)
    {
        switch (rune)
        {
            // Consume until ST/BEL; content is ignored.
            case 0x1B:
                EnterEscape();
                return;
            case 0x07:
                _state = State.Ground;
                break;
        }
    }

    // ---- VT52 ---------------------------------------------------------------

    private void ConsumeVt52(int rune)
    {
        switch (_state)
        {
            case State.Ground:
                if (rune == 0x1B)
                {
                    _state = State.Vt52Escape;
                    return;
                }
                if (IsC0(rune) || rune == 0x7F)
                {
                    actions.Execute((char)rune);
                    return;
                }
                actions.Print(rune);
                return;
            case State.Vt52Escape:
                if (rune == 'Y')
                {
                    _state = State.Vt52CursorRow;
                    return;
                }
                // Deliver every other VT52 command as an ESC dispatch with no intermediates.
                actions.EscDispatch(string.Empty, (char)rune);
                _state = State.Ground;
                return;
            case State.Vt52CursorRow:
                _vt52Row = rune - 0x20;
                _state = State.Vt52CursorCol;
                return;
            case State.Vt52CursorCol:
                _params.Clear();
                _params.Add(_vt52Row + 1);
                _params.Add(rune - 0x20 + 1);
                actions.CsiDispatch('\0', _params, string.Empty, 'H');
                _state = State.Ground;
                return;
            case State.Escape:
            case State.EscapeIntermediate:
            case State.CsiEntry:
            case State.CsiParam:
            case State.CsiIntermediate:
            case State.CsiIgnore:
            case State.OscString:
            case State.DcsEntry:
            case State.DcsParam:
            case State.DcsIntermediate:
            case State.DcsPassthrough:
            case State.DcsIgnore:
            case State.SosPmApcString:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    // ---- Param helpers ------------------------------------------------------

    private void HandleParamDigit(int rune)
    {
        if (rune is ';' or ':')
        {
            FinishParam();
            return;
        }
        if (_params.Count >= MaxParams)
        {
            return;
        }
        _hasCurrentParam = true;
        _currentParam = _currentParam * 10 + (rune - '0');
        if (_currentParam > 65535)
        {
            _currentParam = 65535;
        }
    }

    private void FinishParam()
    {
        _params.Add(_hasCurrentParam ? _currentParam : 0);
        _currentParam = 0;
        _hasCurrentParam = false;
    }

    private void ClearParams()
    {
        _params.Clear();
        _currentParam = 0;
        _hasCurrentParam = false;
    }

    private static bool IsC0(int rune) => rune is >= 0x00 and <= 0x1F;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        SosPmApcString,

        // VT52 sub-states
        Vt52Escape,
        Vt52CursorRow,
        Vt52CursorCol
    }
}
