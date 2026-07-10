using Avalonia.Controls;

namespace VelaShell.Terminal;

/// <summary>
/// Interface for terminal emulator that can be swapped between implementations
/// (AvaloniaTerminal, xterm.js, etc.)
/// </summary>
public interface ITerminalEmulator : IDisposable
{
    /// <summary>
    /// Current cursor row position
    /// </summary>
    int CursorRow { get; }

    /// <summary>
    /// Current cursor column position
    /// </summary>
    int CursorCol { get; }

    /// <summary>
    /// Number of scrollback lines to keep
    /// </summary>
    int ScrollbackLines { get; set; }

    /// <summary>
    /// The Avalonia control to embed in UI
    /// </summary>
    Control Control { get; }

    /// <summary>
    /// Current number of columns
    /// </summary>
    int Columns { get; }

    /// <summary>
    /// Current number of rows
    /// </summary>
    int Rows { get; }

    ScrollbackBuffer ScrollbackBuffer { get; }

    int TotalLines { get; }

    int ViewportRow { get; }

    /// <summary>
    /// Feed raw bytes from SSH stream to terminal
    /// </summary>
    void Feed(byte[] data);

    /// <summary>
    /// Resize the terminal
    /// </summary>
    void Resize(int cols, int rows);

    /// <summary>
    /// Event fired when user types input (to send to SSH)
    /// </summary>
    event Action<byte[]>? UserInput;

    /// <summary>
    /// 仅"用户产生"的输入(键盘/IME/鼠标上报/粘贴/程序化写入),不含终端的协议自动应答
    /// (光标位置报告、设备属性等也经 <see cref="UserInput" /> 发往 PTY,但并非用户键入)。
    /// 命令补全的行跟踪(plan.md #16)只应订阅此事件,否则会话初期的 ESC 应答会把
    /// 跟踪器永久打进未知态。
    /// </summary>
    event Action<byte[]>? TypedInput;

    /// <summary>
    /// Raised when the terminal's character-cell grid changes size (e.g. the control was
    /// laid out at a new size) so the host PTY can be resized to match. Args: (columns, rows).
    /// </summary>
    event Action<int, int>? PtySizeChanged;

    /// <summary>
    /// Programmatically send input bytes as if the user typed them
    /// </summary>
    void WriteInput(byte[] data);

    /// <summary>
    /// Get the content of a specific line in the terminal buffer
    /// </summary>
    string GetBufferLine(int row);
}
