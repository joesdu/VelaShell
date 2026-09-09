using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 终端的"大脑":消费来自 <see cref="VtParser" /> 的已解析转义序列事件,并将其施加到
/// <see cref="TerminalScreen" /> 上。持有当前图形显示属性(画笔)、字符集、终端模式、制表位
/// 以及保存的光标状态,并通过 <see cref="Response" /> 产生发往宿主的应答
/// (设备属性、光标报告等)。行为受当前生效的 <see cref="TerminalType" /> 约束,
/// 因此同一引擎可仿真从 VT52 到 xterm-256color 的任何终端。
/// </summary>
public sealed class TerminalEmulator : IVtActions
{
    /// <summary>OSC 52 载荷上限(base64 解码后),防远端滥发撑爆剪贴板。</summary>
    private const int MaxOsc52Bytes = 64 * 1024;

    // 字符集:G0..G3 指定,GL/GR 调用,单字符移位。
    private readonly bool[] _decGraphics = new bool[4]; // true => DEC 特殊图形
    private readonly TerminalScreen _mainScreen;
    private readonly VtParser _parser;
    private readonly Utf8Sink _utf8 = new();

    // 备用屏切换(DECSET 1049)专用的独立保存槽。与 _saved 区分开来正是 xterm 的做法:
    // 在备用屏(如 nano)中运行的应用可自由使用 DECSC/DECRC,
    // 而不会破坏退出时必须恢复的、主屏的光标位置(#14b)。
    private SavedCursor? _altSaved;
    private TerminalScreen? _altScreen; // 备用缓冲区(无回滚)
    private TerminalColor _bg = TerminalColor.Default;

    // 当前画笔
    private TerminalColor _fg = TerminalColor.Default;
    private CellFlags _flags = CellFlags.None;
    private int _gl; // 当前生效的 GL 字符集索引

    // 当前生效的 OSC 8 超链接句柄(0 = 不在链接内)。它<b>不是画笔的一部分</b>:
    // OSC 8 与 SGR 相互独立,SGR 0 不会关掉链接,只有 `OSC 8 ; ; ST` 才会(规范如此)。
    private ushort _link;

    // 本条命令块的提示符行(OSC 133 A 所在行):D 报回来的退出码要记到它头上,而那时光标
    // 早已在输出末尾。持行对象引用而非行号 —— 行会随滚动迁进 scrollback,行号一直在变。
    private TerminalRow? _promptRow;

    private bool _pendingWrap; // 行尾的延迟自动换行
    private DateTime _feedTimestamp = DateTime.Now; // 当前 Feed 到达时刻,用于给写入的行盖时间戳(行号侧栏)

    // Saved cursor (DECSC / DECRC, CSI s/u, DECSET 1048)
    private SavedCursor? _saved;

    private int _singleShift = -1;
    private bool[] _tabStops;

    /// <summary>以给定的屏幕尺寸、终端类型与回滚容量创建仿真器。</summary>
    public TerminalEmulator(int columns = 80, int rows = 24, TerminalType type = TerminalType.XtermColor256, int scrollback = 10_000)
    {
        Type = type;
        Palette = new();
        Modes = new();
        Screen = new(columns, rows, scrollback);
        _mainScreen = Screen;
        _tabStops = BuildDefaultTabs(columns);
        _parser = new(this) { Vt52Mode = type == TerminalType.Vt52 };
    }

    /// <summary>当前正在仿真的终端类型;约束功能行为。</summary>
    public TerminalType Type { get; private set; }

    /// <summary>当前生效的终端模式(自动换行、原点模式、鼠标跟踪等)。</summary>
    public TerminalModes Modes { get; }

    /// <summary>用于把索引色解析为具体 RGB 值的调色板。</summary>
    public TerminalPalette Palette { get; }

    /// <summary>
    /// 本终端的 OSC 8 超链接驻留表:把单元格里的 <see cref="ushort" /> 句柄换回 URI。
    /// </summary>
    /// <remarks>渲染层用它决定哪些格该画下划线、Ctrl+点击该打开什么。</remarks>
    public HyperlinkTable Hyperlinks { get; } = new();

    /// <summary>
    /// 本会话是否收到过 OSC 133 语义标记 —— 也就是对端装没装 shell 集成。
    /// </summary>
    /// <remarks>
    /// <b>是个只进不退的锁存位,不是"当前屏上有没有标记"。</b>侧栏的标记列据它决定显不显示,
    /// 而按帧去扫描屏幕会让列宽在滚动到没有标记的历史区时突然收起来 —— 正文跟着左右抖。
    /// 锁存之后整个会话宽度恒定,只有 RIS 硬复位(缓冲区连同回滚一并清空)才归零。
    /// </remarks>
    public bool HasPromptMarks { get; private set; }

    /// <summary>当前生效的屏幕缓冲区(主屏或备用屏)。</summary>
    public TerminalScreen Screen { get; private set; }

    /// <summary>主屏保留的最大回滚行数(设置 → 终端 → 回滚行数)。</summary>
    /// <remarks>
    /// <b>读写的恒是主屏,与此刻是否在备用屏无关。</b>备用屏的容量恒为 0 且不可配 ——
    /// 它归全屏程序(vim / htop / less)所有:一旦给了它回滚容量,退休行会被压进历史
    /// (<c>TerminalScreen.ScrollUp</c>),改窗口大小还会连带触发一次它本不该做的 reflow
    /// (<c>TerminalScreen.Resize</c> —— 备用屏靠 SIGWINCH 自行重绘)。
    /// 早先控件直接写 <see cref="Screen" />,于是「在 vim 里保存设置」两头落空:
    /// 主屏没改到(用户以为生效了),备用屏反而被弄脏。
    /// </remarks>
    public int ScrollbackLines
    {
        get => _mainScreen.MaxScrollback;
        set => _mainScreen.MaxScrollback = value;
    }

    /// <summary>当前屏幕的列数。</summary>
    public int Columns => Screen.Columns;

    /// <summary>当前屏幕的行数。</summary>
    public int Rows => Screen.Rows;

    /// <summary>当前光标列(从 0 开始)。</summary>
    public int CursorX => Screen.CursorX;

    /// <summary>当前光标行(从 0 开始)。</summary>
    public int CursorY => Screen.CursorY;

    /// <summary>
    /// 是否接受 OSC 52 远端剪贴板写入。默认关闭:终端输出属于不可信输入,只有用户显式开启后
    /// tmux/vim 等远端 yank 才能改写本机剪贴板。
    /// </summary>
    public bool AllowOsc52ClipboardWrite { get; set; }

