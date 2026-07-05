namespace PulseTerm.Terminal.Emulation;

/// <summary>
/// Terminal operating modes (ANSI and DEC private). Defaults match a freshly reset
/// xterm: autowrap on, cursor visible, everything else off.
/// </summary>
public sealed class TerminalModes
{
    // DEC private modes
    public bool ApplicationCursorKeys;    // DECCKM  ?1
    public bool ApplicationKeypad;        // DECKPAM / DECKPNM
    public bool OriginMode;               // DECOM   ?6
    public bool AutoWrap = true;          // DECAWM  ?7
    public bool ReverseVideo;             // DECSCNM ?5
    public bool CursorVisible = true;     // DECTCEM ?25
    public bool BracketedPaste;           // ?2004
    public bool CursorBlink = true;       // ?12

    // Mouse tracking (?1000/1002/1003) and encoding (?1006 SGR / ?1015 urxvt)
    public MouseTracking Mouse = MouseTracking.None;
    public MouseEncoding MouseEncoding = MouseEncoding.Default;

    // ANSI modes
    public bool InsertMode;               // IRM  (4)
    public bool NewLineMode;              // LNM  (20)

    public void Reset()
    {
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        OriginMode = false;
        AutoWrap = true;
        ReverseVideo = false;
        CursorVisible = true;
        BracketedPaste = false;
        CursorBlink = true;
        Mouse = MouseTracking.None;
        MouseEncoding = MouseEncoding.Default;
        InsertMode = false;
        NewLineMode = false;
    }
}

public enum MouseTracking
{
    None,
    X10,       // ?9
    Normal,    // ?1000
    ButtonEvent, // ?1002
    AnyEvent,  // ?1003
}

public enum MouseEncoding
{
    Default,
    Sgr,       // ?1006
    Urxvt,     // ?1015
}
