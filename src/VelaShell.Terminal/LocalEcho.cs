namespace VelaShell.Terminal;

/// <summary>
/// 本地回显策略:把一段"即将发往主机的键入字节"翻译成"应当喂回终端显示的字节"。
/// <para>
/// 用于对端不回显的链路(Telnet 半双工、串口设备),或主机以 SRM 复位(<c>CSI 12 l</c>)
/// 显式要求终端自行回显的场合。SSH 场景默认关闭 —— 远端 shell 自己回显,再本地回显会出现双字符。
/// </para>
/// <para>
/// **不能直接把编码后的字节回显。** 方向键编码出来是 <c>ESC [ A</c>,喂回终端会被**执行**
/// (光标真的移动),而不是显示出来。所以这里按内容筛选,只回显"打出来就该看见"的部分。
/// </para>
/// </summary>
/// <remarks>
/// 刻意做成纯函数、不依赖 Avalonia 也不依赖任何传输:可直接单测,且 SSH / ConPTY /
/// 未来的串口·Telnet 通用。
/// </remarks>
public static class LocalEcho
{
    private const byte Escape = 0x1B;
    private const byte Backspace = 0x08;
    private const byte Delete = 0x7F;
    private const byte CarriageReturn = 0x0D;
    private const byte LineFeed = 0x0A;
    private const byte Space = 0x20;

    /// <summary>退格的可见擦除:退一格、盖掉、再退回来。</summary>
    private static readonly byte[] EraseLast = [Backspace, Space, Backspace];

    /// <summary>
    /// 计算 <paramref name="input" /> 应当本地回显的字节。无可回显内容时返回空数组。
    /// </summary>
    /// <param name="input">即将发往主机的键入字节(已按终端编码)。</param>
    /// <param name="newLineMode">
    /// LNM(ANSI 模式 20)是否置位。置位时回车要回显 CR+LF,否则只回显 CR ——
    /// 与终端处理主机输出时的换行语义保持一致,免得本地回显的行为和远端回显的不一样。
    /// </param>
    public static byte[] Compute(ReadOnlySpan<byte> input, bool newLineMode)
    {
        if (input.IsEmpty || input.IndexOf(Escape) >= 0)
        {
            // 含 ESC 的载荷整段不回显:方向键/功能键/括号粘贴包裹等,回显出去只会乱屏。
            return [];
        }

        var output = new List<byte>(input.Length);
        foreach (byte b in input)
        {
            switch (b)
            {
                case CarriageReturn:
                    output.Add(CarriageReturn);
                    if (newLineMode)
                    {
                        output.Add(LineFeed);
                    }
                    break;
                case LineFeed:
                    output.Add(LineFeed);
                    break;
                case Backspace:
                case Delete:
                    output.AddRange(EraseLast);
                    break;
                case Space:
                    output.Add(Space);
                    break;
                default:
                    // 其余控制字符(Ctrl+C=0x03、Tab=0x09 等)不回显:它们的屏幕效果该由
                    // 主机决定(^C 提示、制表位展开),本地猜会和远端不一致。
                    // ≥0x21 的一律回显 —— 含 UTF-8 多字节序列的所有后续字节(均 ≥0x80)。
                    if (b > Space)
                    {
                        output.Add(b);
                    }
                    break;
            }
        }
        return [.. output];
    }

    /// <summary>
    /// 本地回显此刻是否生效。
    /// </summary>
    /// <param name="userEnabled">用户设置(设置 → 终端 → 本地回显)。</param>
    /// <param name="sendReceiveMode">
    /// SRM 当前值(<see cref="Emulation.TerminalModes.SendReceive" />)。语义是反的:
    /// true(12h,默认)= 主机负责回显;false(12l)= 主机要求终端本地回显。
    /// </param>
    /// <param name="peerEchoes">
    /// 对端是否自己回显键入(SSH 的远端 PTY、本地 ConPTY 的 shell 都会)。
    /// 为 true 时**无视用户设置** —— 这类链路上开本地回显必然出现双字符,属于配置错误而非用户意图。
    /// 判据刻意是"对端行为"而不是"连接类型":Terminal 层不认识 SSH/Telnet/串口,由宿主告知即可。
    /// </param>
    /// <remarks>
    /// 主机显式复位 SRM(<c>CSI 12 l</c>)时仍然回显,即便 <paramref name="peerEchoes" /> 为 true——
    /// 那是远端程序主动要求终端接管回显(它自己会相应停止回显),照做才是正确行为,
    /// 无视它会导致用户打字完全看不见。
    /// </remarks>
    public static bool IsEnabled(bool userEnabled, bool sendReceiveMode, bool peerEchoes = false) =>
        (userEnabled && !peerEchoes) || !sendReceiveMode;
}