    /// <summary>备用屏缓冲区处于活动状态(DECSET 1047/1049)时为 true。</summary>
    public bool IsAlternateScreen { get; private set; }

    // ---- IVtActions:打印 ----------------------------------------------

    /// <summary>在光标处写入可打印字符,处理字符集翻译、宽/组合字形与自动换行。</summary>
    public void Print(int rune)
    {
        // 应用当前生效的字符集翻译。
        int setIndex = _singleShift >= 0 ? _singleShift : _gl;
        _singleShift = -1;
        if (_decGraphics[setIndex])
        {
            rune = Charsets.MapDecSpecial(rune);
        }
        int width = CharWidth.Of(rune);

        // 组合标记附加到前一个单元格,且不移动光标。
        if (width == 0)
        {
            AttachCombining(rune);
            return;
        }
        if (_pendingWrap)
        {
            Screen.ActiveLine(Screen.CursorY).Wrapped = true;
            CarriageReturnLineFeed();
            _pendingWrap = false;
        }

        // 对放不下的宽字符做自动换行检查。
        if (width == 2 && Screen.CursorX == Screen.Columns - 1)
        {
            if (Modes.AutoWrap)
            {
                Screen.ActiveLine(Screen.CursorY).Wrapped = true;
                CarriageReturnLineFeed();
            }
            else
            {
                Screen.SetCursorX(Screen.Columns - 2);
            }
        }
        if (Modes.InsertMode)
        {
            Screen.InsertChars(width, Blank());
        }
        var cell = new TerminalCell
        {
            Rune = rune,
            Foreground = _fg,
            Background = _bg,
            Flags = _flags
        };
        Screen.SetCell(Screen.CursorX, Screen.CursorY, cell, _link);
        // 行时间戳取「本次 Feed 到达时刻」——按 chunk 取一次,避免逐字符 DateTime.Now;
        // 同一行被多次写入时以最后一次为准(= 该行最后收到输出的时间)。
        Screen.ActiveLine(Screen.CursorY).Timestamp = _feedTimestamp;
        if (width == 2)
        {
            TerminalCell trailing = cell;
            trailing.Rune = 0;
            trailing.Flags |= CellFlags.WideTrailing;
            // 尾格与前导格同属一个字符,链接必须一并盖上 —— 否则宽字符链接的右半格
            // 点不开,悬停时手型在半个字上闪。
            Screen.SetCell(Screen.CursorX + 1, Screen.CursorY, trailing, _link);
        }
        if (Screen.CursorX + width >= Screen.Columns)
        {
            if (Modes.AutoWrap)
            {
                Screen.SetCursorX(Screen.Columns - 1);
                _pendingWrap = true;
            }
            else
            {
                Screen.SetCursorX(Screen.Columns - 1);
            }
        }
        else
        {
            Screen.SetCursorX(Screen.CursorX + width);
        }
    }

    /// <summary>
    /// 批量打印快路径实际接住了多少个字符(测试用)。
    /// </summary>
    /// <remarks>
    /// 快路径与逐字符路径产出必须一致,而"一致"最省事的作弊方式就是快路径压根没触发 ——
    /// 那样等价性用例会全部空过。<c>PrintRunEquivalenceTests</c> 靠这个计数确认它真的跑了。
    /// </remarks>
    internal int PrintRunCharsForTest { get; private set; }

    /// <inheritdoc />
    public void PrintRun(ReadOnlySpan<char> text)
    {
        PrintRunCharsForTest += text.Length;
        // 有三种情况下"连续写 n 个单宽格"不成立,交回逐字符路径 —— 它才是语义的定义者:
        //   单次移位在等一个字符、当前 G 集是 DEC 图形集(要逐个映射)、插入模式(每格都要挪行)。
        // 三者都是罕见状态,退化一次不影响纯文本洪流这个主场景。
        if (_singleShift >= 0 || _decGraphics[_gl] || Modes.InsertMode)
        {
            foreach (char c in text)
            {
                Print(c);
            }
            return;
        }
        TerminalCell template = new() { Foreground = _fg, Background = _bg, Flags = _flags };
        int i = 0;
        while (i < text.Length)
        {
            if (_pendingWrap)
            {
                Screen.ActiveLine(Screen.CursorY).Wrapped = true;
                CarriageReturnLineFeed();
                _pendingWrap = false;
            }
            int x = Screen.CursorX;
            int take = Math.Min(text.Length - i, Screen.Columns - x);
            TerminalRow row = Screen.ActiveLine(Screen.CursorY);
            // 整段取一次可写切片:逐格走索引器的话,每一格都要付一次边界判断加一次
            // EnsureStored 调用,而那两件事对一整段来说做一次就够。
            Span<TerminalCell> cells = row.WritableSpan.Slice(x, take);
            for (int k = 0; k < take; k++)
            {
                template.Rune = text[i + k];
                cells[k] = template;
            }
            // 链接同样整段盖一次。传 0 且本行从无链接时是空操作(见 TerminalRow.SetLinkRange),
            // 所以纯文本洪流这条主路径一分钱不多花;而在链接格上覆写普通文本时,它负责把旧句柄抹掉。
            row.SetLinkRange(x, x + take, _link);
            // 行时间戳按段取一次,与逐字符路径同一语义(该行最后收到输出的时间)。
            row.Timestamp = _feedTimestamp;
            i += take;
            int end = x + take;
            if (end >= Screen.Columns)
            {
                // 与 Print 逐字对齐:写满一行后光标停在最后一列,是否置待换行由 AutoWrap 决定。
                // 关掉 AutoWrap 时后续字符会反复覆盖最后一列 —— 这里每轮 take 恰好是 1,
                // 行为与逐字符路径逐字相同。
                Screen.SetCursorX(Screen.Columns - 1);
                _pendingWrap = Modes.AutoWrap;
            }
            else
            {
                Screen.SetCursorX(end);
            }
        }
    }

    // ---- IVtActions:C0 控制字符 -------------------------------------------

    /// <summary>执行一个 C0 控制字符(BEL、BS、HT、LF/VT/FF、CR、SO/SI)。</summary>
    public void Execute(char control)
    {
        switch (control)
        {
            case '\a': // BEL
                Bell?.Invoke();
                break;
            case '\b': // BS
                if (_pendingWrap)
                {
                    _pendingWrap = false;
                }
                else if (Screen.CursorX > 0)
                {
                    Screen.SetCursorX(Screen.CursorX - 1);
                }
                break;
            case '\t': // HT
                HorizontalTab();
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                _pendingWrap = false;
                IndexAndStamp();
                if (Modes.NewLineMode)
                {
                    Screen.SetCursorX(0);
                }
                break;
            case '\r': // CR
                _pendingWrap = false;
                Screen.SetCursorX(0);
                break;
            case '\x0E': // SO -> 把 G1 调用进 GL
                _gl = 1;
                break;
            case '\x0F': // SI -> 把 G0 调用进 GL
                _gl = 0;
                break;
        }
    }

