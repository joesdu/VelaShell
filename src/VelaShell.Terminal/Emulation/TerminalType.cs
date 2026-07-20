namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 向远端主机宣示的终端仿真档位。决定 TERM 字符串、设备属性(DA)应答,
/// 以及仿真器启用哪些功能集。xterm-256color 是主用/默认档位。
/// </summary>
public enum TerminalType
{
    /// <summary>DEC VT52,早期无 ANSI 转义序列的终端。</summary>
    Vt52,
    /// <summary>DEC VT100,ANSI/VT 转义序列的基线终端。</summary>
    Vt100,
    /// <summary>DEC VT102,VT100 的增强型,含插入/删除行列等能力。</summary>
    Vt102,
    /// <summary>DEC VT220,支持 8 位控制、可下载字符集等扩展。</summary>
    Vt220,
    /// <summary>DEC VT320,VT300 系列,支持彩色与更多扩展。</summary>
    Vt320, // "vt340" 家族 — DEC VT300 系列
    /// <summary>DEC VT340,VT300 系列中带 Sixel 图形/彩色的型号。</summary>
    Vt340,
    /// <summary>DEC VT420,支持多会话与更多显示扩展。</summary>
    Vt420,
    /// <summary>DEC VT520,DEC 终端系列的高端型号。</summary>
    Vt520,
    /// <summary>xterm 终端,VT100 类兼容并带大量扩展。</summary>
    Xterm,
    /// <summary>xterm-256color,支持 256 色/真彩色的主用默认配置。</summary>
    XtermColor256
}

/// <summary><see cref="TerminalType" /> 的扩展方法,提供能力判定与 TERM 名称、Device Attributes 应答的映射。</summary>
public static class TerminalTypeExtensions
{
    /// <summary>把 TERM 字符串(如 "xterm-256color")解析回 <see cref="TerminalType" />。</summary>
    public static TerminalType FromTermName(string? term) =>
        (term ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "vt52" => TerminalType.Vt52,
            "vt100" => TerminalType.Vt100,
            "vt102" => TerminalType.Vt102,
            "vt220" => TerminalType.Vt220,
            "vt320" => TerminalType.Vt320,
            "vt340" => TerminalType.Vt340,
            "vt420" => TerminalType.Vt420,
            "vt520" => TerminalType.Vt520,
            "xterm" => TerminalType.Xterm,
            _ => TerminalType.XtermColor256
        };

    extension(TerminalType type)
    {
        /// <summary>对于支持 ANSI 颜色与 8 位字符集的 VT300+ / xterm 档位返回 true。</summary>
        public bool SupportsColor() =>
            type is TerminalType.Xterm or TerminalType.XtermColor256
                or TerminalType.Vt320 or TerminalType.Vt340 or TerminalType.Vt420 or TerminalType.Vt520;

        /// <summary>
        /// 主设备属性(CSI c)应答。第一个参数标识终端类型;后续参数宣示所支持的扩展。
        /// </summary>
        public string PrimaryDeviceAttributes() =>
            type switch
            {
                // VT52 没有 DA;它改为以 ESC / Z 应答 ESC Z(在仿真器内处理)。
                TerminalType.Vt52 => "\e/Z",
                // VT100 带 AVO:"我是带高级视频选项的 VT100"。
                TerminalType.Vt100 => "\e[?1;2c",
                TerminalType.Vt102 => "\e[?6c",
                // VT220:服务类别 62,带 132 列、打印机、选择性擦除、DRCS、UDK、NRCS。
                TerminalType.Vt220 => "\e[?62;1;2;6;7;8;9c",
                TerminalType.Vt320 => "\e[?63;1;2;6;7;8;9c",
                TerminalType.Vt340 => "\e[?63;1;2;4;6;7;8;9;15c",
                TerminalType.Vt420 => "\e[?64;1;2;6;7;8;9;15;18;19;21c",
                TerminalType.Vt520 => "\e[?65;1;2;6;7;8;9;12;15;18;19;21c",
                // xterm 把自己宣示为带大量扩展的 VT100 类终端。
                TerminalType.Xterm => "\e[?1;2c",
                _ => "\e[?64;1;2;6;9;15;18;21;22c"
            };

        /// <summary>次设备属性(CSI > c)应答,用于标识固件/版本。</summary>
        public string SecondaryDeviceAttributes() =>
            type switch
            {
                TerminalType.Vt220 => "\e[>1;10;0c",
                TerminalType.Vt320 => "\e[>24;20;0c",
                TerminalType.Vt340 => "\e[>19;20;0c",
                TerminalType.Vt420 => "\e[>41;20;0c",
                TerminalType.Vt520 => "\e[>65;20;0c",
                // xterm 报告终端类型 0/41 与补丁级别;我们宣示为兼容 VT420 的 xterm。
                TerminalType.Xterm or TerminalType.XtermColor256 => "\e[>41;360;0c",
                _ => "\e[>0;10;0c"
            };

        /// <summary>作为 TERM 环境变量 / PTY 终端类型发送的值。</summary>
        public string ToTermName() =>
            type switch
            {
                TerminalType.Vt52 => "vt52",
                TerminalType.Vt100 => "vt100",
                TerminalType.Vt102 => "vt102",
                TerminalType.Vt220 => "vt220",
                TerminalType.Vt320 => "vt320",
                TerminalType.Vt340 => "vt340",
                TerminalType.Vt420 => "vt420",
                TerminalType.Vt520 => "vt520",
                TerminalType.Xterm => "xterm",
                _ => "xterm-256color"
            };
    }
}
