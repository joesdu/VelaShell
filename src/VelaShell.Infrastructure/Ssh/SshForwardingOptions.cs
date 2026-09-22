using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 把一条配置里的 <see cref="SshSessionOptions" /> 翻成库的转发选项。
/// </summary>
/// <remarks>
/// 只管「交互式 shell 上请求什么」。压缩不在这里 —— 它在建链时协商,见
/// <see cref="SshConnectionAssembler.Algorithms" />。
/// </remarks>
internal static class SshForwardingOptions
{
    /// <summary>
    /// X11 转发选项;没开 X11、或显示地址写得认不出来时返回 <see langword="null" />
    /// (后一种往 <paramref name="notices" /> 里记一条原因)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 显示地址按「配置里填的 → <c>DISPLAY</c> 环境变量 → <see cref="SshSessionOptions.DefaultX11Display" />」
    /// 依次取。Windows 上几乎没人设 <c>DISPLAY</c>,而 VcXsrv / Xming / X410 默认都监听
    /// <c>localhost:0</c> —— 最后那一档照顾的就是这种「装好 X 服务器、什么都没配」的情形。
    /// </para>
    /// <para>
    /// <b>不设有效期</b>(<see cref="TimeSpan.Zero" />)。库默认 20 分钟后拒绝新的 X11 通道,
    /// 那是给脚本里一次性的 <c>ssh -X</c> 用的;交互式会话里「开了半小时之后 xclock 打不开了」
    /// 只会让人以为转发坏了。PuTTY / MobaXterm 也都是整条会话有效。
    /// </para>
    /// </remarks>
    public static X11ForwardOptions? X11(SshSessionOptions? features, List<ShellStreamNotice> notices)
    {
        if (features is not { X11Forwarding: true })
        {
            return null;
        }

        string text = !string.IsNullOrWhiteSpace(features.X11Display)
            ? features.X11Display.Trim()
            : Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : SshSessionOptions.DefaultX11Display;

        if (X11Display.Parse(text) is not { } display)
        {
            notices.Add(new(Strings.Format("Ssh_X11ForwardFailed", Strings.Format("Ssh_X11BadDisplay", text)), true));
            return null;
        }

        return new X11ForwardOptions
        {
            Display = display,
            Trusted = features.X11Trusted,
            Timeout = TimeSpan.Zero,
        };
    }

    /// <summary>agent 转发策略;没开时返回 <see langword="null" />。</summary>
    /// <remarks>
    /// 用默认策略(agent 里的钥全部可见、不逐次确认)—— 与 <c>ssh -A</c> 一致。
    /// 「只转发指定密钥」「每次签名都问一句」库都支持,要做的话在这里接界面。
    /// </remarks>
    public static AgentForwardPolicy? Agent(SshSessionOptions? features) =>
        features is { AgentForwarding: true } ? AgentForwardPolicy.Default : null;

    /// <summary>给人看的显示地址,形如 <c>localhost:0.0</c>。</summary>
    public static string Describe(X11Display display) => $"{display.XAuthName}.{display.Screen}";
}
