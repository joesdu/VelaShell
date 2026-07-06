namespace PulseTerm.Core.Models;

public class AppSettings
{
    public string Language { get; set; } = "en";
    
    public string Theme { get; set; } = "dark";

    /// <summary>Accent-color override as a hex string (e.g. "#00D4AA"); empty = use theme default.</summary>
    public string AccentColor { get; set; } = "";
    
    public string TerminalFont { get; set; } = "JetBrains Mono";
    
    public int TerminalFontSize { get; set; } = 14;
    
    public int ScrollbackLines { get; set; } = 50000;

    public int DefaultPort { get; set; } = 22;

    /// <summary>Terminal emulation profile advertised as TERM (default xterm-256color).</summary>
    public string TerminalType { get; set; } = "xterm-256color";

    /// <summary>Character encoding used to decode host output (default UTF-8).</summary>
    public string TerminalEncoding { get; set; } = "UTF-8";
}