    // ---- IVtActions:ESC ----------------------------------------------------

    /// <summary>分发 ESC 序列(字符集指定、IND/RI/NEL、DECSC/DECRC、RIS 等)。</summary>
    public void EscDispatch(string intermediates, char final)
    {
        if (Type == TerminalType.Vt52 && intermediates.Length == 0)
        {
            EscDispatchVt52(final);
            return;
        }
        if (intermediates.Length > 0)
        {
            char inter = intermediates[0];
            switch (inter)
            {
                case '(' or ')' or '*' or '+': // designate G0..G3
                    int g = inter switch { '(' => 0, ')' => 1, '*' => 2, _ => 3 };
                    _decGraphics[g] = final == '0';
                    return;
                case '#':
                    if (final == '8')
                    {
                        FillScreenWithE(); // DECALN
                    }
                    return;
            }
            return;
        }
        switch (final)
        {
            case 'D':
                IndexAndStamp();
                break; // IND
            case 'M':
                Screen.ReverseIndex(Blank());
                break; // RI
            case 'E':
                Screen.SetCursorX(0);
                IndexAndStamp();
                break; // NEL
            case 'H':
                _tabStops[Math.Clamp(Screen.CursorX, 0, _tabStops.Length - 1)] = true;
                break; // HTS
            case '7':
                SaveCursor();
                break; // DECSC
            case '8':
                RestoreCursor();
                break; // DECRC
            case '=':
                Modes.ApplicationKeypad = true;
                break; // DECCKPAM
            case '>':
                Modes.ApplicationKeypad = false;
                break; // DECKPNM
            case 'c':
                FullReset();
                break; // RIS
            case '\\':
                break; // ST(字符串终结符)
            case 'n':
                _gl = 2;
                break; // LS2
            case 'o':
                _gl = 3;
                break; // LS3
        }
    }

    // ---- IVtActions:CSI ----------------------------------------------------

    /// <summary>分发 CSI 序列(光标移动、擦除、插入/删除、SGR、模式设置/重置、报告等)。</summary>
    public void CsiDispatch(char prefix, IReadOnlyList<int> p, string intermediates, char final)
    {
        // 带中间字节的 CSI 自成一套文法,末字节与无中间字节的同名序列毫无关系:
        // 认识的在这里消费,其余一律忽略 —— 绝不能落到下面按 final 分派的分支去。
        // (vim 启动时的 xterm 兼容性探针 "CSI 0 % m" 曾被当成 SGR 0 执行。)
        if (intermediates.Length > 0)
        {
            if (prefix == '\0')
            {
                switch (intermediates)
                {
                    case "!" when final == 'p':
                        SoftReset(); // DECSTR 软复位
                        break;
                    case " " when final == 'q':
                        // DECSCUSR:渲染按用户设置,这里只记形状供 DECRQSS 回报。
                        // 钳到 0..6(合法取值),回报必须是单个数字,否则 vim 解析不了会把整段应答当键入。
                        Modes.CursorStyle = Math.Clamp(P0(0), 0, 6);
                        break;
                }
            }
            return;
        }
        // 私有前缀(? > = <)与无前缀同样是彼此独立的文法:vim 启动时的
        // "CSI > 4 ; 2 m"(modifyOtherKeys)不是 SGR,"CSI = 0 ; 1 u"(kitty 键盘协议)
        // 也不是恢复光标 —— 早期按 final 裸分派会让终端凭空变色、光标乱跳。
        switch (prefix)
        {
            case '?':
                HandlePrivateMode(p, final);
                return;
            case '>':
            case '=':
            case '<':
                if (final == 'c')
                {
                    DeviceAttributes(prefix); // DA2(CSI > c);DA3(CSI = c)未实现,静默
                }
                return;
        }
        switch (final)
        {
            case '@':
                Screen.InsertChars(P(0), Blank());
                break; // ICH
            case 'A':
                MoveCursor(0, -P(0));
                break; // CUU
            case 'B':
                MoveCursor(0, P(0));
                break; // CUD
            case 'C':
                MoveCursor(P(0), 0);
                break; // CUF
            case 'D':
                MoveCursor(-P(0), 0);
                break; // CUB
            case 'E':
                Screen.SetCursorX(0);
                MoveCursor(0, P(0));
                break; // CNL
            case 'F':
                Screen.SetCursorX(0);
                MoveCursor(0, -P(0));
                break; // CPL
            case '`':
            case 'G':
                SetCursorColumn(P(0) - 1);
                break; // CHA / HPA
            case 'd':
                SetCursorRow(P(0) - 1);
                break; // VPA
            case 'H':
            case 'f':
                CursorPosition(P(0) - 1, P(1) - 1);
                break; // CUP / HVP
            case 'I':
                TabForward(P(0));
                break; // CHT
            case 'Z':
                TabBackward(P(0));
                break; // CBT
            case 'J':
                Screen.EraseInDisplay(P0(0), Blank());
                _pendingWrap = false;
                break; // ED
            case 'K':
                Screen.EraseInLine(P0(0), Blank());
                _pendingWrap = false;
                break; // EL
            case 'L':
                Screen.InsertLines(P(0), Blank());
                break; // IL
            case 'M':
                Screen.DeleteLines(P(0), Blank());
                break; // DL
            case 'P':
                Screen.DeleteChars(P(0), Blank());
                break; // DCH
            case 'X':
                Screen.EraseChars(P(0), Blank());
                break; // ECH
            case 'S':
                Screen.ScrollUp(P(0), Blank());
                break; // SU
            case 'T':
                Screen.ScrollDown(P(0), Blank());
                break; // SD
            case 'm':
                ApplySgr(p);
                break; // SGR
            case 'r':
                SetScrollRegion(p);
                break; // DECSTBM
            case 'h':
                SetAnsiMode(p, true);
                break;
            case 'l':
                SetAnsiMode(p, false);
                break;
            case 'g':
                ClearTabs(P0(0));
                break; // TBC
            case 'c':
                DeviceAttributes(prefix);
                break; // DA
            case 'n':
                DeviceStatusReport(P0(0));
                break; // DSR
            case 's':
                SaveCursor();
                break; // ANSI.SYS save
            case 'u':
                RestoreCursor();
                break; // ANSI.SYS restore
            case 't':
                break; // 窗口操作(已忽略)
        }
        return;

        int P(int index, int def = 1)
        {
            if (index >= p.Count)
            {
                return def;
            }
            int v = p[index];
            return v == 0 ? def : v;
        }

        int P0(int index) => index < p.Count ? p[index] : 0;
    }

