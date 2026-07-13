namespace VelaShell.Core.Ssh;

/// <summary>
/// 库中立的终端模式操作码(RFC 4254 §8 "Encoding of Terminal Modes")。
/// 值即协议本身定义的 opcode,与任何具体 SSH 库无关;Infrastructure 侧按数值
/// 直接映射到所用库的对应枚举。
/// </summary>
public enum TerminalMode : byte
{
    /// <summary>中断字符(通常为 Ctrl-C),用于向前台进程发送中断信号。</summary>
    VINTR = 1,
    /// <summary>退出字符(通常为 Ctrl-\),用于向前台进程发送退出信号。</summary>
    VQUIT = 2,
    /// <summary>擦除字符(退格),删除光标前的上一个字符。</summary>
    VERASE = 3,
    /// <summary>删除行字符,删除当前整行输入。</summary>
    VKILL = 4,
    /// <summary>文件结束字符(通常为 Ctrl-D),标识输入结束。</summary>
    VEOF = 5,
    /// <summary>行结束字符,标识一行输入的结束。</summary>
    VEOL = 6,
    /// <summary>备用行结束字符。</summary>
    VEOL2 = 7,
    /// <summary>恢复输出字符(XON),用于软件流控。</summary>
    VSTART = 8,
    /// <summary>停止输出字符(XOFF),用于软件流控。</summary>
    VSTOP = 9,
    /// <summary>挂起字符(通常为 Ctrl-Z),用于挂起前台进程。</summary>
    VSUSP = 10,
    /// <summary>延迟挂起字符,在下一次读取输入时挂起进程。</summary>
    VDSUSP = 11,
    /// <summary>重新打印当前行字符。</summary>
    VREPRINT = 12,
    /// <summary>擦除单词字符,删除光标前的上一个单词。</summary>
    VWERASE = 13,
    /// <summary>字面输入下一个字符,取消其特殊含义。</summary>
    VLNEXT = 14,
    /// <summary>刷新字符,丢弃缓冲的输入输出。</summary>
    VFLUSH = 15,
    /// <summary>切换 shell 层字符。</summary>
    VSWTCH = 16,
    /// <summary>状态请求字符,请求当前进程状态。</summary>
    VSTATUS = 17,
    /// <summary>丢弃输出切换字符,切换输出的丢弃状态。</summary>
    VDISCARD = 18,
    /// <summary>忽略输入中的奇偶校验错误。</summary>
    IGNPAR = 30,
    /// <summary>标记输入中的奇偶校验错误。</summary>
    PARMRK = 31,
    /// <summary>启用输入的奇偶校验检查。</summary>
    INPCK = 32,
    /// <summary>剥离输入字符的第八位。</summary>
    ISTRIP = 33,
    /// <summary>将输入的换行(NL)映射为回车(CR)。</summary>
    INLCR = 34,
    /// <summary>忽略输入中的回车(CR)。</summary>
    IGNCR = 35,
    /// <summary>将输入的回车(CR)映射为换行(NL)。</summary>
    ICRNL = 36,
    /// <summary>将输入的大写字母转换为小写。</summary>
    IUCLC = 37,
    /// <summary>启用输出方向的软件流控(XON/XOFF)。</summary>
    IXON = 38,
    /// <summary>允许任意字符恢复已停止的输出。</summary>
    IXANY = 39,
    /// <summary>启用输入方向的软件流控(XON/XOFF)。</summary>
    IXOFF = 40,
    /// <summary>输入队列已满时响铃提示。</summary>
    IMAXBEL = 41,
    /// <summary>启用由特殊字符(INTR、QUIT、SUSP)生成的信号。</summary>
    ISIG = 50,
    /// <summary>启用规范(行编辑)输入模式。</summary>
    ICANON = 51,
    /// <summary>规范大小写呈现模式。</summary>
    XCASE = 52,
    /// <summary>回显输入的字符。</summary>
    ECHO = 53,
    /// <summary>以退格-空格-退格的方式可视化回显擦除字符。</summary>
    ECHOE = 54,
    /// <summary>回显 KILL 字符时删除整行。</summary>
    ECHOK = 55,
    /// <summary>即使未启用 ECHO 也回显换行。</summary>
    ECHONL = 56,
    /// <summary>接收 INTR 或 QUIT 字符时不刷新输入输出队列。</summary>
    NOFLSH = 57,
    /// <summary>后台进程写终端时发送 SIGTTOU 信号。</summary>
    TOSTOP = 58,
    /// <summary>启用扩展的输入字符处理。</summary>
    IEXTEN = 59,
    /// <summary>以 ^X 形式回显控制字符。</summary>
    ECHOCTL = 60,
    /// <summary>擦除 KILL 时逐字符地视觉擦除整行。</summary>
    ECHOKE = 61,
    /// <summary>下一次读取时重新打印待处理的输入。</summary>
    PENDIN = 62,
    /// <summary>启用输出后处理。</summary>
    OPOST = 70,
    /// <summary>将输出的小写字母转换为大写。</summary>
    OLCUC = 71,
    /// <summary>将输出的换行(NL)映射为回车换行(CR-NL)。</summary>
    ONLCR = 72,
    /// <summary>将输出的回车(CR)映射为换行(NL)。</summary>
    OCRNL = 73,
    /// <summary>在第 0 列时不输出回车(CR)。</summary>
    ONOCR = 74,
    /// <summary>换行(NL)执行回车功能。</summary>
    ONLRET = 75,
    /// <summary>每字符使用 7 个数据位。</summary>
    CS7 = 90,
    /// <summary>每字符使用 8 个数据位。</summary>
    CS8 = 91,
    /// <summary>启用奇偶校验位的生成与检测。</summary>
    PARENB = 92,
    /// <summary>使用奇校验(否则为偶校验)。</summary>
    PARODD = 93,
    /// <summary>输入波特率(TTY 操作)。</summary>
    TTY_OP_ISPEED = 128,
    /// <summary>输出波特率(TTY 操作)。</summary>
    TTY_OP_OSPEED = 129
}
