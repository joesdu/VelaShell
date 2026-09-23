namespace VelaShell.Core.Ssh;

/// <summary>
/// 交互式 SSH shell 流之上的库中立抽象,将调用方与底层 SSH 实现解耦。
/// </summary>
public interface IShellStreamWrapper : IAsyncDisposable
{
    /// <summary>获取一个值,指示流上当前是否缓冲有未读数据。</summary>
    bool DataAvailable { get; }

    /// <summary>获取一个值,指示流当前是否支持读取。</summary>
    bool CanRead { get; }

    /// <summary>获取一个值,指示流当前是否支持写入。</summary>
    bool CanWrite { get; }

    /// <summary>读端走到头的原因;<see cref="ReadAsync" /> 返回 0 之后才有意义。</summary>
    /// <remarks>
    /// 各实现都把「远端 shell 退出」与「连接中断」归一成了返回 0 —— 掉线不是崩溃,
    /// 抛出去只会让读循环带着异常收尾。但归一之后这两件事在上层就分不开了,
    /// 而自动重连恰恰只该管后者(#383):在远端敲 <c>exit</c> 之后又被自动连回来,
    /// 用户根本退不掉。于是结论仍然是 EOF,原因单独留一份在这里。
    /// 分不清的实现返回 <see cref="ShellCloseReason.Unknown" />,按连接中断处理,
    /// 即维持原有的自动重连行为。
    /// </remarks>
    ShellCloseReason CloseReason { get; }

    /// <summary>等待与给定正则表达式匹配的输出,直到指定的超时时间。</summary>
    /// <param name="regex">用于匹配到来输出的正则表达式。</param>
    /// <param name="timeout">等待匹配的最长时间。</param>
    /// <returns>匹配到的文本;若超时仍未匹配则返回 <c>null</c>。</returns>
    string? Expect(string regex, TimeSpan timeout);

    /// <summary>向 shell 写入一行文本,并附加行结束符。</summary>
    /// <param name="line">要发送给远端 shell 的文本。</param>
    void WriteLine(string line);

    /// <summary>异步从 shell 读取输出字节到缓冲区。</summary>
    /// <param name="buffer">接收所读取字节的缓冲区。</param>
    /// <param name="offset">在 <paramref name="buffer" /> 中开始存储数据的从零开始的字节偏移量。</param>
    /// <param name="count">要读取的最大字节数。</param>
    /// <param name="cancellationToken">用于取消读取操作的令牌。</param>
    /// <returns>实际读取的字节数。</returns>
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    /// <summary>异步向 shell 写入字节。</summary>
    /// <param name="buffer">包含待写入字节的缓冲区。</param>
    /// <param name="offset">在 <paramref name="buffer" /> 中开始写入数据的从零开始的字节偏移量。</param>
    /// <param name="count">要写入的字节数。</param>
    /// <param name="cancellationToken">用于取消写入操作的令牌。</param>
    /// <returns>写入完成时完成的任务。</returns>
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    /// <summary>将任何缓冲的输出刷新到远端 shell。</summary>
    void Flush();

    /// <summary>
    /// 发送 SSH 窗口变更请求,使远端 PTY 匹配本地终端尺寸。
    /// 像素尺寸报告为 0(仅使用字符单元尺寸)。
    /// </summary>
    void Resize(int columns, int rows);

    /// <summary>
    /// 打开这条流时顺带发生、值得让用户知道的事(已本地化,一条一行):
    /// X11 / agent 转发开成了没有、没开成是为什么。
    /// </summary>
    /// <remarks>
    /// 转发被服务端拒绝时 shell 照样打开(去掉那一项重开一次)—— 为一个附带功能让整条会话
    /// 连不上是本末倒置。但静默去掉又会让用户对着「cannot open display」不知所以,
    /// 所以原因留在这里,由宿主以一行灰字写进终端。没有这类事的实现返回空。
    /// </remarks>
    IReadOnlyList<ShellStreamNotice> Notices => [];
}

/// <summary>打开 shell 流时附带的一条提示。</summary>
/// <param name="Text">已本地化的提示文本。</param>
/// <param name="IsWarning">是不是「没开成」这一类(宿主用醒目一点的颜色)。</param>
public readonly record struct ShellStreamNotice(string Text, bool IsWarning);
