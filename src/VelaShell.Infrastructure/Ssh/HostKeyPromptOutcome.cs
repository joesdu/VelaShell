namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 一次连接尝试里主机指纹裁决留下的痕迹:握手回调写,<see cref="TmdsSshClientWrapper" /> 在
/// 连接失败时读。与代理中继的 <c>LoopbackProxyRelay.Error</c> 同一套路 —— 回调只能返回
/// true/false,真正的原因得另外递出来。
/// </summary>
/// <remarks>
/// 两件事靠它办成:
/// <list type="number">
/// <item>
/// <b>把"为什么连不上"说人话。</b>回调返回 false 后,Tmds 抛的是一句
/// <c>ConnectFailedReason.UntrustedPeer</c>,用户只看到一行英文,根本看不出是指纹变了,
/// 更不知道去哪删记录。
/// </item>
/// <item>
/// <b>补一次超时重连。</b>Tmds 0.24 的 <c>ConnectTimeout</c> 覆盖整个握手,**包括**
/// HostAuthentication 回调 —— 弹窗摆在那儿,计时器照样在跑。用户盯着那条红色警告
/// 多想十几秒,点完"信任"连接已经被判超时,明明认了却报连不上。裁决此时已经落盘
/// (或进了本次运行的临时信任),原地重连一次即可。
/// </item>
/// </list>
/// 握手在后台线程、读取在发起连接的线程,故字段一律走 <see cref="Volatile" />。
/// </remarks>
public sealed class HostKeyPromptOutcome
{
    private string? _rejection;
    private int _approvedAfterPrompt;

    /// <summary>本次尝试中用户在弹窗里点了"信任"(永久或仅本次)。</summary>
    public bool ApprovedAfterPrompt => Volatile.Read(ref _approvedAfterPrompt) != 0;

    /// <summary>本次尝试中指纹被拒的可读原因;没被拒时为 <see langword="null" />。</summary>
    public string? Rejection => Volatile.Read(ref _rejection);

    /// <summary>记下"用户当场认了这把指纹",供超时重连判据使用。</summary>
    public void MarkApproved() => Volatile.Write(ref _approvedAfterPrompt, 1);

    /// <summary>记下拒绝原因(已本地化,直接给用户看)。</summary>
    public void MarkRejected(string reason) => Volatile.Write(ref _rejection, reason);

    /// <summary>重连前清空:上一轮的痕迹不能影响下一轮的判据。</summary>
    public void Reset()
    {
        Volatile.Write(ref _approvedAfterPrompt, 0);
        Volatile.Write(ref _rejection, null);
    }
}
