using System.Text;

namespace VelaShell.Terminal.Input;

/// <summary>
/// 旁路跟踪用户正在终端里键入的命令行(命令补全/历史建议的数据来源,plan.md #16)。
/// 终端本身不知道 shell 的行缓冲,这里以"监听发往 PTY 的用户输入字节"重建它:
/// 可打印字符追加、退格回删、Enter 提交、Ctrl+C/Ctrl+U 清空。凡是本地无法推演结果的
/// 编辑(方向键/Tab 补全/其余控制键、含粘贴的括号模式前缀 ESC)一律进入 <b>未知态</b>
/// (<see cref="CurrentInput" /> = null,建议停用),直到 Enter/Ctrl+C/Ctrl+U 把行重置为
/// 确定的空——宁可少建议,绝不按错误的行内容建议。
/// </summary>
public sealed class TerminalInputTracker
{
    private readonly StringBuilder _buffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _unknown;

    /// <summary>当前推演出的行内容;null = 未知态(发生过本地无法跟踪的编辑)。</summary>
    public string? CurrentInput => _unknown ? null : _buffer.ToString();

    /// <summary>行内容(或未知态)变化时触发,在输入线程(UI 线程)上同步回调。</summary>
    public event Action? InputChanged;

    /// <summary>
    /// 用户按 Enter 提交了一行(仅确定态且非空时触发,参数为提交内容)。
    /// 消费方仍应做回显校验后再入历史(密码输入无回显,不得记录)。
    /// </summary>
    public event Action<string>? CommandSubmitted;

    /// <summary>处理一段发往 PTY 的用户输入字节(与 UserInput 事件的粒度一致)。</summary>
    public void Process(byte[] data)
    {
        bool changed = false;
        foreach (byte b in data)
        {
            switch (b)
            {
                case 0x0D: // Enter:提交并回到确定的空行(shell 开始新行)。
                    if (!_unknown && _buffer.Length > 0)
                    {
                        CommandSubmitted?.Invoke(_buffer.ToString());
                    }
                    changed |= ResetToKnownEmpty();
                    break;
                case 0x7F or 0x08: // Backspace / DEL:回删一个字符。
                    if (!_unknown && _buffer.Length > 0)
                    {
                        RemoveLastCharacter();
                        changed = true;
                    }
                    break;
                case 0x03 or 0x15: // Ctrl+C / Ctrl+U:shell 把行清掉,回到确定的空行。
                    changed |= ResetToKnownEmpty();
                    break;
                default:
                    if (b is < 0x20 or 0x1B)
                    {
                        // 其余控制字节(方向键等 ESC 序列、Tab 补全、Ctrl+W/A/E...):
                        // 行内容从此不可知。
                        changed |= MarkUnknown();
                    }
                    else if (!_unknown)
                    {
                        changed |= AppendByte(b);
                    }
                    break;
            }
        }
        if (changed)
        {
            InputChanged?.Invoke();
        }
    }

    private bool ResetToKnownEmpty()
    {
        bool changed = _unknown || _buffer.Length > 0;
        _unknown = false;
        _buffer.Clear();
        _decoder.Reset();
        return changed;
    }

    private bool MarkUnknown()
    {
        if (_unknown)
        {
            return false;
        }
        _unknown = true;
        _buffer.Clear();
        _decoder.Reset();
        return true;
    }

    private bool AppendByte(byte b)
    {
        Span<char> chars = stackalloc char[2];
        int produced = _decoder.GetChars([b], chars, false);
        for (int i = 0; i < produced; i++)
        {
            _buffer.Append(chars[i]);
        }
        return produced > 0;
    }

    private void RemoveLastCharacter()
    {
        // shell 的一次 DEL 按"字符"回删;代理对(emoji 等)按整字删除。
        int remove = _buffer.Length >= 2 && char.IsLowSurrogate(_buffer[^1]) && char.IsHighSurrogate(_buffer[^2]) ? 2 : 1;
        _buffer.Length -= remove;
    }
}
