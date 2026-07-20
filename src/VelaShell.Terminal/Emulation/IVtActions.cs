namespace VelaShell.Terminal.Emulation;

/// <summary>
/// <see cref="VtParser" /> 所产生的事件的接收端(sink)。<see cref="TerminalEmulator" />
/// 实现此接口,把解析出的转义序列转化为对屏幕缓冲区的修改。
/// </summary>
public interface IVtActions
{
    /// <summary>应在光标处写入一个可打印的 Unicode 标量值。</summary>
    void Print(int rune);

    /// <summary>应执行一个 C0 控制字符(0x00-0x1F)或 DEL。</summary>
    void Execute(char control);

    /// <summary>一个 <c>ESC</c> 序列:<c>ESC {中间字节} {终结字节}</c>(如 ESC ( B)。</summary>
    void EscDispatch(string intermediates, char final);

    /// <summary>
    /// 一个 CSI 序列:<c>CSI {前缀} {参数} {中间字节} {终结字节}</c>。
    /// <paramref name="prefix" /> 是私有标记(如 <c>?</c>、<c>&gt;</c>)或 '\0'。
    /// </summary>
    void CsiDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final);

    /// <summary>一个 OSC 序列。<paramref name="parameters" /> 为按 ';' 拆分后的载荷。</summary>
    void OscDispatch(IReadOnlyList<string> parameters);

    /// <summary>一个带有已收集字符串载荷的 DCS 序列。</summary>
    void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data);
}
