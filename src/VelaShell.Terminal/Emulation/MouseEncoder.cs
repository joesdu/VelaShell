using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>以 xterm 按钮码表示的鼠标按键(或滚轮一格)。</summary>
public enum TerminalMouseButton
{
    /// <summary>鼠标左键(xterm 按钮码 0)。</summary>
    Left = 0,
    /// <summary>鼠标中键 / 滚轮按下(xterm 按钮码 1)。</summary>
    Middle = 1,
    /// <summary>鼠标右键(xterm 按钮码 2)。</summary>
    Right = 2,
    /// <summary>无按键按下;用于无按键移动或传统释放事件(码 3)。</summary>
    None = 3, // 无按键移动 / 传统释放
    /// <summary>滚轮向上滚一格(xterm 按钮码 64)。</summary>
    WheelUp = 64,
    /// <summary>滚轮向下滚一格(xterm 按钮码 65)。</summary>
    WheelDown = 65
}

/// <summary>上报给终端应用程序的指针事件类型。</summary>
public enum TerminalMouseEventType
{
    /// <summary>按键被按下。</summary>
    Press,
    /// <summary>按键被释放。</summary>
    Release,
    /// <summary>指针移动(移动 / 拖拽)。</summary>
    Move
}

/// <summary>
/// 把指针事件编码成应用程序在开启鼠标跟踪(DECSET ?9/?1000/?1002/?1003)时所期望的字节序列,
/// 使 htop、btop、vim、tmux 等程序能够收到点击、拖拽与滚轮事件。遵循当前生效的编码方式:
/// 传统 X10(<c>ESC [ M</c>)、SGR(<c>ESC [ &lt; … M/m</c>, ?1006)以及 urxvt(<c>ESC [ … M</c>, ?1015)。
/// 本类与 UI 无关——调用方只传入纯修饰键标志——因此可直接进行单元测试。
/// </summary>
public static class MouseEncoder
{
    /// <summary>
    /// 返回在当前 <paramref name="modes" /> 下为某鼠标事件应发送的字节;当该事件不应上报时(例如
    /// 在仅按下模式中发生移动,或跟踪关闭时的任意事件)返回 null。列/行是可见屏幕内从 0 开始的单元格坐标。
    /// </summary>
    public static byte[]? Encode(
        TerminalMouseEventType type,
        TerminalMouseButton button,
        int column,
        int row,
        bool shift,
        bool alt,
        bool control,
        TerminalModes modes)
    {
        MouseTracking tracking = modes.Mouse;
        if (tracking == MouseTracking.None)
        {
            return null;
        }
        bool isWheel = button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown;

        // 各跟踪模式分别上报哪些事件类型。
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (tracking)
        {
            case MouseTracking.X10:
                if (type != TerminalMouseEventType.Press || isWheel)
                {
                    return null;
                }
                break;
            case MouseTracking.Normal:
                // ReSharper disable once ConvertIfStatementToSwitchStatement
                if (type == TerminalMouseEventType.Move)
                {
                    return null;
                }
                if (type == TerminalMouseEventType.Release && isWheel)
                {
                    return null;
                }
                break;
            case MouseTracking.ButtonEvent:
            case MouseTracking.AnyEvent:
                if (type == TerminalMouseEventType.Release && isWheel)
                {
                    return null;
                }
                break;
        }
        bool sgr = modes.MouseEncoding == MouseEncoding.Sgr;
        int cb;
        if (isWheel)
        {
            cb = (int)button; // 64 / 65
        }
        else if (type == TerminalMouseEventType.Release && !sgr)
        {
            cb = 3; // 传统释放:按钮位 = 3
        }
        else
        {
            cb = (int)button; // 0/1/2,或 3(None)表示无按键移动
        }
        if (type == TerminalMouseEventType.Move)
        {
            cb += 32; // 移动位
        }

        // X10 早于修饰键上报机制;其余模式都携带 Shift/Alt/Control。
        if (tracking != MouseTracking.X10)
        {
            if (shift)
            {
                cb += 4;
            }
            if (alt)
            {
                cb += 8;
            }
            if (control)
            {
                cb += 16;
            }
        }
        int cx = column + 1; // 上报的坐标从 1 开始
        int cy = row + 1;
        return modes.MouseEncoding switch
        {
            MouseEncoding.Sgr => Ascii($"\e[<{cb};{cx};{cy}{(type == TerminalMouseEventType.Release ? 'm' : 'M')}"),
            MouseEncoding.Urxvt => Ascii($"\e[{cb + 32};{cx};{cy}M"),
            _ => EncodeX10(cb, cx, cy)
        };
    }

    // 传统编码:ESC [ M,随后三个字节各加 32 偏移。坐标上限为
    // 223(255-32);超出者会被钳制,与 xterm 行为一致。
    private static byte[] EncodeX10(int cb, int cx, int cy)
    {
        byte b = (byte)(32 + (cb & 0xFF));
        byte x = (byte)(32 + Math.Clamp(cx, 1, 223));
        byte y = (byte)(32 + Math.Clamp(cy, 1, 223));
        return [0x1b, (byte)'[', (byte)'M', b, x, y];
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
