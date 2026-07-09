namespace PulseTerm.Core.Models;

/// <summary>
/// 终端配色方案预设(§12.5):一键把整套颜色写入 <see cref="AppearanceOptions"/>。
/// 应用后与出厂默认(Dracula)不同的颜色会作为覆盖生效(见 TerminalAppearanceMapper 的
/// 稀疏覆盖机制);选择 Dracula 即恢复默认、终端重新跟随应用主题(暗 Dracula/亮 Alucard)。
/// </summary>
public sealed record TerminalColorScheme(
    string Name,
    string Foreground,
    string Background,
    string Cursor,
    string Selection,
    string[] AnsiNormal,
    string[] AnsiBright)
{
    public void ApplyTo(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        appearance.TerminalForeground = Foreground;
        appearance.TerminalBackground = Background;
        appearance.CursorColor = Cursor;
        appearance.SelectionColor = Selection;
        appearance.AnsiNormal = [.. AnsiNormal];
        appearance.AnsiBright = [.. AnsiBright];
    }

    /// <summary>内置方案;首项 Dracula 等同出厂默认(不产生覆盖、跟随主题)。</summary>
    public static readonly TerminalColorScheme[] BuiltIn =
    [
        new("Dracula(默认)",
            "#F8F8F2", "#282A36", "#F8F8F2", "#44475A",
            ["#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2"],
            ["#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"]),

        new("Solarized Dark",
            "#839496", "#002B36", "#839496", "#073642",
            ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5"],
            ["#586E75", "#CB4B16", "#859900", "#B58900", "#268BD2", "#6C71C4", "#93A1A1", "#FDF6E3"]),

        new("Solarized Light",
            "#657B83", "#FDF6E3", "#657B83", "#EEE8D5",
            ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5"],
            ["#586E75", "#CB4B16", "#859900", "#B58900", "#268BD2", "#6C71C4", "#93A1A1", "#FDF6E3"]),

        new("Nord",
            "#D8DEE9", "#2E3440", "#D8DEE9", "#434C5E",
            ["#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0"],
            ["#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"]),

        new("Gruvbox Dark",
            "#EBDBB2", "#282828", "#EBDBB2", "#504945",
            ["#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984"],
            ["#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2"]),

        new("One Dark",
            "#ABB2BF", "#282C34", "#ABB2BF", "#3E4451",
            ["#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#ABB2BF"],
            ["#5C6370", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#FFFFFF"]),

        new("Monokai",
            "#F8F8F2", "#272822", "#F8F8F2", "#49483E",
            ["#272822", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F8F8F2"],
            ["#75715E", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F9F8F5"]),

        new("Tokyo Night",
            "#C0CAF5", "#1A1B26", "#C0CAF5", "#33467C",
            ["#15161E", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6"],
            ["#414868", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5"]),
    ];
}
