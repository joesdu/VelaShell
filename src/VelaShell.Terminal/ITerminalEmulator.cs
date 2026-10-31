using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.Core.OutputRules;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal;

/// <summary>
/// 终端仿真器接口,可在多种实现之间切换
/// (AvaloniaTerminal、xterm.js 等)。
/// </summary>
public interface ITerminalEmulator : IDisposable
{
    /// <summary>
    /// 当前光标行位置
    /// </summary>
    int CursorRow { get; }

    /// <summary>
    /// 当前光标列位置
    /// </summary>
    int CursorCol { get; }

    /// <summary>
    /// 要保留的回滚行数(主屏;备用屏恒无回滚)
    /// </summary>
    int ScrollbackLines { get; set; }

    /// <summary>
    /// 要嵌入 UI 的 Avalonia 控件
    /// </summary>
    Control Control { get; }

    /// <summary>
    /// 当前列数
    /// </summary>
    int Columns { get; }

    /// <summary>
    /// 当前行数
    /// </summary>
    int Rows { get; }

    /// <summary>
    /// 缓冲区中的总行数(回滚历史行 + 当前可视行)。
    /// </summary>
    int TotalLines { get; }

    /// <summary>
    /// 当前视口顶部在总行序列中的行索引(用于滚动定位)。
    /// </summary>
    int ViewportRow { get; }

    /// <summary>
    /// 把来自 SSH 流的原始字节喂入终端
    /// </summary>
    void Feed(byte[] data);

    /// <summary>
    /// 改变终端尺寸
    /// </summary>
    void Resize(int cols, int rows);

    /// <summary>
    /// 用户键入输入时触发(用于发送到 SSH)
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
    /// 终端的字符单元格网格尺寸变化时触发(例如控件以新尺寸完成布局),
    /// 以便宿主 PTY 调整大小与之匹配。网格没变、只是像素变了(改字号、换到缩放不同的显示器)也会触发。
    /// </summary>
    event Action<PtySize>? PtySizeChanged;

    /// <summary>
    /// 当前网格对应的 PTY 尺寸(行列 + 物理像素),供新挂上的传输补推一次 ——
    /// 挂流之前触发的 <see cref="PtySizeChanged" /> 没有人接。
    /// </summary>
    PtySize CurrentPtySize { get; }

    /// <summary>
    /// shell 上报当前工作目录时触发(绝对路径)。用于「文件浏览器跟随终端目录」。来自 feed 线程。
    /// <para>
    /// <b>发的只有 OSC 7,认的是一整族。</b>SSH 会话会按对端 shell 的种类自动注入一段
    /// OSC 7 上报钩子(bash / zsh / fish / POSIX sh 各一段,见
    /// <c>VelaShell.Core.Ssh.ShellIntegrationScript</c>);而解析端同时认 OSC 7、
    /// OSC 633(VS Code)、OSC 1337(iTerm2)与 OSC 9;9(ConEmu / Windows Terminal)——
    /// 早就为别家配过 rc 的用户,什么都不用改就能用。
    /// </para>
    /// </summary>
    event Action<string>? WorkingDirectoryChanged;

    /// <summary>
    /// 一条自动应答规则(设置 → 输出规则)命中了远端一条等着输入的提示(UI 线程)。
    /// 发送与提示由宿主做 —— 同步输入的转发、「程序注入」的开关都在标签的视图模型上。
    /// </summary>
    event Action<OutputTriggerHit>? OutputTriggerFired;

    /// <summary>
    /// 当前会话字符集:解码对端输出与编码用户输入共用同一套。
    /// </summary>
    /// <remarks>
    /// 宿主侧凡是要自己拼字节送进 <see cref="WriteInput" /> 的地方(快捷命令、接受补全、
    /// 同步输入转发)都必须按它编码,否则那几条路会绕过终端的编码路径,变回写死的 UTF-8。
    /// </remarks>
    Encoding SessionEncoding { get; }

    /// <summary>
    /// 以编程方式发送输入字节,如同用户键入一样
    /// </summary>
    void WriteInput(byte[] data);

    /// <summary>按终端自身编码和状态发送已提交文本。</summary>
    void WriteTextInput(string text);

    /// <summary>按终端自身模式编码并发送一个非文本按键。</summary>
    bool WriteKeyInput(Key key, KeyModifiers modifiers);

    /// <summary>按终端的 bracketed-paste 状态发送粘贴文本。</summary>
    void WritePasteInput(string text);

    /// <summary>
    /// 发送一段<b>不是用户此刻动手</b>的输入(输出规则的自动应答):正文字节之后按需再按一次 Enter(按终端当前模式编)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WriteInput" /> / <see cref="WriteKeyInput" /> 走同一个出口 —— 键入事件、命令补全的行跟踪、
    /// 同步输入转发、桥的输入代次都照常看见它(开着的共享密码列表照样因此作废);只是不做键入之后的视图收尾:
    /// 不把回滚中的视图拉回底部、不清选区。应答在提示出现 0.3 秒或冷却之后才发,那时用户可能正往回翻、选着日志。
    /// </remarks>
    /// <param name="data">正文字节(已按会话字符集编码;可以为空 = 只按回车)。</param>
    /// <param name="submit">正文之后按一次 Enter。</param>
    void WriteBackgroundInput(byte[] data, bool submit);

    /// <summary>
    /// 远端是否开着括号粘贴(DECSET 2004)。开着时 <see cref="WritePasteInput" /> 送去的多行文本
    /// 由 shell 整段放进命令行,不会逐行执行;关着时文本里的每个换行都等于按一次回车。
    /// </summary>
    bool IsBracketedPasteEnabled { get; }

    /// <summary>
    /// 获取终端缓冲区中指定行的文本内容
    /// </summary>
    string GetBufferLine(int row);
}
