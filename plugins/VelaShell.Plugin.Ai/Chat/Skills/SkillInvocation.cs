using System.Text;

namespace VelaShell.Plugin.Ai.Chat.Skills;

/// <summary>
/// 用户用 <c>/技能名 请求</c> 显式调用技能(ai-plan §12.3 的 2b):把这条消息展开成「带着该技能指令的请求」再交给模型。
/// 桌面与 IM 共用;界面与历史里留的仍是用户敲的那一行。
/// </summary>
/// <remarks>
/// <para>
/// 模型自己也会按技能目录判断要不要 <c>load_skill</c>,但用户点名时不该再让它猜:直接把正文附上,省一次工具往返,
/// 而且对话模式(没有工具)下也能用。
/// </para>
/// <para>
/// 各入口自己的内置命令(桌面的 <c>/clear</c>,IM 的 <c>/use</c> 等)先判定、优先于同名技能;
/// 这里只认技能。在「配置工具」里停用了 <c>load_skill</c>(整体关闭技能)时不展开。
/// </para>
/// </remarks>
internal static class SkillInvocation
{
    /// <summary>
    /// 消息是不是 <c>/技能名 …</c>:是就返回那个技能与后面的请求,不是返回 null。
    /// </summary>
    public static (SkillInfo Skill, string Request)? Match(string text, IReadOnlyList<SkillInfo> skills)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/') return null;
        int end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
        string name = trimmed[1..end];
        SkillInfo? skill = skills.FirstOrDefault(s => s.IsValid && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        return skill is null ? null : (skill, trimmed[end..].Trim());
    }

    /// <summary>
    /// 展开成交给模型的正文;不是技能调用、或读不到技能正文时返回 null(调用方照原文发送)。
    /// </summary>
    public static string? TryExpand(string text, IReadOnlyList<SkillInfo> skills)
    {
        if (Match(text, skills) is not { } match) return null;
        string body;
        try
        {
            body = SkillCatalog.ReadBody(match.Skill);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
        return Expand(match.Skill, body, match.Request);
    }

    /// <summary>拼出交给模型的正文。</summary>
    public static string Expand(SkillInfo skill, string body, string request)
    {
        var text = new StringBuilder()
            .Append("The user explicitly invoked the skill \"").Append(skill.Name)
            .Append("\" for this request. Follow its instructions below; they never override the user's explicit request, the tool rules or approvals.\n\n")
            .Append("<skill name=\"").Append(skill.Name).Append("\">\n")
            .Append(body.Length > 0 ? body : skill.Description).Append("\n</skill>\n\n")
            .Append("If the instructions refer to files that come with the skill, read them with ")
            .Append(SkillToolSource.ReadFileToolName).Append(" when that tool is available.\n\n")
            .Append("User request: ")
            .Append(request.Length > 0 ? request : "(none given: apply the skill to the current conversation)");
        return text.ToString();
    }
}
