using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;

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
    /// <para>
    /// <b>尽力而为</b>(<see cref="X11ForwardOptions.BestEffort" />)。配置里的开关是连接级的,
    /// 本机没开 X 服务器、没装 xauth、服务端 <c>X11Forwarding no</c> 都很常见,为它们让会话开不起来是本末倒置。
    /// 设置失败时库不抛、shell 照常启动,原因在 <see cref="VelaShell.Ssh.Channels.SshShell.X11SetupFailure" /> 上,
    /// 由 <see cref="VelaSshClientWrapper" /> 转成终端里的提示 —— 不必为了 X11 重开一次 shell。
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
            BestEffort = true,
        };
    }

    /// <summary>逐次确认时等用户多久;过了按拒绝处理。</summary>
    internal static readonly TimeSpan AgentConfirmTimeout = TimeSpan.FromSeconds(60);

    /// <summary>agent 转发策略;没开、或限定的密钥一把都解析不出来时返回 <see langword="null" />。</summary>
    /// <param name="features">这条配置的 SSH 选项。</param>
    /// <param name="notices">不转发的原因往这里记一条黄字。</param>
    /// <param name="prompt">逐次确认的弹窗;<see langword="null" /> 时开了确认就一律拒签(fail-closed)。</param>
    /// <param name="target">给用户看的「哪条会话在要」,<c>用户@主机:端口</c>。</param>
    /// <param name="confirmTimeout">确认等多久;<see langword="null" /> 为 <see cref="AgentConfirmTimeout" />。</param>
    /// <remarks>
    /// <para>
    /// 默认(不限定、不确认)与 <c>ssh -A</c> 一致。
    /// </para>
    /// <para>
    /// <b>限定了密钥却一把都解析不出来时不转发</b>,而不是交给库一个空的 <see cref="AgentForwardPolicy.AllowedKeys" />
    /// —— 库把空列表解释为「整个 agent 都可见」,那是把用户的限定悄悄翻成了最宽的那一档。
    /// </para>
    /// <para>
    /// <b>逐次确认</b>:同一条会话里用户选了「本次会话内允许」的钥之后不再问;
    /// <paramref name="confirmTimeout" /> 内没人应答按拒绝 —— 远端的 ssh 会报「agent 拒绝签名」,
    /// 比无限期挂着好(用户可能根本不在电脑前,而后台会话里的脚本正在用他的身份)。
    /// </para>
    /// </remarks>
    public static AgentForwardPolicy? Agent(
        SshSessionOptions? features,
        List<ShellStreamNotice> notices,
        IAgentSignPrompt? prompt = null,
        string target = "",
        TimeSpan? confirmTimeout = null)
    {
        if (features is not { AgentForwarding: true })
        {
            return null;
        }

        IReadOnlyList<SshPublicKey> allowed = [];
        if (features.AgentForwardKeys is { } lines)
        {
            allowed = ParseKeys(lines);
            if (allowed.Count == 0)
            {
                notices.Add(new(Strings.Format("Ssh_AgentForwardFailed", Strings.Get("Ssh_AgentForwardNoKeys")), true));
                return null;
            }
        }

        return new AgentForwardPolicy
        {
            AllowedKeys = allowed,
            ConfirmEachSignature = features.AgentForwardConfirm
                ? Confirmer(prompt, target, confirmTimeout ?? AgentConfirmTimeout)
                : null,
        };
    }

    /// <summary>shell 开成之后那行灰字:开着的限定与确认一并写上,用户一眼看得出这条会话借出去的是什么。</summary>
    public static string DescribeAgent(SshSessionOptions? features, AgentForwardPolicy policy)
    {
        string text = Strings.Get("Ssh_AgentForwardOn");
        if (policy.AllowedKeys.Count > 0)
        {
            text += Strings.Format("Ssh_AgentForwardOnlyKeys", policy.AllowedKeys.Count);
        }
        if (features is { AgentForwardConfirm: true })
        {
            text += Strings.Get("Ssh_AgentForwardConfirmEach");
        }
        return text;
    }

    /// <summary>把存下来的公钥行解析成公钥;认不出来的行跳过。</summary>
    internal static IReadOnlyList<SshPublicKey> ParseKeys(IEnumerable<string> lines)
    {
        List<SshPublicKey> keys = [];
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            try
            {
                keys.Add(SshPublicKey.Parse(Convert.FromBase64String(parts[1])));
            }
            catch (Exception ex) when (ex is FormatException or SshPublicKeyException)
            {
                // 坏行或库不认识的类型:跳过,别让一行拖垮整个限定。
            }
        }
        return keys;
    }

    private static Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>> Confirmer(
        IAgentSignPrompt? prompt, string target, TimeSpan timeout)
    {
        // 「本次会话内允许」记在这里:闭包跟着这一条 shell 的转发器走,会话关了就没了。
        HashSet<string> allowedForSession = new(StringComparer.Ordinal);
        Lock gate = new();

        return async (request, cancellationToken) =>
        {
            string fingerprint = request.Key.Sha256Fingerprint;
            lock (gate)
            {
                if (allowedForSession.Contains(fingerprint))
                {
                    return true;
                }
            }

            if (prompt is null)
            {
                return false;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            AgentSignDecision decision;
            try
            {
                decision = await prompt
                    .ConfirmAsync(new AgentSignRequest(target, request.Key.KeyType, fingerprint, request.Comment, timeout), deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;   // 超时、通道关了、弹窗出错:一律拒签
            }

            // 实现方没理会取消、过了期限才交回「允许」:期限就是期限,照样拒。
            if (deadline.IsCancellationRequested)
            {
                return false;
            }

            if (decision == AgentSignDecision.AllowForSession)
            {
                lock (gate)
                {
                    allowedForSession.Add(fingerprint);
                }
            }
            return decision != AgentSignDecision.Deny;
        };
    }

    /// <summary>给人看的显示地址,形如 <c>localhost:0.0</c>。</summary>
    public static string Describe(X11Display display) => $"{display.XAuthName}.{display.Screen}";
}
