using System.Text;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 一轮系统提示词的组成:按固定顺序拼接的若干分段,桌面、IM 等入口共用(见 ai-plan §12.2 的 1a)。
/// </summary>
/// <remarks>
/// <para>
/// 以前每个入口自己拼:基础说明之后各自追加委派、团队、作图这些「本轮有这件工具才说」的指引,
/// 新加一段就得在每个入口各补一处,漏了也看不出来(作图指引就只在桌面有)。
/// 现在入口只交出自己的那部分(<see cref="Base" /> 与入口专属分段),按工具决定的指引由
/// <see cref="AddToolGuidance" /> 统一判定;以后技能目录等分段也从这里进。
/// </para>
/// <para>
/// 分段之间不加分隔:各段文字自带开头的换行或空格,拼出来与原先逐段相加的结果逐字相同。
/// 空分段跳过。<see cref="SectionIds" /> 给测试核对顺序与是否出现。
/// </para>
/// </remarks>
internal sealed class SystemPromptBuilder
{
    /// <summary>入口自己的基础说明:身份、语气、模式、用户的自定义提示词。</summary>
    public const string Base = "base";

    /// <summary>委派指引(本轮暴露了 <c>delegate_subagent</c> 时)。</summary>
    public const string Delegation = "delegation";

    /// <summary>团队指引(本轮暴露了 <c>team_run</c> 时)。</summary>
    public const string Team = "team";

    /// <summary>作图指引(桌面,本轮启用了作图工具时)。</summary>
    public const string Images = "images";

    /// <summary>技能目录(本轮暴露了 <c>load_skill</c> 时;见 <see cref="Skills.SkillToolSource.Index" />)。</summary>
    public const string Skills = "skills";

    /// <summary>用户自定义的提示词,永远排在最后(见 <see cref="AddCustom" />)。</summary>
    public const string Custom = "custom";

    /// <summary>自定义提示词前的引导语;桌面与 IM 同一句。</summary>
    internal const string CustomPrefix = "\n\nOperator instructions: ";

    private readonly List<(string Id, string Text)> _sections = [];

    /// <summary>已加入的分段 id(按顺序)。</summary>
    public IReadOnlyList<string> SectionIds => [.. _sections.Select(s => s.Id)];

    /// <summary>追加一段;文字为空时跳过。</summary>
    public SystemPromptBuilder Add(string id, string? text)
    {
        if (!string.IsNullOrEmpty(text)) _sections.Add((id, text));
        return this;
    }

    /// <summary>按本轮实际暴露的工具追加委派与团队指引(没有那件工具就不说,免得模型去调一个不存在的工具)。</summary>
    public SystemPromptBuilder AddToolGuidance(IEnumerable<AITool>? tools)
    {
        var list = tools?.ToList();
        if (SubagentRunner.HasDelegation(list)) Add(Delegation, SubagentRunner.DelegationGuidance);
        if (SubagentRunner.HasTeam(list)) Add(Team, SubagentRunner.TeamGuidance);
        return this;
    }

    /// <summary>
    /// 追加技能目录:只在本轮真的暴露了 <c>load_skill</c> 时(对话模式、或用户在「配置工具」里停用了它时不说,
    /// 免得模型去调一个不存在的工具)。
    /// </summary>
    public SystemPromptBuilder AddSkills(IEnumerable<AITool>? tools, IReadOnlyList<Skills.SkillInfo> skills)
        => tools?.Any(t => t.Name == Chat.Skills.SkillToolSource.LoadToolName) == true
            ? Add(Skills, Chat.Skills.SkillToolSource.Index(skills))
            : this;

    /// <summary>
    /// 追加用户自定义的提示词:模型级的优先,没有再用全局的;都没填就不加。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前桌面上填了自定义提示词就<b>整段替换</b>内置说明,模式说明、「MCP 跑在本机」的提醒跟着一起丢掉,
    /// 而 IM 是追加 —— 同一份设置在两个入口效果不同。现在两处一样:内置分段照常在前,自定义的排在最后
    /// (排最后,模型更看重它,与内置说明有出入时以它为准)。模式的实际约束仍靠「给不给工具」兜底,不依赖提示词。
    /// </para>
    /// <para>
    /// 模型级提示词按<b>当前这一站</b>的模型取(故障转移换站后跟着换),不读界面选中的那家,免得把别家的专用提示词发出去。
    /// </para>
    /// </remarks>
    /// <param name="modelPrompt">当前这一站模型的专用提示词。</param>
    /// <param name="globalPrompt">全局提示词。</param>
    /// <param name="extra">入口自己再追加的一段(IM 的机器人附加提示词);可为空。</param>
    public SystemPromptBuilder AddCustom(string? modelPrompt, string? globalPrompt, string? extra = null)
    {
        string? chosen = !string.IsNullOrWhiteSpace(modelPrompt) ? modelPrompt.Trim()
            : !string.IsNullOrWhiteSpace(globalPrompt) ? globalPrompt.Trim() : null;
        string text = (chosen is null ? "" : CustomPrefix + chosen)
                      + (string.IsNullOrWhiteSpace(extra) ? "" : "\n\n" + extra.Trim());
        return Add(Custom, text);
    }

    /// <summary>拼成最终的系统提示词。</summary>
    public string Build()
    {
        var text = new StringBuilder();
        foreach ((_, string section) in _sections) text.Append(section);
        return text.ToString();
    }
}