    /// <summary>分发 OSC 命令(窗口标题变更、OSC 52 剪贴板写入)。</summary>
    public void OscDispatch(IReadOnlyList<string> p)
    {
        if (p.Count == 0)
        {
            return;
        }
        if (!int.TryParse(p[0], out int cmd))
        {
            return;
        }
        switch (cmd)
        {
            case 0:
            case 2:
                if (p.Count > 1)
                {
                    TitleChanged?.Invoke(p[1]);
                }
                break;
            case 52:
                // 形如 52;c;<base64>(c/p/s… 选区种类一律当系统剪贴板处理)。
                if (AllowOsc52ClipboardWrite
                    && p.Count > 2 && p[2] is { Length: > 0 } payload
                    && payload != "?" && payload.Length <= MaxOsc52Bytes / 3 * 4 + 4)
                {
                    try
                    {
                        string text = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        if (text.Length > 0)
                        {
                            ClipboardWriteRequested?.Invoke(text);
                        }
                    }
                    catch (FormatException)
                    {
                        // 非法 base64:按规范静默忽略。
                    }
                }
                break;
            case 7:
                // OSC 7:shell 上报当前工作目录(file://host/path)。用于「文件浏览器跟随终端目录」。
                // 由对端 shell 发出(SSH bash 会话会自动安装 PROMPT_COMMAND 钩子;
                // 其他 shell 也可通过各自的提示符钩子上报)。
                if (p.Count > 1 && ParseOsc7Path(p[1]) is { Length: > 0 } dir)
                {
                    WorkingDirectoryChanged?.Invoke(dir);
                }
                break;
            case 8:
                // OSC 8:显式超链接 —— `OSC 8 ; 参数 ; URI ST` 开启,`OSC 8 ; ; ST` 关闭。
                // 后续打印的每一格都盖上这条链接的句柄,直到被关闭或被下一条链接取代。
                // 发出方:ls --hyperlink、gcc/cargo 的诊断、gh、delta、systemd 等。
                SetHyperlink(p);
                break;
            case 133:
                // OSC 133:FinalTerm / FTCS 语义提示符 —— 把一屏输出切成结构化的命令块。
                // 由对端 shell 发出(fish 自带;bash/zsh 需装一段集成片段,见设置 → 终端 → 会话)。
                SetPromptMark(p);
                break;
                // 4(调色板)目前有意接受并忽略。
        }
    }

    /// <summary>
    /// 处理 <c>OSC 133 ; A|B|C|D[;退出码] </c>:在当前光标行上打语义标记。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>标记打在光标所在行</b>,因为四个标记都是 shell 在"此刻这一行"上宣告身份:
    /// <c>A</c> 发在提示符即将画出的位置、<c>C</c> 发在命令回显换行之后(输出的第一行)。
    /// </para>
    /// <para>
    /// <b><c>D</c> 的退出码记在该块的提示符行上,不是当前行。</b>命令结束时光标在输出末尾,
    /// 而侧栏那个标记画在提示符行 —— 记错地方,失败标红就会红在一条无关的空行上。
    /// 为此要一路记着"本块的提示符行"(<see cref="_promptRow" />)。
    /// </para>
    /// <para>
    /// <c>B</c> 被消费但不落行:它标的是列,而列在改列宽重排后会挪位,见
    /// <see cref="PromptMark" /> 的说明。
    /// </para>
    /// </remarks>
    private void SetPromptMark(IReadOnlyList<string> p)
    {
        if (p.Count < 2 || p[1].Length == 0)
        {
            return;
        }
        // 参数里可能跟着 aid=/cl= 这类键值(多路复用器用来区分会话),按规范一律忽略。
        switch (p[1][0])
        {
            case 'A':
                {
                    TerminalRow row = Screen.ActiveLine(Screen.CursorY);
                    row.Mark = PromptMark.Prompt;
                    row.ExitCode = null;
                    _promptRow = row;
                    HasPromptMarks = true;
                    break;
                }
            case 'C':
                Screen.ActiveLine(Screen.CursorY).Mark = PromptMark.Output;
                HasPromptMarks = true;
                break;
            case 'D':
                if (_promptRow is { } prompt)
                {
                    prompt.ExitCode = ParseExitCode(p);
                    _promptRow = null;
                }
                break;
                // B(提示符结束 / 输入开始)有意接受并忽略,理由见 PromptMark 的说明。
        }
    }

    /// <summary>
    /// 从 <c>OSC 133 ; D ; …</c> 的尾部参数里取退出码;没报或报的不是数字时返回 null。
    /// </summary>
    /// <remarks>
    /// 尾部可能是退出码(<c>D;1</c>)、也可能只有键值参数(<c>D;aid=7</c>),还有干脆什么都不带的
    /// (<c>D</c>)。只认纯数字段,其余一律当"没报退出码" —— 宁可不显示成败,也不要把
    /// <c>aid=7</c> 里的 7 当成退出码去标红一条其实成功了的命令。
    /// </remarks>
    private static int? ParseExitCode(IReadOnlyList<string> p)
    {
        for (int i = 2; i < p.Count; i++)
        {
            if (int.TryParse(p[i], out int code))
            {
                return code;
            }
        }
        return null;
    }

    /// <summary>
    /// 处理 <c>OSC 8 ; params ; URI</c>:驻留链接并把它设为当前画笔的链接,
    /// URI 为空(或不可接受)则关闭链接。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>URI 必须把 <c>p[2..]</c> 重新拼回来。</b>分号是 OSC 的字段分隔符,而 URI 的查询串里
    /// 完全可以带分号(<c>?a=1;b=2</c>)—— 解析器按 ';' 无条件切分,只取 <c>p[2]</c> 会把这类
    /// 地址拦腰截断,点开就是另一个页面。
    /// </para>
    /// <para>
    /// <c>params</c> 是 <c>key=value</c> 以 ':' 分隔的列表,规范目前只定义了 <c>id=</c>
    /// (把被换行拆开的同一条链接认回同一条)。未知键按规范忽略。
    /// </para>
    /// </remarks>
    private void SetHyperlink(IReadOnlyList<string> p)
    {
        if (p.Count < 3)
        {
            // `OSC 8 ; ST`(连 URI 字段都没有)按关闭处理,与 `OSC 8 ; ; ST` 一致。
            _link = 0;
            return;
        }
        string uri = p.Count == 3 ? p[2] : string.Join(';', p.Skip(2));
        if (uri.Length == 0)
        {
            _link = 0;
            return;
        }
        _link = Hyperlinks.Intern(ParseHyperlinkId(p[1]), uri);
    }

