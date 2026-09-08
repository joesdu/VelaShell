namespace VelaShell.Terminal.Emulation;

/// <summary>
/// OSC 133(FinalTerm / FTCS 语义提示符)在一行上留下的标记。
/// </summary>
/// <remarks>
/// <para>
/// 协议本身不画任何东西,只是让 shell 在字节流里打四个位置标记,把一屏输出切成结构化的
/// <b>命令块</b>:<c>[提示符][用户敲的命令][命令的输出][退出码]</c>。
/// </para>
/// <list type="bullet">
/// <item><c>OSC 133 ; A ST</c> —— 提示符开始 → <see cref="Prompt" /></item>
/// <item><c>OSC 133 ; B ST</c> —— 提示符结束、用户输入开始(本层消费但不落行,见下)</item>
/// <item><c>OSC 133 ; C ST</c> —— 用户回车了,命令输出从这里开始 → <see cref="Output" /></item>
/// <item><c>OSC 133 ; D ; 退出码 ST</c> —— 命令结束,退出码记在该块的提示符行上</item>
/// </list>
/// <para>
/// <b>B 刻意不落行。</b>它标的是一个<b>列</b>位置(命令文本从提示符的第几列开始),而列在改列宽
/// 重排之后会整体挪位 —— 要正确搬运就得像 <c>ReflowResize</c> 搬运光标那样,把它换算成
/// 逻辑行内的偏移再跟着重新换行走一遍。本轮的功能(跳转 / 选中输出 / 折叠)全部只按<b>行</b>
/// 定界,用不到这个列;等真要做「复制这条命令」「重跑」时再补,那时它才开始付出代价。
/// </para>
/// </remarks>
public enum PromptMark : byte
{
    /// <summary>普通行,不是任何命令块的边界。</summary>
    None = 0,

    /// <summary>提示符所在行(<c>OSC 133 ; A</c>)。命令块由它起头,退出码也记在这一行上。</summary>
    Prompt = 1,

    /// <summary>命令输出的首行(<c>OSC 133 ; C</c>)。「选中这条命令的输出」由它定上界。</summary>
    Output = 2
}
