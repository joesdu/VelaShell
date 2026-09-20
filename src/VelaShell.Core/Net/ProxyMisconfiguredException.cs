namespace VelaShell.Core.Net;

/// <summary>
/// 代理配置本身不成立:用户显式选了 http / socks5,却没把主机端口填全,
/// 或凭据超出了协议能表达的长度(SOCKS5 的用户名密码各 255 字节)。
/// </summary>
/// <remarks>
/// <para>
/// 刻意继承 <see cref="InvalidOperationException" />:各通道既有的
/// <c>catch (InvalidOperationException)</c>(SSH 侧 <c>PrepareProxyRelay</c>)照常命中,
/// 同时让调用方能**按类型**认出「这条消息本身已经说清楚了,别再翻译成笼统的连接错误」。
/// </para>
/// <para>
/// 之所以要一个类型:原先 FTP 侧是拿本地化后的消息文本去比对
/// (<c>ex.Message == Strings.Get("Msg_ProxyMisconfigured")</c>)—— 换一种界面语言就失效,
/// 而且新增一条同类错误(超长凭据)时会被漏掉,正是这样漏过一次。
/// </para>
/// </remarks>
/// <param name="message">面向用户的说明,已本地化。</param>
public sealed class ProxyMisconfiguredException(string message) : InvalidOperationException(message);