    /// <summary>从 OSC 8 的参数字段(<c>key=value</c> 以 ':' 分隔)里取出 <c>id</c>;没有则返回 null。</summary>
    private static string? ParseHyperlinkId(string parameters)
    {
        if (parameters.Length == 0)
        {
            return null;
        }
        foreach (Range segment in parameters.AsSpan().Split(':'))
        {
            ReadOnlySpan<char> pair = parameters.AsSpan()[segment];
            if (pair.StartsWith("id=", StringComparison.Ordinal))
            {
                return pair[3..].ToString();
            }
        }
        return null;
    }

    /// <summary>从 OSC 7 载荷 file://host/path 提取绝对路径(百分号编码按需解码);非法/非绝对路径返回 null。</summary>
    private static string? ParseOsc7Path(string payload)
    {
        const string scheme = "file://";
        if (!payload.StartsWith(scheme, StringComparison.Ordinal))
        {
            return null;
        }
        int slash = payload.IndexOf('/', scheme.Length); // 跳过 host,定位路径起点(file:///abs 时即第三个斜杠)
        if (slash < 0)
        {
            return null;
        }
        string path = payload[slash..];
        try
        {
            path = Uri.UnescapeDataString(path); // VTE 等会百分号编码;我们注入的脚本发原始路径,解码对纯 ASCII 无副作用
        }
        catch (Exception)
        {
            // 解码失败:用原始路径。
        }
        return path.StartsWith('/') ? path : null;
    }

