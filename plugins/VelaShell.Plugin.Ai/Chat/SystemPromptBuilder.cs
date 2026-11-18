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
        List<AITool>? list = tools?.ToList();
        if (SubagentRunner.HasDelegation(list)) Add(Delegation, SubagentRunner.DelegationGuidance);
        if (SubagentRunner.HasTeam(list)) Add(Team, SubagentRunner.TeamGuidance);
        return this;
    }

    /// <summary>拼成最终的系统提示词。</summary>
    public string Build()
    {
        var text = new StringBuilder();
        foreach ((_, string section) in _sections) text.Append(section);
        return text.ToString();
    }
}
