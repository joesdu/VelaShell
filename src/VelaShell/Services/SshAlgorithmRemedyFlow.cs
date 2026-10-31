using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.Services;

/// <summary>「放开这几个算法」确认之后各入口接着做什么;决定按钮、确认框与确认键上的说法。</summary>
public enum SshAlgorithmRemedyFollowUp
{
    /// <summary>就地重连(终端标签的失败覆盖层、独立 SFTP 文档的失败卡片)。</summary>
    Reconnect,

    /// <summary>重新诊断(连接诊断窗口)。</summary>
    Rediagnose,

    /// <summary>重新测试(连接配置页的「测试」)。</summary>
    Retest,
}

/// <summary>写进去了:<paramref name="Stored" /> 是改过并落了盘的库里那份;配置不在库里(临时配置、只改表单)时为 null。</summary>
/// <param name="Stored">改过并落了盘的库里那份。</param>
public sealed record SshAlgorithmRemedyApplied(SessionProfile? Stored);

/// <summary>
/// 协商失败时「放开这几个算法」各入口共用的那一段:找出谈不成的那一跳在库里的配置、点名确认、写回库里。
/// </summary>
/// <remarks>
/// <para>
/// 算哪几个、怎么写进清单在 <see cref="SshAlgorithmRemedy" />;这里只管「改哪一条、问一句、存下来」。
/// 终端标签、独立 SFTP 文档、连接诊断、连接配置页的「测试」四个入口走同一个确认框(同一个标题、同一种点名),
/// 确认之后各按各的语义重连 / 重新诊断 / 重新测试 —— 那一步与「手里那份要不要跟着改」留给调用方。
/// </para>
/// <para>
/// 跳板上谈不成时改的是那台跳板自己的配置:顺着连接用的那份配置的「跳板主机」往上数到那一跳
/// (与 <c>ConnectionWorkflowService</c> 拼跳板链的口径一样:第一跳取手里那份的引用,再往上取库里的)。
/// 终点那一跳改库里那份而不是手里那份 —— 登录框交回来的副本可能带着用户没让记住的口令。
/// </para>
/// </remarks>
/// <param name="repository">会话库;为 <see langword="null" /> 时(headless)只当配置都不在库里。</param>
/// <param name="workflow">连接工作流,落盘优先经它(按记住密码的策略写);没有时直接写库。</param>
public sealed class SshAlgorithmRemedyFlow(ISessionRepository? repository, IConnectionWorkflowService? workflow)
{

    /// <summary>那颗按钮的悬停说明:往哪一类清单里加哪几个,然后做什么。</summary>
    public static string ButtonTip(SshAlgorithmRemedy remedy, SshAlgorithmRemedyFollowUp followUp)
    {
        ArgumentNullException.ThrowIfNull(remedy);
        string key = followUp switch
        {
            SshAlgorithmRemedyFollowUp.Rediagnose => "SshAlgoFix_ButtonTipRediagnose",
            SshAlgorithmRemedyFollowUp.Retest => "SshAlgoFix_ButtonTipRetest",
            _ => "SshAlgoFix_ButtonTip",
        };
        return Strings.Format(key, SshAlgorithmPreferences.Label(remedy.Kind), string.Join(", ", remedy.Algorithms));
    }

