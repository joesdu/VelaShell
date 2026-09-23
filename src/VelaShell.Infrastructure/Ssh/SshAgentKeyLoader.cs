using System.Diagnostics;
using VelaShell.Core.Models;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 「自动加载密钥到 Agent」(设置 → 密钥管理,<c>KeyOptions.AddKeysToAgent</c>):
/// 私钥文件认证成功后,把这把钥加进本机 ssh-agent —— 即 OpenSSH 的 <c>AddKeysToAgent yes</c>。
/// </summary>
/// <remarks>
/// <para>
/// 用处在 agent 那一头:加进去之后,开了「转发 ssh-agent」的会话在跳板机上 <c>ssh</c> 下一跳时
/// 就用得上这把钥,本机的 git / ssh 也用得上,加密私钥不必再输口令。
/// </para>
/// <para>
/// <b>尽力而为,绝不连累连接。</b>agent 没在跑、被锁定、不认这种密钥,一律只记一行诊断日志。
/// 调用方在连接建好之后丢到后台跑,agent 连不上时的那三秒等待也不会落在连接路径上。
/// </para>
/// </remarks>
internal static class SshAgentKeyLoader
{
    /// <summary>一次加钥的结果,供测试与日志区分。</summary>
    internal enum Outcome
    {
        /// <summary>加进去了。</summary>
        Added,

        /// <summary>agent 里已经有这把钥,什么都没做。</summary>
        AlreadyPresent,

        /// <summary>连不上 agent,或者 agent 拒绝了。</summary>
        Failed,
    }

    /// <summary>整件事的上限:连 agent 三秒(与「SSH Agent」认证同一口径),列身份与加钥再给几秒。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>这次连接该不该往 agent 里加钥;该加时给出要加的签名器与注释。</summary>
    /// <remarks>
    /// 只认「私钥」认证:证书认证的签名器包着证书,库暂不支持「证书 + 私钥」的加钥格式;
    /// 「SSH Agent」认证的钥本来就在 agent 里;密码认证没有钥。
    /// </remarks>
    internal static bool TryGetKeyToAdd(
        VelaConnectionInfo info, IReadOnlyList<SshCredential> credentials, out InMemorySshSigner key, out string comment)
    {
        if (info.AuthMethod == AuthMethod.PrivateKey
            && credentials is [PublicKeyCredential { Signer: InMemorySshSigner signer }])
        {
            key = signer;
            comment = info.PrivateKeyPath ?? "";
            return true;
        }

        key = null!;
        comment = "";
        return false;
    }

    /// <summary>把 <paramref name="key"/> 加进 agent;agent 里已有同一把钥时不重复加。</summary>
    /// <param name="key">要加的私钥。</param>
    /// <param name="comment">注释,<c>ssh-add -l</c> 显示的那一列;写私钥文件路径,与 <c>ssh-add</c> 一致。</param>
    /// <param name="connect">连本机 agent。</param>
    internal static async Task<Outcome> AddAsync(
        InMemorySshSigner key,
        string comment,
        Func<CancellationToken, ValueTask<SshAgentClient>> connect)
    {
        using CancellationTokenSource budget = new(Budget);
        try
        {
            await using SshAgentClient agent = await connect(budget.Token).ConfigureAwait(false);

            // 先看一眼:OpenSSH agent 对重复加钥是「更新注释」,本身无害,
            // 但每连一次就往 agent 里写一次私钥没有必要。
            IReadOnlyList<SshAgentIdentity> present =
                await agent.ListIdentitiesAsync(budget.Token).ConfigureAwait(false);
            if (present.Any(id => id.PublicKey.Blob.Span.SequenceEqual(key.PublicKey.Blob.Span)))
            {
                return Outcome.AlreadyPresent;
            }

            await agent.AddIdentityAsync(key, comment, cancellationToken: budget.Token).ConfigureAwait(false);
            Trace.WriteLine($"[ssh-agent] 已把 {comment} 加入 {agent.Endpoint}");
            return Outcome.Added;
        }
        catch (Exception ex) when (ex is SshAgentException or OperationCanceledException or IOException
                                       or ObjectDisposedException)
        {
            Trace.WriteLine($"[ssh-agent] 没能把 {comment} 加入 agent:{ex.Message}");
            return Outcome.Failed;
        }
    }
}
