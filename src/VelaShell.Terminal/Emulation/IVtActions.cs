namespace VelaShell.Terminal.Emulation;

/// <summary>
/// Sink for the events produced by <see cref="VtParser"/>. The <see cref="TerminalEmulator"/>
/// implements this to turn parsed escape sequences into screen-buffer mutations.
/// </summary>
public interface IVtActions
{
    /// <summary>A printable Unicode scalar value should be written at the cursor.</summary>
    void Print(int rune);

    /// <summary>A C0 control character (0x00-0x1F) or DEL should be executed.</summary>
    void Execute(char control);

    /// <summary>An <c>ESC</c> sequence: <c>ESC {intermediates} {final}</c> (e.g. ESC ( B).</summary>
    void EscDispatch(string intermediates, char final);

    /// <summary>
    /// A CSI sequence: <c>CSI {prefix} {params} {intermediates} {final}</c>.
    /// <paramref name="prefix"/> is the private marker (e.g. <c>?</c>, <c>&gt;</c>) or '\0'.
    /// </summary>
    void CsiDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final);

    /// <summary>An OSC sequence. <paramref name="parameters"/> is the ';'-split payload.</summary>
    void OscDispatch(IReadOnlyList<string> parameters);

    /// <summary>A DCS sequence with its collected string payload.</summary>
    void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data);
}