    /// <summary>分发 DCS 序列;目前处理 DECRQSS 状态请求,其余静默消费。</summary>
    public void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data)
    {
        // DECRQSS(DCS $ q Pt ST):按 xterm 惯例应答 DCS 1 $ r <设定> ST(1=有效,0=无效)。
        // sixel 仍未实现,静默消费。
        if (final != 'q' || intermediates != "$")
        {
            return;
        }
        switch (data)
        {
            case "m": // SGR:回报当前画笔属性
                Send($"\eP1$r{BuildSgrReport()}m\e\\");
                break;
            case "r": // DECSTBM:回报当前滚动区域(1 基)
                Send($"\eP1$r{Screen.ScrollTop + 1};{Screen.ScrollBottom + 1}r\e\\");
                break;
            case " q":
                // DECSCUSR 光标形状。vim 启动时必问这一句(t_RS),而且它只认
                // "DCS 1 $ r <一位数字> SP q ST" 这一种形状 —— 我们过去回 "DCS 0 $ r ST"
                // (无效请求),vim 的 handle_dcs() 匹配失败后会把整段应答当成键入吞进去:
                // ESC P 0 $ r ESC \ 依次变成 Esc、P(粘贴 → E353 报错并响铃)、0、$(跳到行尾)、r…
                // 也就是 issue #112 里"打开 vim 自动输入奇怪字符 + 光标乱跳 + 响一声"的全部现象。
                Send($"\eP1$r{Modes.CursorStyle} q\e\\");
                break;
            default:
                Send("\eP0$r\e\\");
                break;
        }
    }

    /// <summary>终端需要发回宿主的字节(DA/DSR 等)。</summary>
    public event Action<byte[]>? Response;

    /// <summary>OSC 0/2 窗口标题变更。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>OSC 7 当前工作目录变更(绝对路径)。事件来自 feed 线程,消费者需自行编组到 UI 线程。</summary>
    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>收到 BEL(0x07)。</summary>
    public event Action? Bell;

    /// <summary>在一块输入被应用后触发,以便 UI 重绘。</summary>
    public event Action? Updated;

    /// <summary>
    /// 主机流里的转义序列改变了网格几何(目前只有获准的 DECCOLM 一条路径)。参数:(columns, rows)。
    /// 事件来自 feed 线程,消费者需自行编组到 UI 线程。
    /// <para>
    /// 宿主<b>必须</b>订阅:模拟器网格一旦不经布局就改动,渲染与命中测试立刻跟着缩,而控件缓存的
    /// 网格尺寸与远端 PTY 的 winsize 毫不知情 —— 这正是 issue #253 那类「悄悄分家」故障的形状。
    /// </para>
    /// </summary>
    public event Action<int, int>? HostGeometryChanged;

    /// <summary>切换所仿真的终端类型,并相应更新 VT52 解析。</summary>
    public void SetTerminalType(TerminalType type)
    {
        Type = type;
        _parser.Vt52Mode = type == TerminalType.Vt52;
    }

    /// <summary>改变字节解码所用字符集(默认为 UTF-8)。挂起的字节会被丢弃。</summary>
    public void SetEncoding(Encoding encoding) => _utf8.SetEncoding(encoding);

    // ---- 输入 --------------------------------------------------------------

    /// <summary>从宿主喂入原始字节。UTF-8 在解析前解码。</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        _feedTimestamp = DateTime.Now;
        // DecodeSpan 复用 sink 的内部缓冲,parser 只读遍历不留引用——全程零 string 物化。
        ReadOnlySpan<char> decoded = _utf8.DecodeSpan(bytes);
        if (decoded.Length > 0)
        {
            _parser.Parse(decoded);
        }
        Updated?.Invoke();
    }

    /// <summary>从宿主喂入原始字节。UTF-8 在解析前解码。</summary>
    public void Feed(byte[] bytes) => Feed(bytes.AsSpan());

    /// <summary>把主屏与备用屏(以及制表位)都调整为给定几何尺寸。</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        _mainScreen.Resize(columns, rows, Blank());
        _altScreen?.Resize(columns, rows, Blank());
        _tabStops = ResizeTabs(_tabStops, columns);
        _pendingWrap = false;
        // ⚠️ 必须丢掉在途的提示符行引用:改列宽会走 ReflowResize,它把旧行对象<b>回收复用</b>
        // (_reflowPool + ResetFor)。攥着一个已被复用的行,等 D 回来时就会把退出码盖到一条
        // 完全无关的行上 —— 屏幕上表现为某条历史输出凭空标红。标记本身已随内容重排搬过去了,
        // 这里丢掉的只是"正在跑的这一条命令的退出码",代价可接受。
        _promptRow = null;
    }

    // ---- 辅助方法 ------------------------------------------------------------

    private TerminalCell Blank() => TerminalCell.Blank(_bg, _flags);

    private static bool[] BuildDefaultTabs(int columns)
    {
        bool[] tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = i % 8 == 0 && i != 0;
        }
        return tabs;
    }

    private static bool[] ResizeTabs(bool[] old, int columns)
    {
        bool[] tabs = new bool[Math.Max(1, columns)];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = i < old.Length ? old[i] : i % 8 == 0 && i != 0;
        }
        return tabs;
    }

    private void AttachCombining(int rune)
    {
        int x = Screen.CursorX - 1;
        int y = Screen.CursorY;
        if (x < 0)
        {
            return;
        }
        ref TerminalCell cell = ref Screen.CellRef(Math.Clamp(x, 0, Screen.Columns - 1), y);
        if (cell.IsWideTrailing && x - 1 >= 0)
        {
            x -= 1;
            cell = ref Screen.CellRef(x, y);
        }
        cell.Combining = (cell.Combining ?? string.Empty) + char.ConvertFromUtf32(rune);
    }

    private void CarriageReturnLineFeed()
    {
        Screen.SetCursorX(0);
        IndexAndStamp();
    }

    /// <summary>
    /// Line feed 并给落到的行盖上本次 Feed 的时间戳。这样即使是输出里的空行(仅 \r\n,无可打印字符),
    /// 也被视为「该次输出产生的真实行」,侧栏据此显示其行号/时间;而光标从未到过的屏幕底部空行不会被盖章。
    /// </summary>
    private void IndexAndStamp()
    {
        Screen.Index(Blank());
        Screen.ActiveLine(Screen.CursorY).Timestamp = _feedTimestamp;
    }

    private void HorizontalTab()
    {
        int x = Screen.CursorX;
        for (int i = x + 1; i < Screen.Columns; i++)
        {
            if (!_tabStops[i])
            {
                continue;
            }
            Screen.SetCursorX(i);
            return;
        }
        Screen.SetCursorX(Screen.Columns - 1);
    }

    private void EscDispatchVt52(char final)
    {
        switch (final)
        {
            case 'A':
                Screen.SetCursor(Screen.CursorX, Screen.CursorY - 1);
                break;
            case 'B':
                Screen.SetCursor(Screen.CursorX, Screen.CursorY + 1);
                break;
            case 'C':
                Screen.SetCursor(Screen.CursorX + 1, Screen.CursorY);
                break;
            case 'D':
                Screen.SetCursor(Screen.CursorX - 1, Screen.CursorY);
                break;
            case 'H':
                Screen.SetCursor(0, 0);
                break;
            case 'I':
                Screen.ReverseIndex(Blank());
                break;
            case 'J':
                Screen.EraseInDisplay(0, Blank());
                break;
            case 'K':
                Screen.EraseInLine(0, Blank());
                break;
            case 'Z':
                Send("\e/Z");
                break; // VT52 identify
            case '<':
                SetTerminalType(TerminalType.Vt100);
                break; // exit VT52 mode
            case '=':
                Modes.ApplicationKeypad = true;
                break;
            case '>':
                Modes.ApplicationKeypad = false;
                break;
            case 'F':
                _decGraphics[0] = true;
                break; // 进入图形模式
            case 'G':
                _decGraphics[0] = false;
                break; // 退出图形模式
        }
    }

    private void HandlePrivateMode(IReadOnlyList<int> p, char final)
    {
        if (final == 'c')
        {
            // 某些宿主发送 "CSI ? ... c" 风格;若 final 为 c 则在别处当作 DA 处理。
            return;
        }
        bool set = final == 'h';
        if (final is not 'h' and not 'l')
        {
            return;
        }
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 1:
                    Modes.ApplicationCursorKeys = set;
                    break; // DECCKM
                case 2:
                    if (!set)
                    {
                        SetTerminalType(TerminalType.Vt52);
                    }
                    break; // DECANM (reset -> VT52)
                case 3:
                    ColumnMode(set);
                    break; // DECCOLM 132/80
                case 5:
                    Modes.ReverseVideo = set;
                    break; // DECSCNM
                case 6:
                    Modes.OriginMode = set;
                    HomeCursor();
                    break; // DECOM
                case 7:
                    Modes.AutoWrap = set;
                    break; // DECAWM
                case 9:
                    Modes.Mouse = set ? MouseTracking.X10 : MouseTracking.None;
                    break;
                case 12:
                    Modes.CursorBlink = set;
                    break;
                case 25:
                    Modes.CursorVisible = set;
                    break; // DECTCEM
                case 40:
                    Modes.AllowColumnMode = set;
                    break; // 允许 80↔132 切换(xterm c132):DECCOLM 的总闸
                case 1000:
                    Modes.Mouse = set ? MouseTracking.Normal : MouseTracking.None;
                    break;
                case 1002:
                    Modes.Mouse = set ? MouseTracking.ButtonEvent : MouseTracking.None;
                    break;
                case 1003:
                    Modes.Mouse = set ? MouseTracking.AnyEvent : MouseTracking.None;
                    break;
                case 1004:
                    break; // 焦点报告(已接受)
                case 1006:
                    Modes.MouseEncoding = set ? MouseEncoding.Sgr : MouseEncoding.Default;
                    break;
                case 1015:
                    Modes.MouseEncoding = set ? MouseEncoding.Urxvt : MouseEncoding.Default;
                    break;
                case 1007:
                    // 备用屏滚轮转方向键(xterm alternateScroll)。应用可以关掉它,
                    // 比如自己实现了滚动、不希望收到一串方向键的全屏程序。
                    Modes.AlternateScroll = set;
                    break;
                case 1047:
                    SwitchAlternate(set);
                    break;
                case 1048:
                    if (set)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }
                    break;
                case 1049:
                    SwitchAlternate(set, true);
                    break;
                case 2004:
                    Modes.BracketedPaste = set;
                    break;
            }
        }
    }

    private void SetAnsiMode(IReadOnlyList<int> p, bool set)
    {
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 4:
                    Modes.InsertMode = set;
                    break; // IRM
                case 12:
                    // SRM。语义是反的:置位(12h)= 本地回显关,复位(12l)= 本地回显开。
                    Modes.SendReceive = set;
                    break; // SRM
                case 20:
                    Modes.NewLineMode = set;
                    break; // LNM
            }
        }
    }

    // ---- 光标操作 --------------------------------------------------

    private void MoveCursor(int dx, int dy)
    {
        _pendingWrap = false;
        int y = Screen.CursorY + dy;
        if (Modes.OriginMode)
        {
            y = Math.Clamp(y, Screen.ScrollTop, Screen.ScrollBottom);
        }
        Screen.SetCursor(Screen.CursorX + dx, y);
    }

    private void SetCursorColumn(int col)
    {
        _pendingWrap = false;
        Screen.SetCursorX(col);
    }

    private void SetCursorRow(int row)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += Screen.ScrollTop;
        }
        Screen.SetCursorY(Modes.OriginMode ? Math.Clamp(row, Screen.ScrollTop, Screen.ScrollBottom) : row);
    }

    private void CursorPosition(int row, int col)
    {
        _pendingWrap = false;
        if (Modes.OriginMode)
        {
            row += Screen.ScrollTop;
            row = Math.Clamp(row, Screen.ScrollTop, Screen.ScrollBottom);
        }
        Screen.SetCursor(col, row);
    }

    private void HomeCursor()
    {
        if (Modes.OriginMode)
        {
            Screen.SetCursor(0, Screen.ScrollTop);
        }
        else
        {
            Screen.SetCursor(0, 0);
        }
    }

    private void TabForward(int count)
    {
        for (int i = 0; i < count; i++)
        {
            HorizontalTab();
        }
    }

    private void TabBackward(int count)
    {
        for (int c = 0; c < count; c++)
        {
            int x = Screen.CursorX;
            int target = 0;
            for (int i = x - 1; i > 0; i--)
            {
                if (!_tabStops[i])
                {
                    continue;
                }
                target = i;
                break;
            }
            Screen.SetCursorX(target);
        }
    }

    private void ClearTabs(int mode)
    {
        switch (mode)
        {
            case 0:
                _tabStops[Math.Clamp(Screen.CursorX, 0, _tabStops.Length - 1)] = false;
                break;
            case 3:
                Array.Clear(_tabStops, 0, _tabStops.Length);
                break;
        }
    }

    private void SetScrollRegion(IReadOnlyList<int> p)
    {
        int top = (p.Count > 0 && p[0] > 0 ? p[0] : 1) - 1;
        int bottom = (p.Count > 1 && p[1] > 0 ? p[1] : Screen.Rows) - 1;
        Screen.SetMargins(top, bottom);
        HomeCursor();
    }

    // ---- SGR ---------------------------------------------------------------

    private void ApplySgr(IReadOnlyList<int> p)
    {
        if (p.Count == 0)
        {
            ResetPen();
            return;
        }
        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    ResetPen();
                    break;
                case 1:
                    _flags |= CellFlags.Bold;
                    break;
                case 2:
                    _flags |= CellFlags.Dim;
                    break;
                case 3:
                    _flags |= CellFlags.Italic;
                    break;
                case 4:
                    _flags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    _flags |= CellFlags.Blink;
                    break;
                case 7:
                    _flags |= CellFlags.Inverse;
                    break;
                case 8:
                    _flags |= CellFlags.Invisible;
                    break;
                case 9:
                    _flags |= CellFlags.Strikethrough;
                    break;
                case 21:
                    _flags |= CellFlags.DoubleUnderline;
                    break;
                case 22:
                    _flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    break;
                case 25:
                    _flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _flags &= ~CellFlags.Invisible;
                    break;
                case 29:
                    _flags &= ~CellFlags.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    _fg = TerminalColor.FromIndex(code - 30);
                    break;
                case 38:
                    i = ParseExtendedColor(p, i, ref _fg);
                    break;
                case 39:
                    _fg = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    _bg = TerminalColor.FromIndex(code - 40);
                    break;
                case 48:
                    i = ParseExtendedColor(p, i, ref _bg);
                    break;
                case 49:
                    _bg = TerminalColor.Default;
                    break;
                case >= 90 and <= 97:
                    _fg = TerminalColor.FromIndex(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _bg = TerminalColor.FromIndex(code - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>解析 <c>38;5;n</c> / <c>48;5;n</c>(256 色)与 <c>38;2;r;g;b</c>(真彩色)。</summary>
    private int ParseExtendedColor(IReadOnlyList<int> p, int i, ref TerminalColor target)
    {
        if (i + 1 >= p.Count)
        {
            return i;
        }
        int kind = p[i + 1];
        switch (kind)
        {
            case 5 when i + 2 < p.Count:
                {
                    if (Type.SupportsColor())
                    {
                        target = TerminalColor.FromIndex(p[i + 2]);
                    }
                    return i + 2;
                }
            case 2 when i + 4 < p.Count:
                {
                    if (Type.SupportsColor())
                    {
                        target = TerminalColor.FromRgb((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]);
                    }
                    return i + 4;
                }
            default:
                return i + 1;
        }
    }

    private void ResetPen()
    {
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
        _flags = CellFlags.None;
    }

    // ---- 模式 / 复位 ------------------------------------------------------

    private void ColumnMode(bool set)
    {
        // DECCOLM(?3):切换 132/80 列并清屏。整条动作都受 DECSET ?40 把关(xterm 的 c132 资源),
        // 默认关闭 = 完全空操作 —— 不改列数、不清屏、不动光标。
        //
        // 这不是偷懒:xterm 系 terminfo 的 is2 初始化串里就带 "ESC[?3l",screen / tmux / tput init /
        // reset 每次启动都会照发一遍。无条件执行会把网格压成 80 列、顺手清掉整屏,而控件布局与
        // 远端 PTY 都还以为是原来的宽度,于是「选区/命中测试只剩 80 列宽」,要切一次标签触发
        // 重新布局才恢复(issue #253)。xterm、Windows Terminal、VTE 一律默认忽略 DECCOLM。
        if (!Modes.AllowColumnMode)
        {
            return;
        }
        int cols = set ? 132 : 80;
        Screen.EraseInDisplay(2, Blank());
        Screen.SetCursor(0, 0);
        Resize(cols, Screen.Rows);
        // 主机流刚刚改掉了网格几何:必须让宿主知道,否则控件的布局认知与远端 PTY 的 winsize
        // 会和模拟器分家(见 VelaTerminalControl.OnHostGeometryChanged)。
        HostGeometryChanged?.Invoke(Screen.Columns, Screen.Rows);
    }

    private void SwitchAlternate(bool enable, bool saveCursor = false)
    {
        if (enable == IsAlternateScreen)
        {
            return;
        }
        // 诊断:记录每次备用屏切换(DECSET 1047/1049)。ZMODEM 传输期间本不该发生此切换,
        // 若日志显示在 sz/rz 取消前后出现 enter=true,即坐实"杂散协议字节污染终端 → 整屏消失"。
        Core.FileTransfer.Diagnostics.TransferTrace.Log($"ALT-SCREEN switch enable={enable} (was {IsAlternateScreen})");
        // 备用屏整块换掉,在途的提示符行不再属于当前缓冲区(退出备用屏时它还会被整个丢弃)。
        // 攥着它等于给一条已经不在场的行记退出码。
        _promptRow = null;
        if (enable)
        {
            // 切换前把主屏光标存入专用的备用屏槽。
            if (saveCursor)
            {
                _altSaved = CaptureCursor();
            }
            _altScreen = new(_mainScreen.Columns, _mainScreen.Rows, 0);
            _altScreen.ResetToBlank(Blank());
            IsAlternateScreen = true;
            Screen = _altScreen;
            Screen.SetCursor(0, 0);
        }
        else
        {
            _altScreen = null;
            IsAlternateScreen = false;
            Screen = _mainScreen;
            // 从专用备用屏槽恢复主屏光标——绝不从 _saved 恢复,因为
            // 备用屏应用可能已经通过 DECSC 覆盖过它。
            if (saveCursor)
            {
                ApplyCursor(_altSaved);
            }
        }
        _pendingWrap = false;
    }

    private SavedCursor CaptureCursor() =>
        new()
        {
            X = Screen.CursorX,
            Y = Screen.CursorY,
            Fg = _fg,
            Bg = _bg,
            Flags = _flags,
            Gl = _gl,
            DecGraphics = (bool[])_decGraphics.Clone(),
            OriginMode = Modes.OriginMode
        };

    private void ApplyCursor(SavedCursor? saved)
    {
        if (saved is not { } s)
        {
            Screen.SetCursor(0, 0);
            return;
        }
        Screen.SetCursor(s.X, s.Y);
        _fg = s.Fg;
        _bg = s.Bg;
        _flags = s.Flags;
        _gl = s.Gl;
        Array.Copy(s.DecGraphics, _decGraphics, _decGraphics.Length);
        Modes.OriginMode = s.OriginMode;
        _pendingWrap = false;
    }

    private void SaveCursor() => _saved = CaptureCursor();

    private void RestoreCursor() => ApplyCursor(_saved);

    private void FillScreenWithE()
    {
        var cell = new TerminalCell { Rune = 'E', Foreground = _fg, Background = _bg, Flags = _flags };
        for (int y = 0; y < Screen.Rows; y++)
        {
            for (int x = 0; x < Screen.Columns; x++)
            {
                Screen.SetCell(x, y, cell);
            }
        }
    }

    private void SoftReset()
    {
        Modes.Reset();
        Screen.ResetMargins();
        ResetPen();
        _gl = 0;
        Array.Clear(_decGraphics, 0, _decGraphics.Length);
        _saved = null;
        _altSaved = null;
        _pendingWrap = false;
    }

    private void FullReset()
    {
        SoftReset();
        _tabStops = BuildDefaultTabs(Screen.Columns);
        if (IsAlternateScreen)
        {
            SwitchAlternate(false);
        }
        Screen.ResetToBlank(Blank());
        Screen.ClearScrollback();
        // RIS 之后整个缓冲区(含回滚)已被清空,没有任何格还引用旧句柄 ——
        // 这是唯一能安全整表回收 OSC 8 链接的时机。
        _link = 0;
        Hyperlinks.Clear();
        _promptRow = null;
        HasPromptMarks = false;
        _utf8.Reset();
        _parser.Reset();
    }

    // ---- 报告 ------------------------------------------------------------

    private void DeviceAttributes(char prefix)
    {
        switch (prefix)
        {
            case '>':
                Send(Type.SecondaryDeviceAttributes());
                break;
            case '\0':
                Send(Type.PrimaryDeviceAttributes());
                break;
        }
    }

    private void DeviceStatusReport(int p)
    {
        switch (p)
        {
            case 5:
                Send("\e[0n");
                break; // OK
            case 6:
                int row = Screen.CursorY + 1;
                int col = Screen.CursorX + 1;
                if (Modes.OriginMode)
                {
                    row = Screen.CursorY - Screen.ScrollTop + 1;
                }
                Send($"\e[{row};{col}R");
                break;
        }
    }

    private void Send(string ascii) => Response?.Invoke(Encoding.ASCII.GetBytes(ascii));

    // ---- IVtActions:OSC / DCS ---------------------------------------------

    /// <summary>
    /// OSC 52:远端程序(tmux/vim 的 yank)请求写系统剪贴板。只支持写方向;
    /// 查询("?")一律不应答,防止远端读取本地剪贴板内容(安全)。宿主控件订阅后落剪贴板。
    /// </summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>把当前画笔状态编码为 SGR 参数串(DECRQSS "m" 应答用),始终以 0 开头。</summary>
    private string BuildSgrReport()
    {
        var sb = new StringBuilder("0");
        if ((_flags & CellFlags.Bold) != 0)
        {
            sb.Append(";1");
        }
        if ((_flags & CellFlags.Dim) != 0)
        {
            sb.Append(";2");
        }
        if ((_flags & CellFlags.Italic) != 0)
        {
            sb.Append(";3");
        }
        if ((_flags & CellFlags.Underline) != 0)
        {
            sb.Append(";4");
        }
        if ((_flags & CellFlags.Blink) != 0)
        {
            sb.Append(";5");
        }
        if ((_flags & CellFlags.Inverse) != 0)
        {
            sb.Append(";7");
        }
        if ((_flags & CellFlags.Invisible) != 0)
        {
            sb.Append(";8");
        }
        if ((_flags & CellFlags.Strikethrough) != 0)
        {
            sb.Append(";9");
        }
        AppendSgrColor(sb, _fg, true);
        AppendSgrColor(sb, _bg, false);
        return sb.ToString();
    }

    private static void AppendSgrColor(StringBuilder sb, TerminalColor color, bool isForeground)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (color.Kind)
        {
            case TerminalColorKind.Indexed when color.Index < 8:
                sb.Append(';').Append((isForeground ? 30 : 40) + color.Index);
                break;
            case TerminalColorKind.Indexed when color.Index < 16:
                sb.Append(';').Append((isForeground ? 90 : 100) + color.Index - 8);
                break;
            case TerminalColorKind.Indexed:
                sb.Append(isForeground ? ";38;5;" : ";48;5;").Append(color.Index);
                break;
            case TerminalColorKind.Rgb:
                sb.Append(isForeground ? ";38;2;" : ";48;2;")
                  .Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B);
                break;
                // Default:SGR 0 已覆盖,无需追加。
        }
    }

    private struct SavedCursor
    {
        public int X, Y;
        public TerminalColor Fg, Bg;
        public CellFlags Flags;
        public int Gl;
        public bool[] DecGraphics;
        public bool OriginMode;
    }
}