    /// <summary>
    /// 找到要改的那条配置、点名确认、写回库里。返回 <see langword="null" /> = 什么都没改
    /// (用户取消、跳板配置找不到、确认期间配置被删、写失败 —— 后三种已经经 <paramref name="reportError" /> 说过了)。
    /// </summary>
    /// <remarks>写回的是确认之后重读的那份,只在上面补这几个算法:确认框开着时别处对这条配置的改动不会被框前读的那份冲掉。</remarks>
    /// <param name="profile">这次连接用的那份配置(终点那一跳)。</param>
    /// <param name="remedy">诊断点名的补救。</param>
    /// <param name="followUp">确认之后做什么(只影响说法)。</param>
    /// <param name="confirm">确认框:标题、正文、确认键文字;确认返回 true。</param>
    /// <param name="reportError">说一句失败原因(浮层、表单错误栏……由入口自己定)。</param>
    /// <param name="stillValid">
    /// 确认框关掉之后再核一次:用户看框的这段时间里,入口可能已经重连上了、换了一个失败原因。
    /// </param>
    /// <param name="formOnly">
    /// 终点那一跳只改表单、不碰库(连接配置页:正在编辑的就是它,按「保存」才存);确认框里会多一句说明。
    /// 跳板那一跳不受影响,照样写库 —— 表单里改不到别的配置。
    /// </param>
    public async Task<SshAlgorithmRemedyApplied?> ConfirmAndSaveAsync(
        SessionProfile profile,
        SshAlgorithmRemedy remedy,
        SshAlgorithmRemedyFollowUp followUp,
        Func<string, string, string, Task<bool>> confirm,
        Action<string> reportError,
        Func<bool>? stillValid = null,
        bool formOnly = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(remedy);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(reportError);

        bool touchesForm = formOnly && remedy.HopDepth == 0;
        SessionProfile? stored = null;
        if (!touchesForm)
        {
            try
            {
                stored = await FindStoredHopAsync(profile, remedy.HopDepth).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                reportError(Strings.Format("SshAlgoFix_SaveFailed", ex.Message));
                return null;
            }
            if (remedy.HopDepth > 0 && stored is null)
            {
                // 跳板的配置找不到了(删了、链改了):不猜,让用户自己去改。
                reportError(Strings.Format("SshAlgoFix_HopMissing", remedy.HopTarget ?? string.Empty));
                return null;
            }
        }

        SessionProfile named = stored ?? profile;
        string name = string.IsNullOrWhiteSpace(named.Name) ? HostAddress.FormatEndPoint(named.Host, named.Port) : named.Name;
        string body = Strings.Format(
            followUp switch
            {
                SshAlgorithmRemedyFollowUp.Rediagnose => "SshAlgoFix_ConfirmBodyRediagnose",
                SshAlgorithmRemedyFollowUp.Retest => "SshAlgoFix_ConfirmBodyRetest",
                _ => "SshAlgoFix_ConfirmBody",
            },
            name,
            SshAlgorithmPreferences.Label(remedy.Kind),
            string.Join(Environment.NewLine, remedy.Algorithms.Select(a => "  " + a)));
        if (touchesForm)
        {
            body += Environment.NewLine + Environment.NewLine + Strings.Get("SshAlgoFix_FormOnlyNote");
        }
        string action = Strings.Get(followUp switch
        {
            SshAlgorithmRemedyFollowUp.Rediagnose => "SshAlgoFix_ConfirmActionRediagnose",
            SshAlgorithmRemedyFollowUp.Retest => "SshAlgoFix_ConfirmActionRetest",
            _ => "SshAlgoFix_ConfirmAction",
        });
        if (!await confirm(Strings.Get("SshAlgoFix_ConfirmTitle"), body, action).ConfigureAwait(true)
            || stillValid?.Invoke() == false)
        {
            return null;
        }

        if (stored is null)
        {
            return new(null);
        }
        // 确认框可以开着任意久:这期间云同步拉下来的、巡检窗口「编辑配置」存的、别的标签重连成功写回的都可能改了这条配置。
        // 存的是整份文档,拿框前读的那份去存就把它们冲掉 —— 确认之后重读一次,只在最新的那份上补这几个算法。
        try
        {
            // stored 读自库里,repository 一定在。
            stored = await repository!.GetSessionAsync(stored.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            reportError(Strings.Format("SshAlgoFix_SaveFailed", ex.Message));
            return null;
        }
        if (stored is null)
        {
            // 看框的这段时间里配置被删了:不凭框前那份再建回来。
            reportError(remedy.HopDepth > 0
                ? Strings.Format("SshAlgoFix_HopMissing", remedy.HopTarget ?? string.Empty)
                : Strings.Format("SshAlgoFix_ProfileMissing", name));
            return null;
        }
        stored.Ssh = remedy.ApplyTo(stored.Ssh);
        try
        {
            if (workflow is not null)
            {
                await workflow.SaveProfileAsync(stored).ConfigureAwait(true);
            }
            else if (repository is not null)
            {
                await repository.SaveSessionAsync(stored).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            reportError(Strings.Format("SshAlgoFix_SaveFailed", ex.Message));
            return null;
        }
        return new(stored);
    }

    /// <summary>
    /// 库里那条配置:<paramref name="depth" /> 为 0 是这条本身,1 是它的跳板,2 是跳板的跳板……
    /// 不在库里(临时配置)或链上断了时为 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 第一跳取手里那份的「跳板主机」(连接时拼链用的就是它 —— 连接配置页里刚换过、还没保存的跳板也算),
    /// 再往上取库里的。
    /// </remarks>
    private async Task<SessionProfile?> FindStoredHopAsync(SessionProfile profile, int depth)
    {
        if (repository is null)
        {
            return null;
        }
        if (depth == 0)
        {
            return await repository.GetSessionAsync(profile.Id).ConfigureAwait(true);
        }
        HashSet<Guid> visited = [profile.Id];
        Guid? next = profile.JumpHostProfileId;
        SessionProfile? current = null;
        for (int i = 0; i < depth; i++)
        {
            if (next is not { } id || !visited.Add(id))
            {
                return null;
            }
            current = await repository.GetSessionAsync(id).ConfigureAwait(true);
            next = current?.JumpHostProfileId;
        }
        return current;
    }
}
